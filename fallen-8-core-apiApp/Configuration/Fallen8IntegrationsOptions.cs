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
    ///   <c>Fallen8:Integrations</c>. Default OFF: the four <c>/integrations</c> routes answer 403
    ///   and no sidecar is contacted.
    ///
    ///   <para>The integration runtime is a separate deployable (<c>fallen-8-integrations</c>) whose
    ///   container port is deliberately not published, because jobs hand it third-party credentials.
    ///   The apiApp is therefore the only way in: it proxies the runtime's four routes, being already
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
        /// computation, and the caller is waiting on that one call.</summary>
        public Int32 TimeoutSeconds { get; set; } = 120;
    }
}
