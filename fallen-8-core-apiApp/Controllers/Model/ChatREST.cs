// MIT License
//
// ChatREST.cs
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
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>A chat completion request proxied to the instance's model backend (feature
    /// instance-config). The model is SERVER-owned (the selected backend's own <c>Model</c>
    /// setting); there is no client model field.</summary>
    /// <example>
    /// { "messages": [ { "role": "user", "content": "Draft a vertex filter for label person" } ] }
    /// </example>
    public sealed class ChatSpecification
    {
        /// <summary>The conversation turns, in order (at least one).</summary>
        [Required]
        [JsonPropertyName("messages")]
        public List<ChatMessageSpecification> Messages
        {
            get; set;
        }

        /// <summary>Optional generation knobs.</summary>
        [JsonPropertyName("options")]
        public ChatOptionsSpecification Options
        {
            get; set;
        }

        /// <summary>
        ///   What the completion is FOR, which is how the server picks the model that serves it:
        ///   <c>assist</c> (the default) or <c>agent</c>. It names a JOB and never a model, so the
        ///   server still owns every model name and no client can choose one.
        ///   <para>
        ///     Omit it and nothing changes: the request behaves exactly as it did before purposes
        ///     existed. A value that is neither name is refused with a 400 listing both, rather than
        ///     falling back to <c>assist</c> - answering a tool-calling agent with the model trained
        ///     to emit one C# fragment would look like an answer and be useless.
        ///   </para>
        /// </summary>
        /// <example>assist</example>
        [JsonPropertyName("purpose")]
        public String Purpose
        {
            get; set;
        }

        /// <summary>
        ///   The tools the model may call on this request. Omit them and the request is a plain
        ///   completion, which is what every caller before agents sent.
        ///   <para>
        ///     Offering a tool does not run it. The model answers with <c>toolCalls</c>, the CALLER
        ///     runs them and sends the results back as <c>tool</c> messages carrying
        ///     <c>toolCallId</c>; the server holds no conversation state and executes nothing.
        ///     Whether the configured model can call tools at all is the model's property, not
        ///     this field's - the assist fine-tune cannot, which is what the <c>agent</c> purpose
        ///     exists for.
        ///   </para>
        /// </summary>
        [JsonPropertyName("tools")]
        public List<ChatToolSpecification> Tools
        {
            get; set;
        }
    }

    /// <summary>One tool offered to the model: a name, a sentence saying when to use it, and a
    /// JSON Schema for its arguments.</summary>
    public sealed class ChatToolSpecification
    {
        /// <summary>The tool's name, which the model repeats back in a call.</summary>
        /// <example>count_vertices</example>
        [Required]
        [JsonPropertyName("name")]
        public String Name
        {
            get; set;
        }

        /// <summary>What the tool does and when to use it. This is the whole of what the model
        /// knows about it, so a vague sentence is a tool that gets called wrongly.</summary>
        /// <example>Count the vertices in one namespace.</example>
        [JsonPropertyName("description")]
        public String Description
        {
            get; set;
        }

        /// <summary>
        ///   A JSON Schema object describing the arguments. Passed to the provider as given:
        ///   nothing here validates it, and nothing validates the arguments a model produces
        ///   against it either, because the caller runs the tool and is the only party that knows
        ///   what its arguments mean.
        /// </summary>
        /// <example>{ "type": "object", "properties": { "namespace": { "type": "string" } } }</example>
        [JsonPropertyName("parameters")]
        public JsonElement Parameters
        {
            get; set;
        }
    }

    /// <summary>One call to a tool, in either direction: what the model wants run, and on a later
    /// turn what it already asked for.</summary>
    public sealed class ChatToolCallREST
    {
        /// <summary>The id this call is matched by. Send it back as <c>toolCallId</c> on the
        /// <c>tool</c> message carrying the result.</summary>
        /// <example>call_1</example>
        [JsonPropertyName("id")]
        public String Id
        {
            get; set;
        }

        /// <summary>The tool the model chose.</summary>
        /// <example>count_vertices</example>
        [JsonPropertyName("name")]
        public String Name
        {
            get; set;
        }

        /// <summary>The arguments the model produced, as it produced them.</summary>
        /// <example>{ "namespace": "default" }</example>
        [JsonPropertyName("arguments")]
        public JsonElement Arguments
        {
            get; set;
        }
    }

    /// <summary>One chat turn.</summary>
    public sealed class ChatMessageSpecification
    {
        /// <summary>The role: <c>system</c>, <c>user</c>, <c>assistant</c>, or <c>tool</c>
        /// (unknown values are treated as <c>user</c>).</summary>
        /// <example>user</example>
        [JsonPropertyName("role")]
        public String Role
        {
            get; set;
        }

        /// <summary>
        ///   The message content. Required on every role EXCEPT an <c>assistant</c> turn that
        ///   carries <c>toolCalls</c>: a model that decided to call a tool said nothing else, and
        ///   that turn still has to be replayed or the next turn answers a call nobody can see.
        /// </summary>
        /// <example>Draft a vertex filter for label person</example>
        [JsonPropertyName("content")]
        public String Content
        {
            get; set;
        }

        /// <summary>
        ///   On an <c>assistant</c> turn, the calls that turn made, replayed so the model sees its
        ///   own decision. Ignored on any other role.
        /// </summary>
        [JsonPropertyName("toolCalls")]
        public List<ChatToolCallREST> ToolCalls
        {
            get; set;
        }

        /// <summary>
        ///   On a <c>tool</c> turn, which call this result answers, taken from the
        ///   <c>toolCalls</c> the assistant produced. Required on that role: all three providers
        ///   demand it, and a result nobody can match is a result the model cannot use.
        /// </summary>
        /// <example>call_1</example>
        [JsonPropertyName("toolCallId")]
        public String ToolCallId
        {
            get; set;
        }
    }

    /// <summary>Optional per-request generation knobs.</summary>
    public sealed class ChatOptionsSpecification
    {
        /// <summary>Sampling temperature (backend default when omitted).</summary>
        /// <example>0.1</example>
        [JsonPropertyName("temperature")]
        public Double? Temperature
        {
            get; set;
        }

        /// <summary>
        ///   Sequences that stop generation, sent through to the model (its own defaults when
        ///   omitted). Needed when the configured model's stop tokens are not baked into it: the same
        ///   weights published to a registry arrive without the template and stop tokens a locally
        ///   BUILT image carries, so whatever relies on them has to send them per request.
        /// </summary>
        /// <example>["&lt;|im_start|&gt;", "&lt;|im_end|&gt;"]</example>
        [JsonPropertyName("stop")]
        public List<String> Stop
        {
            get; set;
        }
    }

    /// <summary>A chat completion plus the backend's generation stats.</summary>
    public sealed class ChatResultREST
    {
        /// <summary>The assistant message content. EMPTY when the model answered with tool calls
        /// instead of text, which is the ordinary shape of a tool-calling turn.</summary>
        [JsonPropertyName("content")]
        public String Content
        {
            get; set;
        }

        /// <summary>
        ///   The calls the model wants made, in the order it asked. ABSENT rather than null when it
        ///   just answered, so a caller that never offered tools sees the response it always saw.
        ///   Run them, then send the results back as <c>tool</c> messages carrying the matching
        ///   <c>toolCallId</c> - the server ran nothing and remembers nothing.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [JsonPropertyName("toolCalls")]
        public List<ChatToolCallREST> ToolCalls
        {
            get; set;
        }

        /// <summary>The model that produced it (the server-owned model).</summary>
        /// <example>phi4-f8-mini</example>
        [JsonPropertyName("model")]
        public String Model
        {
            get; set;
        }

        /// <summary>
        ///   The backend selector value that served THIS call (feature model-providers), stamped
        ///   per response rather than read from current configuration: a draft made under one
        ///   backend must still say so after the operator switches the deployment to another. For
        ///   the ambient "requests will go to X" answer, read <c>GET /status</c>'s chat block
        ///   instead.
        /// </summary>
        /// <example>Nahil</example>
        [JsonPropertyName("backend")]
        public String Backend
        {
            get; set;
        }

        /// <summary>Generation stats (token counts and durations); fields are null when the
        /// backend does not report them.</summary>
        [JsonPropertyName("stats")]
        public ChatStatsREST Stats
        {
            get; set;
        }
    }

    /// <summary>Generation stats forwarded from the backend.</summary>
    public sealed class ChatStatsREST
    {
        /// <summary>Prompt (input) token count.</summary>
        [JsonPropertyName("promptTokens")]
        public Int64? PromptTokens
        {
            get; set;
        }

        /// <summary>Completion (output) token count.</summary>
        [JsonPropertyName("completionTokens")]
        public Int64? CompletionTokens
        {
            get; set;
        }

        /// <summary>Total wall-clock generation time in milliseconds.</summary>
        [JsonPropertyName("durationMs")]
        public Double? DurationMs
        {
            get; set;
        }

        /// <summary>Output tokens per second.</summary>
        [JsonPropertyName("tokensPerSecond")]
        public Double? TokensPerSecond
        {
            get; set;
        }
    }
}
