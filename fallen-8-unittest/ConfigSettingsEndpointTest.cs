// MIT License
//
// ConfigSettingsEndpointTest.cs
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
using System.Linq;
using System.Net;
using System.Net.Http;
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
    /// The configuration READ surface over the wire (feature writable-instance-config phase 2): the
    /// settings inventory and the pending-restart set on GET /config, against a real host.
    ///
    /// The withholding rule is the security-relevant one here. GET /config carries neither
    /// [Authorize] nor [AllowAnonymous], and the fallback policy that would demand a principal is
    /// installed only when an API key is configured, so on a keyless instance this response is
    /// ANONYMOUS. Every never-writable key therefore publishes its tier and reason but no value,
    /// which is what keeps sidecar URLs, model file paths and durability paths out of an
    /// unauthenticated response.
    /// </summary>
    [TestClass]
    public class ConfigSettingsEndpointTest
    {
        private String _metadata;

        [TestInitialize]
        public void CreateMetadataDirectory()
        {
            _metadata = Path.Combine(Path.GetTempPath(), "f8-config-rest-" + Guid.NewGuid().ToString("N"));
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

        private WebApplicationFactory<Program> CreateFactory(String storedOverrides = null)
        {
            if (storedOverrides != null)
            {
                File.WriteAllText(Path.Combine(_metadata, Fallen8ConfigOverridesSource.FileName), storedOverrides);
            }

            return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Fallen8:Durability:Volatile", "true");
                builder.UseSetting("Fallen8:Metadata:Directory", _metadata);
            });
        }

        private static async Task<JsonElement> ReadConfig(HttpClient client)
        {
            using var response = await client.GetAsync("/config");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            return JsonDocument.Parse(body).RootElement.Clone();
        }

        private static IEnumerable<JsonElement> Settings(JsonElement config)
        {
            return config.GetProperty("settings").EnumerateArray();
        }

        private static JsonElement Setting(JsonElement config, String key)
        {
            var match = Settings(config).FirstOrDefault(s => s.GetProperty("key").GetString() == key);
            Assert.AreNotEqual(JsonValueKind.Undefined, match.ValueKind, key + " is missing from settings[]");
            return match;
        }

        [TestMethod]
        public async Task GetConfig_PublishesEveryCataloguedKey()
        {
            using var factory = CreateFactory();
            using var client = factory.CreateClient();

            var config = await ReadConfig(client);
            var published = Settings(config).Select(s => s.GetProperty("key").GetString()).ToList();

            CollectionAssert.AreEquivalent(
                Fallen8SettingCatalog.Entries.Select(entry => entry.Key).ToList(),
                published,
                "the read surface is the live inventory, so it publishes exactly the catalogue");
        }

        /// <summary>
        ///   The withholding rule, asserted over settings[] rather than over the whole body. It has to be
        ///   scoped: the response has published <c>observability.otlpEndpoint</c> and the embedding
        ///   identity since feature instance-config, so a substring search for a never-writable value
        ///   would fail on behaviour this feature did not introduce and does not change.
        /// </summary>
        [TestMethod]
        public async Task GetConfig_WithholdsTheValueOfEveryNeverWritableKey()
        {
            using var factory = CreateFactory();
            using var client = factory.CreateClient();

            var config = await ReadConfig(client);
            var violations = new List<String>();

            foreach (var setting in Settings(config))
            {
                var key = setting.GetProperty("key").GetString();
                var writable = setting.GetProperty("tier").GetString() != "notWritable";

                if (writable)
                {
                    if (setting.TryGetProperty("valueWithheld", out _))
                    {
                        violations.Add(key + " is writable but reports a withheld value");
                    }

                    continue;
                }

                if (setting.TryGetProperty("value", out _))
                {
                    violations.Add(key + " is never writable but its value is published");
                }

                if (!setting.TryGetProperty("valueWithheld", out var withheld) || !withheld.GetBoolean())
                {
                    violations.Add(key + " withholds its value without saying so");
                }

                if (String.IsNullOrWhiteSpace(setting.GetProperty("reason").GetString()))
                {
                    violations.Add(key + " is excluded without a reason an operator can read");
                }
            }

            Assert.AreEqual(0, violations.Count, String.Join("\n", violations));
        }

        /// <summary>
        ///   The specific values the spec names: a keyless instance answers this route anonymously, so a
        ///   caller must not be able to learn the durability paths or the sidecar addresses from it.
        /// </summary>
        [TestMethod]
        public async Task GetConfig_NeverPublishesADurabilityPathOrASidecarUrlInSettings()
        {
            using var factory = CreateFactory();
            using var client = factory.CreateClient();

            var config = await ReadConfig(client);

            foreach (var key in new[]
            {
                "Fallen8:Durability:StorageDirectory", "Fallen8:Durability:WalPath",
                "Fallen8:Metadata:Directory", "Fallen8:Embedding:Onnx:ModelPath",
                "Fallen8:Nlp:Endpoint", "Fallen8:Integrations:Endpoint", "Fallen8:Security:ApiKey",
                // Nahil's URL and, above all, the credential this server PRESENTS to it:
                // this route is anonymous on a keyless instance, so a published value would hand the
                // key to whoever asked (feature nahil-backend, rule R8).
                "Fallen8:Chat:Nahil:Endpoint", "Fallen8:Chat:Nahil:ApiKey",
                "Fallen8:Embedding:Nahil:Endpoint", "Fallen8:Embedding:Nahil:ApiKey"
            })
            {
                var setting = Setting(config, key);
                Assert.IsFalse(setting.TryGetProperty("value", out _), key + " must publish no value");
                Assert.AreEqual("notWritable", setting.GetProperty("tier").GetString());
            }
        }

        [TestMethod]
        public async Task GetConfig_PublishesTierApplyModeAndSourceAsStrings()
        {
            using var factory = CreateFactory();
            using var client = factory.CreateClient();

            var config = await ReadConfig(client);
            var restartTier = Setting(config, "Fallen8:Ingestion:MaxPages");

            Assert.AreEqual("int", restartTier.GetProperty("kind").GetString(),
                "wire values are strings; an enum would publish an integer whose meaning is private to the server");
            Assert.AreEqual("restart", restartTier.GetProperty("tier").GetString());
            Assert.AreEqual("restart", restartTier.GetProperty("applyMode").GetString());
            Assert.AreEqual(1, restartTier.GetProperty("minimum").GetDouble());

            // A promoted key publishes the live tier and the narrower promise separately, so a client can
            // say "in force for new work" rather than implying everything already running changed.
            var live = Setting(config, "Fallen8:Plugins:MaxCount");
            Assert.AreEqual("live", live.GetProperty("tier").GetString());
            Assert.AreEqual("liveForNewWork", live.GetProperty("applyMode").GetString());
            Assert.IsFalse(live.GetProperty("restartPending").GetBoolean(),
                "a live key is never waiting for a restart");

            var apiKey = Setting(config, "Fallen8:Security:ApiKey");
            Assert.AreEqual("notWritable", apiKey.GetProperty("tier").GetString());
            Assert.AreEqual("never", apiKey.GetProperty("applyMode").GetString());
            Assert.AreEqual("R1", apiKey.GetProperty("rule").GetString());

            var backend = Setting(config, "Fallen8:Chat:Backend");
            Assert.AreEqual("enum", backend.GetProperty("kind").GetString());
            CollectionAssert.AreEquivalent(new[] { "Ollama", "Nahil" },
                backend.GetProperty("allowedValues").EnumerateArray().Select(v => v.GetString()).ToList());
        }

        /// <summary>
        ///   A stored override is in force at boot, so it is NOT pending: the process is already using it.
        ///   This is the case a stored-flag implementation gets wrong, and it is why the pending set is
        ///   derived from a boot snapshot instead.
        /// </summary>
        [TestMethod]
        public async Task AStoredOverrideInForceAtBoot_IsReportedAsItsSourceAndNotAsPending()
        {
            using var factory = CreateFactory(
                "{ \"version\": 1, \"settings\": { \"Fallen8:Plugins:MaxCount\": \"128\" } }");
            using var client = factory.CreateClient();

            var config = await ReadConfig(client);
            var plugins = Setting(config, "Fallen8:Plugins:MaxCount");

            Assert.AreEqual("128", plugins.GetProperty("value").GetString());
            Assert.AreEqual("override", plugins.GetProperty("source").GetString());
            Assert.IsFalse(plugins.GetProperty("restartPending").GetBoolean(),
                "the boot already applied it, so nothing is waiting for a restart");
            Assert.AreEqual(0, config.GetProperty("pendingRestart").GetArrayLength());
        }

        /// <summary>
        ///   A host setting arrives as a command-line argument, which outranks the stored layer, so the
        ///   row reports commandLine and the stored value is reported nowhere as effective.
        /// </summary>
        [TestMethod]
        public async Task AKeyDeclaredByTheHost_ReportsAnAuthoritySourceAndKeepsTheStoredValueOut()
        {
            using var factory = CreateFactory(
                "{ \"version\": 1, \"settings\": { \"Fallen8:Durability:Volatile\": \"false\" } }");
            using var client = factory.CreateClient();

            var config = await ReadConfig(client);
            var volatileSetting = Setting(config, "Fallen8:Durability:Volatile");

            // Volatile is never writable, so its value is withheld either way; what matters is that the
            // hand-edited stored value did not become the effective one.
            Assert.AreEqual("notWritable", volatileSetting.GetProperty("tier").GetString());
            Assert.AreEqual("commandLine", volatileSetting.GetProperty("source").GetString(),
                "the host declared this key, and an authority declaration is reported as such");
        }

        [TestMethod]
        public async Task TheExistingConfigFields_AreUnchanged()
        {
            using var factory = CreateFactory();
            using var client = factory.CreateClient();

            var config = await ReadConfig(client);

            Assert.IsTrue(config.TryGetProperty("semantic", out _), "the semantic block still ships");
            Assert.IsTrue(config.TryGetProperty("observability", out _), "the observability block still ships");
            Assert.IsTrue(config.TryGetProperty("apiKeyRequired", out var apiKeyRequired));
            Assert.IsFalse(apiKeyRequired.GetBoolean(), "this host configures no key");
        }

        /// <summary>
        ///   The absorbed namespace-startup fields (spec 3.2), published uncomposed: the default and the
        ///   mode are separate so a client can say "the default is skip AND the mode is overriding it"
        ///   rather than showing a composed true that makes saving skip look broken.
        /// </summary>
        [TestMethod]
        public async Task GetNamespaces_PublishesTheStartupPolicyUncomposed()
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Fallen8:Durability:Volatile", "true");
                builder.UseSetting("Fallen8:Metadata:Directory", _metadata);
                builder.UseSetting("Fallen8:Namespaces:LoadOnStartup", "false");
                builder.UseSetting("Fallen8:Namespaces:StartupLoadMode", "All");
            });
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/ns");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

            Assert.IsFalse(body.GetProperty("loadOnStartupDefault").GetBoolean(),
                "the default is published raw: composing the mode in would report true and make saving skip look broken");
            Assert.AreEqual("all", body.GetProperty("startupLoadMode").GetString(),
                "the mode is a camelCase string, not the enum's integer");
        }
    }
}
