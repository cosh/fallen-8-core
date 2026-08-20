// MIT License
//
// Fallen8ChatOptions.cs
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

        /// <summary>The backend: <c>Ollama</c> (the local sidecar, the default) or <c>Nahil</c>
        /// (nahil.dev, remote and authenticated). Both speak the same protocol; Nahil adds a
        /// credential and a warm-up state, which is the whole difference.</summary>
        public String Backend { get; set; } = "Ollama";

        /// <summary>The per-request proxy timeout; exceeded requests answer 504. It is the SINGLE
        /// deadline on a chat call at any value: the Ollama transport is built without one, so this
        /// is never pre-empted by a shorter undocumented bound (it once was - OllamaSharp's default
        /// 100s client timeout fired first and surfaced as an unhandled 500).
        /// <para>
        ///   The default is generous because a local model on CPU is SLOW: measured on a 16-core
        ///   laptop, a fine-tuned phi4-mini answers a Studio NL-assist prompt in minutes, not
        ///   seconds. Raising this does not make such a host usable; it only decides how long a
        ///   caller waits before the honest 504. See the NL-assist troubleshooting page.
        /// </para></summary>
        public Int32 TimeoutSeconds { get; set; } = 120;

        /// <summary>
        ///   Whether to ask the backend to stream the completion. On by default: the tokens then
        ///   arrive as they are produced instead of after the whole answer exists, and Nahil runs
        ///   its own verification pass AFTER delivery rather than in front of it, so a slow remote
        ///   worker stops paying for that pass twice over in latency.
        ///   <para>
        ///     <c>POST /chat</c> buffers either way - its response shape is unchanged - so this is
        ///     about what the BACKEND is asked to do, not about what a client sees. Turn it off only
        ///     for a backend whose streaming is broken.
        ///   </para>
        /// </summary>
        public Boolean Stream { get; set; } = true;

        /// <summary>Ollama backend settings (reuses the sidecar the embedding provider uses).</summary>
        public OllamaOptions Ollama { get; set; } = new OllamaOptions();

        /// <summary>Nahil settings; used only when <see cref="Backend" /> is <c>Nahil</c>.</summary>
        public NahilOptions Nahil { get; set; } = new NahilOptions();

        public sealed class OllamaOptions
        {
            /// <summary>The Ollama endpoint (the compose-shipped container by default). Using this
            /// backend couples chat availability to that container: when it is down <c>POST /chat</c>
            /// answers 503 while everything else keeps running.</summary>
            public String Endpoint { get; set; } = "http://localhost:11434";

            /// <summary>The chat model to invoke (pull a model, e.g. the fine-tuned phi4-f8-mini
            /// default, a stock phi4-mini, or any Ollama chat model). Server-owned: clients cannot
            /// override it on the default path. Reaches the request body VERBATIM - nothing here
            /// strips, appends or normalizes a <c>:tag</c> - so the tag is explicit rather than
            /// left to whatever default each end assumes.</summary>
            public String Model { get; set; } = "phi4-f8-mini:latest";
        }

        /// <summary>
        ///   Nahil (nahil.dev): the same Ollama protocol, authenticated, served from someone else's
        ///   hardware. There is no default endpoint, so selecting this backend without configuring one
        ///   is refused with the reason rather than silently dialling localhost.
        /// </summary>
        public sealed class NahilOptions
        {
            /// <summary>The Nahil base URL. Must be a host root (scheme, host, optional port);
            /// HTTPS for anything off the operator's own network.</summary>
            public String Endpoint
            {
                get; set;
            }

            /// <summary>The bearer credential Nahil requires on EVERY route, including its version and
            /// residency probes. Never logged and never published on the config read surface.</summary>
            public String ApiKey
            {
                get; set;
            }

            /// <summary>The chat model to invoke, as Nahil's catalog names it (the published
            /// registry name, which may differ from a locally tagged copy of the same weights).
            /// Reaches the request body verbatim.</summary>
            public String Model
            {
                get; set;
            }
        }
    }
}
