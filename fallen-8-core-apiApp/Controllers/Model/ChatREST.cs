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
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>A chat completion request proxied to the instance's model backend (feature
    /// instance-config). The model is SERVER-owned (<c>Fallen8:Chat:Ollama:Model</c>); there is no
    /// client model field.</summary>
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

        /// <summary>The message content.</summary>
        /// <example>Draft a vertex filter for label person</example>
        [Required]
        [JsonPropertyName("content")]
        public String Content
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
    }

    /// <summary>A chat completion plus the backend's generation stats.</summary>
    public sealed class ChatResultREST
    {
        /// <summary>The assistant message content.</summary>
        [JsonPropertyName("content")]
        public String Content
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
