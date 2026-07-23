// MIT License
//
// ObservabilityEndpointTest.cs
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
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Pipeline tests for the observability surfaces (feature observability): the Prometheus
    /// scrape endpoint on/off + auth matrix, health endpoints, the zero-config guarantee, and
    /// GET /statistics correctness, budget sampling and auth.
    /// </summary>
    [TestClass]
    public class ObservabilityEndpointTest
    {
        private sealed class ObservabilityFactory : WebApplicationFactory<Program>
        {
            private readonly Dictionary<String, String> _settings;

            public ObservabilityFactory(Dictionary<String, String> settings = null)
            {
                _settings = settings ?? new Dictionary<String, String>();
            }

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseSetting("Fallen8:Durability:Volatile", "true");
                foreach (var pair in _settings)
                {
                    builder.UseSetting(pair.Key, pair.Value);
                }
            }
        }

        private static Fallen8 EngineOf(ObservabilityFactory factory)
        {
            return factory.Services.GetRequiredService<NoSQL.GraphDB.App.Namespaces.Fallen8Namespaces>().Default.Engine;
        }

        private static Int32 SeedVertex(ObservabilityFactory factory, string label = "person",
            Dictionary<string, object> properties = null)
        {
            var tx = new CreateVertexTransaction
            {
                Definition = new VertexDefinition { CreationDate = 1u, Label = label, Properties = properties }
            };
            EngineOf(factory).EnqueueTransaction(tx).WaitUntilFinished();
            return tx.VertexCreated.Id;
        }

        private static void SeedEdge(ObservabilityFactory factory, Int32 source, Int32 target,
            string edgePropertyId = "link", string label = null)
        {
            var tx = new CreateEdgeTransaction
            {
                Definition = new EdgeDefinition
                {
                    SourceVertexId = source,
                    TargetVertexId = target,
                    EdgePropertyId = edgePropertyId,
                    CreationDate = 1u,
                    Label = label
                }
            };
            EngineOf(factory).EnqueueTransaction(tx).WaitUntilFinished();
        }

        #region /metrics

        [TestMethod]
        public async Task ZeroConfig_NoMetricsEndpoint_AndNoOtelServices()
        {
            using var factory = new ObservabilityFactory();
            using var client = factory.CreateClient();

            // No scrape endpoint is mapped. (When a built SPA is present its fallback serves the
            // app shell for unmatched paths, so the honest assertion is "not Prometheus output",
            // covering both the pure-API 404 and the SPA-fallback deployments.)
            using var response = await client.GetAsync("/metrics");
            var body = await response.Content.ReadAsStringAsync();
            Assert.IsFalse(body.Contains("fallen8_"),
                "a default configuration exposes no scrape endpoint (got: " + response.StatusCode + ")");

            Assert.IsNull(factory.Services.GetService<OpenTelemetry.Metrics.MeterProvider>(),
                "a default configuration registers zero OpenTelemetry services");
        }

        [TestMethod]
        public async Task PrometheusEnabled_ServesFallen8Series_AfterRealOperations()
        {
            using var factory = new ObservabilityFactory(new Dictionary<String, String>
            {
                { "Fallen8:Observability:Prometheus:Enabled", "true" }
            });
            using var client = factory.CreateClient();

            var a = SeedVertex(factory);
            var b = SeedVertex(factory);
            SeedEdge(factory, a, b);

            // A rollback too, so its series appears through the exporter name-mapping.
            EngineOf(factory).EnqueueTransaction(new RemoveGraphElementTransaction { GraphElementId = 424242 })
                .WaitUntilFinished();

            using var response = await client.GetAsync("/metrics");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();

            StringAssert.Contains(body, "fallen8_transaction_commits");
            StringAssert.Contains(body, "fallen8_transaction_rollbacks");
            StringAssert.Contains(body, "fallen8_graph_vertices");
            StringAssert.Contains(body, "fallen8_transaction_commit_duration");
        }

        [TestMethod]
        public async Task MetricsAuthMatrix_AnonymousByDefault_401WhenRequireApiKey()
        {
            var withKey = new Dictionary<String, String>
            {
                { "Fallen8:Observability:Prometheus:Enabled", "true" },
                { "Fallen8:Security:ApiKey", "test-key-123" },
                { "Fallen8:Security:AllowRemoteAccess", "true" }
            };

            using (var factory = new ObservabilityFactory(withKey))
            using (var client = factory.CreateClient())
            {
                using var anonymous = await client.GetAsync("/metrics");
                Assert.AreEqual(HttpStatusCode.OK, anonymous.StatusCode,
                    "the documented anonymous default (aggregate numbers only), even with a key configured");
            }

            withKey["Fallen8:Observability:Prometheus:RequireApiKey"] = "true";
            using (var factory = new ObservabilityFactory(withKey))
            using (var client = factory.CreateClient())
            {
                using var anonymous = await client.GetAsync("/metrics");
                Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode,
                    "RequireApiKey drops the anonymous exemption");

                using var request = new HttpRequestMessage(HttpMethod.Get, "/metrics");
                request.Headers.Add("X-Api-Key", "test-key-123");
                using var authenticated = await client.SendAsync(request);
                Assert.AreEqual(HttpStatusCode.OK, authenticated.StatusCode);
            }
        }

        #endregion

        #region health

        [TestMethod]
        public async Task HealthEndpoints_AnonymousStatusOnly_EvenWithAnApiKey()
        {
            using var factory = new ObservabilityFactory(new Dictionary<String, String>
            {
                { "Fallen8:Security:ApiKey", "test-key-123" },
                { "Fallen8:Security:AllowRemoteAccess", "true" }
            });
            using var client = factory.CreateClient();

            using (var live = await client.GetAsync("/healthz"))
            {
                Assert.AreEqual(HttpStatusCode.OK, live.StatusCode);
                Assert.AreEqual("Healthy", await live.Content.ReadAsStringAsync());
            }
            using (var ready = await client.GetAsync("/readyz"))
            {
                Assert.AreEqual(HttpStatusCode.OK, ready.StatusCode,
                    "volatile mode marks readiness immediately on startup");
                Assert.AreEqual("Healthy", await ready.Content.ReadAsStringAsync());
            }
        }

        #endregion

        #region /statistics

        [TestMethod]
        public async Task Statistics_ExactOnAKnownSmallGraph()
        {
            using var factory = new ObservabilityFactory();
            using var client = factory.CreateClient();

            // A hub with two leaves plus an isolated robot; one property; one index.
            var hub = SeedVertex(factory, "person", new Dictionary<string, object> { { "name", "hub" } });
            var leaf1 = SeedVertex(factory, "person");
            var leaf2 = SeedVertex(factory, "person");
            SeedVertex(factory, "robot");
            SeedEdge(factory, hub, leaf1, "knows", "friendship");
            SeedEdge(factory, hub, leaf2, "knows", "friendship");
            Assert.IsTrue(EngineOf(factory).IndexFactory.TryCreateIndex(out _, "byName", "DictionaryIndex",
                new Dictionary<string, object>()));

            using var response = await client.GetAsync("/statistics");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var stats = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

            Assert.AreEqual(4, stats.GetProperty("vertexCount").GetInt32());
            Assert.AreEqual(2, stats.GetProperty("edgeCount").GetInt32());
            Assert.IsFalse(stats.GetProperty("sampled").GetBoolean());
            Assert.AreEqual(1, stats.GetProperty("sampleStride").GetInt32());

            var vertexLabels = stats.GetProperty("vertexLabels");
            Assert.AreEqual(2, vertexLabels.GetProperty("distinctTotal").GetInt32());
            Assert.AreEqual("person", vertexLabels.GetProperty("top")[0].GetProperty("name").GetString());
            Assert.AreEqual(3, vertexLabels.GetProperty("top")[0].GetProperty("count").GetInt64());

            var edgeLabels = stats.GetProperty("edgeLabels");
            Assert.AreEqual("friendship", edgeLabels.GetProperty("top")[0].GetProperty("name").GetString());
            Assert.AreEqual(2, edgeLabels.GetProperty("top")[0].GetProperty("count").GetInt64());

            // Out-degrees are [2,0,0,0]: max 2, mean 0.5, p50 0 (nearest-rank over the sorted sample).
            var outDegree = stats.GetProperty("outDegree");
            Assert.AreEqual(2, outDegree.GetProperty("max").GetInt64());
            Assert.AreEqual(0.5, outDegree.GetProperty("mean").GetDouble(), 1e-9);
            Assert.AreEqual(0, outDegree.GetProperty("p50").GetInt64());
            Assert.AreEqual(2, outDegree.GetProperty("p99").GetInt64());

            var propertyKeys = stats.GetProperty("propertyKeys");
            Assert.AreEqual(1, propertyKeys.GetProperty("distinctTotal").GetInt32());
            Assert.AreEqual("name", propertyKeys.GetProperty("top")[0].GetProperty("name").GetString());

            var indices = stats.GetProperty("indices");
            Assert.AreEqual(1, indices.GetArrayLength());
            Assert.AreEqual("byName", indices[0].GetProperty("name").GetString());
            Assert.AreEqual("DictionaryIndex", indices[0].GetProperty("type").GetString());

            var memory = stats.GetProperty("memory");
            Assert.IsTrue(memory.GetProperty("processWorkingSetBytes").GetInt64() > 0);
            Assert.IsTrue(memory.GetProperty("gcHeapBytes").GetInt64() > 0);
            Assert.IsTrue(stats.GetProperty("computedInMs").GetDouble() >= 0);
        }

        [TestMethod]
        public async Task Statistics_AboveTheBudget_SamplesWithAStrideAndSaysSo()
        {
            using var factory = new ObservabilityFactory(new Dictionary<String, String>
            {
                { "Fallen8:Observability:StatisticsElementBudget", "10" }
            });
            using var client = factory.CreateClient();

            var tx = new CreateVerticesTransaction();
            for (var i = 0; i < 40; i++)
            {
                tx.AddVertex(1u, "bulk");
            }
            EngineOf(factory).EnqueueTransaction(tx).WaitUntilFinished();

            using var response = await client.GetAsync("/statistics");
            var stats = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

            Assert.IsTrue(stats.GetProperty("sampled").GetBoolean());
            Assert.AreEqual(4, stats.GetProperty("sampleStride").GetInt32(), "ceil(40/10)");
            Assert.AreEqual(40, stats.GetProperty("vertexCount").GetInt32(), "counts stay exact (O(1) counters)");
            Assert.AreEqual(10, stats.GetProperty("vertexLabels").GetProperty("top")[0].GetProperty("count").GetInt64(),
                "per-label counts are AS COUNTED IN THE SAMPLE (40 / stride 4)");
        }

        [TestMethod]
        public async Task Statistics_RequiresTheApiKey_UnlikeMetrics()
        {
            using var factory = new ObservabilityFactory(new Dictionary<String, String>
            {
                { "Fallen8:Security:ApiKey", "test-key-123" },
                { "Fallen8:Security:AllowRemoteAccess", "true" }
            });
            using var client = factory.CreateClient();

            using (var anonymous = await client.GetAsync("/statistics"))
            {
                Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode,
                    "/statistics exposes schema-shaped data (label/property/index names) - normal auth applies");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, "/statistics");
            request.Headers.Add("X-Api-Key", "test-key-123");
            using var authenticated = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, authenticated.StatusCode);
        }

        [TestMethod]
        public async Task Statistics_IsRateLimited_UnderTheSensitivePolicy()
        {
            using var factory = new ObservabilityFactory(new Dictionary<String, String>
            {
                // A tiny window so the test exhausts it deterministically without hammering.
                { "Fallen8:Security:SensitiveRateLimitPermitPerWindow", "3" },
                { "Fallen8:Security:RateLimitWindowSeconds", "60" }
            });
            using var client = factory.CreateClient();

            for (var i = 0; i < 3; i++)
            {
                using var ok = await client.GetAsync("/statistics");
                Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode, "request " + (i + 1) + " within the window");
            }

            using var limited = await client.GetAsync("/statistics");
            Assert.AreEqual((HttpStatusCode)429, limited.StatusCode,
                "the sensitive fixed-window limiter caps a scrape-loop misconfiguration");
        }

        [TestMethod]
        public void ReadinessCheck_ReflectsTheStartupFlag()
        {
            var state = new NoSQL.GraphDB.App.Services.StartupState();
            var check = new NoSQL.GraphDB.App.Services.StartupReadinessCheck(state);

            var notReady = check.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext()).Result;
            Assert.AreEqual(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy, notReady.Status,
                "before load-at-startup completes, /readyz reports unhealthy");

            state.MarkReady();
            var ready = check.CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext()).Result;
            Assert.AreEqual(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy, ready.Status);
        }

        #endregion

        [TestMethod]
        public async Task OpenApiDocument_ContainsTheStatisticsOperation()
        {
            using var factory = new ObservabilityFactory();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/openapi/v0.1.json");
            response.EnsureSuccessStatusCode();
            var doc = await response.Content.ReadAsStringAsync();
            StringAssert.Contains(doc, "/statistics");
        }
    }
}
