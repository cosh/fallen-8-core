// MIT License
//
// Fallen8SecurityOptions.cs
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
    ///   Security configuration for the hosted API, bound from the <c>Fallen8:Security</c> section
    ///   (feature api-security-boundary). Establishes an authentication trust boundary. Roslyn filter
    ///   compilation is ALWAYS available (Fallen-8's "queries are C#" model has no off switch); plugin
    ///   DLL loading stays an opt-in operator kill switch.
    ///
    ///   <para>HONEST LIMIT: in-process Roslyn compilation cannot be sandboxed - a compiled delegate
    ///   runs with the server process's full authority. Authentication is the trust boundary (who may
    ///   reach the code endpoints), NOT a sandbox. Anyone allowed to post a filter is trusted as the
    ///   process, so an internet-facing deployment MUST configure an API key (or OAuth).</para>
    /// </summary>
    public sealed class Fallen8SecurityOptions
    {
        /// <summary>The configuration section this binds from.</summary>
        public const String SectionName = "Fallen8:Security";

        /// <summary>
        ///   Authorization policy name gating source plugin registration (POST /plugins/*, feature
        ///   plugin-registration). Named for its history (it formerly gated the removed PUT /plugin DLL
        ///   upload); the policy name is a config/contract constant, so it is kept stable.
        /// </summary>
        public const String DynamicPluginPolicy = "Fallen8.DynamicPluginLoading";

        /// <summary>The authentication scheme name for the built-in API-key handler.</summary>
        public const String ApiKeyScheme = "Fallen8ApiKey";

        /// <summary>Rate-limiter policy name applied to the expensive/dangerous code + plugin endpoints.</summary>
        public const String SensitiveRateLimitPolicy = "Fallen8.SensitiveEndpoints";

        /// <summary>
        ///   The API key required in the <see cref="ApiKeyHeader"/>. Supply from user-secrets or
        ///   environment - NEVER a checked-in default. When null/blank the API-key scheme authenticates
        ///   nobody: the server logs a prominent warning and runs UNAUTHENTICATED - including the
        ///   always-on in-process code endpoints, so only bind an unauthenticated instance to a
        ///   trusted network.
        /// </summary>
        public String ApiKey { get; set; }

        /// <summary>Header carrying the API key. Defaults to <c>X-Api-Key</c>.</summary>
        public String ApiKeyHeader { get; set; } = "X-Api-Key";

        /// <summary>
        ///   The GLOBAL default for runtime plugin REGISTRATION (POST /plugins/*, feature
        ///   plugin-registration). Default <b>true</b>: registration is on unless an operator disables
        ///   it - consistent with the always-on dynamic-code model (compiled path/subgraph fragments
        ///   already run unconditionally, so gating source plugin registration off by default was
        ///   inconsistent). It gates only the introduction surface - invoking an already-registered
        ///   plugin, listing, and deletion are never gated by this switch.
        ///
        ///   <para>This is the per-instance DEFAULT; an individual namespace can override it (enable
        ///   or disable) via <c>PATCH /ns/{name}</c> (<c>pluginRegistrationEnabled</c>), which the
        ///   authorization gate resolves ahead of this global value.</para>
        ///
        ///   <para>HONEST LIMIT: a registered plugin still runs IN-PROCESS WITH FULL TRUST when
        ///   invoked. This narrows who can INTRODUCE code and makes it visible/validated/logged - it is
        ///   not a sandbox. On an unauthenticated internet-facing instance this is an open
        ///   code-execution surface, so set an API key (as the always-on code endpoints already
        ///   require).</para>
        /// </summary>
        public Boolean EnableDynamicPluginLoading { get; set; } = true;

        /// <summary>
        ///   Origins allowed by the CORS policy. Empty (default) means deny all cross-origin requests.
        ///   No wildcard-with-credentials is ever configured.
        /// </summary>
        public String[] AllowedCorsOrigins { get; set; } = Array.Empty<String>();

        /// <summary>
        ///   Permitted request count per fixed window for the sensitive (code/plugin) endpoints. A
        ///   breach returns 429. Defaults to 30 per <see cref="RateLimitWindowSeconds"/>.
        /// </summary>
        public Int32 SensitiveRateLimitPermitPerWindow { get; set; } = 30;

        /// <summary>Fixed-window length (seconds) for the sensitive-endpoint rate limiter. Default 10.</summary>
        public Int32 RateLimitWindowSeconds { get; set; } = 10;

        private const Int32 DefaultBenchmarkMaxIterations = 10000;

        private Int32 _benchmarkMaxIterations = DefaultBenchmarkMaxIterations;

        /// <summary>
        ///   Ceiling on the timed iterations <c>GET /ns/{ns}/benchmark</c> accepts: a higher count is a 400,
        ///   and a request that names no count gets the endpoint default clamped to this value.
        ///   Default 10000, the same ceiling analytics puts on its iterations
        ///   (<c>GraphAnalyticsDefinition.MaxIterationsCeiling</c>). It sits with the other
        ///   abuse ceilings (the sensitive-endpoint rate limiter above) because it is the ONLY bound
        ///   on a benchmark pass: each iteration saturates every core and the loop cannot be
        ///   interrupted once started, so a mistyped extra zero would otherwise pin the host until
        ///   it finished. Non-positive values reset to the default.
        /// </summary>
        public Int32 BenchmarkMaxIterations
        {
            get { return _benchmarkMaxIterations; }
            set { _benchmarkMaxIterations = value > 0 ? value : DefaultBenchmarkMaxIterations; }
        }

        /// <summary>
        ///   Reserved and currently NOT enforced: the app binds wherever ASPNETCORE_URLS / Kestrel is
        ///   configured (0.0.0.0:8080 in the container image) regardless of this value. Do not rely on it
        ///   to keep the surface loopback-only - control off-box reachability via the bind address and set
        ///   an <see cref="ApiKey"/>.
        /// </summary>
        public Boolean AllowRemoteAccess { get; set; } = false;
    }
}
