// MIT License
//
// Fallen8IntegrationsOptions.cs
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
    ///   The integrations configuration (feature integrations), section
    ///   <c>Fallen8:Integrations</c>. Default OFF: every <c>/integrations</c> route answers 403
    ///   and no sidecar is contacted.
    ///
    ///   <para>The integration runtime is a separate deployable (<c>fallen-8-integrations</c>) whose
    ///   container port is deliberately not published, because jobs hand it third-party credentials.
    ///   The apiApp is therefore the only way in: it proxies the runtime's six routes, being already
    ///   the authenticated front door, which is why the runtime needs no second auth story.</para>
    /// </summary>
    public sealed class Fallen8IntegrationsOptions
    {
        public const String SectionName = "Fallen8:Integrations";

        /// <summary>The authorization policy gating the integrations surface
        /// (<see cref="Security.DynamicCapabilityRequirement.Capability.Integrations" />).</summary>
        public const String IntegrationsPolicy = "Fallen8.Integrations";

        /// <summary>The capability flag. Default off.</summary>
        public Boolean Enabled
        {
            get; set;
        }

        /// <summary>The fallen-8-integrations endpoint (empty: not configured - the proxy answers
        /// 503 rather than timing out, so a bare <c>dotnet run</c> with no sidecar says so).</summary>
        public String Endpoint { get; set; } = String.Empty;

        /// <summary>Per-proxied-request timeout. Generous by default because running a job is a
        /// SYNCHRONOUS read of somebody's console followed by graph writes, not a local
        /// computation, and the caller is waiting on that one call.
        ///
        /// <para>This is the budget for the six SMALL routes only. The job route has its own
        /// (<see cref="JobTimeoutSeconds" />), because one number cannot serve both: this one also
        /// bounds the run poll Studio calls repeatedly, where a fifteen-minute budget would let a
        /// wedged runtime hold a poll for fifteen minutes.</para></summary>
        public Int32 TimeoutSeconds { get; set; } = 120;

        /// <summary>
        ///   The budget for <c>POST /integrations/job</c> alone, which is the only route whose
        ///   request carries a body worth measuring.
        ///
        ///   <para>Separate from <see cref="TimeoutSeconds" /> because the budget has to cover the
        ///   UPLOAD, not just the answer: the proxy streams the caller's body through, so the clock
        ///   runs at the browser's send rate. Measured, 120 seconds needs a sustained 40 Mbit/s to
        ///   move a maximal 576 MiB job and fails outright at 25; 900 seconds is 5.4 Mbit/s
        ///   sustained, which is a slow link rather than a broken one.</para>
        ///
        ///   <para>Floored at 1 like its sibling: a zero budget would fail every job instantly.</para>
        /// </summary>
        public Int32 JobTimeoutSeconds { get; set; } = 900;
    }
}
