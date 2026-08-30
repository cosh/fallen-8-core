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
    ///   code change. Called lazily on first use only. There are two remote cases and they are not
    ///   alike: <c>Nahil</c> (feature nahil-backend) is the same Ollama protocol serving the same
    ///   model on someone else's hardware, while <c>OpenAI</c> (feature model-providers) is a
    ///   different protocol AND a different embedding function. <c>Anthropic</c> is a chat-only
    ///   provider and is refused here with its own reason.
    /// </summary>
    internal static class EmbeddingBackendFactory
    {
        internal static IEmbeddingGenerator<String, Embedding<Single>> Create(Fallen8EmbeddingOptions options,
            ILoggerFactory loggerFactory)
        {
            if (Validate(options) is { } problem)
            {
                throw new InvalidOperationException(problem);
            }

            switch (options.Backend)
            {
                case "Onnx":
                    return new OnnxEmbeddingGenerator(options.Onnx);

                case "LLamaSharp":
                    return new LLamaSharpEmbeddingGenerator(options.LLamaSharp);

                case "OpenAI":
                    // A different embedding FUNCTION, not the same one somewhere else. Moving here
                    // therefore means DECLARING the new function's identity as well (ModelName,
                    // Dimension, IntendedMetric): that declaration is what makes every vector and
                    // index stored under the old stamp report an honest mismatch. Nothing here can
                    // check a stamp against a function on the far side of an endpoint, so leaving the
                    // old identity in place would file the new function's vectors under it instead -
                    // which is why moving is a deliberate configuration act and never arrives with a
                    // chat provider (feature model-providers, decision 3). The transport carries no
                    // deadline of its own; see OllamaHttpClientFactory for the rule.
                    return new OpenAIEmbeddingGenerator(ResolveRemoteTarget(options), options.Dimension,
                        loggerFactory?.CreateLogger("NoSQL.GraphDB.App.Embedding.OpenAI"));

                default:
                    // Ollama or Nahil, which share one protocol and therefore one client. Nothing
                    // else reaches here: Validate refuses every other name above.
                    //
                    // OllamaSharp implements the abstraction natively. The sidecar case couples
                    // embedding availability to the (compose-shipped) Ollama container - stated in the
                    // spec, surfaced as 503 while it is down; the Nahil case couples it to a remote
                    // service instead, and adds the credential plus the warm-up wait.
                    // The transport carries no deadline: Fallen8:Embedding:TimeoutSeconds, applied
                    // by Fallen8EmbeddingProvider as a linked token, is the single budget (see
                    // OllamaHttpClientFactory for the rule). The client is process-lifetime, held
                    // by the returned DI singleton, so it is not disposed here.
                    var connection = ResolveConnection(options);
                    return new OllamaApiClient(
                        OllamaHttpClientFactory.CreateForProvider(connection,
                            loggerFactory?.CreateLogger("NoSQL.GraphDB.App.Embedding.Ollama")),
                        connection.Model);
            }
        }

        /// <summary>
        ///   Why the configured backend cannot serve, or <c>null</c> when it can be built;
        ///   the counterpart of <see cref="Chat.ChatBackendFactory.Validate" />. It is the ONE home
        ///   for these sentences, read by <see cref="Create" /> (whose throw becomes the surface's
        ///   <c>503</c>) and by the startup warning, so an operator cannot be told two different
        ///   things about one deployment.
        ///
        ///   <para>It answers for the SELECTOR, not for readiness: it builds nothing and dials
        ///   nothing, so a reachable endpoint and a present model file are still only discovered on
        ///   the first embed. That is why <c>Onnx</c> and <c>LLamaSharp</c> validate clean - they run
        ///   in-process on operator-supplied files, and whether those files are there is their own
        ///   constructor's answer, not a name this method can refuse.</para>
        /// </summary>
        internal static String Validate(Fallen8EmbeddingOptions options)
        {
            switch (options.Backend)
            {
                case "Onnx":
                case "LLamaSharp":
                    return null;

                case "OpenAI":
                    return ResolveRemoteTarget(options).IsValid(out var openAiProblem) ? null : openAiProblem;

                case "Ollama":
                case "Nahil":
                    return ResolveConnection(options).IsValid(out var problem) ? null : problem;

                case "Anthropic":
                    // A name an operator can reasonably have expected to work deserves a different
                    // sentence from a typo: the next move is picking another backend, not fixing a
                    // spelling.
                    return "Fallen8:Embedding:Backend cannot be 'Anthropic': Anthropic publishes no embeddings API. "
                        + "Use Onnx, LLamaSharp, Ollama, Nahil or OpenAI (chat may stay on Anthropic; the two "
                        + "backends are configured independently).";

                default:
                    return String.Format(
                        "'{0}' is not a supported embedding backend. Expected Onnx, LLamaSharp, Ollama, Nahil or OpenAI.",
                        options.Backend);
            }
        }

        /// <summary>The OpenAI target this section names. One spelling of the section key, shared by
        /// <see cref="Validate" /> and <see cref="Create" />.</summary>
        private static RemoteModelTarget ResolveRemoteTarget(Fallen8EmbeddingOptions options)
        {
            return RemoteModelTarget.OpenAI("Fallen8:Embedding:OpenAI",
                options.OpenAI?.Endpoint, options.OpenAI?.Model, options.OpenAI?.ApiKey);
        }

        /// <summary>
        ///   THE one home for which endpoint, model and credential a configured embedding backend
        ///   dials, shared with the residency probe; <c>null</c> for a backend the probe cannot
        ///   ask - ONNX and LLamaSharp run in-process on operator-provided files, and OpenAI speaks
        ///   its own protocol with no residency route, so residency reads "unknown" rather than a
        ///   guess.
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
