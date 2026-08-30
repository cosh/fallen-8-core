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
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.App.Helper;
using OllamaSharp;
using OllamaSharp.Models;
using OllamaSharp.Models.Chat;

namespace NoSQL.GraphDB.App.Chat
{
    /// <summary>
    ///   The Ollama-protocol <see cref="IChatBackend" /> (features instance-config and
    ///   nahil-backend): a thin wrapper over OllamaSharp's native <c>ChatAsync</c> so it can
    ///   forward the generation stats (token counts, durations) that the NL-assist UX renders -
    ///   stats the generic <c>Microsoft.Extensions.AI</c> chat abstraction does not expose. It
    ///   serves both the local sidecar and Nahil; everything that differs between them
    ///   lives in <see cref="OllamaConnection" /> and the transport built from it.
    ///   <para>
    ///     It STREAMS by default (<c>Fallen8:Chat:Stream</c>). The tokens are still accumulated here
    ///     because <c>POST /chat</c> answers with a whole completion, but asking the backend to
    ///     stream is not cosmetic: Nahil runs its verification pass after delivery instead of in
    ///     front of it, and a stream that dies half way is DETECTABLE, where a buffered response
    ///     that arrives short is not. Which is the second half of the contract below.
    ///   </para>
    ///   <para>
    ///     The transport carries NO deadline of its own: <c>Fallen8:Chat:TimeoutSeconds</c>, applied
    ///     by <see cref="Fallen8ChatProvider" /> as a linked token, is the single authoritative
    ///     budget. See <see cref="OllamaHttpClientFactory" /> for why (the deadline rule lives there).
    ///   </para>
    /// </summary>
    public sealed class OllamaChatBackend : IChatBackend, IDisposable
    {
        private readonly IOllamaApiClient _client;
        private readonly HttpClient _http;
        private readonly String _model;
        private readonly Boolean _stream;

        /// <param name="connection">The target. Already validated by the factory.</param>
        /// <param name="stream">Whether to ask the backend to stream (<c>Fallen8:Chat:Stream</c>).</param>
        /// <param name="logger">Carried into the transport for Nahil's per-retry lines.</param>
        /// <param name="handler">A test-supplied transport handler; used verbatim when non-null, so a
        /// test exercises the real client composition (credential, retry) rather than a stand-in.</param>
        public OllamaChatBackend(OllamaConnection connection, Boolean stream, ILogger logger,
            HttpMessageHandler handler = null)
        {
            _http = OllamaHttpClientFactory.CreateForProvider(connection, logger, handler);
            _client = new OllamaApiClient(_http, connection.Model);
            _model = connection.Model;
            _stream = stream;
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
                Stream = _stream,
                Messages = messages.Select(m => new Message(ParseRole(m.Role), m.Content)).ToList(),
                Options = RequestOptionsFor(options)
            };

            var content = new StringBuilder();
            ChatDoneResponseStream done = null;

            // Streaming yields one chunk per delta and a terminal done-response carrying the stats;
            // non-streaming yields that terminal chunk alone. Accumulating covers both.
            try
            {
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
            }
            catch (Exception ex) when (!(ex is OperationCanceledException)
                && !(ex is ModelRetryTimeoutException) && content.Length > 0)
            {
                // A stream that dies AFTER producing tokens is a truncation, and returning what
                // arrived would be indistinguishable from a short answer the model chose to give.
                // Fail, and say how much arrived so the operator can tell the two apart.
                //
                // The two exclusions are the whole reason this filter is not a blanket catch. A
                // fault before the first token is not a truncation, it is a backend that did not
                // answer - which the provider maps to 503, exactly as it did before this backend
                // could stream at all; blanket-catching it turned every stopped sidecar into a 502
                // blaming the response for a connection problem. And a warm-up give-up never
                // produced a response to truncate: it belongs to the provider, which is the only
                // layer that knows whether the budget or the caller ran out.
                throw new ChatBackendOutputException(String.Format(
                    "The chat backend's response ended early after {0} character(s): {1}",
                    content.Length, ex.Message), ex);
            }

            if (done == null)
            {
                // A cancelled call is a cancellation, NOT a truncation. Checked before blaming the
                // backend because OllamaSharp's iterator can end without throwing when the token
                // trips, which would otherwise turn every timed-out or client-abandoned request into
                // a 502 that accuses the backend of a fault it did not commit.
                cancellationToken.ThrowIfCancellationRequested();

                // No terminal chunk: the connection closed cleanly mid-answer, which is the same
                // truncation as above wearing a success's clothes.
                throw new ChatBackendOutputException(String.Format(
                    "The chat backend's response ended after {0} character(s) without a completion marker.",
                    content.Length));
            }

            Double? tps = null;
            if (done.EvalDuration > 0 && done.EvalCount > 0)
            {
                // EvalDuration is nanoseconds; tokens / seconds.
                tps = done.EvalCount / (done.EvalDuration / 1_000_000_000.0);
            }

            return new ChatBackendResult
            {
                Content = content.ToString(),
                Model = _model,
                PromptTokens = done.PromptEvalCount,
                CompletionTokens = done.EvalCount,
                DurationMs = done.TotalDuration / 1_000_000.0,
                TokensPerSecond = tps
            };
        }

        /// <summary>
        ///   The per-call generation knobs, MERGED rather than replaced: a request that carries stop
        ///   sequences must not lose its temperature on the way, and vice versa. Null when the caller
        ///   asked for neither, which leaves every knob at the model's own defaults.
        /// </summary>
        private static RequestOptions RequestOptionsFor(ChatBackendOptions options)
        {
            RequestOptions request = null;

            if (options?.Temperature is Double temperature)
            {
                request = new RequestOptions { Temperature = (Single)temperature };
            }

            if (options?.Stop is { Count: > 0 } stop)
            {
                request ??= new RequestOptions();
                request.Stop = stop.ToArray();
            }

            return request;
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
