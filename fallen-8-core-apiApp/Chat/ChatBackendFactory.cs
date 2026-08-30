// MIT License
//
// ChatBackendFactory.cs
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
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Helper;

namespace NoSQL.GraphDB.App.Chat
{
    /// <summary>
    ///   Maps <c>Fallen8:Chat:Backend</c> to an <see cref="IChatBackend" /> (features
    ///   instance-config, nahil-backend and model-providers): the local Ollama sidecar, a remote
    ///   Nahil, which speaks the same protocol, or OpenAI or Anthropic, each of which speaks its own
    ///   and is reached through its own SDK. Called lazily on first use only, so
    ///   nothing is constructed while the capability is off, and a configuration fault raised here
    ///   is latched by that <see cref="Lazy{T}" /> into the permanent 503 this instance answers
    ///   until the configuration changes - the same failure shape a bad backend name has always had.
    /// </summary>
    internal static class ChatBackendFactory
    {
        internal static IChatBackend Create(Fallen8ChatOptions options, ILoggerFactory loggerFactory)
        {
            if (Validate(options) is { } problem)
            {
                throw new InvalidOperationException(problem);
            }

            switch (options.Backend)
            {
                case "OpenAI":
                    return new OpenAIChatBackend(ResolveRemoteTarget(options), options.Stream,
                        loggerFactory?.CreateLogger<OpenAIChatBackend>());

                case "Anthropic":
                    return new AnthropicChatBackend(ResolveRemoteTarget(options),
                        (options.Anthropic ?? new Fallen8ChatOptions.AnthropicOptions()).MaxTokens,
                        options.Stream, loggerFactory?.CreateLogger<AnthropicChatBackend>());

                default:
                    // Ollama or Nahil, which share one protocol and therefore one backend type.
                    // Nothing else reaches here: Validate refuses every other name above.
                    return new OllamaChatBackend(ResolveConnection(options), options.Stream,
                        loggerFactory?.CreateLogger<OllamaChatBackend>());
            }
        }

        /// <summary>
        ///   THE one home for which endpoint, model and credential a configured OLLAMA-PROTOCOL chat
        ///   backend dials; <c>null</c> both when the selector names no backend this app has AND when
        ///   it names one that speaks another protocol. Shared with the residency
        ///   probe and the config view, so neither can report on a different target than the one a
        ///   completion would actually reach - and the null is what makes the probe skip a backend
        ///   with no residency API to ask. Says nothing about whether the target is USABLE - that
        ///   is <see cref="OllamaConnection.IsValid" />, asked by the callers that need it, and
        ///   <see cref="Validate" /> for the whole question.
        /// </summary>
        internal static OllamaConnection ResolveConnection(Fallen8ChatOptions options)
        {
            switch (options.Backend)
            {
                case "Ollama":
                    return OllamaConnection.Sidecar("Fallen8:Chat:Ollama",
                        options.Ollama?.Endpoint, options.Ollama?.Model);

                case "Nahil":
                    return OllamaConnection.Nahil("Fallen8:Chat:Nahil",
                        options.Nahil?.Endpoint, options.Nahil?.Model, options.Nahil?.ApiKey);

                default:
                    return null;
            }
        }

        /// <summary>The same, for the backends that speak their own protocol; <c>null</c> for the
        /// Ollama-protocol ones and for a name this app does not have.</summary>
        internal static RemoteModelTarget ResolveRemoteTarget(Fallen8ChatOptions options)
        {
            switch (options.Backend)
            {
                case "OpenAI":
                    return RemoteModelTarget.OpenAI("Fallen8:Chat:OpenAI",
                        options.OpenAI?.Endpoint, options.OpenAI?.Model, options.OpenAI?.ApiKey);

                case "Anthropic":
                    return RemoteModelTarget.Anthropic("Fallen8:Chat:Anthropic",
                        options.Anthropic?.Endpoint, options.Anthropic?.Model, options.Anthropic?.ApiKey);

                default:
                    return null;
            }
        }

        /// <summary>
        ///   The server-owned model, whichever backend serves it. Read from the block the selector
        ///   points at rather than from the probe target, which answers the narrower question of
        ///   which Ollama-protocol backend can be asked about residency.
        /// </summary>
        internal static String ResolveModel(Fallen8ChatOptions options)
        {
            return ResolveConnection(options)?.Model ?? ResolveRemoteTarget(options)?.Model;
        }

        /// <summary>
        ///   Whether the configured backend can be used at all, and the operator-facing reason when
        ///   it cannot; <c>null</c> when it can. THE home for the three-way distinction a null
        ///   resolution cannot make on its own: a name this app does not have, a supported name whose
        ///   block is incomplete, and a usable target. The boot warning and the 503 both read this,
        ///   so the two can never say different things about one deployment.
        /// </summary>
        internal static String Validate(Fallen8ChatOptions options)
        {
            if (ResolveConnection(options) is { } connection)
            {
                return connection.IsValid(out var problem) ? null : problem;
            }

            if (ResolveRemoteTarget(options) is { } target)
            {
                return target.IsValid(out var problem) ? null : problem;
            }

            return String.Format(
                "Fallen8:Chat:Backend is '{0}', which is not a supported chat backend. "
                + "Expected Ollama, Nahil, OpenAI or Anthropic.", options.Backend);
        }
    }
}
