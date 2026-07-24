// MIT License
//
// ApiKeyAuthenticationHandler.cs
//
// Copyright (c) 2025 Henning Rauch
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
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.App.Configuration;

namespace NoSQL.GraphDB.App.Security
{
    /// <summary>
    ///   A minimal API-key authentication handler (feature api-security-boundary): a request is
    ///   authenticated when it carries the configured header whose value matches
    ///   <see cref="Fallen8SecurityOptions.ApiKey"/> (compared in constant time). When no key is
    ///   configured the handler authenticates nobody (returns NoResult) - the server then runs
    ///   unauthenticated, so the off-by-default code/plugin gates are the only out-of-the-box
    ///   protection (the bind is whatever ASPNETCORE_URLS/Kestrel is configured to, not loopback-only).
    ///   The comparison never logs the key.
    /// </summary>
    public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        private readonly Fallen8SecurityOptions _security;

        public ApiKeyAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder,
            IOptions<Fallen8SecurityOptions> security)
            : base(options, logger, encoder)
        {
            _security = security.Value;
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var configuredKey = _security.ApiKey;
            if (String.IsNullOrEmpty(configuredKey))
            {
                // No credential configured: authenticate nobody (do not fail - a missing scheme result
                // lets an [AllowAnonymous] endpoint still serve; the off-by-default code/plugin gates
                // protect the dangerous surface).
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var headerName = String.IsNullOrWhiteSpace(_security.ApiKeyHeader) ? "X-Api-Key" : _security.ApiKeyHeader;

            String presented = null;
            if (Request.Headers.TryGetValue(headerName, out var headerValues) && headerValues.Count > 0)
            {
                presented = headerValues.ToString();
            }
            else
            {
                // Fallback: the same key as an RFC 6750-shaped bearer token
                // ("Authorization: Bearer <key>"). Today this compares against the static API key;
                // the header shape is the seam where a token validator (OIDC/JWT, e.g. AWS Cognito)
                // slots in later without changing clients (feature web-ui).
                var authorization = Request.Headers.Authorization.ToString();
                const String bearerPrefix = "Bearer ";
                if (authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    presented = authorization.Substring(bearerPrefix.Length).Trim();
                }
            }

            if (String.IsNullOrEmpty(presented))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            if (!FixedTimeEquals(presented, configuredKey))
            {
                return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
            }

            var identity = new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Name, "api-key-client") },
                Fallen8SecurityOptions.ApiKeyScheme);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }

        private static Boolean FixedTimeEquals(String a, String b)
        {
            var ba = Encoding.UTF8.GetBytes(a);
            var bb = Encoding.UTF8.GetBytes(b);
            // CryptographicOperations.FixedTimeEquals requires equal lengths; guard first (the length
            // itself is not secret) and still run the fixed-time compare on the equal-length path.
            return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
        }
    }
}
