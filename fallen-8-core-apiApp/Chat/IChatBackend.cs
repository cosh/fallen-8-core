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

    /// <summary>One chat turn: a role (<c>system</c>/<c>user</c>/<c>assistant</c>/<c>tool</c>) and content.</summary>
    public sealed class ChatTurn
    {
        public ChatTurn(String role, String content)
        {
            Role = role;
            Content = content;
        }

        public String Role { get; }

        public String Content { get; }
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
        public String Content { get; init; }

        public String Model { get; init; }

        public Int64? PromptTokens { get; init; }

        public Int64? CompletionTokens { get; init; }

        public Double? DurationMs { get; init; }

        public Double? TokensPerSecond { get; init; }
    }
}
