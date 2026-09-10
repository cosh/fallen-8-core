// MIT License
//
// AgentsOptions.cs
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

namespace NoSQL.GraphDB.Agents.Configuration
{
    /// <summary>
    ///   This host's OWN behaviour (config section <c>Agents</c>). The Fallen-8 it asks for
    ///   completions lives in the separate <see cref="Fallen8TargetOptions" />
    ///   (<c>Fallen8Target</c>), split the way <c>fallen-8-mcp</c> and
    ///   <c>fallen-8-integrations</c> split theirs: conflating them leaves a compose reader unable
    ///   to tell which values describe the process and which its target.
    ///
    ///   <para><b>There is deliberately no model configuration here at all</b>, and its absence is
    ///   the feature: this host names no provider, holds no provider credential and cannot choose a
    ///   model. It asks its instance, which owns the selector, the credential and one model per
    ///   purpose. Switching a deployment from a local sidecar to a hosted provider therefore
    ///   changes nothing in this process, and the only place a provider key exists is the
    ///   instance.</para>
    /// </summary>
    public sealed class AgentsOptions
    {
        /// <summary>The configuration section this binds from.</summary>
        public const String SectionName = "Agents";

        /// <summary>
        ///   The address Kestrel binds. Loopback by default; the image sets <c>0.0.0.0</c> because a
        ///   container binding loopback is unreachable, and the port is deliberately not published
        ///   to the host - an agent can be talked into calling a tool, so the apiApp's authenticated
        ///   proxy is the only way in, exactly as it is for the integrations runtime.
        /// </summary>
        public String BindAddress { get; set; } = "127.0.0.1";

        /// <summary>The listen port. Not published; see <see cref="BindAddress" />.</summary>
        public Int32 Port { get; set; } = 8120;

        /// <summary>The MCP server this host's agents reach the graph through.</summary>
        public McpOptions Mcp { get; set; } = new McpOptions();

        /// <summary>The caps every run is held to.</summary>
        public LimitsOptions Limits { get; set; } = new LimitsOptions();

        /// <summary>
        ///   Per-role configuration, keyed by role name (<c>assistant</c>, <c>orchestrator</c>,
        ///   <c>worker</c>), so a leaf is <c>Agents:Roles:&lt;role&gt;:Tools</c>.
        ///   <para>
        ///     The nesting is load-bearing rather than decorative. A
        ///     <c>Dictionary&lt;String, List&lt;String&gt;&gt;</c> here would bind
        ///     <c>Agents:Roles:&lt;role&gt;:0</c>, so an operator writing the documented
        ///     <c>...:Tools</c> key would configure NOTHING and every role would keep its shipped
        ///     default with no error anywhere. A tool allowlist that silently fails to narrow is
        ///     worse than one that refuses to load.
        ///   </para>
        /// </summary>
        public Dictionary<String, RoleOptions> Roles { get; set; } =
            new Dictionary<String, RoleOptions>(StringComparer.OrdinalIgnoreCase);

        /// <summary>What one role may do.</summary>
        public sealed class RoleOptions
        {
            /// <summary>
            ///   The MCP tool names this role may see. Absent or empty means every tool the server
            ///   advertises; a non-empty list NARROWS that set and can never widen it, because the
            ///   server's own tiers are the outer bound and are enforced server-side.
            /// </summary>
            public List<String> Tools { get; set; } = new List<String>();
        }

        /// <summary>
        ///   The MCP server, which is this host's ONLY route to a graph. It holds no Fallen-8 API
        ///   key of its own for that: the MCP server presents its own credential downstream, so an
        ///   agent's reach is whatever that server's tiers allow and nothing more.
        /// </summary>
        public sealed class McpOptions
        {
            /// <summary>The MCP server's base URL (the compose-shipped one by default).</summary>
            public String Endpoint { get; set; } = "http://localhost:8090";

            /// <summary>The bearer token this host presents to the MCP server when that server
            /// requires one. Never logged. Empty is correct against a server in its
            /// <c>None</c> auth mode, which is the local-dev posture.</summary>
            public String? BearerToken
            {
                get; set;
            }

            /// <summary>How long the startup tool-list read may take before the host gives up on it
            /// and starts anyway. Bounded because an unreachable MCP server must not stop this
            /// process from starting and reporting that it is unreachable.</summary>
            public Int32 ConnectTimeoutSeconds { get; set; } = 15;
        }

        /// <summary>
        ///   What bounds a run. Every one of these is enforced in this process rather than asked of
        ///   the model, because a model that is looping is exactly the model that will not honour an
        ///   instruction to stop.
        ///   <para>
        ///     The defaults are small on purpose, and the reason is measured: one step against a
        ///     remote provider took between 0.3 and 41 seconds, reported token counts were sometimes
        ///     zero, and the provider meters a per-key hourly budget. So the caps are conservative
        ///     and are configuration.
        ///   </para>
        /// </summary>
        public sealed class LimitsOptions
        {
            /// <summary>Model calls in one run. Where the framework has its own maximum-iterations
            /// knob, this is the value handed to it.</summary>
            public Int32 MaxStepsPerRun { get; set; } = 24;

            /// <summary>Tool calls in one run, counted across steps.</summary>
            public Int32 MaxToolCallsPerRun { get; set; } = 48;

            /// <summary>Wall clock for one run, measured HERE. A backend's own reported durations
            /// do not cover a remote provider's routing and verification passes: one measured step
            /// reported 45 ms for a call that took 41 seconds.</summary>
            public Int32 MaxRunSeconds { get; set; } = 1800;

            /// <summary>Tokens one agent may spend when its spawn names no budget, from the usage
            /// the instance reports. Usage that a backend does not report counts as zero and is
            /// flagged, never estimated.</summary>
            public Int32 DefaultTokenBudget { get; set; } = 100_000;

            /// <summary>
            ///   The most any one agent may spend, whatever its spawn asked for. Separate from the
            ///   default because they answer different questions: the default is what a caller who
            ///   did not think about it gets, and this is what a caller who did cannot exceed.
            ///   <para>
            ///     Without it a spawn could name its own budget and the operator's number would be
            ///     advice. The provider meters a per-key hourly budget shared by every agent on this
            ///     host, so "how much may one caller spend" is the operator's decision, not the
            ///     caller's. A non-positive value switches the ceiling off.
            ///   </para>
            /// </summary>
            public Int32 MaxTokenBudget { get; set; } = 400_000;

            /// <summary>How many agents may be running at once. Low because the provider's hourly
            /// budget and the instance's own rate limit are both shared by all of them.</summary>
            public Int32 MaxConcurrentAgents { get; set; } = 4;

            /// <summary>How long a finished agent stays readable before it is evicted. A run's
            /// value is in reviewing it afterwards, and nothing here is durable: a restart ends
            /// everything.</summary>
            public Int32 RetainFinishedMinutes { get; set; } = 60;

            /// <summary>The ceiling on retained finished agents, oldest evicted first, so a busy
            /// host cannot grow without bound between restarts.</summary>
            public Int32 MaxRetainedAgents { get; set; } = 200;
        }
    }
}
