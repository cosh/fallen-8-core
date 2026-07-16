// MIT License
//
// AnalyticsEndpointTest.cs
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
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App;
using NoSQL.GraphDB.App.Services;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Pipeline tests for the analytics REST surface (feature graph-analytics): discovery,
    /// runs with bounded projections, every documented 400/404, the 429 slot semantics,
    /// partition paging, and the property write-back (keys, types, chunking, idempotency) -
    /// all with the dynamic-code switch off (the test host default).
    /// </summary>
    [TestClass]
    public class AnalyticsEndpointTest
    {
        private sealed class AnalyticsFactory : WebApplicationFactory<Program>
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseSetting("Fallen8:Durability:Volatile", "true");
            }
        }

        private static StringContent Json(string body)
        {
            return new StringContent(body, Encoding.UTF8, "application/json");
        }

        private static Fallen8 EngineOf(AnalyticsFactory factory)
        {
            return (Fallen8)factory.Services.GetRequiredService<IFallen8>();
        }

        private static Int32 SeedVertex(AnalyticsFactory factory, string label = "person")
        {
            var tx = new CreateVertexTransaction
            {
                Definition = new VertexDefinition { CreationDate = 1u, Label = label }
            };
            EngineOf(factory).EnqueueTransaction(tx).WaitUntilFinished();
            return tx.VertexCreated.Id;
        }

        private static void SeedEdge(AnalyticsFactory factory, Int32 source, Int32 target, string edgePropertyId = "link")
        {
            var tx = new CreateEdgeTransaction
            {
                Definition = new EdgeDefinition
                {
                    SourceVertexId = source,
                    TargetVertexId = target,
                    EdgePropertyId = edgePropertyId,
                    CreationDate = 1u
                }
            };
            EngineOf(factory).EnqueueTransaction(tx).WaitUntilFinished();
        }

        [TestMethod]
        public async Task Algorithms_ListsTheFiveBuiltins()
        {
            using var factory = new AnalyticsFactory();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/analytics/algorithms");
            response.EnsureSuccessStatusCode();
            var algorithms = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

            foreach (var name in new[] { "PAGERANK", "WCC", "LABELPROPAGATION", "DEGREE", "TRIANGLECOUNT" })
            {
                Assert.IsTrue(algorithms.TryGetProperty(name, out _), name + " must be listed");
            }
        }

        [TestMethod]
        public async Task Run_TopK_OrderingAndCeiling_WithStatistics()
        {
            using var factory = new AnalyticsFactory();
            using var client = factory.CreateClient();

            var hub = SeedVertex(factory);
            var mid = SeedVertex(factory);
            var leaf = SeedVertex(factory);
            SeedEdge(factory, hub, mid);
            SeedEdge(factory, hub, leaf);
            SeedEdge(factory, mid, leaf);

            using var response = await client.PostAsync("/analytics/DEGREE", Json("{\"maxResults\":2}"));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

            Assert.AreEqual("DEGREE", result.GetProperty("algorithm").GetString());
            Assert.IsTrue(result.GetProperty("converged").GetBoolean());
            Assert.AreEqual(3, result.GetProperty("vertexCount").GetInt32());
            Assert.AreEqual(2d, result.GetProperty("statistics").GetProperty("Max").GetDouble());

            var rows = result.GetProperty("results");
            Assert.AreEqual(2, rows.GetArrayLength(), "bounded to maxResults");
            Assert.AreEqual(hub, rows[0].GetProperty("graphElementId").GetInt32(),
                "top-K is score-descending; the 2-degree hub leads (ties break by ascending id)");
            Assert.AreEqual(2d, rows[0].GetProperty("score").GetDouble());
        }

        [TestMethod]
        public async Task Run_EveryDocumented400_AndThe404()
        {
            using var factory = new AnalyticsFactory();
            using var client = factory.CreateClient();

            using (var r = await client.PostAsync("/analytics/NOPE", Json("{}")))
            {
                Assert.AreEqual(HttpStatusCode.NotFound, r.StatusCode, "unknown algorithm");
            }
            using (var r = await client.PostAsync("/analytics/PAGERANK", Json("{\"maxResults\":0}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, r.StatusCode, "maxResults below 1");
            }
            using (var r = await client.PostAsync("/analytics/PAGERANK", Json("{\"maxResults\":10001}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, r.StatusCode, "maxResults over the ceiling");
            }
            using (var r = await client.PostAsync("/analytics/PAGERANK", Json("{\"maxIterations\":10001}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, r.StatusCode, "maxIterations over the ceiling");
            }
            using (var r = await client.PostAsync("/analytics/PAGERANK", Json("{\"epsilon\":-1}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, r.StatusCode, "negative epsilon");
            }
            using (var r = await client.PostAsync("/analytics/PAGERANK", Json("{\"direction\":\"sideways\"}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, r.StatusCode, "unknown direction");
            }
            using (var r = await client.PostAsync("/analytics/PAGERANK", Json("{\"timeBudgetSeconds\":301}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, r.StatusCode, "time budget over the configured ceiling");
            }
            using (var r = await client.PostAsync("/analytics/PAGERANK", Json("{\"parameters\":{\"DampingFactor\":1.5}}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, r.StatusCode, "damping outside [0,1]");
            }
            using (var r = await client.PostAsync("/analytics/PAGERANK", Json("{\"writeBack\":true,\"writeBackPropertyKey\":\"   \"}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, r.StatusCode, "blank write-back key");
            }
            using (var r = await client.PostAsync("/analytics/PAGERANK",
                Json("{\"writeBack\":true,\"writeBackPropertyKey\":\"" + new string('k', 257) + "\"}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, r.StatusCode, "write-back key over 256 chars");
            }
        }

        [TestMethod]
        public async Task Partitions_Summaries_AndMembershipPaging()
        {
            using var factory = new AnalyticsFactory();
            using var client = factory.CreateClient();

            // Component 1: a chain of 3; component 2: a pair.
            var a1 = SeedVertex(factory);
            var a2 = SeedVertex(factory);
            var a3 = SeedVertex(factory);
            SeedEdge(factory, a1, a2);
            SeedEdge(factory, a2, a3);
            var b1 = SeedVertex(factory);
            var b2 = SeedVertex(factory);
            SeedEdge(factory, b2, b1);

            using (var response = await client.PostAsync("/analytics/WCC", Json("{}")))
            {
                var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
                Assert.AreEqual(2d, result.GetProperty("statistics").GetProperty("ComponentCount").GetDouble());

                var partitions = result.GetProperty("partitions");
                Assert.AreEqual(2, partitions.GetArrayLength());
                Assert.AreEqual(a1, partitions[0].GetProperty("partitionId").GetInt32(), "largest first");
                Assert.AreEqual(3, partitions[0].GetProperty("size").GetInt32());
                Assert.AreEqual(b1, partitions[1].GetProperty("partitionId").GetInt32());
                Assert.AreEqual(2, partitions[1].GetProperty("size").GetInt32());
            }

            // Membership paging: offset 1, page size 1 of the 3-member component.
            using (var response = await client.PostAsync("/analytics/WCC/partition/" + a1,
                Json("{\"offset\":1,\"maxResults\":1}")))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
                Assert.AreEqual(3, page.GetProperty("size").GetInt32(), "size is the partition total, not the page");
                Assert.AreEqual(1, page.GetProperty("members").GetArrayLength());
                Assert.AreEqual(a2, page.GetProperty("members")[0].GetInt32(), "members ascend; offset 1 lands on the middle vertex");
            }

            using (var r = await client.PostAsync("/analytics/WCC/partition/424242", Json("{}")))
            {
                Assert.AreEqual(HttpStatusCode.NotFound, r.StatusCode, "a partition the run did not produce");
            }
            using (var r = await client.PostAsync("/analytics/DEGREE/partition/0", Json("{}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, r.StatusCode, "membership paging is for partition algorithms");
            }
            using (var r = await client.PostAsync("/analytics/WCC/partition/" + a1, Json("{\"offset\":-1}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, r.StatusCode, "negative offset");
            }
            using (var r = await client.PostAsync("/analytics/WCC/partition/" + a1, Json("{\"writeBack\":true}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, r.StatusCode,
                    "writeBack is refused here, never silently ignored");
            }
            using (var r = await client.PostAsync("/analytics/WCC/partition/" + a1,
                Json("{\"vertexLabel\":\"no-such-label\"}")))
            {
                Assert.AreEqual(HttpStatusCode.NotFound, r.StatusCode,
                    "a partition algorithm over an EMPTY scope produced no partitions - 404, not the score-algorithm 400");
            }
        }

        [TestMethod]
        public async Task ConcurrentRunSlots_Exhausted_Is429()
        {
            using var factory = new AnalyticsFactory();
            using var client = factory.CreateClient();
            SeedVertex(factory);

            var gate = factory.Services.GetRequiredService<AnalyticsRunGate>();
            Assert.IsTrue(gate.TryEnter(), "take the single default slot");
            try
            {
                using var response = await client.PostAsync("/analytics/DEGREE", Json("{}"));
                Assert.AreEqual((HttpStatusCode)429, response.StatusCode);
                Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            }
            finally
            {
                gate.Exit();
            }

            using var afterRelease = await client.PostAsync("/analytics/DEGREE", Json("{}"));
            Assert.AreEqual(HttpStatusCode.OK, afterRelease.StatusCode, "the slot is free again");
        }

        [TestMethod]
        public async Task WriteBack_ConventionKeysAndTypes_IdempotentOverwrite()
        {
            using var factory = new AnalyticsFactory();
            using var client = factory.CreateClient();
            var engine = EngineOf(factory);

            var a = SeedVertex(factory);
            var b = SeedVertex(factory);
            SeedEdge(factory, a, b);

            // DEGREE (both) -> analytics.degree.both as UInt32.
            using (var response = await client.PostAsync("/analytics/DEGREE", Json("{\"writeBack\":true}")))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                var writeBack = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
                    .RootElement.GetProperty("writeBack");
                Assert.AreEqual("analytics.degree.both", writeBack.GetProperty("propertyKey").GetString());
                Assert.AreEqual(2, writeBack.GetProperty("verticesWritten").GetInt32());
                Assert.AreEqual(1, writeBack.GetProperty("chunks").GetInt32());
            }

            Assert.IsTrue(engine.TryGetVertex(out var vertexA, a));
            Assert.IsTrue(vertexA.TryGetProperty<UInt32>(out var degree, "analytics.degree.both"));
            Assert.AreEqual(1u, degree, "the convention value type is UInt32");

            // PAGERANK -> analytics.pagerank as Double.
            (await client.PostAsync("/analytics/PAGERANK", Json("{\"writeBack\":true}"))).EnsureSuccessStatusCode();
            Assert.IsTrue(vertexA.TryGetProperty<Double>(out var rank1, "analytics.pagerank"));

            // Idempotent re-run: the property is overwritten, not duplicated or appended.
            (await client.PostAsync("/analytics/PAGERANK", Json("{\"writeBack\":true}"))).EnsureSuccessStatusCode();
            Assert.IsTrue(vertexA.TryGetProperty<Double>(out var rank2, "analytics.pagerank"));
            Assert.AreEqual(rank1, rank2, "quiescent graph => identical value on re-run");

            // WCC -> analytics.wcc as Int32; custom key override honoured.
            (await client.PostAsync("/analytics/WCC", Json("{\"writeBack\":true}"))).EnsureSuccessStatusCode();
            Assert.IsTrue(vertexA.TryGetProperty<Int32>(out var component, "analytics.wcc"));
            Assert.AreEqual(a, component);

            (await client.PostAsync("/analytics/WCC",
                Json("{\"writeBack\":true,\"writeBackPropertyKey\":\"my.custom.key\"}"))).EnsureSuccessStatusCode();
            Assert.IsTrue(vertexA.TryGetProperty<Int32>(out _, "my.custom.key"));
        }

        [TestMethod]
        public async Task WriteBack_MultiChunkRun_AppliesAllChunks()
        {
            using var factory = new AnalyticsFactory();
            using var client = factory.CreateClient();
            var engine = EngineOf(factory);

            // One more vertex than a chunk holds => exactly 2 chunks.
            var tx = new CreateVerticesTransaction();
            for (var i = 0; i < 50_001; i++)
            {
                tx.AddVertex(1u, "bulk");
            }
            engine.EnqueueTransaction(tx).WaitUntilFinished();

            using var response = await client.PostAsync("/analytics/DEGREE",
                Json("{\"writeBack\":true,\"maxResults\":1}"));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var writeBack = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
                .RootElement.GetProperty("writeBack");

            Assert.AreEqual(50_001, writeBack.GetProperty("verticesWritten").GetInt32());
            Assert.AreEqual(2, writeBack.GetProperty("chunks").GetInt32());

            // Spot-check a vertex from each chunk (ascending-id order: the first and the last).
            Assert.IsTrue(engine.TryGetVertex(out var first, 0));
            Assert.IsTrue(first.TryGetProperty<UInt32>(out _, "analytics.degree.both"));
            Assert.IsTrue(engine.TryGetVertex(out var last, 50_000));
            Assert.IsTrue(last.TryGetProperty<UInt32>(out _, "analytics.degree.both"));
        }

        [TestMethod]
        public async Task OpenApiDocument_ContainsTheAnalyticsOperations()
        {
            using var factory = new AnalyticsFactory();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/openapi/v0.1.json");
            response.EnsureSuccessStatusCode();
            var doc = await response.Content.ReadAsStringAsync();

            StringAssert.Contains(doc, "/analytics/algorithms");
            StringAssert.Contains(doc, "/analytics/{algorithmName}");
            StringAssert.Contains(doc, "/analytics/{algorithmName}/partition/{partitionId}");
        }
    }
}
