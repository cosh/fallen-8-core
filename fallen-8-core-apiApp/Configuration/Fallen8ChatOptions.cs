// MIT License
//
// Fallen8ChatOptions.cs
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

namespace NoSQL.GraphDB.App.Configuration
{
    /// <summary>
    ///   The chat (SLM/LLM) provider configuration (feature instance-config), section
    ///   <c>Fallen8:Chat</c>. It is the server-side half of the semantic gateway: the instance
    ///   proxies chat completions to the model backend (the Ollama sidecar by default) so Studio
    ///   and other clients can reach a model THROUGH the instance instead of directly. Default
    ///   OFF: <c>POST /chat</c> answers 403 and no client is constructed, so a bare deployment
    ///   stays model-free. The instance OWNS the model (<see cref="Ollama" />.<c>Model</c>); the
    ///   endpoint takes no client-supplied model, mirroring the embedding gateway.
    /// </summary>
    public sealed class Fallen8ChatOptions
    {
        public const String SectionName = "Fallen8:Chat";

        /// <summary>The authorization policy gating <c>POST /chat</c>
        /// (<see cref="Security.DynamicCapabilityRequirement.Capability.Chat" />).</summary>
        public const String ChatPolicy = "Fallen8.Chat";

        /// <summary>The capability flag. Default off (403 when off).</summary>
        public Boolean Enabled
        {
            get; set;
        }

        /// <summary>The backend: <c>Ollama</c> (the only backend in v1).</summary>
        public String Backend { get; set; } = "Ollama";

        /// <summary>The per-request proxy timeout; exceeded requests answer 504.</summary>
        public Int32 TimeoutSeconds { get; set; } = 120;

        /// <summary>Ollama backend settings (reuses the sidecar the embedding provider uses).</summary>
        public OllamaOptions Ollama { get; set; } = new OllamaOptions();

        public sealed class OllamaOptions
        {
            /// <summary>The Ollama endpoint (the compose-shipped container by default). Using this
            /// backend couples chat availability to that container: when it is down <c>POST /chat</c>
            /// answers 503 while everything else keeps running.</summary>
            public String Endpoint { get; set; } = "http://localhost:11434";

            /// <summary>The chat model to invoke (pull a model, e.g. the fine-tuned phi4-f8-mini
            /// default, a stock phi4-mini, or any Ollama chat model). Server-owned: clients cannot
            /// override it on the default path.</summary>
            public String Model { get; set; } = "phi4-f8-mini";
        }
    }
}
