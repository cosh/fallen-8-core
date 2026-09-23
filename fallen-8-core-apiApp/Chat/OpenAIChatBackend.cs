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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.App.Helper;
using OpenAI.Chat;
using OpenAITool = OpenAI.Chat.ChatTool;
using OpenAIToolCall = OpenAI.Chat.ChatToolCall;

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
        private readonly String _providerName;
        private readonly String _model;
        private readonly Boolean _stream;

        /// <summary>
        ///   One SDK client per model, because this SDK binds the model at CONSTRUCTION where the
        ///   other two take it per request. The alternative was a whole backend per purpose, which
        ///   would mean a second transport, a second retry handler and a second connection pool to
        ///   the same host for the sake of one string.
        ///   <para>
        ///     Cheap by construction: every entry shares the one <see cref="HttpClient" /> and the
        ///     one options object built in the constructor, so a client here is a thin wrapper and
        ///     not a connection. Bounded in practice by the number of purposes, since the models
        ///     come from configuration and a caller cannot name one.
        ///   </para>
        /// </summary>
        private readonly ConcurrentDictionary<String, ChatClient> _clients =
            new ConcurrentDictionary<String, ChatClient>(StringComparer.Ordinal);

        private readonly Func<String, ChatClient> _newClient;

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

            // Built once and closed over, so every per-model client shares this transport and
            // these options rather than composing its own (and a second copy of the settings is
            // how one of them quietly keeps an SDK default - see RemoteModelHttpClient).
            var credential = new ApiKeyCredential(target.ApiKey);
            var clientOptions = RemoteModelHttpClient.OpenAIOptions(target, _http);
            _newClient = model => new ChatClient(model, credential, clientOptions);

            _providerName = target.ProviderName;
            _model = target.Model;
            _stream = stream;
            _clients[_model] = _newClient(_model);
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
            // Resolved ONCE so the client, the request and the echoed result all name one model.
            var model = ChatBackendOptions.ModelOr(options, _model);
            var client = _clients.GetOrAdd(model, _newClient);

            var turns = messages.Select(ToMessage).ToList();
            var request = BuildOptions(options);

            // Tools mean no streaming, for the reason stated once on ChatBackendOptions.Tools. This
            // SDK's own version of it: streamed calls arrive as ToolCallUpdates that have to be
            // reassembled from fragments.
            var stream = _stream && (options?.Tools == null || options.Tools.Count == 0);

            var content = new StringBuilder();
            List<ChatToolCall> toolCalls = null;
            ChatFinishReason? finish = null;
            ChatTokenUsage usage = null;

            // Neither wire format carries a duration, so the only honest one is measured here.
            var clock = Stopwatch.StartNew();

            try
            {
                if (stream)
                {
                    await foreach (var update in client.CompleteChatStreamingAsync(turns, request, cancellationToken))
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
                    ChatCompletion completion = await client.CompleteChatAsync(turns, request, cancellationToken);
                    toolCalls = ToolCallsOf(completion);
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
                ToolCalls = toolCalls,
                Model = model,
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

            // Added rather than assigned: the SDK exposes Tools as a read-only collection property.
            if (options?.Tools is { Count: > 0 } tools)
            {
                foreach (var tool in tools)
                {
                    request.Tools.Add(OpenAITool.CreateFunctionTool(
                        tool.Name, tool.Description, SchemaOf(tool.Parameters)));
                }
            }

            return request;
        }

        /// <summary>
        ///   A turn as the SDK spells it. Each of the seam's roles has a message type here,
        ///   including <c>tool</c>, which carries the id of the call it answers
        ///   (<see cref="ChatTurn.ToolCallId" />).
        /// </summary>
        private static ChatMessage ToMessage(ChatTurn turn)
        {
            switch (turn.Role?.Trim().ToLowerInvariant())
            {
                case "system":
                    return ChatMessage.CreateSystemMessage(turn.Content);
                case "assistant":
                    if (turn.ToolCalls is { Count: > 0 } calls)
                    {
                        // BOTH halves survive. This SDK models the text and the calls as two fields
                        // of ONE assistant message rather than as alternatives, so a model that
                        // spoke before it called keeps what it said - which the next turn reads, and
                        // which the other two backends have always kept.
                        var spokeAndCalled = ChatMessage.CreateAssistantMessage(
                            calls.Select(ToToolCall).ToList());
                        if (!String.IsNullOrEmpty(turn.Content))
                        {
                            spokeAndCalled.Content.Add(
                                ChatMessageContentPart.CreateTextPart(turn.Content));
                        }

                        return spokeAndCalled;
                    }

                    return ChatMessage.CreateAssistantMessage(turn.Content);
                case "tool":
                    return ChatMessage.CreateToolMessage(turn.ToolCallId, turn.Content);
                default:
                    return ChatMessage.CreateUserMessage(turn.Content);
            }
        }

        /// <summary>One replayed call in this SDK's shape. Arguments travel as the bytes the model
        /// produced, which is also how the SDK hands them back.</summary>
        private static OpenAIToolCall ToToolCall(ChatToolCall call)
        {
            return OpenAIToolCall.CreateFunctionToolCall(call.Id, call.Name,
                BinaryData.FromString(ArgumentsText(call.Arguments)));
        }

        /// <summary>
        ///   The tool's argument schema as this SDK wants it, carried verbatim so nothing the caller
        ///   wrote is dropped. A tool declaring none still gets the empty object schema: providers
        ///   reject a missing one, and "no arguments" is a schema rather than an absence.
        /// </summary>
        private static BinaryData SchemaOf(JsonElement parameters)
        {
            return BinaryData.FromString(parameters.ValueKind == JsonValueKind.Object
                ? parameters.GetRawText()
                : "{\"type\":\"object\",\"properties\":{}}");
        }

        private static String ArgumentsText(JsonElement arguments)
        {
            return arguments.ValueKind == JsonValueKind.Object ? arguments.GetRawText() : "{}";
        }

        /// <summary>The calls a completion asked for, in the seam's shape.</summary>
        private static List<ChatToolCall> ToolCallsOf(ChatCompletion completion)
        {
            if (completion?.ToolCalls == null || completion.ToolCalls.Count == 0)
            {
                return null;
            }

            var mapped = new List<ChatToolCall>(completion.ToolCalls.Count);
            for (var i = 0; i < completion.ToolCalls.Count; i++)
            {
                var call = completion.ToolCalls[i];
                mapped.Add(new ChatToolCall
                {
                    Id = String.IsNullOrEmpty(call.Id)
                        ? "call_" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : call.Id,
                    Name = call.FunctionName,
                    Arguments = Parsed(call.FunctionArguments),
                });
            }

            return mapped;
        }

        /// <summary>
        ///   What an absent or unparseable argument set becomes. It has to be a real empty OBJECT
        ///   and not <c>default</c>: an unset <see cref="JsonElement" /> has
        ///   <see cref="JsonValueKind.Undefined" />, the REST shape it is copied into cannot express
        ///   that, and writing one throws, so returning it turned a malformed argument string into
        ///   a failure of the whole response instead of the empty arguments promised below.
        ///   <para>
        ///     One cloned instance, reused. A clone outlives the document it was parsed from, this
        ///     one is never disposed or mutated, and every consumer only reads it.
        ///   </para>
        /// </summary>
        private static readonly JsonElement NoArguments = EmptyObject();

        private static JsonElement EmptyObject()
        {
            using var document = JsonDocument.Parse("{}");
            return document.RootElement.Clone();
        }

        /// <summary>
        ///   The model's arguments as JSON, or an empty object when they are absent or unparseable.
        ///   A model can emit malformed JSON here, and this layer does not get to decide that a
        ///   whole completion failed because of it: the caller sees a call with no arguments and
        ///   can refuse it, which is a better failure than a 502 that names nothing.
        /// </summary>
        private static JsonElement Parsed(BinaryData arguments)
        {
            if (arguments == null)
            {
                return NoArguments;
            }

            try
            {
                using var document = JsonDocument.Parse(arguments.ToMemory());
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return NoArguments;
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
