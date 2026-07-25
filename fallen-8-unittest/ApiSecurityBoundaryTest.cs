// MIT License
//
// ApiSecurityBoundaryTest.cs
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
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Pipeline tests for the API security boundary (feature api-security-boundary), through the real
    /// ASP.NET pipeline via WebApplicationFactory: anonymous requests to protected endpoints are 401,
    /// a valid API key is accepted, source plugin registration (POST /plugins/*, feature
    /// plugin-registration) is 403 unless the operator enables it (dynamic code execution is always
    /// on), and open reads stay reachable. The
    /// controller unit tests new up controllers directly and bypass the pipeline, so they cannot see
    /// any of this - these do.
    /// </summary>
    [TestClass]
    public class ApiSecurityBoundaryTest
    {
        private const string ApiKey = "test-secret-key";

        private sealed class SecurityFactory : WebApplicationFactory<Program>
        {
            private readonly IReadOnlyDictionary<string, string> _settings;

            public SecurityFactory(IReadOnlyDictionary<string, string> settings)
            {
                _settings = settings;
            }

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                // Volatile durability so booting the host writes no checkpoint/WAL.
                builder.UseSetting("Fallen8:Durability:Volatile", "true");
                foreach (var kv in _settings)
                {
                    builder.UseSetting(kv.Key, kv.Value);
                }
            }
        }

        private SecurityFactory NewHost(bool withApiKey = true, bool enablePlugin = false)
        {
            var settings = new Dictionary<string, string>
            {
                ["Fallen8:Security:EnableDynamicPluginLoading"] = enablePlugin ? "true" : "false",
            };
            if (withApiKey)
            {
                settings["Fallen8:Security:ApiKey"] = ApiKey;
            }
            return new SecurityFactory(settings);
        }

        private static HttpClient Client(SecurityFactory factory, bool withKey)
        {
            var client = factory.CreateClient();
            if (withKey)
            {
                client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
            }
            return client;
        }

        #region S1 - authentication trust boundary

        [TestMethod]
        public async Task Anonymous_ProtectedEndpoints_Return401()
        {
            using var factory = NewHost();
            using var client = Client(factory, withKey: false);

            using var trim = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/trim"));
            Assert.AreEqual(HttpStatusCode.Unauthorized, trim.StatusCode, "Anonymous HEAD /trim (admin) must be 401.");

            using var path = await client.PostAsync("/path/0/to/1",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.AreEqual(HttpStatusCode.Unauthorized, path.StatusCode, "Anonymous POST /path (code) must be 401.");

            using var subgraph = await client.PutAsync("/subgraph",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.AreEqual(HttpStatusCode.Unauthorized, subgraph.StatusCode, "Anonymous PUT /subgraph (code) must be 401.");
        }

        [TestMethod]
        public async Task ValidApiKey_ProtectedEndpoint_IsAccepted()
        {
            using var factory = NewHost();
            using var client = Client(factory, withKey: true);

            using var trim = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/trim"));
            Assert.AreNotEqual(HttpStatusCode.Unauthorized, trim.StatusCode,
                "A request carrying the configured API key must not be 401.");
            Assert.IsTrue((int)trim.StatusCode < 400, "HEAD /trim with a valid key should succeed (2xx).");
        }

        [TestMethod]
        public async Task OpenReadEndpoint_IsReachableAnonymously()
        {
            using var factory = NewHost();
            using var client = Client(factory, withKey: false);

            using var status = await client.GetAsync("/status");
            Assert.AreEqual(HttpStatusCode.OK, status.StatusCode,
                "An [AllowAnonymous] read endpoint (/status) must stay reachable without a credential.");
        }

        /// <summary>
        /// /status doubles as the connection probe (StatusREST.ApiKeyRequired): clients must be able
        /// to tell "reachable" apart from "authorized" from the anonymous status document alone.
        /// </summary>
        [TestMethod]
        public async Task Status_WithKeyConfigured_ReportsAuthenticationState()
        {
            using var factory = NewHost();

            using (var anonymous = Client(factory, withKey: false))
            {
                var (apiKeyRequired, authenticated) = await GetStatusAuth(anonymous);
                Assert.IsTrue(apiKeyRequired, "With a key configured, /status must report apiKeyRequired.");
                Assert.IsFalse(authenticated, "A request without a credential must not report authenticated.");
            }

            using (var wrongKey = factory.CreateClient())
            {
                wrongKey.DefaultRequestHeaders.Add("X-Api-Key", "not-the-key");
                var (apiKeyRequired, authenticated) = await GetStatusAuth(wrongKey);
                Assert.IsTrue(apiKeyRequired, "apiKeyRequired reflects server configuration, not the request.");
                Assert.IsFalse(authenticated, "An INVALID key must not report authenticated.");
            }

            using (var validKey = Client(factory, withKey: true))
            {
                var (apiKeyRequired, authenticated) = await GetStatusAuth(validKey);
                Assert.IsTrue(apiKeyRequired, "apiKeyRequired reflects server configuration, not the request.");
                Assert.IsTrue(authenticated, "A valid key must report authenticated.");
            }
        }

        [TestMethod]
        public async Task Status_WithoutKeyConfigured_ReportsNoAuthRequirement()
        {
            using var factory = NewHost(withApiKey: false);
            using var client = Client(factory, withKey: false);

            var (apiKeyRequired, authenticated) = await GetStatusAuth(client);
            Assert.IsFalse(apiKeyRequired, "Without a configured key the server is open - no auth requirement.");
            Assert.IsFalse(authenticated, "The API-key handler authenticates nobody when no key is configured.");
        }

        private static async Task<(bool apiKeyRequired, bool authenticated)> GetStatusAuth(HttpClient client)
        {
            using var response = await client.GetAsync("/status");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "/status must answer 200 regardless of credential.");
            using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return (doc.RootElement.GetProperty("apiKeyRequired").GetBoolean(),
                    doc.RootElement.GetProperty("authenticated").GetBoolean());
        }

        #endregion

        #region S2/S3/S4 - RCE surface (dynamic code always on; plugin loading opt-in)

        [TestMethod]
        public async Task CodeEndpoint_Authenticated_InlineFragments_ReachTheActionNot403()
        {
            // Dynamic code execution is unconditional: an authenticated caller submitting inline
            // fragments reaches the action (200/400 by body) and is never 403. Auth still applies.
            using var factory = NewHost();
            using var client = Client(factory, withKey: true);

            using var path = await client.PostAsync("/path/0/to/1",
                new StringContent("{\"filter\":{\"vertexFilter\":\"return (v) => true;\"}}", Encoding.UTF8, "application/json"));
            Assert.AreNotEqual(HttpStatusCode.Forbidden, path.StatusCode, "Dynamic code is always on — POST /path with fragments must not be 403.");
            Assert.AreNotEqual(HttpStatusCode.Unauthorized, path.StatusCode, "With a valid key, POST /path must not be 401.");

            using var subgraph = await client.PutAsync("/subgraph",
                new StringContent("{\"name\":\"gated\",\"vertexFilter\":\"return (ge) => true;\"}", Encoding.UTF8, "application/json"));
            Assert.AreNotEqual(HttpStatusCode.Forbidden, subgraph.StatusCode, "PUT /subgraph with fragments must not be 403.");
        }

        private static StringContent FunctionRegistrationBody()
        {
            // A minimal registration body: the gate is evaluated BEFORE the source is compiled, so the
            // gate assertions do not depend on the source being valid.
            return new StringContent("{\"name\":\"x\",\"sourceCode\":\"// x\"}", Encoding.UTF8, "application/json");
        }

        [TestMethod]
        public async Task PluginRegistration_GateOff_Returns403()
        {
            using var factory = NewHost(enablePlugin: false);
            using var client = Client(factory, withKey: true);

            using var response = await client.PostAsync("/plugins/function", FunctionRegistrationBody());

            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode,
                "With source plugin registration disabled, POST /plugins/* must be 403 even for an authenticated caller.");
        }

        [TestMethod]
        public async Task PluginRegistration_GateOn_IsNotBlockedByTheGate()
        {
            using var factory = NewHost(enablePlugin: true);
            using var client = Client(factory, withKey: true);

            using var response = await client.PostAsync("/plugins/function", FunctionRegistrationBody());

            Assert.AreNotEqual(HttpStatusCode.Forbidden, response.StatusCode, "With registration enabled the request must not be 403.");
            Assert.AreNotEqual(HttpStatusCode.Unauthorized, response.StatusCode, "With a valid key the request must not be 401.");
        }

        #endregion
    }
}
