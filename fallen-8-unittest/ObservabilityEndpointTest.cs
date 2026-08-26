// MIT License
//
// ObservabilityEndpointTest.cs
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
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Index.Spatial;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Pipeline tests for the observability surfaces (feature observability): the Prometheus
    /// scrape endpoint on/off + auth matrix, health endpoints, the zero-config guarantee, and
    /// GET /statistics correctness, budget sampling, auth, and the index inventory it must
    /// report identically to GET /status.
    /// </summary>
    [TestClass]
    public class ObservabilityEndpointTest
    {
        private static Fallen8 EngineOf(VolatileAppFactory factory)
        {
            return factory.Services.GetRequiredService<NoSQL.GraphDB.App.Namespaces.Fallen8Namespaces>().Default.Engine;
        }

        private static Int32 SeedVertex(VolatileAppFactory factory, string label = "person",
            Dictionary<string, object> properties = null)
        {
            var tx = new CreateVertexTransaction
            {
                Definition = new VertexDefinition { CreationDate = 1u, Label = label, Properties = properties }
            };
            EngineOf(factory).EnqueueTransaction(tx).WaitUntilFinished();
            return tx.VertexCreated.Id;
        }

        private static void SeedEdge(VolatileAppFactory factory, Int32 source, Int32 target,
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
            using var factory = new VolatileAppFactory();
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
            using var factory = new VolatileAppFactory(new Dictionary<String, String>
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
                { "Fallen8:Security:ApiKey", "test-key-123" }
            };

            using (var factory = new VolatileAppFactory(withKey))
            using (var client = factory.CreateClient())
            {
                using var anonymous = await client.GetAsync("/metrics");
                Assert.AreEqual(HttpStatusCode.OK, anonymous.StatusCode,
                    "the documented anonymous default (aggregate numbers only), even with a key configured");
            }

            withKey["Fallen8:Observability:Prometheus:RequireApiKey"] = "true";
            using (var factory = new VolatileAppFactory(withKey))
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
            using var factory = new VolatileAppFactory(new Dictionary<String, String>
            {
                { "Fallen8:Security:ApiKey", "test-key-123" }
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
            using var factory = new VolatileAppFactory();
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
            using var factory = new VolatileAppFactory(new Dictionary<String, String>
            {
                { "Fallen8:Observability:StatisticsElementBudget", "10" }
            });
            using var client = factory.CreateClient();

            TestVertices.Create(EngineOf(factory), 40, "bulk");

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
            using var factory = new VolatileAppFactory(new Dictionary<String, String>
            {
                { "Fallen8:Security:ApiKey", "test-key-123" }
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
            using var factory = new VolatileAppFactory(new Dictionary<String, String>
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

        // The index inventory that /statistics and /status both report. The engine's spatial
        // R-Tree answers a NEGATIVE "count not supported" sentinel from CountOfKeys, and
        // /statistics leaked that raw sentinel as a real key count while /status already
        // normalised it to null. Both inventories now normalise through
        // IndexStatsREST.NonNegativeCount, so they cannot disagree; the mapping helper's own
        // unit lives in AnalyticsPluginVendorContractTest.
        private const String DictionaryIndexName = "byName";
        private const String SpatialIndexName = "byLocation";

        /// <summary>
        /// Arranges the two indices the sentinel contract needs: a DictionaryIndex (counts BOTH keys
        /// and values honestly) and a SpatialIndex (an R-Tree: CountOfValues is honest,
        /// CountOfKeys answers the negative "not supported" sentinel). The spatial index is created
        /// engine-side on purpose - its Initialize needs live CLR objects the REST pluginOptions
        /// cannot carry (pinned by StatusIndexInventoryTest).
        /// </summary>
        private static void SeedTwoIndices(VolatileAppFactory factory)
        {
            var engine = EngineOf(factory);

            var created = TestVertices.Create(engine, 2, "person");
            Assert.AreEqual(2, created.Length, "Arrange failed: the vertices were not created.");

            Assert.IsTrue(engine.IndexFactory.TryCreateIndex(out var dictionaryIndex, DictionaryIndexName,
                "DictionaryIndex"), "Arrange failed: the dictionary index was not created.");
            dictionaryIndex.AddOrUpdate("alice", created[0]);
            dictionaryIndex.AddOrUpdate("bob", created[1]);

            Assert.IsTrue(engine.IndexFactory.TryCreateIndex(out var spatialIndex, SpatialIndexName,
                "SpatialIndex", RTreeParameters()), "Arrange failed: the spatial index was not created.");
            spatialIndex.AddOrUpdate(new Point(1.0f, 1.0f), created[0]);
            spatialIndex.AddOrUpdate(new Point(2.0f, 2.0f), created[1]);

            Assert.IsTrue(spatialIndex.CountOfKeys() < 0,
                "Arrange failed: the R-Tree must answer the negative 'count not supported' sentinel " +
                "- that sentinel is what this defect was about leaking.");
            Assert.AreEqual(2, spatialIndex.CountOfValues(),
                "Arrange failed: the R-Tree counts VALUES honestly, so only the key count may null out.");
        }

        private static IDictionary<String, Object> RTreeParameters()
        {
            return new Dictionary<String, Object>
            {
                ["IMetric"] = new NoSQL.GraphDB.Core.Index.Spatial.Implementation.Metric.EuclidianMetric(),
                ["MinCount"] = 2,
                ["MaxCount"] = 5,
                ["Space"] = new List<IDimension>
                {
                    new NoSQL.GraphDB.Core.Index.Spatial.Implementation.Geometry.RealDimension(),
                    new NoSQL.GraphDB.Core.Index.Spatial.Implementation.Geometry.RealDimension(),
                }
            };
        }

        private static async Task<JsonElement> Get(HttpClient client, String path)
        {
            using var response = await client.GetAsync(path);
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        }

        /// <summary>
        /// The per-index (keys, values) pair of one inventory, keyed by index name. Both fields must
        /// be PRESENT on the wire - a null count is reported as an explicit null, never omitted, so
        /// a client can tell "not supported" apart from "field missing".
        /// </summary>
        private static Dictionary<String, (Int32? Keys, Int32? Values)> CountsByIndex(
            JsonElement inventory, String nameProperty)
        {
            return inventory.EnumerateArray().ToDictionary(
                entry => entry.GetProperty(nameProperty).GetString(),
                entry => (Keys: Count(entry, "keys"), Values: Count(entry, "values")));
        }

        private static Int32? Count(JsonElement entry, String property)
        {
            Assert.IsTrue(entry.TryGetProperty(property, out var value),
                property + " must be present on every inventory entry (null, never omitted)");
            return value.ValueKind == JsonValueKind.Null ? (Int32?)null : value.GetInt32();
        }

        [TestMethod]
        public async Task Statistics_SpatialKeyCount_IsNull_NotTheNegativeSentinel()
        {
            using var factory = new VolatileAppFactory();
            using var client = factory.CreateClient();
            SeedTwoIndices(factory);

            var indices = CountsByIndex((await Get(client, "/statistics")).GetProperty("indices"), "name");

            Assert.IsNull(indices[SpatialIndexName].Keys,
                "the R-Tree's negative 'count not supported' sentinel must surface as null, never as -1");
            Assert.AreEqual<Int32?>(2, indices[SpatialIndexName].Values,
                "a count the index DOES support is still reported (only the sentinel nulls out)");
            Assert.AreEqual<Int32?>(2, indices[DictionaryIndexName].Keys,
                "an index with real counts is unaffected by the normalisation");
            Assert.AreEqual<Int32?>(2, indices[DictionaryIndexName].Values);
        }

        [TestMethod]
        public async Task Statistics_And_Status_ReportIdenticalIndexCounts()
        {
            using var factory = new VolatileAppFactory();
            using var client = factory.CreateClient();
            SeedTwoIndices(factory);

            // The agreement assertion is the load-bearing one: it is what stops the two discovery
            // surfaces drifting apart again (the /statistics field was Int32 and raw, /status
            // Int32? and normalised).
            var statistics = CountsByIndex((await Get(client, "/statistics")).GetProperty("indices"), "name");
            var status = CountsByIndex((await Get(client, "/status")).GetProperty("indices"), "indexId");

            CollectionAssert.AreEquivalent(statistics.Keys.ToList(), status.Keys.ToList(),
                "both inventories list the same indices");
            foreach (var name in statistics.Keys)
            {
                Assert.AreEqual(status[name].Keys, statistics[name].Keys,
                    "/statistics and /status must report the same key count for " + name);
                Assert.AreEqual(status[name].Values, statistics[name].Values,
                    "/statistics and /status must report the same value count for " + name);
            }
        }

        [TestMethod]
        public async Task Statistics_WithoutAnySpatialIndex_ReportsPlainNumbers()
        {
            // The unchanged default: nullable fields do not mean "usually null". Every index that
            // supports counting still answers a number.
            using var factory = new VolatileAppFactory();
            using var client = factory.CreateClient();
            Assert.IsTrue(EngineOf(factory).IndexFactory.TryCreateIndex(out _, DictionaryIndexName,
                "DictionaryIndex"), "Arrange failed: the dictionary index was not created.");

            var indices = CountsByIndex((await Get(client, "/statistics")).GetProperty("indices"), "name");

            Assert.AreEqual<Int32?>(0, indices[DictionaryIndexName].Keys, "a fresh index reports zero, not null");
            Assert.AreEqual<Int32?>(0, indices[DictionaryIndexName].Values);
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
            using var factory = new VolatileAppFactory();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/openapi/v0.1.json");
            response.EnsureSuccessStatusCode();
            var doc = await response.Content.ReadAsStringAsync();
            StringAssert.Contains(doc, "/statistics");
        }
    }
}
