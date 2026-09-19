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

namespace NoSQL.GraphDB.Agents.Configuration
{
    /// <summary>
    ///   The Fallen-8 this host asks for completions (config section <c>Fallen8Target</c>). Section
    ///   name and shape match <c>fallen-8-mcp</c>'s and <c>fallen-8-integrations</c>' as a small
    ///   copied options class: an operator configuring three sidecars should not learn three
    ///   spellings, while the configuration SHAPE stays each deployable's own so one may gain a
    ///   knob the others have no use for.
    ///
    ///   <para><b>What this host asks that instance for is narrower than the other two sidecars</b>
    ///   and is pinned as such: the chat gateway, and nothing else. It reads no vertex, writes no
    ///   edge and calls no graph route - every graph capability arrives as an MCP tool instead. A
    ///   REST call added here that is not the chat gateway fails
    ///   <c>CodeQualityTest</c>.</para>
    /// </summary>
    public sealed class Fallen8TargetOptions
    {
        /// <summary>The configuration section this binds from.</summary>
        public const String SectionName = "Fallen8Target";

        /// <summary>The base URL, e.g. <c>http://fallen8:8080</c> in-network.</summary>
        public String BaseUrl { get; set; } = "http://localhost:8080";

        /// <summary>The API key this host presents to Fallen-8 (its own single downstream
        /// identity). Never surfaced to callers and never logged. A caller's credential is never
        /// forwarded: the host asks as itself, so an agent cannot reach past what this deployable
        /// may already do.</summary>
        public String? ApiKey
        {
            get; set;
        }

        /// <summary>The header the key is sent under (default <c>X-Api-Key</c>, which the apiApp
        /// accepts alongside a bearer token).</summary>
        public String ApiKeyHeader { get; set; } = "X-Api-Key";

        /// <summary>
        ///   The per-request deadline on a completion this host asks for. A value below 1 is floored
        ///   at 1 second, which is a short deadline and NOT an "off": a 0 here fails every model
        ///   call after one second, and the failure names this key, which is the cheapest thing an
        ///   operator can act on. To wait longer, raise the number. There is deliberately no way to
        ///   switch the deadline off, because a call with no deadline is bounded only by
        ///   <c>Agents:Limits:MaxRunSeconds</c>, which can itself be switched off.
        ///   <para>
        ///     The default sits deliberately ABOVE the largest budget the apiApp applies to the route
        ///     it calls, for the reason <c>fallen-8-mcp</c>'s equivalent states in full: two competing
        ///     deadlines make the NEARER one report a vague local failure instead of the downstream
        ///     answer that names what to change. Here the far one is
        ///     <c>Fallen8:Chat:TimeoutSeconds</c>, whose own default is 600 because the shipped chat
        ///     backend may spend that budget waiting for a cold model to be pulled onto a worker.
        ///   </para>
        /// </summary>
        public Int32 TimeoutSeconds { get; set; } = 630;

        /// <summary>The deadline actually in force, which is <see cref="TimeoutSeconds" /> with its
        /// floor applied. It exists so the floor has one home: the startup line and the status route
        /// both printed the raw setting, which said "no deadline" for a host that gives every call
        /// one second.</summary>
        public TimeSpan Deadline => TimeSpan.FromSeconds(Math.Max(1, TimeoutSeconds));
    }
}
