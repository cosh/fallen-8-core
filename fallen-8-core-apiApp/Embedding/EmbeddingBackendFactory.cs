// MIT License
//
// EmbeddingBackendFactory.cs
//
// Copyright (c) 2011-2026 Henning Rauch
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
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Helper;
using OllamaSharp;

namespace NoSQL.GraphDB.App.Embedding
{
    /// <summary>
    ///   Maps <c>Fallen8:Embedding:Backend</c> to an
    ///   <see cref="IEmbeddingGenerator{TInput,TEmbedding}" /> (feature embedding-provider,
    ///   FR-4). Swapping the backend is exactly this switch - a configuration change, never a
    ///   code change. Called lazily on first use only. The remote case is <c>Nahil</c> (feature
    ///   nahil-backend): the same Ollama protocol, authenticated, on someone else's
    ///   hardware.
    /// </summary>
    internal static class EmbeddingBackendFactory
    {
        internal static IEmbeddingGenerator<String, Embedding<Single>> Create(Fallen8EmbeddingOptions options,
            ILoggerFactory loggerFactory)
        {
            switch (options.Backend)
            {
                case "Onnx":
                    return new OnnxEmbeddingGenerator(options.Onnx);

                case "LLamaSharp":
                    return new LLamaSharpEmbeddingGenerator(options.LLamaSharp);

                case "Ollama":
                case "Nahil":
                    // OllamaSharp implements the abstraction natively. The sidecar case couples
                    // embedding availability to the (compose-shipped) Ollama container - stated in the
                    // spec, surfaced as 503 while it is down; the Nahil case couples it to a remote
                    // service instead, and adds the credential plus the warm-up wait.
                    // The transport carries no deadline: Fallen8:Embedding:TimeoutSeconds, applied
                    // by Fallen8EmbeddingProvider as a linked token, is the single budget (see
                    // OllamaHttpClientFactory for the rule). The client is process-lifetime, held
                    // by the returned DI singleton, so it is not disposed here.
                    var connection = ResolveConnection(options);
                    if (!connection.IsValid(out var problem))
                    {
                        throw new InvalidOperationException(problem);
                    }

                    return new OllamaApiClient(
                        OllamaHttpClientFactory.CreateForProvider(connection,
                            loggerFactory?.CreateLogger("NoSQL.GraphDB.App.Embedding.Ollama")),
                        connection.Model);

                default:
                    throw new InvalidOperationException(String.Format(
                        "'{0}' is not a supported embedding backend. Expected Onnx, LLamaSharp, Ollama or Nahil.",
                        options.Backend));
            }
        }

        /// <summary>
        ///   THE one home for which endpoint, model and credential a configured embedding backend
        ///   dials, shared with the residency probe; <c>null</c> for a backend that dials nothing
        ///   (ONNX and LLamaSharp run in-process on operator-provided files).
        /// </summary>
        internal static OllamaConnection ResolveConnection(Fallen8EmbeddingOptions options)
        {
            switch (options.Backend)
            {
                case "Ollama":
                    return OllamaConnection.Sidecar("Fallen8:Embedding:Ollama",
                        options.Ollama?.Endpoint, options.Ollama?.Model);

                case "Nahil":
                    return OllamaConnection.Nahil("Fallen8:Embedding:Nahil",
                        options.Nahil?.Endpoint, options.Nahil?.Model, options.Nahil?.ApiKey);

                default:
                    return null;
            }
        }
    }
}
