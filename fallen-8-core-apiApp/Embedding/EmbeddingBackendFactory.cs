// MIT License
//
// EmbeddingBackendFactory.cs
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
using Microsoft.Extensions.AI;
using NoSQL.GraphDB.App.Configuration;
using OllamaSharp;

namespace NoSQL.GraphDB.App.Embedding
{
    /// <summary>
    ///   Maps <c>Fallen8:Embedding:Backend</c> to an
    ///   <see cref="IEmbeddingGenerator{TInput,TEmbedding}" /> (feature embedding-provider,
    ///   FR-4). Swapping the backend is exactly this switch - a configuration change, never a
    ///   code change. Called lazily on first use only. An OpenAI-compatible remote backend is
    ///   the documented extension point: one more case here, config shape reserved.
    /// </summary>
    internal static class EmbeddingBackendFactory
    {
        internal static IEmbeddingGenerator<String, Embedding<Single>> Create(Fallen8EmbeddingOptions options)
        {
            switch (options.Backend)
            {
                case "Onnx":
                    return new OnnxEmbeddingGenerator(options.Onnx);

                case "LLamaSharp":
                    return new LLamaSharpEmbeddingGenerator(options.LLamaSharp);

                case "Ollama":
                    // OllamaSharp implements the abstraction natively; this couples embedding
                    // availability to the (compose-shipped) Ollama container - stated in the
                    // spec, surfaced as 503 while it is down.
                    return new OllamaApiClient(new Uri(options.Ollama.Endpoint), options.Ollama.Model);

                default:
                    throw new InvalidOperationException(String.Format(
                        "'{0}' is not a supported embedding backend. Expected Onnx, LLamaSharp or Ollama.",
                        options.Backend));
            }
        }
    }
}
