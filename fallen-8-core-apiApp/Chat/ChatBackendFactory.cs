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
    ///   instance-config and nahil-backend): the local Ollama sidecar, or a remote
    ///   Nahil, which speaks the same protocol. Called lazily on first use only, so
    ///   nothing is constructed while the capability is off, and a configuration fault raised here
    ///   is latched by that <see cref="Lazy{T}" /> into the permanent 503 this instance answers
    ///   until the configuration changes - the same failure shape a bad backend name has always had.
    /// </summary>
    internal static class ChatBackendFactory
    {
        internal static IChatBackend Create(Fallen8ChatOptions options, ILoggerFactory loggerFactory)
        {
            var connection = ResolveConnection(options);
            if (connection == null)
            {
                throw new InvalidOperationException(String.Format(
                    "'{0}' is not a supported chat backend. Expected Ollama or Nahil.", options.Backend));
            }

            if (!connection.IsValid(out var problem))
            {
                throw new InvalidOperationException(problem);
            }

            return new OllamaChatBackend(connection, options.Stream,
                loggerFactory?.CreateLogger<OllamaChatBackend>());
        }

        /// <summary>
        ///   THE one home for which endpoint, model and credential a configured chat backend dials;
        ///   <c>null</c> when the selector names no backend this app has. Shared with the residency
        ///   probe and the config view, so neither can report on a different target than the one a
        ///   completion would actually reach. Says nothing about whether the target is USABLE - that
        ///   is <see cref="OllamaConnection.IsValid" />, asked by the callers that need it.
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
    }
}
