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

namespace NoSQL.GraphDB.Mcp.Configuration
{
    /// <summary>
    ///   The downstream Fallen-8 the bridge points at (config section <c>Fallen8Target</c>).
    ///   Named <c>Fallen8Target</c> — not a bare <c>F8</c> — because the repo's .NET config
    ///   sections are all <c>Fallen8:*</c> and <c>F8_*</c> already means the compose shell
    ///   variables; this section says "a remote Fallen-8 I point at" (spec §3.9).
    /// </summary>
    public sealed class Fallen8TargetOptions
    {
        public const String SectionName = "Fallen8Target";

        /// <summary>The base URL, e.g. <c>http://fallen8:8080</c> in-network or an https URL
        /// cross-host.</summary>
        public String BaseUrl { get; set; } = "http://localhost:8080";

        /// <summary>The API key this server presents to Fallen-8 (its own single downstream
        /// identity — spec §3.9). Never surfaced to callers.</summary>
        public String? ApiKey { get; set; }

        /// <summary>The header the key is sent under (default <c>X-Api-Key</c>, mirroring
        /// api-security-boundary; the apiApp also accepts <c>Authorization: Bearer</c>).</summary>
        public String ApiKeyHeader { get; set; } = "X-Api-Key";

        /// <summary>Lab-only escape hatch that disables downstream TLS validation for a
        /// self-signed Fallen-8. Default false; loudly logged when on.</summary>
        public Boolean TlsInsecure { get; set; }

        /// <summary>
        ///   The per-request deadline on a bridged REST call. Stated rather than inherited: without
        ///   it the client kept the .NET default of 100s, an undocumented bound no operator could
        ///   tune. Values below 1 are floored at 1 second (the repo's convention for a config
        ///   seconds value, as in <c>DoclingClient</c>), so a stray 0 cannot make every call throw.
        ///   <para>
        ///     The default is deliberately ABOVE the longest synchronous budget the apiApp applies on
        ///     a bridged route, so the downstream error wins and the agent is told which server
        ///     setting to change. That budget is <c>Fallen8:Embedding:TimeoutSeconds</c> (300s): a
        ///     bridged <c>POST /embedding/search</c> or <c>POST /document/search</c> embeds the query
        ///     text in-request, so it can legitimately run for minutes. A shorter bound here would
        ///     pre-empt it and report the bridge's vague retryable <c>504</c> instead - the same
        ///     two-competing-deadlines mistake this repo removed from the chat gateway.
        ///   </para>
        ///   Exceeding it surfaces as the bridge's retryable <c>504</c> ("Fallen-8 timeout"), which
        ///   <c>Fallen8RestClient</c> already maps.
        /// </summary>
        public Int32 TimeoutSeconds { get; set; } = 330;
    }
}
