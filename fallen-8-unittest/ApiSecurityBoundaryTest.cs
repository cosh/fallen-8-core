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
    /// a valid API key is accepted, the RCE code/plugin endpoints are 403 unless the operator enables
    /// them, open reads stay reachable, and an uploaded plugin lands in the isolated directory. The
    /// controller unit tests new up controllers directly and bypass the pipeline, so they cannot see
    /// any of this - these do.
    /// </summary>
    [TestClass]
    public class ApiSecurityBoundaryTest
    {
        private const string ApiKey = "test-secret-key";
        private string _pluginDir;

        [TestInitialize]
        public void TestInitialize()
        {
            _pluginDir = Path.Combine(Path.GetTempPath(), "f8_sec_plugins_" + Guid.NewGuid().ToString("N"));
        }

        [TestCleanup]
        public void TestCleanup()
        {
            try
            {
                if (_pluginDir != null && Directory.Exists(_pluginDir))
                {
                    Directory.Delete(_pluginDir, true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }

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

        private SecurityFactory NewHost(bool withApiKey = true, bool enableCode = false, bool enablePlugin = false)
        {
            var settings = new Dictionary<string, string>
            {
                ["Fallen8:Security:EnableDynamicCodeExecution"] = enableCode ? "true" : "false",
                ["Fallen8:Security:EnableDynamicPluginLoading"] = enablePlugin ? "true" : "false",
                ["Fallen8:Security:PluginDirectory"] = _pluginDir,
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

        #endregion

        #region S2/S3/S4 - RCE opt-in gate

        [TestMethod]
        public async Task CodeEndpoints_Authenticated_GateOff_Return403()
        {
            using var factory = NewHost(enableCode: false);
            using var client = Client(factory, withKey: true);

            using var path = await client.PostAsync("/path/0/to/1",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.AreEqual(HttpStatusCode.Forbidden, path.StatusCode,
                "Authenticated POST /path with dynamic code disabled must be 403 (gate closed).");

            using var subgraph = await client.PutAsync("/subgraph",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.AreEqual(HttpStatusCode.Forbidden, subgraph.StatusCode,
                "Authenticated PUT /subgraph with dynamic code disabled must be 403 (gate closed).");
        }

        [TestMethod]
        public async Task CodeEndpoint_Authenticated_GateOn_ReachesTheActionNot403()
        {
            using var factory = NewHost(enableCode: true);
            using var client = Client(factory, withKey: true);

            // Gate open: the request reaches the action (which may 200/400 depending on the body), but
            // must not be 401 (authenticated) or 403 (gate open). That the action runs at all is the
            // proof the code path is reachable only when authenticated AND enabled.
            using var path = await client.PostAsync("/path/0/to/1",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.AreNotEqual(HttpStatusCode.Forbidden, path.StatusCode, "With the gate open, POST /path must not be 403.");
            Assert.AreNotEqual(HttpStatusCode.Unauthorized, path.StatusCode, "With a valid key, POST /path must not be 401.");
        }

        [TestMethod]
        public async Task PluginUpload_GateOff_Returns403_AndWritesNothing()
        {
            using var factory = NewHost(enablePlugin: false);
            using var client = Client(factory, withKey: true);

            using var content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 });
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            using var response = await client.PutAsync("/plugin", content);

            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode,
                "Authenticated PUT /plugin with plugin loading disabled must be 403.");
            Assert.IsFalse(Directory.Exists(_pluginDir) && Directory.GetFiles(_pluginDir).Length > 0,
                "A 403'd plugin upload must write nothing.");
        }

        [TestMethod]
        public async Task PluginUpload_GateOn_IsNotBlockedByTheGate()
        {
            using var factory = NewHost(enablePlugin: true);
            using var client = Client(factory, withKey: true);

            using var content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 });
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            using var response = await client.PutAsync("/plugin", content);

            Assert.AreNotEqual(HttpStatusCode.Forbidden, response.StatusCode, "With plugin loading enabled the upload must not be 403.");
            Assert.AreNotEqual(HttpStatusCode.Unauthorized, response.StatusCode, "With a valid key the upload must not be 401.");
        }

        /// <summary>
        /// Verifies the S4 isolation directly at the controller (the HTTP `[FromBody] Stream` binding
        /// is orthogonal): an upload is written to the configured plugin directory, never
        /// AppContext.BaseDirectory.
        /// </summary>
        [TestMethod]
        public void PluginUpload_WritesIntoTheConfiguredIsolatedDirectory()
        {
            var loggerFactory = TestLoggerFactory.Create();
            using var engine = new NoSQL.GraphDB.Core.Fallen8(loggerFactory);
            var options = Microsoft.Extensions.Options.Options.Create(new NoSQL.GraphDB.App.Configuration.Fallen8SecurityOptions
            {
                PluginDirectory = _pluginDir,
                EnableDynamicPluginLoading = true,
            });
            var controller = new NoSQL.GraphDB.App.Controllers.AdminController(
                loggerFactory.CreateLogger<NoSQL.GraphDB.App.Controllers.AdminController>(), engine, options,
                new NoSQL.GraphDB.App.Services.SaveGameRegistry(
                    Microsoft.Extensions.Options.Options.Create(new NoSQL.GraphDB.App.Configuration.Fallen8MetadataOptions
                    {
                        Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "f8_meta_" + System.Guid.NewGuid().ToString("N"))
                    }),
                    loggerFactory.CreateLogger<NoSQL.GraphDB.App.Services.SaveGameRegistry>()));

            var before = Directory.GetFiles(AppContext.BaseDirectory, "*.dll").Length;
            using (var dll = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 }))
            {
                controller.UploadPlugin(dll);
            }

            Assert.IsTrue(Directory.Exists(_pluginDir), "The isolated plugin directory must be created.");
            Assert.AreEqual(1, Directory.GetFiles(_pluginDir, "*.dll").Length,
                "The uploaded DLL must be written into the configured isolated plugin directory.");
            Assert.AreEqual(before, Directory.GetFiles(AppContext.BaseDirectory, "*.dll").Length,
                "No plugin DLL may be written next to the server binaries (AppContext.BaseDirectory).");
        }

        #endregion
    }
}
