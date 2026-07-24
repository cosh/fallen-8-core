// MIT License
//
// McpTransportTest.cs
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
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Mcp.Configuration;
using NoSQL.GraphDB.Mcp.Hosting;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Phase 3 transport hardening (feature mcp-server §3.3/§3.8): the pure origin / bearer /
    ///   startup-posture decisions, and the wired HTTP middleware (origin validation, the static
    ///   bearer, the rate limiter, and the anonymous /healthz) against a hosted MCP server.
    /// </summary>
    [TestClass]
    public class McpTransportTest
    {
        // --- pure functions -----------------------------------------------------------------

        [DataTestMethod]
        [DataRow("127.0.0.1", true)]
        [DataRow("::1", true)]
        [DataRow("localhost", true)]
        [DataRow("0.0.0.0", false)]
        [DataRow("graph.example.com", false)]
        [DataRow("", false)]
        public void IsLoopbackHost_ClassifiesCorrectly(String host, Boolean expected)
        {
            Assert.AreEqual(expected, TransportSecurity.IsLoopbackHost(host));
        }

        [TestMethod]
        public void OriginValidation_AllowsMissingAndLoopback_RejectsUnlisted()
        {
            var sec = new McpSecurityOptions();
            Assert.IsTrue(TransportSecurity.IsOriginAllowed(null, sec), "a missing Origin (non-browser client) is allowed");
            Assert.IsTrue(TransportSecurity.IsOriginAllowed("", sec));
            Assert.IsTrue(TransportSecurity.IsOriginAllowed("http://localhost:3000", sec), "loopback origins are allowed by default");
            Assert.IsTrue(TransportSecurity.IsOriginAllowed("http://127.0.0.1", sec));
            Assert.IsFalse(TransportSecurity.IsOriginAllowed("https://evil.example.com", sec), "an unlisted remote origin is rejected");

            sec.Origins.Add("https://app.example.com");
            Assert.IsTrue(TransportSecurity.IsOriginAllowed("https://app.example.com", sec), "a configured origin is allowed");
        }

        [TestMethod]
        public void BearerValidation_ConstantTimeDigestCompare()
        {
            Assert.IsTrue(TransportSecurity.IsBearerValid("Bearer s3cret-token", "s3cret-token"));
            Assert.IsTrue(TransportSecurity.IsBearerValid("bearer s3cret-token", "s3cret-token"), "scheme is case-insensitive");
            Assert.IsFalse(TransportSecurity.IsBearerValid("Bearer wrong", "s3cret-token"));
            Assert.IsFalse(TransportSecurity.IsBearerValid("s3cret-token", "s3cret-token"), "must carry the Bearer scheme");
            Assert.IsFalse(TransportSecurity.IsBearerValid(null, "s3cret-token"));
            Assert.IsFalse(TransportSecurity.IsBearerValid("Bearer x", null), "no configured token → never valid");
        }

        [TestMethod]
        public void StartupPosture_IsFailClosedForAnonymousRemote()
        {
            Assert.IsNull(Refusal("127.0.0.1", "None", accept: false), "loopback + anonymous is fine");
            Assert.IsNotNull(Refusal("0.0.0.0", "None", accept: false), "non-loopback + anonymous is refused");
            Assert.IsNull(Refusal("0.0.0.0", "None", accept: true), "the explicit override permits anonymous remote");
            Assert.IsNull(Refusal("0.0.0.0", "StaticToken", accept: false), "non-loopback with a real token is fine");

            // StaticToken mode with an EMPTY token fails closed regardless of bind.
            var emptyToken = TransportSecurity.EvaluateStartupRefusal(new McpOptions
            {
                Security = new McpSecurityOptions { BindAddress = "127.0.0.1" },
                Auth = new McpAuthOptions { Mode = "StaticToken", StaticToken = "" },
            });
            Assert.IsNotNull(emptyToken, "StaticToken mode with no token is refused (credential-less by mistake)");
        }

        private static String Refusal(String bind, String authMode, Boolean accept)
        {
            return TransportSecurity.EvaluateStartupRefusal(new McpOptions
            {
                Security = new McpSecurityOptions { BindAddress = bind, AcceptAnonymousRemote = accept },
                Auth = new McpAuthOptions { Mode = authMode, StaticToken = authMode == "StaticToken" ? "a-real-token" : null },
            });
        }

        [TestMethod]
        public void CleartextAuthWarning_OnlyWhenCredentialsCouldLeak()
        {
            var loopbackAnon = new McpOptions();
            Assert.IsFalse(TransportSecurity.ShouldWarnCleartextAuth(loopbackAnon, "http", null));

            var remoteAuth = new McpOptions
            {
                Security = new McpSecurityOptions { BindAddress = "0.0.0.0" },
                Auth = new McpAuthOptions { Mode = "StaticToken", StaticToken = "t" },
            };
            Assert.IsTrue(TransportSecurity.ShouldWarnCleartextAuth(remoteAuth, "http", null), "cleartext + remote + auth warns");
            Assert.IsFalse(TransportSecurity.ShouldWarnCleartextAuth(remoteAuth, "http", "https"), "a trusted https proxy clears it");
        }

        // --- hosted middleware --------------------------------------------------------------

        private sealed class McpFactory : WebApplicationFactory<NoSQL.GraphDB.Mcp.Program>
        {
            private readonly Dictionary<String, String> _settings;

            public McpFactory(Dictionary<String, String> settings = null) => _settings = settings ?? new();

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseEnvironment("Development");
                foreach (var kv in _settings)
                {
                    builder.UseSetting(kv.Key, kv.Value);
                }
            }
        }

        private static HttpRequestMessage McpPost(String origin = null, String bearer = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/")
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            };
            if (origin is not null)
            {
                request.Headers.TryAddWithoutValidation("Origin", origin);
            }
            if (bearer is not null)
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
            }
            return request;
        }

        [TestMethod]
        public async Task Origin_UnlistedRemote_Is403_MissingAndLoopbackPass()
        {
            using var factory = new McpFactory();
            using var client = factory.CreateClient();

            using (var evil = await client.SendAsync(McpPost(origin: "https://evil.example.com")))
            {
                Assert.AreEqual(HttpStatusCode.Forbidden, evil.StatusCode, "an unlisted cross-origin request is blocked");
            }
            using (var none = await client.SendAsync(McpPost()))
            {
                Assert.AreNotEqual(HttpStatusCode.Forbidden, none.StatusCode, "a missing Origin passes the DNS-rebinding guard");
            }
            using (var loopback = await client.SendAsync(McpPost(origin: "http://localhost:5173")))
            {
                Assert.AreNotEqual(HttpStatusCode.Forbidden, loopback.StatusCode, "a loopback Origin passes");
            }
        }

        [TestMethod]
        public async Task StaticBearer_EnforcedOnMcp_HealthzStaysAnonymous()
        {
            using var factory = new McpFactory(new Dictionary<String, String>
            {
                ["Mcp:Auth:Mode"] = "StaticToken",
                ["Mcp:Auth:StaticToken"] = "s3cret-token",
            });
            using var client = factory.CreateClient();

            using (var missing = await client.SendAsync(McpPost()))
            {
                Assert.AreEqual(HttpStatusCode.Unauthorized, missing.StatusCode, "no bearer → 401");
                Assert.IsTrue(missing.Headers.WwwAuthenticate.ToString().Contains("Bearer"), "401 carries WWW-Authenticate: Bearer");
            }
            using (var wrong = await client.SendAsync(McpPost(bearer: "nope")))
            {
                Assert.AreEqual(HttpStatusCode.Unauthorized, wrong.StatusCode);
            }
            using (var right = await client.SendAsync(McpPost(bearer: "s3cret-token")))
            {
                Assert.AreNotEqual(HttpStatusCode.Unauthorized, right.StatusCode, "the correct bearer passes the auth gate");
            }
            using (var health = await client.GetAsync("/healthz"))
            {
                Assert.AreEqual(HttpStatusCode.OK, health.StatusCode, "/healthz stays anonymous for orchestrators");
            }
        }

        [TestMethod]
        public async Task RateLimiter_RejectsBeyondTheWindow()
        {
            using var factory = new McpFactory(new Dictionary<String, String>
            {
                ["Mcp:Security:RateLimit:PermitPerWindow"] = "2",
                ["Mcp:Security:RateLimit:WindowSeconds"] = "60",
            });
            using var client = factory.CreateClient();

            Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
            Assert.AreEqual(HttpStatusCode.TooManyRequests, (await client.GetAsync("/healthz")).StatusCode,
                "the third request in the window is throttled");
        }
    }
}
