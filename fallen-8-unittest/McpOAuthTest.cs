// MIT License
//
// McpOAuthTest.cs
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
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Mcp.Bridge;
using NoSQL.GraphDB.Mcp.Configuration;
using NoSQL.GraphDB.Mcp.Hosting;
using NoSQL.GraphDB.Mcp.Tools;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Phase 4 OAuth 2.1 resource server (feature mcp-server §3.8): JWT validation with
    ///   mandatory audience binding, the RFC 9728 protected-resource metadata + challenge, the
    ///   FAIL-CLOSED scope→tier intersection, and the no-token-passthrough guarantee.
    /// </summary>
    [TestClass]
    public class McpOAuthTest
    {
        private const String SigningKey = "test-signing-key-that-is-comfortably-longer-than-32-bytes";
        private const String Issuer = "https://issuer.test/";
        private const String Audience = "https://mcp.example/resource";

        private static String Mint(String issuer, String audience, String scope, Boolean expired = false)
        {
            var credentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256);
            var now = DateTime.UtcNow;
            var token = new JwtSecurityToken(
                issuer: issuer,
                audience: audience,
                claims: new[] { new Claim("scope", scope) },
                notBefore: now.AddMinutes(-5),
                expires: expired ? now.AddMinutes(-1) : now.AddMinutes(30),
                signingCredentials: credentials);
            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        // --- hosted resource server ---------------------------------------------------------

        private sealed class OAuthFactory : WebApplicationFactory<NoSQL.GraphDB.Mcp.Program>
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseEnvironment("Development");
                builder.UseSetting("Mcp:Auth:Mode", "OAuth");
                builder.UseSetting("Mcp:Auth:Issuer", Issuer);
                builder.UseSetting("Mcp:Auth:Audience", Audience);
                builder.UseSetting("Mcp:Auth:SigningKey", SigningKey);
                builder.UseSetting("Mcp:Tools:EnableWrite", "true");
            }
        }

        private static HttpRequestMessage McpPost(String token)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/")
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            if (token is not null)
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            }
            return request;
        }

        [TestMethod]
        public async Task ProtectedResourceMetadata_IsServedAnonymously()
        {
            using var factory = new OAuthFactory();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/.well-known/oauth-protected-resource");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

            Assert.AreEqual(Audience, doc.GetProperty("resource").GetString());
            Assert.AreEqual(Issuer, doc.GetProperty("authorization_servers")[0].GetString());
            var scopes = doc.GetProperty("scopes_supported").EnumerateArray().Select(s => s.GetString()).ToHashSet();
            Assert.IsTrue(scopes.Contains("f8:write"), "the metadata advertises the tier scopes");
        }

        [TestMethod]
        public async Task NoToken_Challenges401WithResourceMetadataPointer()
        {
            using var factory = new OAuthFactory();
            using var client = factory.CreateClient();

            using var response = await client.SendAsync(McpPost(token: null));
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            StringAssert.Contains(response.Headers.WwwAuthenticate.ToString(), "resource_metadata",
                "the 401 points the client at the protected-resource metadata (RFC 9728)");
        }

        [TestMethod]
        public async Task WrongAudience_Issuer_Expiry_AreRejected_ValidAccepted()
        {
            using var factory = new OAuthFactory();
            using var client = factory.CreateClient();

            using (var badAud = await client.SendAsync(McpPost(Mint(Issuer, "https://other/resource", "f8:read"))))
            {
                Assert.AreEqual(HttpStatusCode.Unauthorized, badAud.StatusCode, "audience binding is mandatory");
            }
            using (var badIss = await client.SendAsync(McpPost(Mint("https://evil.test/", Audience, "f8:read"))))
            {
                Assert.AreEqual(HttpStatusCode.Unauthorized, badIss.StatusCode, "a wrong issuer is rejected");
            }
            using (var expired = await client.SendAsync(McpPost(Mint(Issuer, Audience, "f8:read", expired: true))))
            {
                Assert.AreEqual(HttpStatusCode.Unauthorized, expired.StatusCode, "an expired token is rejected");
            }
            using (var valid = await client.SendAsync(McpPost(Mint(Issuer, Audience, "f8:read"))))
            {
                Assert.AreNotEqual(HttpStatusCode.Unauthorized, valid.StatusCode, "a valid audience-bound token passes auth");
            }
        }

        // --- fail-closed scope → tier (catalog level) ---------------------------------------

        private static IHttpContextAccessor Caller(String scope)
        {
            var context = new DefaultHttpContext();
            if (scope is not null)
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("scope", scope) }, "test"));
            }
            return new HttpContextAccessor { HttpContext = context };
        }

        private static ToolCatalog OAuthCatalog(McpToolsOptions serverFlags, String callerScope)
        {
            var bridge = McpTestSupport.Bridge(new McpTestSupport.LambdaHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
            var options = new McpOptions { Auth = new McpAuthOptions { Mode = "OAuth" }, Tools = serverFlags };
            return McpTestSupport.Catalog(options, McpTestSupport.AllTools(bridge), Caller(callerScope));
        }

        [TestMethod]
        public void Scope_GrantsTierOnlyWhenScopeAndServerFlagBothPresent()
        {
            // Server allows write; caller holds f8:write → write tools visible.
            var granted = OAuthCatalog(new McpToolsOptions { EnableWrite = true }, "f8:read f8:write")
                .ListTools().Select(t => t.Name).ToHashSet();
            Assert.IsTrue(granted.Contains("f8_mutate"), "write scope + server flag → write tools");

            // Server allows write; caller lacks f8:write → fail-closed, hidden.
            var noScope = OAuthCatalog(new McpToolsOptions { EnableWrite = true }, "f8:read")
                .ListTools().Select(t => t.Name).ToHashSet();
            Assert.IsFalse(noScope.Contains("f8_mutate"), "no write scope → write tools hidden even when the server flag is on");
        }

        [TestMethod]
        public void Scope_CannotEnableATierTheOperatorDisabled()
        {
            // Server has write OFF; a caller scope must not be able to unlock it.
            var names = OAuthCatalog(new McpToolsOptions { EnableWrite = false }, "f8:read f8:write f8:admin")
                .ListTools().Select(t => t.Name).ToHashSet();
            Assert.IsFalse(names.Contains("f8_mutate"), "a scope never enables a server-disabled tier (intersection)");
        }

        [TestMethod]
        public async Task Scope_CallOnUngrantedTier_IsRejected()
        {
            var result = await OAuthCatalog(new McpToolsOptions { EnableWrite = true }, "f8:read")
                .CallAsync("f8_mutate", McpTestSupport.Args("{\"op\":\"create_vertex\"}"), CancellationToken.None);
            Assert.IsTrue(result.IsError, "calling a tool whose scope the caller lacks is rejected");
        }

        // --- no token passthrough -----------------------------------------------------------

        [TestMethod]
        public async Task Bridge_CarriesTheServerApiKey_NeverACallerToken()
        {
            HttpRequestMessage captured = null;
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<String, String>
            {
                ["Fallen8Target:BaseUrl"] = "http://downstream.local",
                ["Fallen8Target:ApiKey"] = "server-downstream-key",
                ["Fallen8Target:ApiKeyHeader"] = "X-Api-Key",
            }).Build();

            var services = new ServiceCollection();
            McpHost.AddFallen8Mcp(services, config, stdio: true);
            // Capture what the bridge actually sends downstream.
            services.AddHttpClient(Fallen8RestClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() =>
                new McpTestSupport.LambdaHandler(request =>
                {
                    captured = request;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
                }));

            using var provider = services.BuildServiceProvider();
            var bridge = provider.GetRequiredService<Fallen8RestClient>();
            await bridge.GetStatusAsync(null, CancellationToken.None);

            Assert.IsNotNull(captured);
            Assert.IsTrue(captured.Headers.TryGetValues("X-Api-Key", out var key) && key.First() == "server-downstream-key",
                "the downstream request carries the server's own API key");
            Assert.IsFalse(captured.Headers.Contains("Authorization"),
                "a caller's bearer token is never forwarded to Fallen-8");
        }
    }
}
