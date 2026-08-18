// MIT License
//
// OllamaChatBackend.cs
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
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Helper;
using OllamaSharp;
using OllamaSharp.Models;
using OllamaSharp.Models.Chat;

namespace NoSQL.GraphDB.App.Chat
{
    /// <summary>
    ///   The Ollama-backed <see cref="IChatBackend" /> (feature instance-config): a thin wrapper
    ///   over OllamaSharp's native <c>ChatAsync</c> (non-streaming) so it can forward the
    ///   generation stats (token counts, durations) that the NL-assist UX renders - stats the
    ///   generic <c>Microsoft.Extensions.AI</c> chat abstraction does not expose. The GPU probe
    ///   reads <c>/api/ps</c> and matches the configured model's VRAM residency.
    ///   <para>
    ///     The transport carries NO deadline of its own
    ///     (<see cref="Timeout.InfiniteTimeSpan" />): <c>Fallen8:Chat:TimeoutSeconds</c>, applied by
    ///     <see cref="Fallen8ChatProvider" /> as a linked token, is the single authoritative budget.
    ///     See <see cref="OllamaHttpClientFactory" /> for why (the deadline rule lives there).
    ///   </para>
    /// </summary>
    internal sealed class OllamaChatBackend : IChatBackend, IDisposable
    {
        private readonly IOllamaApiClient _client;
        private readonly HttpClient _http;
        private readonly String _model;

        internal OllamaChatBackend(Fallen8ChatOptions.OllamaOptions options)
        {
            _http = OllamaHttpClientFactory.Create(options.Endpoint, Timeout.InfiniteTimeSpan);
            _client = new OllamaApiClient(_http, options.Model);
            _model = options.Model;
        }

        /// <summary>Releases the owned transport. OllamaSharp does NOT dispose an injected
        /// <see cref="HttpClient" />, so this type owns it; the DI singleton is disposed at
        /// shutdown.</summary>
        public void Dispose()
        {
            _http.Dispose();
        }

        public async Task<ChatBackendResult> ChatAsync(IReadOnlyList<ChatTurn> messages,
            ChatBackendOptions options, CancellationToken cancellationToken)
        {
            var request = new ChatRequest
            {
                Model = _model,
                Stream = false,
                Messages = messages.Select(m => new Message(ParseRole(m.Role), m.Content)).ToList(),
                Options = options?.Temperature is Double t
                    ? new RequestOptions { Temperature = (Single)t }
                    : null
            };

            var content = new StringBuilder();
            ChatDoneResponseStream done = null;

            // Stream=false yields a single terminal chunk; accumulate defensively in case a
            // backend still chunks, and keep the last done-response for the stats.
            await foreach (var chunk in _client.ChatAsync(request, cancellationToken))
            {
                if (chunk?.Message?.Content is { Length: > 0 } piece)
                {
                    content.Append(piece);
                }

                if (chunk is ChatDoneResponseStream doneChunk)
                {
                    done = doneChunk;
                }
            }

            Double? tps = null;
            if (done != null && done.EvalDuration > 0 && done.EvalCount > 0)
            {
                // EvalDuration is nanoseconds; tokens / seconds.
                tps = done.EvalCount / (done.EvalDuration / 1_000_000_000.0);
            }

            return new ChatBackendResult
            {
                Content = content.ToString(),
                Model = _model,
                PromptTokens = done?.PromptEvalCount,
                CompletionTokens = done?.EvalCount,
                DurationMs = done != null ? done.TotalDuration / 1_000_000.0 : (Double?)null,
                TokensPerSecond = tps
            };
        }

        private static ChatRole ParseRole(String role)
        {
            switch (role?.Trim().ToLowerInvariant())
            {
                case "system":
                    return ChatRole.System;
                case "assistant":
                    return ChatRole.Assistant;
                case "tool":
                    return ChatRole.Tool;
                default:
                    return ChatRole.User;
            }
        }
    }
}
