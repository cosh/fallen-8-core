// MIT License
//
// Fallen8TargetOptions.cs
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

namespace NoSQL.GraphDB.Integrations.Configuration
{
    /// <summary>
    ///   The Fallen-8 this runtime writes into (config section <c>Fallen8Target</c>). Section name and
    ///   shape match <c>fallen-8-mcp</c>'s as a small copied options class: an operator configuring two
    ///   sidecars should not learn two spellings, while the CONFIGURATION SHAPE stays each deployable's
    ///   own, so one may gain a knob the other has no use for. What the two do share is the behavioural
    ///   seam (<c>fallen-8-rest-client</c>), where a divergence would be a difference in what a run
    ///   reports rather than in what an operator may set.
    ///
    ///   <para>A CALLER'S CREDENTIAL IS NEVER FORWARDED: the runtime authenticates to the graph as
    ///   itself, so a job cannot escalate beyond what this deployable may already do and a graph audit
    ///   trail names one writer per sidecar instead of whoever submitted a job. This key is not an
    ///   exception to the credential rules, which govern credentials held on somebody else's behalf;
    ///   this one is rotated by whoever restarts the deployable.</para>
    /// </summary>
    public sealed class Fallen8TargetOptions
    {
        /// <summary>The configuration section this binds from.</summary>
        public const String SectionName = "Fallen8Target";

        /// <summary>The base URL, e.g. <c>http://fallen8:8080</c> in-network.</summary>
        public String BaseUrl { get; set; } = "http://localhost:8080";

        /// <summary>The API key this runtime presents to Fallen-8 (its own single downstream
        /// identity). Never surfaced to callers.</summary>
        public String? ApiKey { get; set; }

        /// <summary>The header the key is sent under (default <c>X-Api-Key</c>, which the apiApp
        /// already accepts alongside a bearer token).</summary>
        public String ApiKeyHeader { get; set; } = "X-Api-Key";

        /// <summary>
        ///   The namespace a job writes into when it names none: the reserved <c>default</c>. Downstream
        ///   TLS is validated normally with no insecure-target escape hatch, which keeps
        ///   <see cref="IntegrationsOptions.SelfSignedHosts"/> this feature's single reduction of trust.
        /// </summary>
        public String DefaultNamespace { get; set; } = "default";

        /// <summary>
        ///   The per-request deadline on every call this runtime makes to the graph. Values below 1 are
        ///   floored at 1 second, so a stray 0 cannot make every call throw.
        ///   <para>
        ///     The default sits deliberately ABOVE the longest budget the apiApp applies to a route this
        ///     runtime calls, for the reason <c>fallen-8-mcp</c>'s <c>Fallen8TargetOptions.TimeoutSeconds</c>
        ///     states in full: two competing deadlines make the nearer one report a vague local failure
        ///     instead of the downstream answer that names what to change. Here the far one is
        ///     <c>Fallen8:Embedding:TimeoutSeconds</c> (300s), which the embedding write can legitimately
        ///     spend on model inference and cold-model warm-up.
        ///   </para>
        /// </summary>
        public Int32 TimeoutSeconds { get; set; } = 330;
    }
}
