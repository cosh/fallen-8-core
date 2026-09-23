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
using System.Text.Json;
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
            // The purpose's model when the provider named one, the constructed (assist) model
            // otherwise - see ChatBackendOptions.Model. Resolved ONCE so the request and the result
            // cannot name different models.
            var model = ChatBackendOptions.ModelOr(options, _model);

            var tools = ToolsFor(options);

            var request = new ChatRequest
            {
                Model = model,
                // Tools mean no streaming, for the reason stated once on ChatBackendOptions.Tools.
                // This library's own version of it: OllamaSharp documents its tools field as
                // requiring stream to be false.
                Stream = _stream && tools == null,
                Messages = messages.Select((m, i) => ToMessage(m, i, messages)).ToList(),
                Options = RequestOptionsFor(options),
                Tools = tools
            };

            var content = new StringBuilder();
            var toolCalls = new List<ChatToolCall>();
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

                    // Appended rather than replaced: a model may ask for several tools, and the
                    // protocol is free to deliver them across chunks.
                    if (chunk?.Message?.ToolCalls is { } calls)
                    {
                        foreach (var call in calls)
                        {
                            if (ToolCallFrom(call, toolCalls.Count, messages.Count) is { } mapped)
                            {
                                toolCalls.Add(mapped);
                            }
                        }
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
                // A turn whose whole answer is a tool call can arrive with no content, so the
                // truncation checks below must not read "no content" as "nothing arrived".

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
                Model = model,
                ToolCalls = toolCalls.Count > 0 ? toolCalls : null,
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

        /// <summary>
        ///   One turn in this protocol's shape. Two roles need more than role-and-content:
        ///   an ASSISTANT turn replays the calls it made, and a TOOL turn has to say which tool it
        ///   is answering for.
        ///   <para>
        ///     <b>This protocol matches a tool result by NAME, where the other two match by call
        ///     id.</b> Its message has a <c>ToolName</c> and no id field at all, so the id the
        ///     caller sent is resolved back to a name by walking BACKWARDS from this turn to the
        ///     nearest call carrying it, and only then by sweeping the conversation. Nearest-first
        ///     is what makes more than one round right: the protocol carries no id, so ids are
        ///     synthesised per reply and two rounds can legitimately share one, while the round
        ///     that asked is always the last one before the result. If no such call is found the
        ///     name is left unset rather than guessed: the model then sees an unattributed result,
        ///     which is wrong in a way it can notice, unlike a result attributed to the wrong tool.
        ///   </para>
        /// </summary>
        private static Message ToMessage(ChatTurn turn, Int32 index, IReadOnlyList<ChatTurn> conversation)
        {
            // EMPTY, never null. This protocol types content as a string and refuses a null with
            // "invalid type: null, expected a string" - a 422 the whole request dies on. It bites
            // exactly the turn tools introduced: an assistant turn that only called a tool has no
            // text, so its content is null everywhere above this layer. Measured against the live
            // service; no stub can catch it, because a stub accepts whatever it is handed.
            var message = new Message(ParseRole(turn.Role), turn.Content ?? String.Empty);

            if (turn.ToolCalls is { Count: > 0 } calls)
            {
                message.ToolCalls = calls.Select(c => new Message.ToolCall
                {
                    Id = c.Id,
                    Function = new Message.Function
                    {
                        Name = c.Name,
                        Arguments = ArgumentsOf(c.Arguments),
                    },
                }).ToList();
            }

            if (!String.IsNullOrEmpty(turn.ToolCallId))
            {
                message.ToolName = NameOfCall(turn.ToolCallId, index, conversation);
            }

            return message;
        }

        /// <summary>
        ///   The tool a call id belongs to: the NEAREST preceding call carrying that id, and only
        ///   then the first match anywhere. The fallback keeps the behaviour a caller supplying its
        ///   own ids has always had, for a conversation that orders them some other way.
        /// </summary>
        private static String NameOfCall(String id, Int32 index, IReadOnlyList<ChatTurn> conversation)
        {
            for (var before = index - 1; before >= 0; before--)
            {
                if (TryNameOfCall(conversation[before], id, out var nearest))
                {
                    return nearest;
                }
            }

            foreach (var turn in conversation)
            {
                if (TryNameOfCall(turn, id, out var anywhere))
                {
                    return anywhere;
                }
            }

            return null;
        }

        /// <summary>
        ///   The name one turn gives a call id, if it made that call. A Try* rather than a
        ///   null-returning helper because a matched call's name may itself be null, and a
        ///   null-means-no-match helper would walk PAST the round that asked and attribute the
        ///   result to an older one.
        /// </summary>
        private static Boolean TryNameOfCall(ChatTurn turn, String id, out String name)
        {
            name = null;

            if (turn.ToolCalls == null)
            {
                return false;
            }

            foreach (var call in turn.ToolCalls)
            {
                if (String.Equals(call.Id, id, StringComparison.Ordinal))
                {
                    name = call.Name;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        ///   The offered tools in this protocol's envelope. Built as plain objects rather than the
        ///   SDK's typed <c>Function.Parameters</c> ON PURPOSE: that type models a fixed subset of
        ///   JSON Schema, so mapping into it would silently drop whatever it does not describe,
        ///   and the wire contract promises the caller's schema reaches the provider as given.
        ///   <c>ChatRequest.Tools</c> is a sequence of objects, so the schema is carried verbatim.
        /// </summary>
        private static List<Object> ToolsFor(ChatBackendOptions options)
        {
            if (options?.Tools == null || options.Tools.Count == 0)
            {
                return null;
            }

            var tools = new List<Object>(options.Tools.Count);
            foreach (var tool in options.Tools)
            {
                tools.Add(new Dictionary<String, Object>(StringComparer.Ordinal)
                {
                    ["type"] = "function",
                    ["function"] = new Dictionary<String, Object>(StringComparer.Ordinal)
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description ?? String.Empty,
                        ["parameters"] = SchemaOf(tool.Parameters),
                    },
                });
            }

            return tools;
        }

        /// <summary>
        ///   The tool's argument schema, or the empty object schema when it declares none. A tool
        ///   that takes no arguments still needs a schema on the wire: providers reject a missing
        ///   one, and "no arguments" is a schema rather than an absence.
        /// </summary>
        private static Object SchemaOf(JsonElement parameters)
        {
            return parameters.ValueKind == JsonValueKind.Object
                ? parameters
                : new Dictionary<String, Object>(StringComparer.Ordinal)
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<String, Object>(StringComparer.Ordinal),
                };
        }

        /// <summary>
        ///   One call the model asked for. The id is SYNTHESISED when the provider sent none, which
        ///   this protocol permits: a result is matched by id everywhere above this layer, so
        ///   several unnamed calls in one turn would otherwise be indistinguishable.
        ///   <para>
        ///     It names the ROUND as well as the call, because an ordinal alone made every reply's
        ///     first call <c>call_0</c> and a trace of several rounds unreadable. DERIVED from the
        ///     request rather than generated, and that is a requirement and not a preference: the
        ///     client echoes this id back on the next request, so the same reply to the same
        ///     conversation has to produce the same id. That rules out a counter on this backend
        ///     (one instance serves every conversation), anything random, and anything clock-based.
        ///     It is not a uniqueness GUARANTEE, and does not need to be: attribution is settled by
        ///     walking back to the nearest call, not by the id being unique.
        ///   </para>
        /// </summary>
        private static ChatToolCall ToolCallFrom(Message.ToolCall call, Int32 ordinal, Int32 turns)
        {
            if (call?.Function?.Name is not { Length: > 0 } name)
            {
                return null;
            }

            return new ChatToolCall
            {
                Id = String.IsNullOrEmpty(call.Id)
                    ? "call_" + turns.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + "_" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : call.Id,
                Name = name,
                Arguments = JsonSerializer.SerializeToElement(
                    call.Function.Arguments ?? new Dictionary<String, Object>()),
            };
        }

        /// <summary>The arguments in this protocol's shape: a dictionary, not a JSON string.</summary>
        private static IDictionary<String, Object> ArgumentsOf(JsonElement arguments)
        {
            if (arguments.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<String, Object>();
            }

            return JsonSerializer.Deserialize<Dictionary<String, Object>>(arguments.GetRawText())
                ?? new Dictionary<String, Object>();
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
