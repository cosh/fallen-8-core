// MIT License
//
// IChatBackend.cs
//
// Copyright (c) 2025 Henning Rauch
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
        /// <summary>Runs one non-streaming chat completion and returns the assistant content plus
        /// the backend's generation stats. Throws on backend failure (surfaced by the provider as
        /// 503) - never returns a partial/garbled result silently.</summary>
        Task<ChatBackendResult> ChatAsync(IReadOnlyList<ChatTurn> messages, ChatBackendOptions options,
            CancellationToken cancellationToken);

        /// <summary>Best-effort GPU residency of the configured model: <c>true</c>/<c>false</c> when
        /// the backend reports it (Ollama <c>/api/ps</c> VRAM), <c>null</c> when it cannot be
        /// determined (model not resident, backend down, or backend cannot report). Never throws.</summary>
        Task<Boolean?> TryDetectGpuAsync(CancellationToken cancellationToken);
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

    /// <summary>Optional per-call knobs. Temperature is the only one surfaced in v1.</summary>
    public sealed class ChatBackendOptions
    {
        public Double? Temperature { get; init; }
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
