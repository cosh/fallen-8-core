// MIT License
//
// Fallen8ChatClient.cs
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
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using NoSQL.GraphDB.Rest;

namespace NoSQL.GraphDB.Agents.Model
{
    /// <summary>
    ///   The whole of this host's model story: an <see cref="IChatClient" /> that asks its Fallen-8
    ///   instance for a completion over <c>POST /chat</c> with <c>purpose: agent</c>.
    ///
    ///   <para>
    ///     <b>It is a transport adapter and not a loop.</b> The agentic loop, the tool invocation
    ///     and the multi-turn bookkeeping belong to Microsoft Agent Framework, which drives this
    ///     through one method; all that happens here is translation between that framework's
    ///     message model and one REST body.
    ///   </para>
    ///   <para>
    ///     <b>Why through the instance at all</b> (feature agent-host, route B): the instance
    ///     already owns the provider selector, the credential, the deadline and retry rules, the
    ///     warm-up and quota waits, and the per-call provenance stamp. A host that dialled providers
    ///     itself would hold a second copy of all of it and a second home for the key, and
    ///     "switch this deployment to another provider" would stop being a one-place change. Where
    ///     a model runs is the deployment's concern, not the agent's.
    ///   </para>
    ///   <para>
    ///     It reports no streaming. <see cref="GetStreamingResponseAsync" /> delegates to the
    ///     buffered call and renders the whole completion as updates, because <c>POST /chat</c>
    ///     answers with a whole completion; a framework that asks to stream therefore gets a
    ///     correct answer rather than an exception. Nothing above this needs deltas: the agent feed
    ///     streams STEPS.
    ///   </para>
    /// </summary>
    public sealed class Fallen8ChatClient : IChatClient
    {
        private readonly HttpClient _http;
        private readonly TimeSpan _timeout;
        private ModelProvenance? _lastSeen;

        /// <param name="http">The transport, already carrying the base address and this host's own
        /// API key. Not owned: the DI container built it and disposes it.</param>
        /// <param name="timeout">This host's budget for one completion, which sits ABOVE the
        /// instance's own so the instance's answer is the one a caller sees.</param>
        public Fallen8ChatClient(HttpClient http, TimeSpan timeout)
        {
            _http = http;
            _timeout = timeout;
        }

        /// <summary>
        ///   What served the LAST completion, as the instance reported it. Read by the host's
        ///   posture route, because this host cannot know it from configuration: it has none.
        ///   <para>
        ///     One immutable object behind one volatile field, deliberately, rather than two
        ///     properties. Several agents share this client, so two fields written one after the
        ///     other let a reader see one agent's backend beside another agent's model, and a
        ///     mismatched pair is worse than a stale one because it describes a deployment that does
        ///     not exist.
        ///   </para>
        /// </summary>
        public ModelProvenance? LastSeen => Volatile.Read(ref _lastSeen);

        /// <summary>
        ///   The response property names the per-step provenance travels under. Why it is per step
        ///   rather than per host is on <c>TraceStep.Backend</c>, the field that carries it; the
        ///   aggregate above cannot answer that question, which is why these exist.
        /// </summary>
        public const String BackendProperty = "fallen8.backend";

        public const String ModelProperty = "fallen8.model";

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var request = new ChatRequestBody
            {
                Purpose = "agent",
                Messages = Turns(messages, options),
                Tools = ToolsOf(options),
                Options = KnobsOf(options),
            };

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(_timeout);

            // Through the shared REST seam, which is what the project reference is FOR: it owns the
            // one distinction this layer kept getting wrong, between an answer that never came and a
            // caller who went away. Left to HttpClient, both arrive as the same exception and the
            // runner recorded a hung gateway as an agent somebody cancelled.
            var response = await RestSeam.SendAsync(_http, HttpMethod.Post, "chat", request,
                NoAnswer, budget.Token).ConfigureAwait(false);

            using (response)
            {
                // The instance's own answer is the useful one - it names the key to set, the model
                // that was refused, or the provider that would not answer - so a failure carries its
                // body rather than a status this layer invented. Bounded, because it reaches a caller.
                if (!response.IsSuccessStatusCode)
                {
                    var detail = await Detail(response, budget.Token).ConfigureAwait(false);
                    throw new Fallen8ChatException(String.Format(
                        "The Fallen-8 chat gateway answered {0}: {1}",
                        (Int32)response.StatusCode, detail));
                }

                return await Read(response, budget.Token, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        ///   This host's name for an answer that never came. Both outcomes are the GATEWAY's
        ///   failure and neither is a cancellation, which is the point: the runner turns an
        ///   <see cref="OperationCanceledException" /> into "somebody cancelled this agent", so a
        ///   timeout that reached it unnamed was reported as a cancel.
        /// </summary>
        private Exception NoAnswer(RestSendFailure failure, Exception cause)
        {
            return failure == RestSendFailure.TimedOut
                ? new Fallen8ChatException(String.Format(
                    "The Fallen-8 chat gateway did not answer within {0} seconds "
                    + "(Fallen8Target:TimeoutSeconds).", (Int64)_timeout.TotalSeconds), cause)
                : new Fallen8ChatException(String.Format(
                    "The Fallen-8 chat gateway at {0} could not be reached: {1}",
                    _http.BaseAddress, cause.Message), cause);
        }

        /// <summary>The success body, as the framework's own response shape.</summary>
        private async Task<ChatResponse> Read(HttpResponseMessage response,
            CancellationToken budget, CancellationToken caller)
        {
            ChatResponseBody? body;
            try
            {
                body = await response.Content
                    .ReadFromJsonAsync<ChatResponseBody>(RestSeam.JsonOptions, budget)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!caller.IsCancellationRequested)
            {
                throw new Fallen8ChatException(String.Format(
                    "The Fallen-8 chat gateway did not finish answering within {0} seconds "
                    + "(Fallen8Target:TimeoutSeconds).", (Int64)_timeout.TotalSeconds));
            }
            catch (JsonException failure)
            {
                throw new Fallen8ChatException(
                    "The Fallen-8 chat gateway answered with a body this host could not read: "
                    + failure.Message, failure);
            }

            if (body == null)
            {
                throw new Fallen8ChatException("The Fallen-8 chat gateway answered with no body.");
            }

            Volatile.Write(ref _lastSeen, new ModelProvenance(body.Backend, body.Model));

            var contents = new List<AIContent>();
            if (!String.IsNullOrEmpty(body.Content))
            {
                contents.Add(new TextContent(body.Content));
            }

            // A tool call is the ordinary answer to a request that offered tools, and it arrives
            // WITHOUT text. The framework reads these, invokes the matching tool itself and calls
            // back - which is the entire reason this host needs no loop of its own.
            foreach (var call in body.ToolCalls ?? new List<ToolCallBody>())
            {
                contents.Add(new FunctionCallContent(call.Id ?? String.Empty, call.Name ?? String.Empty,
                    Arguments(call.Arguments)));
            }

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents))
            {
                ModelId = body.Model,
                // Absent stays absent. A backend that reported no usage is counted as zero and
                // flagged upstream, never estimated - one measured provider reply carried a
                // completion count of zero for a real tool call.
                //
                // Decided on the token FIELDS rather than on the stats object, because the gateway
                // always sends that object and nulls the fields a backend did not fill. Reading its
                // presence as a report turned "nobody measured this" into a measured zero, which is
                // exactly the substitution the flag above exists to prevent.
                Usage = Reported(body.Stats),
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    [BackendProperty] = body.Backend,
                    [ModelProperty] = body.Model,
                },
            };
        }

        /// <summary>
        ///   The buffered call, rendered as updates. <c>POST /chat</c> has no streamed shape (an SSE
        ///   variant is a recorded non-goal of the instance's chat feature), so pretending to stream
        ///   would either lie or throw; handing the completion through is the honest rendering and
        ///   keeps any framework path that prefers streaming working.
        ///   <para>
        ///     <b>It is not ONE update</b>, which this said and two other sites repeated.
        ///     Measured: <c>ToChatResponseUpdates</c> emits one update per message plus a TRAILING
        ///     update carrying the response-level metadata, so the sequence is at least two, and
        ///     the reported usage rides on that trailing one. A consumer reading the first update
        ///     as the whole answer therefore loses the usage, which is what the meter above this
        ///     prices a step from.
        ///   </para>
        /// </summary>
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public Object? GetService(Type serviceType, Object? serviceKey = null)
        {
            if (serviceType == null)
            {
                throw new ArgumentNullException(nameof(serviceType));
            }

            return serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
            // The transport is the container's, not this client's.
        }

        /// <summary>
        ///   Every turn this request carries, INSTRUCTIONS FIRST.
        ///
        ///   <para>
        ///     <b>The instructions are the load-bearing part and they do not arrive as a message.</b>
        ///     Microsoft Agent Framework carries an agent's system prompt on
        ///     <see cref="ChatOptions.Instructions" />, not in the message list, so a mapping that
        ///     reads only the messages puts the model's whole instruction set on the floor and sends
        ///     a bare user question. That was measured against the live service: the request left
        ///     with nine prompt tokens, and the model answered a question about the graph with an
        ///     invented number instead of calling the tool it had been given. Which is the exact
        ///     failure the role prompts exist to prevent, arriving because they never got there.
        ///   </para>
        ///   <para>
        ///     A system message the caller also supplied is kept and follows the instructions, in
        ///     that order, because that is the framework's own precedence: instructions are the
        ///     agent's identity and a message is part of the conversation.
        ///   </para>
        /// </summary>
        private static List<ChatMessageBody> Turns(IEnumerable<ChatMessage> messages, ChatOptions? options)
        {
            var turns = new List<ChatMessageBody>();

            if (!String.IsNullOrWhiteSpace(options?.Instructions))
            {
                turns.Add(new ChatMessageBody
                {
                    Role = "system",
                    Content = options!.Instructions,
                });
            }

            turns.AddRange(messages.SelectMany(ToWire));
            return turns;
        }

        /// <summary>
        ///   One framework message as the wire's turns. Usually one, but an assistant turn that both
        ///   spoke and called tools is one message here and stays one on the wire, while TOOL
        ///   RESULTS are one turn each because each answers a different call.
        /// </summary>
        private static IEnumerable<ChatMessageBody> ToWire(ChatMessage message)
        {
            var results = message.Contents.OfType<FunctionResultContent>().ToList();
            if (results.Count > 0)
            {
                foreach (var result in results)
                {
                    yield return new ChatMessageBody
                    {
                        Role = "tool",
                        Content = Text(result.Result),
                        ToolCallId = result.CallId,
                    };
                }

                yield break;
            }

            var calls = message.Contents.OfType<FunctionCallContent>().ToList();
            yield return new ChatMessageBody
            {
                Role = RoleOf(message.Role),
                // Empty rather than null: a turn that only called a tool has no text, and the
                // gateway admits that only for an assistant turn carrying calls.
                Content = message.Text ?? String.Empty,
                ToolCalls = calls.Count == 0
                    ? null
                    : calls.Select(c => new ToolCallBody
                    {
                        Id = c.CallId,
                        Name = c.Name,
                        Arguments = c.Arguments == null
                            ? (JsonElement?)null
                            : JsonSerializer.SerializeToElement(c.Arguments, RestSeam.JsonOptions),
                    }).ToList(),
            };
        }

        private static String RoleOf(ChatRole role)
        {
            if (role == ChatRole.System)
            {
                return "system";
            }

            if (role == ChatRole.Assistant)
            {
                return "assistant";
            }

            if (role == ChatRole.Tool)
            {
                return "tool";
            }

            return "user";
        }

        /// <summary>
        ///   The tools the framework is offering, in the gateway's shape. Only function tools
        ///   travel: anything else in that list is a hosted tool the provider would run itself,
        ///   which this path does not offer, and silently sending it would be worse than not
        ///   sending it.
        /// </summary>
        private static List<ChatToolBody>? ToolsOf(ChatOptions? options)
        {
            var functions = options?.Tools?.OfType<AIFunction>().ToList();
            if (functions == null || functions.Count == 0)
            {
                return null;
            }

            return functions.Select(f => new ChatToolBody
            {
                Name = f.Name,
                Description = f.Description,
                Parameters = f.JsonSchema,
            }).ToList();
        }

        private static ChatKnobsBody? KnobsOf(ChatOptions? options)
        {
            var stop = options?.StopSequences?.Where(s => !String.IsNullOrEmpty(s)).ToList();
            if (options?.Temperature == null && (stop == null || stop.Count == 0))
            {
                return null;
            }

            return new ChatKnobsBody
            {
                Temperature = options?.Temperature,
                Stop = stop is { Count: > 0 } ? stop : null,
            };
        }

        private static IDictionary<String, Object?> Arguments(JsonElement? arguments)
        {
            if (arguments is not { ValueKind: JsonValueKind.Object } element)
            {
                return new Dictionary<String, Object?>();
            }

            var map = new Dictionary<String, Object?>(StringComparer.Ordinal);
            foreach (var member in element.EnumerateObject())
            {
                map[member.Name] = member.Value.Clone();
            }

            return map;
        }

        private static String Text(Object? result)
        {
            return result switch
            {
                null => String.Empty,
                String text => text,
                JsonElement element => element.ValueKind == JsonValueKind.String
                    ? element.GetString() ?? String.Empty
                    : element.GetRawText(),
                _ => JsonSerializer.Serialize(result, RestSeam.JsonOptions),
            };
        }

        /// <summary>
        ///   The usage a backend actually reported, or null. Null when NEITHER count is present:
        ///   the gateway ships a stats object on every answer and leaves the fields a backend did
        ///   not fill as null, so the object's presence says nothing. One count without the other
        ///   is still a report and travels as it arrived.
        /// </summary>
        private static UsageDetails? Reported(ChatStatsBody? stats)
        {
            if (stats?.PromptTokens == null && stats?.CompletionTokens == null)
            {
                return null;
            }

            return new UsageDetails
            {
                InputTokenCount = stats.PromptTokens,
                OutputTokenCount = stats.CompletionTokens,
            };
        }

        /// <summary>The instance's own reason, bounded. A problem+json <c>detail</c> when there is
        /// one, since that is the sentence naming what to change.</summary>
        private static async Task<String> Detail(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            try
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (String.IsNullOrWhiteSpace(body))
                {
                    return response.ReasonPhrase ?? "no body";
                }

                if (body.Length > 2048)
                {
                    body = body.Substring(0, 2048);
                }

                try
                {
                    using var document = JsonDocument.Parse(body);
                    if (document.RootElement.ValueKind == JsonValueKind.Object
                        && document.RootElement.TryGetProperty("detail", out var detail)
                        && detail.ValueKind == JsonValueKind.String)
                    {
                        return detail.GetString() ?? body;
                    }
                }
                catch (JsonException)
                {
                    // Not problem+json, or truncated by the bound above. The raw prefix is still the
                    // most useful thing available.
                }

                return body;
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                return response.ReasonPhrase ?? "unreadable";
            }
        }

        private sealed class ChatRequestBody
        {
            [JsonPropertyName("purpose")]
            public String? Purpose { get; set; }

            [JsonPropertyName("messages")]
            public List<ChatMessageBody>? Messages { get; set; }

            [JsonPropertyName("tools")]
            public List<ChatToolBody>? Tools { get; set; }

            [JsonPropertyName("options")]
            public ChatKnobsBody? Options { get; set; }
        }

        private sealed class ChatMessageBody
        {
            [JsonPropertyName("role")]
            public String? Role { get; set; }

            [JsonPropertyName("content")]
            public String? Content { get; set; }

            [JsonPropertyName("toolCalls")]
            public List<ToolCallBody>? ToolCalls { get; set; }

            [JsonPropertyName("toolCallId")]
            public String? ToolCallId { get; set; }
        }

        private sealed class ChatToolBody
        {
            [JsonPropertyName("name")]
            public String? Name { get; set; }

            [JsonPropertyName("description")]
            public String? Description { get; set; }

            [JsonPropertyName("parameters")]
            public JsonElement Parameters { get; set; }
        }

        private sealed class ChatKnobsBody
        {
            [JsonPropertyName("temperature")]
            public Double? Temperature { get; set; }

            [JsonPropertyName("stop")]
            public List<String>? Stop { get; set; }
        }

        private sealed class ChatResponseBody
        {
            [JsonPropertyName("content")]
            public String? Content { get; set; }

            [JsonPropertyName("toolCalls")]
            public List<ToolCallBody>? ToolCalls { get; set; }

            [JsonPropertyName("model")]
            public String? Model { get; set; }

            [JsonPropertyName("backend")]
            public String? Backend { get; set; }

            [JsonPropertyName("stats")]
            public ChatStatsBody? Stats { get; set; }
        }

        private sealed class ToolCallBody
        {
            [JsonPropertyName("id")]
            public String? Id { get; set; }

            [JsonPropertyName("name")]
            public String? Name { get; set; }

            [JsonPropertyName("arguments")]
            public JsonElement? Arguments { get; set; }
        }

        private sealed class ChatStatsBody
        {
            [JsonPropertyName("promptTokens")]
            public Int64? PromptTokens { get; set; }

            [JsonPropertyName("completionTokens")]
            public Int64? CompletionTokens { get; set; }
        }
    }

    /// <summary>
    ///   What served a completion, as the instance reported it. Immutable so the pair is read as a
    ///   pair; see <see cref="Fallen8ChatClient.LastSeen" />.
    /// </summary>
    public sealed class ModelProvenance
    {
        public ModelProvenance(String? backend, String? model)
        {
            Backend = backend;
            Model = model;
        }

        /// <summary>The instance's <c>Fallen8:Chat:Backend</c> value, as it named it.</summary>
        public String? Backend
        {
            get;
        }

        /// <summary>The model the instance chose for <c>purpose: agent</c>. This host never
        /// configures it and never asks for it by name.</summary>
        public String? Model
        {
            get;
        }
    }

    /// <summary>
    ///   The Fallen-8 chat gateway refused or could not be reached. Its message carries the
    ///   INSTANCE's own sentence, which is the one that names what to change - a missing model key,
    ///   a provider that would not answer - rather than a status this host invented.
    /// </summary>
    public sealed class Fallen8ChatException : Exception
    {
        public Fallen8ChatException(String message, Exception? inner = null)
            : base(message, inner)
        {
        }
    }
}
