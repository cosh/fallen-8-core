// MIT License
//
// McpOptions.cs
//
// Copyright (c) 2026 Henning Rauch
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

namespace NoSQL.GraphDB.Mcp.Configuration
{
    /// <summary>
    ///   The MCP server's own behaviour (config section <c>Mcp</c>). The downstream Fallen-8
    ///   connection lives in the separate <see cref="Fallen8TargetOptions"/> (<c>Fallen8Target</c>);
    ///   the split, and why the prefix is <c>Mcp</c>/<c>Fallen8Target</c> rather than a bare
    ///   <c>F8</c>, is the feature spec §3.7/§3.9.
    /// </summary>
    public sealed class McpOptions
    {
        public const String SectionName = "Mcp";

        /// <summary>"http" (Streamable HTTP, default) or "stdio" (local dev).</summary>
        public String Transport { get; set; } = "http";

        /// <summary>The Streamable-HTTP listen port (ignored under stdio).</summary>
        public Int32 Port { get; set; } = 8090;

        public McpSecurityOptions Security { get; set; } = new();

        public McpToolsOptions Tools { get; set; } = new();

        public McpAuthOptions Auth { get; set; } = new();

        public Boolean IsStdio => String.Equals(Transport, "stdio", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///   Transport hardening posture. Two deliberately-separate concepts (spec §3.3): where
    ///   Kestrel binds, and whether remote callers are accepted at all. Loopback bind is the
    ///   safe default.
    /// </summary>
    public sealed class McpSecurityOptions
    {
        /// <summary>The address Kestrel binds. Loopback by default; a container must set
        /// <c>0.0.0.0</c> to be reachable.</summary>
        public String BindAddress { get; set; } = "127.0.0.1";

        /// <summary>Whether the server accepts remote (non-loopback) callers at all.</summary>
        public Boolean AllowRemoteAccess { get; set; }

        /// <summary>Explicit, loudly-logged override that lets the server start remote+anonymous
        /// (otherwise a fail-closed startup refusal — spec §3.3).</summary>
        public Boolean AcceptAnonymousRemote { get; set; }

        /// <summary>Allowed <c>Origin</c> values (DNS-rebinding protection). A missing/empty
        /// Origin is allowed (non-browser clients); loopback origins are allowed by default.</summary>
        public IList<String> Origins { get; set; } = new List<String>();

        /// <summary>Fixed-window request throttle for the HTTP transport (right-sized backpressure
        /// for the single-process downstream against a looping agent).</summary>
        public McpRateLimitOptions RateLimit { get; set; } = new();
    }

    /// <summary>A lightweight fixed-window limiter (spec §3.3).</summary>
    public sealed class McpRateLimitOptions
    {
        /// <summary>Requests permitted per window (0 disables the limiter).</summary>
        public Int32 PermitPerWindow { get; set; } = 600;

        public Int32 WindowSeconds { get; set; } = 60;

        public Boolean Enabled => PermitPerWindow > 0;
    }

    /// <summary>Opt-in tool tiers (spec §3.6). All default off (least privilege).</summary>
    public sealed class McpToolsOptions
    {
        public Boolean EnableWrite { get; set; }

        public Boolean EnableAdmin { get; set; }

        /// <summary>The <c>code</c> capability: widens f8_paths/f8_subgraph with inline C#
        /// fragment parameters. Effective only when the target also has
        /// <c>EnableDynamicCodeExecution</c> (surfaced as its own 403 — spec §3.6).</summary>
        public Boolean EnableCode { get; set; }
    }

    /// <summary>Caller authentication (spec §3.8). Phases are additive, selected by mode.</summary>
    public sealed class McpAuthOptions
    {
        /// <summary>"None" (default), "StaticToken", or "OAuth".</summary>
        public String Mode { get; set; } = "None";

        /// <summary>Phase B bearer (env/user-secrets only; never checked in).</summary>
        public String? StaticToken { get; set; }

        /// <summary>Phase C: the external authorization server's issuer.</summary>
        public String? Issuer { get; set; }

        /// <summary>Phase C: this server's canonical external resource identifier (the audience
        /// the token's <c>aud</c> claim must equal).</summary>
        public String? Audience { get; set; }

        public Boolean IsAnonymous => String.Equals(Mode, "None", StringComparison.OrdinalIgnoreCase);
    }
}
