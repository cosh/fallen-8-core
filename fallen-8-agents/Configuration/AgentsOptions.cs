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
using NoSQL.GraphDB.Rest.Configuration;

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
        ///   container binding loopback is unreachable.
        ///
        ///   <para>
        ///     <b>This is the one home for what bounds this listener, because it is the network and
        ///     not a credential.</b> The compose service publishes no host port, so the browser and
        ///     anything else outside the compose network reach this host only through the apiApp's
        ///     authenticated proxy at <c>/agents/*</c>. Inside that network the bound is weaker than
        ///     this doc used to claim: it said the proxy was the ONLY way in, and every service on
        ///     <c>f8-net</c> can reach this port. This process authenticates none of them, and it
        ///     holds the instance's API key and the MCP bearer, so a caller that reaches it can
        ///     spawn an agent at whatever tiers the operator enabled on the MCP server.
        ///   </para>
        ///   <para>
        ///     That is the integrations runtime's posture exactly, which is what makes it a house
        ///     convention for these sidecars rather than something this host decided; the published
        ///     home is https://docs.fallen-8.com/security/. A non-loopback bind is WARNED about at
        ///     startup rather than refused: the image sets one deliberately, so a refusal would
        ///     break the shipped container, and unlike <c>fallen-8-mcp</c> there is no auth mode to
        ///     fall back to.
        ///   </para>
        /// </summary>
        public String BindAddress { get; set; } = "127.0.0.1";

        /// <summary>The listen port. The compose service publishes none; see
        /// <see cref="BindAddress" /> for what does and does not bound this listener.</summary>
        public Int32 Port { get; set; } = 8120;

        /// <summary>The MCP server this host's agents reach the graph through.</summary>
        public McpOptions Mcp { get; set; } = new McpOptions();

        /// <summary>The caps every run is held to.</summary>
        public LimitsOptions Limits { get; set; } = new LimitsOptions();

        /// <summary>What a run records about itself.</summary>
        public TraceOptions Trace { get; set; } = new TraceOptions();

        /// <summary>How the event feed is delivered.</summary>
        public FeedOptions Feed { get; set; } = new FeedOptions();

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

        /// <summary>
        ///   What a run records about itself, and what it refuses to record.
        ///
        ///   <para>
        ///     <b>Every number here is a ceiling rather than a target, and the reason is the same
        ///     one in all three cases: a trace is for REVIEW, and review needs recency and shape,
        ///     not an archive.</b> An agent that ran for an hour against a graph can produce results
        ///     measured in megabytes, and holding all of it in a process that keeps nothing durable
        ///     would trade the thing the trace is for against the thing it costs.
        ///   </para>
        /// </summary>
        public sealed class TraceOptions
        {
            /// <summary>
            ///   ROWS one agent's trace holds. Past this the oldest steps are dropped and a marker
            ///   row records how many went, so a reader can tell a short run from a truncated one.
            ///   <para>
            ///     Rows rather than steps, because the marker is one of them: a trace that has
            ///     dropped anything holds this many minus one real steps. A positive value below 2
            ///     is floored at 2, since a single row could hold either a step or the news that
            ///     steps were lost. A non-positive value switches the bound off.
            ///   </para>
            /// </summary>
            public Int32 MaxSteps { get; set; } = 1000;

            /// <summary>Bytes of a tool call's ARGUMENTS kept. Small, because arguments are a model's
            /// output and a model can be talked into putting anything in them.</summary>
            public Int32 ArgsBytes { get; set; } = 2048;

            /// <summary>
            ///   Bytes of a tool call's RESULT kept. Larger than the arguments, because this is what a
            ///   reviewer checks an answer against, and it is the graph's data rather than the model's.
            ///   <para>
            ///     Still a small fraction of what a graph read can return, which is the honest
            ///     position: the trace records that a call happened, with what, and what came back
            ///     in outline. A caller who needs the whole result asks the graph again.
            ///   </para>
            /// </summary>
            public Int32 ResultBytes { get; set; } = 8192;
        }

        /// <summary>How the event feed is delivered.</summary>
        public sealed class FeedOptions
        {
            /// <summary>How often an idle stream sends a keep-alive comment. It bounds
            /// dead-connection detection and defeats a proxy's idle timeout, which is why an idle
            /// feed is never silent and why a non-positive value is floored at 1 second rather than
            /// sending none. <see cref="KeepAlive" /> is what is in force.</summary>
            public Int32 KeepAliveSeconds { get; set; } = 15;

            /// <summary>The interval actually in force, which is <see cref="KeepAliveSeconds" />
            /// through both bounds. It exists for the reason the target host's deadline has the
            /// same pair: the status route reported the raw setting, so a host sending a comment
            /// every second reported that it sends none.</summary>
            public TimeSpan KeepAlive => OptionBounds.Seconds(KeepAliveSeconds);

            /// <summary>
            ///   Concurrent feed subscribers. Bounded because each one holds a queue, and an
            ///   unbounded number of them is an unbounded amount of memory in a process that a
            ///   caller can open connections to.
            /// </summary>
            public Int32 MaxSubscribers { get; set; } = 16;

            /// <summary>
            ///   Events one subscriber may fall behind by. Past this the subscriber is DROPPED
            ///   rather than the events being discarded silently, because a feed that quietly
            ///   skipped events would let a reader believe they saw everything. A dropped
            ///   subscriber reconnects and reads the trace to catch up, which is the documented
            ///   catch-up mechanism.
            ///   <para>
            ///     A non-positive value is floored at 1 rather than switching the bound off. There
            ///     is deliberately no "off" here: an unbounded queue is an unbounded amount of
            ///     memory held for one slow subscriber, which is the failure
            ///     <see cref="MaxSubscribers" /> is bounded to avoid.
            ///   </para>
            /// </summary>
            public Int32 MaxQueuedEvents { get; set; } = 512;
        }

        /// <summary>What one role may do.</summary>
        public sealed class RoleOptions
        {
            /// <summary>
            ///   The MCP tool names this role may see, REPLACING the list it ships with rather than
            ///   adding to it. Absent or empty keeps the shipped list (for <c>orchestrator</c> that
            ///   is the overview read, not everything); a list of names means exactly those; a lone
            ///   <see cref="Runtime.RoleCatalog.EveryTool" /> means every tool the server
            ///   advertises. Whichever it is, that server's own tiers are the outer bound and are
            ///   enforced server-side, so nothing here reaches past them.
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
            /// and starts anyway, as the operator wrote it. Read <see cref="Connect" /> to arm
            /// anything with it. Bounded because an unreachable MCP server must not stop this
            /// process from starting and reporting that it is unreachable, which is also why a
            /// non-positive value is floored at 1 second rather than switching the bound off.</summary>
            public Int32 ConnectTimeoutSeconds { get; set; } = 15;

            /// <summary>
            ///   The connect bound actually in force, which is <see cref="ConnectTimeoutSeconds" />
            ///   through both of <c>OptionBounds</c>' ends.
            ///   <para>
            ///     It exists because this key had the floor and not the ceiling, in the one project
            ///     the clamp came from: four copies of <c>Math.Max(1, ...)</c> armed
            ///     <c>CancelAfter</c> with it, which refuses a delay past about 49.7 days, so a
            ///     large value threw an exception naming a parameter and the broad catch below
            ///     reported it as "the MCP server did not answer" - an unreachable server that was
            ///     up, and a retry cooldown computed from the same number that would not have
            ///     retried this century. A review of the branch that shared this clamp found it.
            ///   </para>
            /// </summary>
            public TimeSpan Connect => OptionBounds.Seconds(ConnectTimeoutSeconds);
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

            /// <summary>
            ///   How deep a swarm may nest. A caller's agent is depth 0, a worker it spawns is 1,
            ///   so the default of 2 lets an orchestrator delegate and stops its workers from
            ///   orchestrating in turn.
            ///   <para>
            ///     The bound exists because the cost of a tree is multiplicative while the thing an
            ///     operator configures is per agent: three levels of four workers is 21 agents from
            ///     one request, each with its own token budget, against a provider quota they all
            ///     share. Depth is recorded on the record at admission rather than walked at spawn
            ///     time, so an evicted ancestor cannot make a deep agent look shallow. A
            ///     non-positive value switches the bound off.
            ///   </para>
            /// </summary>
            public Int32 MaxSwarmDepth { get; set; } = 2;

            /// <summary>
            ///   How many workers ONE orchestrator may spawn over its whole life.
            ///   <para>
            ///     Over its life, not at once, and the difference is the whole point: a live-only
            ///     count would let an orchestrator spawn its four, await them, and spawn four more
            ///     without limit, because an orchestrator's token budget bounds its OWN calls and
            ///     not its workers'. So the count is kept on the orchestrator's record and never
            ///     decremented, which also makes it survive the eviction of the workers it counted.
            ///     A non-positive value switches the bound off.
            ///   </para>
            /// </summary>
            public Int32 MaxWorkersPerOrchestrator { get; set; } = 4;

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
