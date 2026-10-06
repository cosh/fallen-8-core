// MIT License
//
// McpTransportTest.cs
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
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        // --- launch (feature mcp-plugin-gaps, spec section 5: the dnx tool launch) -------------

        /// <summary>A launcher may put its own arguments before or after the tool's, so the flag
        /// must be recognised anywhere in <c>args</c>, in any case, and the environment selector
        /// must still work when the flag is absent.</summary>
        [DataTestMethod]
        [DataRow(new[] { "--stdio" }, "stdio")]
        [DataRow(new[] { "--verbosity", "quiet", "--stdio" }, "stdio")]
        [DataRow(new[] { "--STDIO", "--other" }, "stdio")]
        [DataRow(new String[0], "http")]
        [DataRow(new[] { "--stdio-ish" }, "http")]
        public void ResolveTransport_RecognisesTheFlagAnywhere_AndDefaultsToHttp(String[] args, String expected)
        {
            var previous = Environment.GetEnvironmentVariable("Mcp__Transport");
            try
            {
                Environment.SetEnvironmentVariable("Mcp__Transport", null);
                Assert.AreEqual(expected, McpHost.ResolveTransport(args));
            }
            finally
            {
                Environment.SetEnvironmentVariable("Mcp__Transport", previous);
            }
        }

        /// <summary>The posture line prints the transport that is RUNNING. It used to print the
        /// configured <c>Mcp:Transport</c>, so a <c>--stdio</c> launch over the shipped settings
        /// announced <c>transport=http</c> (measured on the packed tool).</summary>
        [TestMethod]
        public void StartupPosture_NamesTheResolvedTransport_NotTheConfiguredOne()
        {
            using var sink = new TestLogSink();
            var configured = new McpOptions { Transport = "http" };

            McpHost.LogStartupPosture(sink.CreateFactory().CreateLogger("posture"), "stdio", configured, new Fallen8TargetOptions());

            Assert.IsTrue(sink.Contains(Microsoft.Extensions.Logging.LogLevel.Information, "transport=stdio"),
                "the posture line names the resolved transport: " + String.Join(" | ", sink.Entries.Select(e => e.Message)));
            Assert.IsFalse(sink.Contains(Microsoft.Extensions.Logging.LogLevel.Information, "transport=http"));
        }

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
            Assert.IsNull(Refusal("127.0.0.1", "None"), "loopback + anonymous is fine");
            Assert.IsNotNull(Refusal("0.0.0.0", "None"), "non-loopback + anonymous is refused");
            Assert.IsNull(Refusal("0.0.0.0", "None", accept: true), "the explicit override permits anonymous remote");

            // A non-loopback bind needs AllowRemoteAccess even WITH auth (the live second catch).
            Assert.IsNotNull(Refusal("0.0.0.0", "StaticToken"), "non-loopback + token but AllowRemoteAccess=false is refused");
            Assert.IsNull(Refusal("0.0.0.0", "StaticToken", allowRemote: true), "non-loopback + token + AllowRemoteAccess is fine");
            Assert.IsNull(Refusal("127.0.0.1", "StaticToken"), "loopback + token is fine");

            // StaticToken mode with an EMPTY token fails closed regardless of bind.
            Assert.IsNotNull(TransportSecurity.EvaluateStartupRefusal(new McpOptions
            {
                Security = new McpSecurityOptions { BindAddress = "127.0.0.1" },
                Auth = new McpAuthOptions { Mode = "StaticToken", StaticToken = "" },
            }), "StaticToken mode with no token is refused (credential-less by mistake)");

            // OAuth mode with no audience fails closed (audience binding is mandatory).
            Assert.IsNotNull(TransportSecurity.EvaluateStartupRefusal(new McpOptions
            {
                Security = new McpSecurityOptions { BindAddress = "127.0.0.1" },
                Auth = new McpAuthOptions { Mode = "OAuth", Issuer = "https://issuer", Audience = "" },
            }), "OAuth mode with no audience is refused (no audience binding)");
            Assert.IsNull(TransportSecurity.EvaluateStartupRefusal(new McpOptions
            {
                Security = new McpSecurityOptions { BindAddress = "127.0.0.1" },
                Auth = new McpAuthOptions { Mode = "OAuth", Issuer = "https://issuer", Audience = "https://mcp/resource" },
            }), "OAuth with an audience configured is fine");
        }

        private static String Refusal(String bind, String authMode, Boolean accept = false, Boolean allowRemote = false)
        {
            return TransportSecurity.EvaluateStartupRefusal(new McpOptions
            {
                Security = new McpSecurityOptions { BindAddress = bind, AcceptAnonymousRemote = accept, AllowRemoteAccess = allowRemote },
                Auth = new McpAuthOptions { Mode = authMode, StaticToken = authMode == "StaticToken" ? "a-real-token" : null },
            });
        }

        // --- hosted middleware --------------------------------------------------------------

        private sealed class McpFactory : WebApplicationFactory<NoSQL.GraphDB.Mcp.Program>
        {
            private readonly Dictionary<String, String> _settings;
            private readonly HttpMessageHandler _target;

            /// <param name="target">When given, the Fallen-8 the bridge talks to is this handler
            /// instead of the configured URL, so a readiness test decides what the target does.</param>
            public McpFactory(Dictionary<String, String> settings = null, HttpMessageHandler target = null)
            {
                _settings = settings ?? new();
                _target = target;
            }

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseEnvironment("Development");
                foreach (var kv in _settings)
                {
                    builder.UseSetting(kv.Key, kv.Value);
                }
                if (_target is not null)
                {
                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<IHttpClientFactory>();
                        services.AddSingleton<IHttpClientFactory>(
                            new McpTestSupport.SingleClientFactory(_target, new Uri("http://target.test")));
                    });
                }
            }
        }

        /// <summary>A target that never answers, honouring cancellation (the probe's deadline).</summary>
        private sealed class HangingHandler : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new InvalidOperationException("unreachable");
            }
        }

        private static McpTestSupport.LambdaHandler StatusTarget(String body, HttpStatusCode code = HttpStatusCode.OK)
        {
            return new McpTestSupport.LambdaHandler(_ => new HttpResponseMessage(code)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }

        private const String ReadyBody = "{\"usedMemory\":1,\"vertexCount\":0,\"edgeCount\":0,\"apiKeyRequired\":true,\"authenticated\":true}";

        // --- /readyz (feature mcp-plugin-gaps, spec section 9) --------------------------------------

        [TestMethod]
        public async Task Readyz_IsReady_WhenTheTargetAnswersAndAcceptedTheKey()
        {
            using var factory = new McpFactory(target: StatusTarget(ReadyBody));
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/readyz");
            var body = await response.Content.ReadAsStringAsync();

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
            StringAssert.Contains(body, "\"status\":\"ready\"");
            StringAssert.Contains(body, "\"authenticated\":true");
        }

        [TestMethod]
        public async Task Readyz_IsUnready_WhenTheTargetRejectedTheKey()
        {
            using var factory = new McpFactory(target: StatusTarget(
                "{\"usedMemory\":1,\"vertexCount\":0,\"edgeCount\":0,\"apiKeyRequired\":true,\"authenticated\":false}"));
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/readyz");
            var body = await response.Content.ReadAsStringAsync();

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode, body);
            StringAssert.Contains(body, "\"status\":\"unready\"");
            StringAssert.Contains(body, "rejected the configured one", "the reason names the credential problem");
        }

        [TestMethod]
        public async Task Readyz_IsUnready_WithTheBridgeStatus_WhenTheTargetFails()
        {
            using var factory = new McpFactory(target: StatusTarget("boom", HttpStatusCode.InternalServerError));
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/readyz");
            var body = await response.Content.ReadAsStringAsync();

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode, body);
            StringAssert.Contains(body, "500", "the bridge's status for the failed call is in the reason");
        }

        [TestMethod]
        public async Task Readyz_IsUnready_WithATimeoutReason_WhenTheTargetNeverAnswers()
        {
            using var factory = new McpFactory(
                new Dictionary<String, String> { ["Mcp:Readiness:TimeoutSeconds"] = "1" },
                target: new HangingHandler());
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/readyz");
            var body = await response.Content.ReadAsStringAsync();

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode, body);
            StringAssert.Contains(body, "within 1s", "the reason names the deadline that expired");
            StringAssert.Contains(body, "Mcp:Readiness:TimeoutSeconds", "and the setting that changes it");
        }

        [TestMethod]
        public async Task Readyz_StaysOutsideBearerOriginAndRateLimit_LikeHealthz()
        {
            using var factory = new McpFactory(new Dictionary<String, String>
            {
                ["Mcp:Auth:Mode"] = "StaticToken",
                ["Mcp:Auth:StaticToken"] = "s3cret-token",
                ["Mcp:Security:RateLimit:PermitPerWindow"] = "1",
                ["Mcp:Security:RateLimit:WindowSeconds"] = "60",
            }, target: StatusTarget(ReadyBody));
            using var client = factory.CreateClient();

            for (var i = 0; i < 3; i++)
            {
                var request = new HttpRequestMessage(HttpMethod.Get, "/readyz");
                request.Headers.TryAddWithoutValidation("Origin", "https://evil.example.com");
                using var response = await client.SendAsync(request);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                    $"probe {i + 1}: no bearer, a foreign origin and a one-permit window must not stop a readiness probe");
            }
            for (var i = 0; i < 3; i++)
            {
                using var health = await client.GetAsync("/healthz");
                Assert.AreEqual(HttpStatusCode.OK, health.StatusCode, $"liveness probe {i + 1} is outside the rate limiter too");
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

            // Counted on the MCP endpoint, which is what the limiter protects. It used to be counted
            // on /healthz, which the probes' exemption (feature mcp-plugin-gaps, spec section 9) took
            // out of the window on purpose: a probe must never read a 429 as "unhealthy".
            Assert.AreNotEqual(HttpStatusCode.TooManyRequests, (await client.SendAsync(McpPost())).StatusCode);
            Assert.AreNotEqual(HttpStatusCode.TooManyRequests, (await client.SendAsync(McpPost())).StatusCode);
            Assert.AreEqual(HttpStatusCode.TooManyRequests, (await client.SendAsync(McpPost())).StatusCode,
                "the third request in the window is throttled");
        }
    }
}
