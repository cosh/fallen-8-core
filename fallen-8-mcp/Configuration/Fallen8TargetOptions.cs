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
using NoSQL.GraphDB.Rest.Configuration;

namespace NoSQL.GraphDB.Mcp.Configuration
{
    /// <summary>
    ///   The downstream Fallen-8 the bridge points at (config section <c>Fallen8Target</c>). Named
    ///   <c>Fallen8Target</c> - not a bare <c>F8</c> - because the repo's .NET config sections are
    ///   all <c>Fallen8:*</c> and <c>F8_*</c> already means the compose shell variables; this
    ///   section says "a remote Fallen-8 I point at" (spec 3.9).
    ///
    ///   <para>
    ///     The base URL, the credential, its header and the deadline are
    ///     <see cref="AFallen8TargetOptions" />' - one section spelling for every sidecar. What is
    ///     this server's own is below: the default deadline, which sits above a budget only its
    ///     routes have, and the lab-only TLS escape hatch the other two deliberately refuse.
    ///   </para>
    /// </summary>
    public sealed class Fallen8TargetOptions : AFallen8TargetOptions
    {
        /// <summary>
        ///   Applies this server's own default deadline of 330 seconds.
        ///   <para>
        ///     Deliberately ABOVE the longest synchronous budget the apiApp applies on a bridged
        ///     route, so the downstream error wins and the agent is told which server setting to
        ///     change. That budget is <c>Fallen8:Embedding:TimeoutSeconds</c> (300s): a bridged
        ///     <c>POST /embedding/search</c> or <c>POST /document/search</c> embeds the query text
        ///     in-request, so it can legitimately run for minutes. A shorter bound here would
        ///     pre-empt it and report the bridge's vague retryable <c>504</c> instead - the
        ///     two-competing-deadlines mistake the base class names once and this repo removed from
        ///     the chat gateway.
        ///   </para>
        ///   Exceeding it surfaces as the bridge's retryable <c>504</c> ("Fallen-8 timeout"), which
        ///   <c>Fallen8RestClient</c> already maps.
        /// </summary>
        public Fallen8TargetOptions()
            : base(330)
        {
        }

        /// <summary>Lab-only escape hatch that disables downstream TLS validation for a
        /// self-signed Fallen-8. Default false; loudly logged when on.</summary>
        public Boolean TlsInsecure { get; set; }
    }
}
