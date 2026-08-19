// MIT License
//
// ConfigWriteEndpointTest.cs
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
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App;
using NoSQL.GraphDB.App.Configuration;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// PATCH /config (feature writable-instance-config phase 3).
    ///
    /// The authorization cases are hand-written rather than assumed, because the repository's security
    /// boundary test is hand-picked spot checks and not a route sweep: a new write route gets ZERO
    /// automatic pipeline-auth coverage, so a green suite says nothing about it. Two independent
    /// operator acts are required, and the asymmetry matters: every other capability policy adds
    /// RequireAuthenticatedUser only when a key is configured, so copying that shape here would have
    /// made configuration anonymously writable on the default deployment.
    /// </summary>
    [TestClass]
    public class ConfigWriteEndpointTest
    {
        private const String Key = "test-key-abc";

        private String _metadata;

        [TestInitialize]
        public void CreateMetadataDirectory()
        {
            _metadata = Path.Combine(Path.GetTempPath(), "f8-config-write-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_metadata);
        }

        [TestCleanup]
        public void RemoveMetadataDirectory()
        {
            try
            {
                if (_metadata != null && Directory.Exists(_metadata))
                {
                    Directory.Delete(_metadata, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }

        private WebApplicationFactory<Program> CreateFactory(Boolean apiKey = true, Boolean capability = true,
            String metadataDirectory = null, IDictionary<String, String> extra = null)
        {
            return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Fallen8:Durability:Volatile", "true");
                if (metadataDirectory != null)
                {
                    builder.UseSetting("Fallen8:Metadata:Directory", metadataDirectory);
                }

                if (apiKey)
                {
                    builder.UseSetting("Fallen8:Security:ApiKey", Key);
                }

                if (capability)
                {
                    builder.UseSetting("Fallen8:Security:EnableConfigurationWrite", "true");
                }

                if (extra != null)
                {
                    foreach (var pair in extra)
                    {
                        builder.UseSetting(pair.Key, pair.Value);
                    }
                }
            });
        }

        private static HttpClient Authenticated(WebApplicationFactory<Program> factory)
        {
            var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Api-Key", Key);
            return client;
        }

        private static Task<HttpResponseMessage> Patch(HttpClient client, params (String Key, String Value)[] settings)
        {
            var body = new Dictionary<String, String>();
            foreach (var setting in settings)
            {
                body[setting.Key] = setting.Value;
            }

            return client.PatchAsJsonAsync("/config", new { settings = body });
        }

        private static async Task<JsonElement> Json(HttpResponseMessage response)
        {
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        }

        #region the five authorization cases

        [TestMethod]
        public async Task Anonymous_WithAKeyConfigured_Is401()
        {
            using var factory = CreateFactory(metadataDirectory: _metadata);
            using var client = factory.CreateClient();

            using var response = await Patch(client, ("Fallen8:Plugins:MaxCount", "128"));

            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [TestMethod]
        public async Task AValidKey_WithTheCapabilityOff_Is403()
        {
            using var factory = CreateFactory(capability: false, metadataDirectory: _metadata);
            using var client = Authenticated(factory);

            using var response = await Patch(client, ("Fallen8:Plugins:MaxCount", "128"));

            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode,
                "the capability is the second of two independent operator acts");
        }

        /// <summary>
        ///   The asymmetry that matters: the capability alone must not be enough. Every other capability
        ///   policy requires authentication only when a key is configured, so a symmetric policy here
        ///   would have made configuration anonymously writable on the DEFAULT deployment, which is
        ///   keyless. Anonymous code execution is already possible there, but it is per request; a
        ///   configuration write persists a posture change across the restart.
        /// </summary>
        [TestMethod]
        public async Task TheCapabilityOn_WithNoApiKeyConfigured_Is403()
        {
            using var factory = CreateFactory(apiKey: false, metadataDirectory: _metadata);
            using var client = factory.CreateClient();

            using var response = await Patch(client, ("Fallen8:Plugins:MaxCount", "128"));

            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode,
                "no API key means no configuration write, ever, however the capability is set");

            Assert.IsFalse(File.Exists(Path.Combine(_metadata, Fallen8ConfigOverridesSource.FileName)),
                "and nothing was persisted");
        }

        [TestMethod]
        public async Task AnEnvironmentDeclaredKey_Is409_AndWritesNothing()
        {
            const String Variable = "Fallen8__Plugins__MaxCount";
            try
            {
                Environment.SetEnvironmentVariable(Variable, "256");
                using var factory = CreateFactory(metadataDirectory: _metadata);
                using var client = Authenticated(factory);

                using var response = await Patch(client,
                    ("Fallen8:Plugins:MaxCount", "128"),
                    ("Fallen8:StoredQueries:MaxCount", "300"));

                Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
                Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);

                var detail = (await Json(response)).GetProperty("detail").GetString();
                StringAssert.Contains(detail, "Fallen8__Plugins__MaxCount",
                    "the refusal names the exact variable an operator has to remove");

                Assert.IsFalse(File.Exists(Path.Combine(_metadata, Fallen8ConfigOverridesSource.FileName)),
                    "the whole batch is refused, so the innocent key in it was not written either");
            }
            finally
            {
                Environment.SetEnvironmentVariable(Variable, null);
            }
        }

        [TestMethod]
        public async Task ANonCataloguedOrNeverWritableKey_Is400_AndWritesNothing()
        {
            using var factory = CreateFactory(metadataDirectory: _metadata);
            using var client = Authenticated(factory);

            using var unknown = await Patch(client, ("Fallen8:NoSuch:Key", "1"));
            Assert.AreEqual(HttpStatusCode.BadRequest, unknown.StatusCode);

            using var excluded = await Patch(client,
                ("Fallen8:Security:ApiKey", "hijacked"),
                ("Fallen8:StoredQueries:MaxCount", "300"));
            Assert.AreEqual(HttpStatusCode.BadRequest, excluded.StatusCode);
            var detail = (await Json(excluded)).GetProperty("detail").GetString();
            StringAssert.Contains(detail, "R1", "the refusal names the rule that excludes the key");

            Assert.IsFalse(File.Exists(Path.Combine(_metadata, Fallen8ConfigOverridesSource.FileName)),
                "a batch containing one refused key stores none of it");
        }

        #endregion

        #region what a write actually does

        [TestMethod]
        public async Task AWrite_PersistsAndReportsTheEffectiveValueAndThePendingRestart()
        {
            using var factory = CreateFactory(metadataDirectory: _metadata);
            using var client = Authenticated(factory);

            using var response = await Patch(client, ("Fallen8:Plugins:MaxCount", "128"));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                "a restart-tier write is a 200 that persists: never a 202, never an error");

            var body = await Json(response);
            var result = body.GetProperty("results")[0];
            Assert.AreEqual("Fallen8:Plugins:MaxCount", result.GetProperty("key").GetString());
            Assert.AreEqual("128", result.GetProperty("value").GetString());
            Assert.AreEqual("restart", result.GetProperty("applyMode").GetString(),
                "the promise is honest: nothing changed in this process");
            Assert.IsTrue(result.GetProperty("restartPending").GetBoolean());
            Assert.IsFalse(result.GetProperty("coerced").GetBoolean());

            Assert.AreEqual(1, body.GetProperty("pendingRestart").GetArrayLength());
            var pending = body.GetProperty("pendingRestart")[0];
            Assert.AreEqual("64", pending.GetProperty("runningValue").GetString(),
                "the running value is what this process started with");
            Assert.AreEqual("128", pending.GetProperty("pendingValue").GetString());

            // Persisted, and visible on the read surface.
            Assert.IsTrue(File.Exists(Path.Combine(_metadata, Fallen8ConfigOverridesSource.FileName)));
            using var read = await client.GetAsync("/config");
            var config = await Json(read);
            Assert.AreEqual(1, config.GetProperty("pendingRestart").GetArrayLength());
        }

        /// <summary>
        ///   A null value clears the override and restores the layer below, which is the undo this surface
        ///   ships instead of history or versioning.
        /// </summary>
        [TestMethod]
        public async Task ANullValue_ClearsTheOverrideAndRestoresTheLayerBelow()
        {
            using var factory = CreateFactory(metadataDirectory: _metadata);
            using var client = Authenticated(factory);

            using var written = await Patch(client, ("Fallen8:Plugins:MaxCount", "128"));
            Assert.AreEqual(HttpStatusCode.OK, written.StatusCode);

            using var cleared = await client.PatchAsJsonAsync("/config",
                new Dictionary<String, Object>
                {
                    ["settings"] = new Dictionary<String, String> { ["Fallen8:Plugins:MaxCount"] = null }
                });
            Assert.AreEqual(HttpStatusCode.OK, cleared.StatusCode);

            var result = (await Json(cleared)).GetProperty("results")[0];
            Assert.IsTrue(result.GetProperty("cleared").GetBoolean());
            Assert.AreEqual("64", result.GetProperty("value").GetString(),
                "clearing restores the value below, which here is the class default");
            Assert.IsFalse(result.GetProperty("restartPending").GetBoolean(),
                "and it matches what the process booted with again, so nothing is pending");
        }

        [TestMethod]
        public async Task AnOutOfDomainValue_Is400_NamingTheBound()
        {
            using var factory = CreateFactory(metadataDirectory: _metadata);
            using var client = Authenticated(factory);

            using var tooSmall = await Patch(client, ("Fallen8:Plugins:MaxCount", "0"));
            Assert.AreEqual(HttpStatusCode.BadRequest, tooSmall.StatusCode);
            StringAssert.Contains((await Json(tooSmall)).GetProperty("detail").GetString(), "at least 1");

            using var notANumber = await Patch(client, ("Fallen8:Plugins:MaxCount", "many"));
            Assert.AreEqual(HttpStatusCode.BadRequest, notANumber.StatusCode);

            using var tooLarge = await Patch(client, ("Fallen8:Chat:TimeoutSeconds", "99999999"));
            Assert.AreEqual(HttpStatusCode.BadRequest, tooLarge.StatusCode,
                "a seconds value above the timer ceiling would throw at the next boot");
        }

        /// <summary>
        ///   The case trial-binding cannot catch and only the catalog's allowed-value set can: a free-form
        ///   string the runtime exact-matches and then throws on, whose throw is cached and surfaces as a
        ///   permanent 503.
        /// </summary>
        [TestMethod]
        public async Task AnUnsupportedEnumValue_Is400_BeforeItCanLatchA503()
        {
            using var factory = CreateFactory(metadataDirectory: _metadata);
            using var client = Authenticated(factory);

            using var wrongCase = await Patch(client, ("Fallen8:Chat:Backend", "ollama"));
            Assert.AreEqual(HttpStatusCode.BadRequest, wrongCase.StatusCode,
                "the runtime matches ordinally, so a case variant would be stored and then refused");
            StringAssert.Contains((await Json(wrongCase)).GetProperty("detail").GetString(), "Ollama");

            using var accepted = await Patch(client, ("Fallen8:Chat:Backend", "Ollama"));
            Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode);
        }

        /// <summary>
        ///   With no metadata directory configured there is nowhere for a write to survive a restart, so
        ///   the write is refused with an explanation instead of appearing to succeed and vanishing.
        /// </summary>
        [TestMethod]
        public async Task WithNoMetadataDirectory_AWriteIsRefusedWithAnExplanation()
        {
            using var factory = CreateFactory();
            using var client = Authenticated(factory);

            using var response = await Patch(client, ("Fallen8:Plugins:MaxCount", "128"));

            Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
            StringAssert.Contains((await Json(response)).GetProperty("detail").GetString(),
                "Fallen8:Metadata:Directory", "the refusal names the setting to configure");
        }

        /// <summary>
        ///   A value an options class clamps in its setter comes back as what it BECAME, so an operator
        ///   sees "stored, adjusted" instead of a silent difference between what they typed and what runs.
        /// </summary>
        [TestMethod]
        public async Task ACoercedValue_IsReportedAsCoerced()
        {
            using var factory = CreateFactory(metadataDirectory: _metadata);
            using var client = Authenticated(factory);

            // TracingSamplingRatio clamps to [0, 1] in its setter, and the catalog bounds it the same way,
            // so the domain check refuses out-of-range values before any clamp is needed.
            using var refused = await Patch(client, ("Fallen8:Observability:TracingSamplingRatio", "2.5"));
            Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode,
                "the catalog's bounds refuse it rather than letting the setter silently clamp it");

            using var accepted = await Patch(client, ("Fallen8:Observability:TracingSamplingRatio", "0.25"));
            Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode);
            var result = (await Json(accepted)).GetProperty("results")[0];
            Assert.AreEqual("0.25", result.GetProperty("value").GetString());
            Assert.IsFalse(result.GetProperty("coerced").GetBoolean());
        }

        #endregion
    }
}
