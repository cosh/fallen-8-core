// MIT License
//
// Fallen8AgentsOptions.cs
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
    ///   The agent-host proxy configuration (feature agent-host), section <c>Fallen8:Agents</c>.
    ///   Default OFF: every <c>/agents</c> route refuses before a sidecar is contacted, with 403
    ///   on a keyed instance and 401 on a keyless one (<c>AgentsController</c> is the one home for
    ///   why, and a client that reads only 403 as "absent" breaks on the second kind).
    ///
    ///   <para>The agent host is a separate deployable (<c>fallen-8-agents</c>) whose container port
    ///   is deliberately not published, so this proxy is the authenticated way in from outside the
    ///   compose network. It is not the only caller that can reach the host: every service on that
    ///   network can, and the host authenticates none of them, which is the posture the integrations
    ///   runtime ships with too. https://docs.fallen-8.com/security/ is the one home for that
    ///   boundary.</para>
    ///
    ///   <para><b>Nothing about a MODEL appears here or on the host.</b> Agents ask this instance's
    ///   own chat gateway with <c>purpose: agent</c>, so the provider, the credential and the model
    ///   are <c>Fallen8:Chat</c>'s, in one place. The one dependency worth knowing is therefore
    ///   real: the Chat capability has to be on and its backend needs a
    ///   <c>Models:Agent</c>, or agents fail on their first model call with the gateway's own
    ///   message saying which key to set.</para>
    /// </summary>
    public sealed class Fallen8AgentsOptions
    {
        public const String SectionName = "Fallen8:Agents";

        /// <summary>The authorization policy gating the agents surface
        /// (<see cref="Security.DynamicCapabilityRequirement.Capability.Agents" />).</summary>
        public const String AgentsPolicy = "Fallen8.Agents";

        /// <summary>The capability flag. Default off.</summary>
        public Boolean Enabled
        {
            get; set;
        }

        /// <summary>The fallen-8-agents endpoint (empty: not configured - the proxy answers 503
        /// rather than timing out, so a bare <c>dotnet run</c> with no sidecar says so).</summary>
        public String Endpoint { get; set; } = String.Empty;

        /// <summary>
        ///   Per-proxied-request timeout for the small control-plane routes, and for the wait on
        ///   the event feed's response HEADERS.
        ///
        ///   <para>Small on purpose, and it does not need to cover inference: nothing on the agent
        ///   host's control plane blocks on a model. A spawn answers 202 with an id and the run
        ///   happens on the host's own time, so 30 seconds is a generous budget for a listing. The
        ///   feed's BODY takes none of it, because a stream stays open for as long as its
        ///   subscriber wants it; <see cref="Agents.IAgentsClient.StreamAsync" /> is the one home
        ///   for that split.</para>
        /// </summary>
        public Int32 TimeoutSeconds { get; set; } = 30;
    }
}
