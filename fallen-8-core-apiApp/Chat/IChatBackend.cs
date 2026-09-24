// MIT License
//
// IChatBackend.cs
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
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NoSQL.GraphDB.App.Chat
{
    /// <summary>
    ///   The seam between <see cref="Fallen8ChatProvider" /> and the concrete model backend
    ///   (feature instance-config). Kept purpose-built (not the whole OllamaSharp client) so the
    ///   provider stays backend-agnostic and tests can substitute a deterministic fake, exactly as
    ///   the embedding provider substitutes its <c>IEmbeddingGenerator</c>.
    /// </summary>
    public interface IChatBackend
    {
        /// <summary>Runs one chat completion and returns the WHOLE assistant content plus the
        /// backend's generation stats. Whether the backend streamed to produce it is its own
        /// business. Throws on backend failure (surfaced by the provider as 503), and throws
        /// <see cref="ChatBackendOutputException" /> (502) rather than returning an answer it
        /// received only part of - never a partial/garbled result silently.</summary>
        Task<ChatBackendResult> ChatAsync(IReadOnlyList<ChatTurn> messages, ChatBackendOptions options,
            CancellationToken cancellationToken);
    }

    /// <summary>One chat turn: a role (<c>system</c>/<c>user</c>/<c>assistant</c>/<c>tool</c>) and
    /// content, plus the two things a tool-calling conversation has to replay.</summary>
    public sealed class ChatTurn
    {
        public ChatTurn(String role, String content,
            IReadOnlyList<ChatToolCall> toolCalls = null, String toolCallId = null)
        {
            Role = role;
            Content = content;
            ToolCalls = toolCalls;
            ToolCallId = toolCallId;
        }

        public String Role { get; }

        /// <summary>The turn's text. EMPTY IS LEGAL on an assistant turn that carries
        /// <see cref="ToolCalls" />: a model that decided to call a tool said nothing else, and
        /// dropping such a turn from the history loses the call the next turn answers.</summary>
        public String Content { get; }

        /// <summary>
        ///   The calls an ASSISTANT turn made, replayed so the model can see its own decision. Null
        ///   or empty on every other role.
        /// </summary>
        public IReadOnlyList<ChatToolCall> ToolCalls { get; }

        /// <summary>
        ///   Which call a TOOL turn answers, so a conversation with several calls in flight can be
        ///   reassembled. Null on every other role. Required by all three providers on a tool
        ///   result, which is why a tool turn without it is refused at the edge rather than sent.
        /// </summary>
        public String ToolCallId { get; }
    }

    /// <summary>
    ///   A tool the caller is offering the model, as a name, a sentence and a JSON Schema for its
    ///   arguments. Deliberately the lowest common shape of the three providers' function tools
    ///   rather than any one provider's type: the schema travels as a
    ///   <see cref="JsonElement" /> because every provider wants it re-serialised into its own
    ///   envelope, and nothing here inspects or validates it - the model reads it, and the caller
    ///   owns whether it is true.
    /// </summary>
    public sealed class ChatTool
    {
        public String Name { get; init; }

        public String Description { get; init; }

        /// <summary>The JSON Schema object describing the arguments. An <c>Undefined</c> element
        /// means the tool takes none, which each backend renders in its own way.</summary>
        public JsonElement Parameters { get; init; }
    }

    /// <summary>
    ///   One call to a tool: which tool, with what arguments, under an id the conversation uses to
    ///   match the eventual result back to it. Carried in both directions - out of a completion as
    ///   what the model wants done, and back in on a later turn as what it already asked for.
    /// </summary>
    public sealed class ChatToolCall
    {
        /// <summary>
        ///   The provider's id for this call. Providers differ on whether they supply one at all,
        ///   so a backend that gets none SYNTHESISES a stable one rather than leaving it null: the
        ///   id is what a tool result is matched by, and a null id makes several calls in one turn
        ///   indistinguishable.
        /// </summary>
        public String Id { get; init; }

        public String Name { get; init; }

        /// <summary>The arguments the model produced, as it produced them. Not validated against
        /// the tool's schema here: the caller invoked the tool and is the only party that knows
        /// what its arguments mean.</summary>
        public JsonElement Arguments { get; init; }

        /// <summary>
        ///   The id a backend uses when the provider supplied none. It names the ROUND as well as
        ///   the call, because an ordinal alone made every reply's first call <c>call_0</c>: a
        ///   conversation of several rounds then carried one id for several different calls, which
        ///   is unreadable in a trace and, on a protocol that matches results BY id, wrong on the
        ///   wire.
        ///
        ///   <para>
        ///     DERIVED from the request rather than generated, and that is a requirement and not a
        ///     preference: the client echoes this id back on the next request, so the same reply to
        ///     the same conversation has to produce the same id. That rules out a counter on a
        ///     backend (one instance serves every conversation, so ids would interleave between
        ///     callers), anything random, and anything clock-based.
        ///   </para>
        ///   <para>
        ///     Not a uniqueness GUARANTEE, and it does not need to be: a caller that rewrites
        ///     history could repeat a turn count. On the Ollama protocol, which carries no id at
        ///     all, attribution is settled by walking back to the nearest call rather than by the
        ///     id being unique; on the other two the provider supplies ids in practice and this is
        ///     the fallback for a provider that does not.
        ///   </para>
        /// </summary>
        /// <param name="turns">How many turns the request carried, which is what names the round.</param>
        /// <param name="ordinal">This call's position within the reply.</param>
        public static String SynthesiseId(Int32 turns, Int32 ordinal)
        {
            return "call_" + turns.ToString(CultureInfo.InvariantCulture)
                + "_" + ordinal.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Optional per-call knobs; each is left at the model's own default when null/empty.</summary>
    public sealed class ChatBackendOptions
    {
        /// <summary>
        ///   Sampling temperature, or null to leave it at the model's own default.
        ///   <see cref="AnthropicChatBackend" /> IGNORES it: current Claude models reject a
        ///   <c>temperature</c> outright with a 400, so honouring it there would turn every request
        ///   that carries one into a failure rather than a differently-sampled answer.
        /// </summary>
        public Double? Temperature { get; init; }

        /// <summary>
        ///   Sequences that stop generation. Per-request because a model's stop tokens are only
        ///   baked into a locally BUILT image: the same weights published to a registry arrive
        ///   without them, so whatever needs them has to send them.
        /// </summary>
        public IReadOnlyList<String> Stop { get; init; }

        /// <summary>
        ///   The tools the caller is offering the model on THIS call. Per request rather than per
        ///   backend because the tool set is the caller's, not the deployment's: the agent host
        ///   offers whatever its role allowlist admits, and NL assist offers none.
        ///   <para>
        ///     Null or empty means no tools, and then nothing tool-shaped goes on the wire at all -
        ///     not an empty array - because a provider that sees a tools field behaves differently
        ///     from one that never saw the field.
        ///   </para>
        ///   <para>
        ///     <b>A request carrying tools is NOT streamed, on any backend.</b> Stated here because
        ///     all three obey it and none of them owns the reason. It is a client-library
        ///     constraint rather than a wire one - the raw protocol was measured returning parsed
        ///     tool calls either way - and each library adds its own version of it: OllamaSharp
        ///     documents its tools field as requiring a non-streamed request, and the OpenAI SDK
        ///     delivers streamed calls as fragments that have to be reassembled, which is a class
        ///     of bug worth not having. What streaming buys is truncation detection and, on Nahil, a
        ///     verification pass that runs after delivery; neither is worth much on a turn whose
        ///     whole answer is a tool call, and no other request is affected.
        ///   </para>
        /// </summary>
        public IReadOnlyList<ChatTool> Tools { get; init; }

        /// <summary>
        ///   The model to invoke, which the SERVER chose - never a caller. It is on the per-call
        ///   options because one backend now serves several models: the request names a
        ///   <see cref="ChatPurpose" />, <see cref="ChatBackendFactory" /> turns that into a
        ///   configured name, and the same client carries whichever came out. Before purposes the
        ///   model was fixed at construction, which is why a backend still holds one: see
        ///   <see cref="ChatBackendResult.Model" />.
        ///   <para>
        ///     <c>null</c> means the caller named none, and a backend then uses the model it was
        ///     constructed with - the ASSIST model, because that is the default purpose. That is a
        ///     coherent answer rather than a silent guess, and <see cref="Fallen8ChatProvider" />
        ///     always sets it explicitly anyway.
        ///   </para>
        /// </summary>
        public String Model { get; init; }

        /// <summary>
        ///   This same set of knobs with <see cref="Model" /> replaced, which is how the provider
        ///   adds the server's choice to a caller's request without mutating what the caller passed
        ///   in. <b>Every property has to be copied here</b>: a new one that is not is a knob that
        ///   silently stops reaching the backend, which is why the copy lives on the type rather
        ///   than at the call site.
        /// </summary>
        /// <summary>
        ///   Which model a call actually uses: the one it names, or the backend's configured one
        ///   when it names none. One home for the fallback rule so three backends cannot each
        ///   decide it differently.
        /// </summary>
        public static String ModelOr(ChatBackendOptions options, String configured)
        {
            return String.IsNullOrWhiteSpace(options?.Model) ? configured : options.Model;
        }

        public ChatBackendOptions WithModel(String model)
        {
            return new ChatBackendOptions
            {
                Temperature = Temperature,
                Stop = Stop,
                Tools = Tools,
                Model = model,
            };
        }
    }

    /// <summary>
    ///   The backend produced an incomplete or unreadable answer - a stream that died part-way, or
    ///   one that ended with no completion marker. Distinct from an unreachable backend because the
    ///   fault is in the RESPONSE, so the provider maps it to 502 rather than 503, and it carries
    ///   how much content had arrived: without that number a truncation is indistinguishable from a
    ///   short answer the model meant to give.
    /// </summary>
    public sealed class ChatBackendOutputException : Exception
    {
        public ChatBackendOutputException(String message, Exception inner = null)
            : base(message, inner)
        {
        }
    }

    /// <summary>The completion plus the backend's generation stats (all nullable: a backend may
    /// not report them).</summary>
    public sealed class ChatBackendResult
    {
        /// <summary>
        ///   The assistant's text. EMPTY IS A VALID ANSWER when <see cref="ToolCalls" /> is not,
        ///   which is the ordinary shape of a model that decided to call a tool instead of
        ///   replying. Only empty content AND no calls is an unusable answer.
        /// </summary>
        public String Content { get; init; }

        /// <summary>The calls the model wants made, in the order it asked. Null or empty when it
        /// just answered.</summary>
        public IReadOnlyList<ChatToolCall> ToolCalls { get; init; }

        public String Model { get; init; }

        public Int64? PromptTokens { get; init; }

        public Int64? CompletionTokens { get; init; }

        public Double? DurationMs { get; init; }

        public Double? TokensPerSecond { get; init; }
    }
}
