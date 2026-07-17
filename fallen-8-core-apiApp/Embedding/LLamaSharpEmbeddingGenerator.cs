// MIT License
//
// LLamaSharpEmbeddingGenerator.cs
//
// Copyright (c) 2025 Henning Rauch
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LLama;
using LLama.Common;
using Microsoft.Extensions.AI;
using NoSQL.GraphDB.App.Configuration;

namespace NoSQL.GraphDB.App.Embedding
{
    /// <summary>
    ///   In-process GGUF backend via LLamaSharp (feature embedding-provider): points at an
    ///   embedding-capable GGUF file - typically a blob already on the <c>f8-ollama-models</c>
    ///   volume, so large weights exist ONCE on disk. CPU backend by design (spec non-goal:
    ///   the GPU stays with the Ollama sidecar). Honesty note: the same weights under this
    ///   runtime and under the Ollama daemon are two llama.cpp builds - bit-identical output
    ///   across them is NOT guaranteed; the FR-8 identity contract is what protects
    ///   correctness. Construction loads the model - callers defer construction until first
    ///   use (the provider's lazy).
    /// </summary>
    public sealed class LLamaSharpEmbeddingGenerator : IEmbeddingGenerator<String, Embedding<Single>>
    {
        private readonly LLamaWeights _weights;
        private readonly LLamaEmbedder _embedder;

        /// <summary>The embedder is not re-entrant; requests serialize on this semaphore.</summary>
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        public LLamaSharpEmbeddingGenerator(Fallen8EmbeddingOptions.LLamaSharpOptions options)
        {
            if (String.IsNullOrWhiteSpace(options.ModelPath) || !File.Exists(options.ModelPath))
            {
                throw new FileNotFoundException(
                    "Fallen8:Embedding:LLamaSharp:ModelPath must point at an existing, embedding-capable GGUF file; nothing is downloaded.",
                    options.ModelPath);
            }

            var parameters = new ModelParams(options.ModelPath)
            {
                Embeddings = true
            };

            _weights = LLamaWeights.LoadFromFile(parameters);
            _embedder = new LLamaEmbedder(_weights, parameters);
        }

        public async Task<GeneratedEmbeddings<Embedding<Single>>> GenerateAsync(IEnumerable<String> values,
            EmbeddingGenerationOptions options = null, CancellationToken cancellationToken = default)
        {
            var result = new GeneratedEmbeddings<Embedding<Single>>();

            await _gate.WaitAsync(cancellationToken);
            try
            {
                foreach (var value in values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var embeddings = await _embedder.GetEmbeddings(value ?? String.Empty, cancellationToken);
                    if (embeddings == null || embeddings.Count == 0)
                    {
                        throw new InvalidOperationException(
                            "The GGUF model returned no embedding - it is likely not an embedding-capable model.");
                    }

                    result.Add(new Embedding<Single>(embeddings[0]));
                }
            }
            finally
            {
                _gate.Release();
            }

            return result;
        }

        public Object GetService(Type serviceType, Object serviceKey = null)
        {
            return serviceType?.IsInstanceOfType(this) == true ? this : null;
        }

        public void Dispose()
        {
            _embedder.Dispose();
            _weights.Dispose();
            _gate.Dispose();
        }
    }
}
