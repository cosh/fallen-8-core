// MIT License
//
// TransportSecurity.cs
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
using System.Net;
using System.Security.Cryptography;
using System.Text;
using NoSQL.GraphDB.Mcp.Configuration;

namespace NoSQL.GraphDB.Mcp.Hosting
{
    /// <summary>
    ///   The transport-hardening primitives (spec §3.3/§3.8), factored as pure functions so the
    ///   posture, origin, and bearer decisions are directly unit-testable. Origin validation is
    ///   DNS-rebinding protection (a missing Origin is allowed — the primary MCP clients are not
    ///   browsers; a present-but-unlisted Origin is rejected). The bearer compare is over
    ///   fixed-length SHA-256 digests so neither token length nor content leaks via timing. The
    ///   startup posture is fail-closed: a non-loopback bind must not run anonymously.
    /// </summary>
    public static class TransportSecurity
    {
        /// <summary>Whether a host is loopback (127.0.0.1/::1/localhost) — the safe bind/origin.</summary>
        public static Boolean IsLoopbackHost(String? host)
        {
            if (String.IsNullOrEmpty(host))
            {
                return false;
            }
            if (String.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
        }

        /// <summary>
        ///   Origin allow decision: a missing/empty Origin passes (non-browser clients send none);
        ///   a loopback Origin passes; a configured Origin passes; anything else is rejected.
        /// </summary>
        public static Boolean IsOriginAllowed(String? origin, McpSecurityOptions security)
        {
            if (String.IsNullOrEmpty(origin))
            {
                return true;
            }

            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            {
                return false;
            }

            if (IsLoopbackHost(uri.Host))
            {
                return true;
            }

            foreach (var allowed in security.Origins)
            {
                if (String.Equals(allowed, origin, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        ///   Validates an <c>Authorization: Bearer &lt;token&gt;</c> header against the configured
        ///   static token by comparing 32-byte SHA-256 digests with a constant-time compare (no
        ///   length- or content-timing leak). Returns false for a missing/malformed header or an
        ///   unconfigured token.
        /// </summary>
        public static Boolean IsBearerValid(String? authorizationHeader, String? configuredToken)
        {
            if (String.IsNullOrEmpty(configuredToken) || String.IsNullOrEmpty(authorizationHeader))
            {
                return false;
            }

            const String prefix = "Bearer ";
            if (!authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var presented = authorizationHeader.Substring(prefix.Length).Trim();
            if (presented.Length == 0)
            {
                return false;
            }

            var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
            var configuredHash = SHA256.HashData(Encoding.UTF8.GetBytes(configuredToken));
            return CryptographicOperations.FixedTimeEquals(presentedHash, configuredHash);
        }

        /// <summary>
        ///   The fail-closed startup gate (spec §3.3): a non-loopback bind may not serve anonymous
        ///   callers. Returns a refusal message (the caller logs it and aborts) or null when the
        ///   posture is safe. The explicit <c>AcceptAnonymousRemote</c> override bypasses it, loudly.
        /// </summary>
        public static String? EvaluateStartupRefusal(McpOptions options)
        {
            // StaticToken mode with no token is credential-less by mistake — fail closed
            // regardless of bind, so an empty token can never silently 401 every request.
            if (String.Equals(options.Auth.Mode, "StaticToken", StringComparison.OrdinalIgnoreCase) &&
                String.IsNullOrEmpty(options.Auth.StaticToken))
            {
                return "Refusing to start: auth mode is 'StaticToken' but Mcp:Auth:StaticToken is empty. " +
                       "Set a strong token (compose: F8_MCP_TOKEN), or use a different auth mode.";
            }

            // OAuth without an audience cannot enforce audience binding (a token minted for another
            // resource would validate on issuer+signature alone) — fail closed (spec §3.8).
            if (options.Auth.IsOAuth && String.IsNullOrEmpty(options.Auth.Audience))
            {
                return "Refusing to start: auth mode is 'OAuth' but Mcp:Auth:Audience is empty. " +
                       "Audience binding is mandatory — set the canonical resource identifier this " +
                       "server validates the token's 'aud' claim against.";
            }

            var bindsNonLoopback = !IsLoopbackHost(options.Security.BindAddress);
            if (!bindsNonLoopback || options.Security.AcceptAnonymousRemote)
            {
                return null;
            }

            if (options.Auth.IsAnonymous)
            {
                return $"Refusing to start: bind address '{options.Security.BindAddress}' is not loopback and " +
                       "auth mode is 'None'. Set Mcp:Auth:Mode (StaticToken/OAuth), or bind loopback, or set " +
                       "Mcp:Security:AcceptAnonymousRemote=true to explicitly accept an anonymous remote surface.";
            }

            // A non-loopback bind must also be an explicit decision to accept remote callers — the
            // live second catch beyond auth (spec §3.3). AllowRemoteAccess is the operator's switch.
            if (!options.Security.AllowRemoteAccess)
            {
                return $"Refusing to start: bind address '{options.Security.BindAddress}' is not loopback but " +
                       "Mcp:Security:AllowRemoteAccess is false. Set it true to accept remote callers, or bind loopback.";
            }

            return null;
        }
    }
}
