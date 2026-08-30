// MIT License
//
// AnthropicChatBackend.cs
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
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.App.Helper;

namespace NoSQL.GraphDB.App.Chat
{
    /// <summary>
    ///   The Anthropic Messages <see cref="IChatBackend" /> (feature model-providers): the official
    ///   Anthropic SDK wrapped the way <see cref="OllamaChatBackend" /> wraps OllamaSharp, so the chat
    ///   provider stays backend-agnostic and the generation stats the NL-assist UX renders still
    ///   arrive.
    ///   <para>
    ///     The SDK's own deadline and its own retry are BOTH switched off in the constructor, beside
    ///     the transport that carries neither: <c>Fallen8:Chat:TimeoutSeconds</c> is the single
    ///     deadline (the rule lives on <see cref="OllamaHttpClientFactory" />) and the attempts are
    ///     ours, in <see cref="RemoteModelRetryHandler" />, where they are logged and bounded by that
    ///     budget. The SDK's retry must not go in ITS handler list either: that list re-sends the
    ///     request message, which HttpClient refuses.
    ///   </para>
    ///   <para>
    ///     Which sampling parameters this sends, and why it is none of them, is documented on
    ///     <see cref="Configuration.Fallen8ChatOptions.AnthropicOptions" />.
    ///   </para>
    /// </summary>
    /// <remarks>
    ///   Public rather than internal because the test suite constructs it, and the repository adds no
    ///   <c>InternalsVisibleTo</c> - the same reason <see cref="OllamaChatBackend" /> is public.
    /// </remarks>
    public sealed class AnthropicChatBackend : IChatBackend, IDisposable
    {
        /// <summary>The two statuses Anthropic uses to mean "ask again". <c>529</c> is its
        /// non-standard "overloaded" status, which has no <see cref="HttpStatusCode" /> member, hence
        /// the cast.</summary>
        private static readonly HttpStatusCode[] Retryable =
        {
            HttpStatusCode.TooManyRequests, (HttpStatusCode)529
        };

        private readonly HttpClient _http;
        private readonly Anthropic.AnthropicClient _client;
        private readonly String _providerName;
        private readonly String _model;
        private readonly Int32 _maxTokens;
        private readonly Boolean _stream;

        /// <param name="target">The endpoint, model and credential. Already validated by the factory,
        /// which is also what keeps an empty key from silently becoming the ambient
        /// <c>ANTHROPIC_API_KEY</c>: the SDK's opt-out for that is not reachable from here.</param>
        /// <param name="maxTokens">
        ///   <c>Fallen8:Chat:Anthropic:MaxTokens</c>. The Messages API requires it per request, which
        ///   is why this backend takes a knob the others do not.
        /// </param>
        /// <param name="stream">Whether to ask the backend to stream (<c>Fallen8:Chat:Stream</c>).</param>
        /// <param name="logger">Where the per-retry lines go.</param>
        /// <param name="handler">A test-supplied transport handler; used verbatim when non-null, so a
        /// test exercises the real client composition (credential, retry) rather than a stand-in.</param>
        public AnthropicChatBackend(RemoteModelTarget target, Int32 maxTokens, Boolean stream, ILogger logger,
            HttpMessageHandler handler = null)
        {
            _http = RemoteModelHttpClient.Create(
                new RemoteModelRetryHandler(target.ProviderName, target.Model, logger, Retryable), handler);

            _client = new Anthropic.AnthropicClient
            {
                ApiKey = target.ApiKey,
                // The host root, unchanged: this SDK appends its own route. OpenAI's does the
                // opposite; see EndpointRule for why configuration takes a host root either way.
                BaseUrl = target.Endpoint,
                MaxRetries = 0,
                Timeout = Timeout.InfiniteTimeSpan,
                HttpClient = _http
            };

            _providerName = target.ProviderName;
            _model = target.Model;
            _maxTokens = maxTokens;
            _stream = stream;
        }

        /// <summary>Releases the owned transport. The SDK's client is not disposable and does not own
        /// an injected <see cref="HttpClient" />, so this type owns it; the DI singleton is disposed
        /// at shutdown.</summary>
        public void Dispose()
        {
            _http.Dispose();
        }

        public async Task<ChatBackendResult> ChatAsync(IReadOnlyList<ChatTurn> messages,
            ChatBackendOptions options, CancellationToken cancellationToken)
        {
            var parameters = BuildParameters(messages, options);

            var content = new StringBuilder();
            Int64? promptTokens = null;
            Int64? completionTokens = null;
            String refusal = null;
            var hitTheCeiling = false;
            var complete = false;

            // Neither wire format carries a duration, so the only honest one is measured here.
            var clock = Stopwatch.StartNew();

            try
            {
                if (_stream)
                {
                    await foreach (var streamEvent in _client.Messages.CreateStreaming(parameters, cancellationToken))
                    {
                        if (streamEvent.TryPickContentBlockDelta(out var block)
                            && block.Delta.TryPickText(out var text))
                        {
                            if (text.Text is { Length: > 0 } piece)
                            {
                                content.Append(piece);
                            }

                            continue;
                        }

                        if (streamEvent.TryPickStart(out var start))
                        {
                            promptTokens = start.Message.Usage.InputTokens;
                            continue;
                        }

                        if (streamEvent.TryPickDelta(out var delta))
                        {
                            completionTokens = delta.Usage.OutputTokens;
                            refusal = RefusalOf(delta.Delta.StopDetails) ?? refusal;
                            hitTheCeiling |= HitTheCeiling(delta.Delta.StopReason);
                            continue;
                        }

                        if (streamEvent.TryPickStop(out _))
                        {
                            // Tracked by hand, because the manual loop ends WITHOUT throwing on a
                            // truncated stream: this flag is the only thing that tells a complete
                            // answer from an amputated one.
                            complete = true;
                        }
                    }
                }
                else
                {
                    Message response = await _client.Messages.Create(parameters, cancellationToken);
                    content.Append(String.Join(String.Empty,
                        response.Content.Select(block => block.Value).OfType<TextBlock>().Select(t => t.Text)));
                    promptTokens = response.Usage.InputTokens;
                    completionTokens = response.Usage.OutputTokens;
                    refusal = RefusalOf(response.StopDetails);
                    hitTheCeiling = HitTheCeiling(response.StopReason);
                    complete = true;
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
            catch (Anthropic.Exceptions.AnthropicException ex)
            {
                throw Failed(ex);
            }

            clock.Stop();

            if (!complete)
            {
                // A cancelled call is a cancellation, NOT a truncation. Checked first because a
                // timed-out request would otherwise become a 502 accusing the backend of a fault it
                // did not commit.
                cancellationToken.ThrowIfCancellationRequested();

                throw new ChatBackendOutputException(String.Format(
                    "The chat backend's response ended after {0} character(s) without a completion marker.",
                    content.Length));
            }

            if (refusal != null)
            {
                // A refused answer is not a short answer: it lands on the same 502 a truncation does
                // rather than being handed on as a draft the model never agreed to write.
                throw new ChatBackendOutputException(String.Format(
                    "The chat backend refused to answer ({0}) after {1} character(s).",
                    refusal, content.Length));
            }

            if (hitTheCeiling)
            {
                // The answer stopped at Fallen8:Chat:Anthropic:MaxTokens, which means it is AMPUTATED,
                // not short. Handed on as a draft it reads as a complete one, and the next thing that
                // fails is whatever consumes it - naming the setting here is the only place the cause
                // is still known.
                throw new ChatBackendOutputException(String.Format(
                    "The chat backend stopped at Fallen8:Chat:Anthropic:MaxTokens ({0}) after {1} "
                    + "character(s), so the answer is incomplete. Raise that ceiling or ask for less.",
                    _maxTokens, content.Length));
            }

            var durationMs = clock.Elapsed.TotalMilliseconds;

            return new ChatBackendResult
            {
                Content = content.ToString(),
                Model = _model,
                // Absent stays absent: a stream that ended before its usage frame reports no counts,
                // and a 0 there would read as "it generated nothing" rather than "it did not say".
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                DurationMs = durationMs,
                TokensPerSecond = completionTokens is > 0 && durationMs > 0
                    ? completionTokens.Value / (durationMs / 1000d)
                    : (Double?)null
            };
        }

        /// <summary>
        ///   The request body: the model, the required token ceiling, the turns, and NOTHING else the
        ///   caller did not ask for. System turns are hoisted out of the message list because this API
        ///   takes them as their own top-level field rather than as a turn.
        /// </summary>
        private MessageCreateParams BuildParameters(IReadOnlyList<ChatTurn> messages, ChatBackendOptions options)
        {
            var system = String.Join("\n\n", messages
                .Where(IsSystem)
                .Select(turn => turn.Content));

            var turns = messages
                .Where(turn => !IsSystem(turn))
                .Select(turn => new MessageParam { Role = RoleOf(turn.Role), Content = turn.Content })
                .ToList();

            // Absent rather than empty, in both cases: neither an empty system prompt nor an empty
            // stop list is something the caller asked for. A null StopSequences is left out of the
            // body, but a null System is SERIALIZED as an explicit null, so the only way to omit that
            // field is to build the object without it - which is what the two initializers are for.
            var stop = options?.Stop is { Count: > 0 } asked ? new List<String>(asked) : null;

            return system.Length == 0
                ? new MessageCreateParams
                {
                    Model = _model,
                    MaxTokens = _maxTokens,
                    Messages = turns,
                    StopSequences = stop
                }
                : new MessageCreateParams
                {
                    Model = _model,
                    MaxTokens = _maxTokens,
                    Messages = turns,
                    StopSequences = stop,
                    System = system
                };
        }

        private static Boolean IsSystem(ChatTurn turn)
        {
            return String.Equals(turn.Role?.Trim(), "system", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The refusal category, or <c>null</c> for an answer that was not refused. The
        /// stop-details type is refusal-specific, so its mere presence IS the signal.</summary>
        private static String RefusalOf(RefusalStopDetails details)
        {
            return details == null ? null : Convert.ToString(details.Category);
        }

        /// <summary>
        ///   Whether the model stopped because it ran out of output budget.
        ///   <para>The raw wire value is read off the JSON rather than through the SDK's
        ///   <c>Raw()</c>/<c>Value()</c> accessors, and that is not fussiness: both THROW on a
        ///   <c>stop_reason</c> that is not a string (measured), so a gateway sending one would fault
        ///   inside the response loop and be reported as a truncated stream - a fault report about
        ///   the wrong thing. A reason this client cannot read is simply not the ceiling.</para>
        /// </summary>
        private static Boolean HitTheCeiling(Anthropic.Core.ApiEnum<String, StopReason> stopReason)
        {
            return stopReason is { } reason
                && reason.Json.ValueKind == JsonValueKind.String
                && String.Equals(reason.Json.GetString(), "max_tokens", StringComparison.Ordinal);
        }

        /// <summary>
        ///   A <c>tool</c> turn becomes a user turn: a tool result has to name the tool call it
        ///   answers, an id this seam does not carry, and refusing the turn outright would fail a
        ///   request instead of answering it.
        /// </summary>
        private static Role RoleOf(String role)
        {
            return String.Equals(role?.Trim(), "assistant", StringComparison.OrdinalIgnoreCase)
                ? Role.Assistant
                : Role.User;
        }

        private Exception Failed(Anthropic.Exceptions.AnthropicException ex)
        {
            return new HttpRequestException(Describe(ex), ex);
        }

        /// <summary>
        ///   All that may be said about an SDK failure. Its own message embeds the raw response body
        ///   verbatim, and neither a body nor a URL is safe to repeat: either can carry the
        ///   credential.
        /// </summary>
        private String Describe(Exception ex)
        {
            if (ex is Anthropic.Exceptions.AnthropicApiException failed)
            {
                return _providerName + " answered " + failed.StatusCode + " (" + failed.ErrorType + ").";
            }

            if (ex is Anthropic.Exceptions.AnthropicException)
            {
                // A transport or stream fault. Its kind is actionable, its message is not: the SDK
                // quotes the request it was making.
                return _providerName + " could not be reached or answered unreadably ("
                    + ex.GetType().Name + ").";
            }

            return ex.Message;
        }
    }
}
