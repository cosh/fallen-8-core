// MIT License
//
// OpenAIChatBackend.cs
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
using System.ClientModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.App.Helper;
using OpenAI.Chat;

namespace NoSQL.GraphDB.App.Chat
{
    /// <summary>
    ///   The OpenAI-protocol <see cref="IChatBackend" /> (feature model-providers): the official
    ///   OpenAI SDK wrapped the way <see cref="OllamaChatBackend" /> wraps OllamaSharp, so the chat
    ///   provider stays backend-agnostic and the generation stats the NL-assist UX renders still
    ///   arrive. It serves OpenAI itself and any gateway that speaks its protocol; which one is
    ///   <see cref="RemoteModelTarget" />'s business.
    ///   <para>
    ///     The SDK's own deadline and its own retry are BOTH switched off in the constructor, beside
    ///     the transport that carries neither: <c>Fallen8:Chat:TimeoutSeconds</c> is the single
    ///     deadline (the rule lives on <see cref="OllamaHttpClientFactory" />) and the attempts are
    ///     ours, in <see cref="RemoteModelRetryHandler" />, where they are logged and bounded by that
    ///     budget. Left alone, the SDK's default policy makes four attempts against one failure and
    ///     says nothing about it, which on a metered provider is spend the operator did not ask for.
    ///   </para>
    /// </summary>
    /// <remarks>
    ///   Public rather than internal because the test suite constructs it, and the repository adds no
    ///   <c>InternalsVisibleTo</c> - the same reason <see cref="OllamaChatBackend" /> is public.
    /// </remarks>
    public sealed class OpenAIChatBackend : IChatBackend, IDisposable
    {
        private readonly HttpClient _http;
        private readonly ChatClient _client;
        private readonly String _providerName;
        private readonly String _model;
        private readonly Boolean _stream;

        /// <param name="target">The endpoint, model and credential. Already validated by the factory.</param>
        /// <param name="stream">Whether to ask the backend to stream (<c>Fallen8:Chat:Stream</c>).</param>
        /// <param name="logger">Where the per-retry lines go.</param>
        /// <param name="handler">A test-supplied transport handler; used verbatim when non-null, so a
        /// test exercises the real client composition (credential, retry) rather than a stand-in.</param>
        public OpenAIChatBackend(RemoteModelTarget target, Boolean stream, ILogger logger,
            HttpMessageHandler handler = null)
        {
            // Which statuses mean "ask again" is the OpenAI protocol's answer, not this backend's, so
            // it comes from the one home the embedding client reads too.
            _http = RemoteModelHttpClient.Create(
                new RemoteModelRetryHandler(target.ProviderName, target.Model, logger,
                    RemoteModelHttpClient.OpenAIRetryable),
                handler);

            _client = new ChatClient(target.Model, new ApiKeyCredential(target.ApiKey),
                RemoteModelHttpClient.OpenAIOptions(target, _http));
            _providerName = target.ProviderName;
            _model = target.Model;
            _stream = stream;
        }

        /// <summary>Releases the owned transport. Neither the SDK's client nor its pipeline transport
        /// is disposable from here, so this type owns the <see cref="HttpClient" /> it built; the DI
        /// singleton is disposed at shutdown.</summary>
        public void Dispose()
        {
            _http.Dispose();
        }

        public async Task<ChatBackendResult> ChatAsync(IReadOnlyList<ChatTurn> messages,
            ChatBackendOptions options, CancellationToken cancellationToken)
        {
            var turns = messages.Select(ToMessage).ToList();
            var request = BuildOptions(options);

            var content = new StringBuilder();
            ChatFinishReason? finish = null;
            ChatTokenUsage usage = null;

            // Neither wire format carries a duration, so the only honest one is measured here.
            var clock = Stopwatch.StartNew();

            try
            {
                if (_stream)
                {
                    await foreach (var update in _client.CompleteChatStreamingAsync(turns, request, cancellationToken))
                    {
                        Append(content, update.ContentUpdate);

                        if (update.FinishReason is ChatFinishReason reason)
                        {
                            finish = reason;
                        }

                        // The SDK asks for the trailing usage frame by itself, so this arrives on the
                        // last update rather than never.
                        if (update.Usage != null)
                        {
                            usage = update.Usage;
                        }
                    }
                }
                else
                {
                    ChatCompletion completion = await _client.CompleteChatAsync(turns, request, cancellationToken);
                    Append(content, completion.Content);
                    finish = completion.FinishReason;
                    usage = completion.Usage;
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException)
                && !(ex is ModelRetryTimeoutException) && content.Length > 0)
            {
                // A stream that dies AFTER producing tokens is a truncation, and returning what
                // arrived would be indistinguishable from a short answer the model chose to give.
                // Fail, and say how much arrived so the operator can tell the two apart.
                //
                // The two exclusions and the length guard are the whole reason this is not a blanket
                // catch: a fault before the first token is a backend that did not answer, which the
                // provider maps to 503, and a spent retry budget belongs to the provider, which is
                // the only layer that knows whether the budget or the caller ran out.
                throw new ChatBackendOutputException(String.Format(
                    "The chat backend's response ended early after {0} character(s): {1}",
                    content.Length, Describe(ex)), ex);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                // The SDK models finish_reason as a closed CLR enum, so a gateway answering with a
                // value outside OpenAI's own set faults the enumeration itself. The fault is in the
                // RESPONSE, so it lands on 502 rather than escaping as an unhandled 500.
                throw new ChatBackendOutputException(String.Format(
                    "The chat backend reported a completion reason this client cannot read, after "
                    + "{0} character(s).", content.Length), ex);
            }
            catch (ClientResultException ex)
            {
                throw RemoteModelHttpClient.Failed(_providerName, ex);
            }

            clock.Stop();

            if (finish == null)
            {
                // A cancelled call is a cancellation, NOT a truncation. Checked first because a
                // timed-out request would otherwise become a 502 accusing the backend of a fault it
                // did not commit.
                cancellationToken.ThrowIfCancellationRequested();

                // The SDK cannot see the stream's [DONE] sentinel, so a body that ends cleanly with
                // no finish reason is the only signal a graceful mid-answer close leaves.
                throw new ChatBackendOutputException(String.Format(
                    "The chat backend's response ended after {0} character(s) without a completion marker.",
                    content.Length));
            }

            if (finish == ChatFinishReason.ContentFilter)
            {
                // A refused answer is not a short answer: it lands on the same 502 a truncation does
                // rather than being handed on as a draft the model never agreed to write.
                throw new ChatBackendOutputException(String.Format(
                    "The chat backend refused to answer (content filter) after {0} character(s).",
                    content.Length));
            }

            if (finish == ChatFinishReason.Length)
            {
                // The model stopped at an output ceiling, which means the answer is AMPUTATED, not
                // short. Handed on as a draft it reads as a complete one, and the next thing that
                // fails is whatever consumes it - naming the ceiling here is the only place the cause
                // is still known.
                throw new ChatBackendOutputException(String.Format(
                    "The chat backend stopped at its output ceiling after {0} character(s), so the "
                    + "answer is incomplete. Raise the model's output limit or ask for less.",
                    content.Length));
            }

            var durationMs = clock.Elapsed.TotalMilliseconds;
            Int64? completionTokens = usage?.OutputTokenCount;

            return new ChatBackendResult
            {
                Content = content.ToString(),
                Model = _model,
                // Absent stays absent: this provider omits `usage` on some responses, and a 0 there
                // would read as "it generated nothing" rather than "it did not say".
                PromptTokens = usage?.InputTokenCount,
                CompletionTokens = completionTokens,
                DurationMs = durationMs,
                TokensPerSecond = completionTokens is > 0 && durationMs > 0
                    ? completionTokens.Value / (durationMs / 1000d)
                    : (Double?)null
            };
        }

        private static void Append(StringBuilder content, IEnumerable<ChatMessageContentPart> parts)
        {
            foreach (var part in parts)
            {
                if (part.Text is { Length: > 0 } piece)
                {
                    content.Append(piece);
                }
            }
        }

        /// <summary>
        ///   The per-call knobs, and nothing else: an unset temperature must not travel as a
        ///   <c>0</c>, which would pin a knob the caller never asked about.
        /// </summary>
        private static ChatCompletionOptions BuildOptions(ChatBackendOptions options)
        {
            var request = new ChatCompletionOptions();

            if (options?.Temperature is Double temperature)
            {
                request.Temperature = (Single)temperature;
            }

            if (options?.Stop is { Count: > 0 } stop)
            {
                foreach (var sequence in stop)
                {
                    request.StopSequences.Add(sequence);
                }
            }

            return request;
        }

        /// <summary>
        ///   A turn as the SDK spells it. A <c>tool</c> turn becomes a user turn: a tool result has to
        ///   name the tool call it answers, an id this seam does not carry, and refusing the turn
        ///   outright would fail a request instead of answering it.
        /// </summary>
        private static ChatMessage ToMessage(ChatTurn turn)
        {
            switch (turn.Role?.Trim().ToLowerInvariant())
            {
                case "system":
                    return ChatMessage.CreateSystemMessage(turn.Content);
                case "assistant":
                    return ChatMessage.CreateAssistantMessage(turn.Content);
                default:
                    return ChatMessage.CreateUserMessage(turn.Content);
            }
        }

        /// <summary>
        ///   All that may be said about a failure. The SDK's own message is unsafe to repeat and
        ///   <see cref="RemoteModelHttpClient" /> owns that sentence for both OpenAI-protocol clients;
        ///   anything else here is one of ours and says what it means.
        /// </summary>
        private String Describe(Exception ex)
        {
            return ex is ClientResultException failed
                ? RemoteModelHttpClient.Describe(_providerName, failed)
                : ex.Message;
        }
    }
}
