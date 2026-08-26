// MIT License
//
// BenchmarkEndpointTest.cs
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
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Pipeline tests for GET /ns/{ns}/benchmark and GET /ns/{ns}/generate (feature web-ui): both
    /// return structured JSON instead of a formatted (locale-dependent) string, the benchmark
    /// defaults to 1000 iterations when the parameter is omitted, and the empty-graph and bad-input
    /// cases map to 400 instead of a 200 with an error sentence.
    ///
    /// The iteration count is also BOUNDED: the benchmark used to accept any positive count although
    /// one pass saturates every core and cannot be interrupted once started, so a mistyped extra zero
    /// pinned the host until it finished. It now refuses anything above
    /// <c>Fallen8:Security:BenchmarkMaxIterations</c> and clamps the omitted-count default to that
    /// ceiling; the ceiling VALUE's own contract (default, and self-correction of a non-positive
    /// configuration) lives with the options class in <c>SettingCatalogTest</c>.
    /// </summary>
    /// <remarks>
    /// Every URL here is EXPLICITLY scoped, because these two routes are the only namespace-scoped
    /// ones whose bare form is refused rather than aliased to "default" (feature graph-namespaces).
    /// That refusal is also a 400, so a bare URL would leave the input-validation assertions below
    /// passing for the wrong reason; the twin/refusal contract itself lives in
    /// <c>NamespaceEndpointTest</c>.
    /// </remarks>
    [TestClass]
    public class BenchmarkEndpointTest
    {
        /// <summary>The addressed namespace every request here names.</summary>
        private const string Ns = "/ns/default";

        #region helpers

        private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
        {
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        }

        /// <summary>Gives the addressed namespace a small graph, so the benchmark has edges to follow.</summary>
        private static async Task Generate(HttpClient client)
        {
            using var response = await client.GetAsync(Ns + "/generate?nodeCount=20&edgeCount=2");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "GET " + Ns + "/generate");
        }

        #endregion

        [TestMethod]
        public async Task Benchmark_OnGeneratedGraph_ReturnsStructuredStatistics()
        {
            using var factory = new VolatileAppFactory();
            using var client = factory.CreateClient();

            var generate = await client.GetAsync(Ns + "/generate?nodeCount=50&edgeCount=2");
            Assert.AreEqual(HttpStatusCode.OK, generate.StatusCode);

            var response = await client.GetAsync(Ns + "/benchmark?iterations=3");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = body.RootElement;

            Assert.AreEqual(3, root.GetProperty("iterations").GetInt32());
            Assert.IsTrue(root.GetProperty("edgesTraversed").GetInt64() > 0,
                "The generated graph has edges, so the traversal count must be positive.");
            Assert.IsTrue(root.GetProperty("averageTps").GetDouble() > 0);
            Assert.IsTrue(root.GetProperty("medianTps").GetDouble() > 0);
            Assert.IsTrue(root.GetProperty("standardDeviationTps").GetDouble() >= 0);
        }

        [TestMethod]
        public async Task Benchmark_OnEmptyGraph_Returns400()
        {
            using var factory = new VolatileAppFactory();
            using var client = factory.CreateClient();

            var response = await client.GetAsync(Ns + "/benchmark?iterations=1");
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            StringAssert.Contains(await response.Content.ReadAsStringAsync(), "No vertices");
        }

        [TestMethod]
        public async Task Benchmark_NonPositiveOrGarbageIterations_Return400()
        {
            using var factory = new VolatileAppFactory();
            using var client = factory.CreateClient();

            var generate = await client.GetAsync(Ns + "/generate?nodeCount=10&edgeCount=1");
            Assert.AreEqual(HttpStatusCode.OK, generate.StatusCode);

            var zero = await client.GetAsync(Ns + "/benchmark?iterations=0");
            Assert.AreEqual(HttpStatusCode.BadRequest, zero.StatusCode);

            var garbage = await client.GetAsync(Ns + "/benchmark?iterations=abc");
            Assert.AreEqual(HttpStatusCode.BadRequest, garbage.StatusCode);
        }

        #region the iteration ceiling

        [TestMethod]
        public async Task Benchmark_AboveTheConfiguredCeiling_Returns400_NamingTheKeyAndTheCeiling()
        {
            using var factory = new VolatileAppFactory(new Dictionary<String, String>
            {
                ["Fallen8:Security:BenchmarkMaxIterations"] = "3"
            });
            using var client = factory.CreateClient();
            await Generate(client);

            using var tooMany = await client.GetAsync(Ns + "/benchmark?iterations=4");
            Assert.AreEqual(HttpStatusCode.BadRequest, tooMany.StatusCode);
            Assert.AreEqual("application/problem+json", tooMany.Content.Headers.ContentType?.MediaType);
            var detail = (await ReadJson(tooMany)).GetProperty("detail").GetString();
            StringAssert.Contains(detail, "3", "the ceiling is named so the caller can lower the count");
            StringAssert.Contains(detail, "Fallen8:Security:BenchmarkMaxIterations",
                "the caller is told which key raises it");

            // The boundary itself runs.
            using var atCeiling = await client.GetAsync(Ns + "/benchmark?iterations=3");
            Assert.AreEqual(HttpStatusCode.OK, atCeiling.StatusCode);
            Assert.AreEqual(3, (await ReadJson(atCeiling)).GetProperty("iterations").GetInt32(),
                "an accepted count is run and echoed, never silently clamped");
        }

        [TestMethod]
        public async Task Benchmark_WithoutIterations_ClampsTheDefaultToTheCeiling()
        {
            // The endpoint default is 1000; with a lower ceiling a request that names NO count must
            // still succeed (it never asked for the rejected value) and report what it ran.
            using var factory = new VolatileAppFactory(new Dictionary<String, String>
            {
                ["Fallen8:Security:BenchmarkMaxIterations"] = "2"
            });
            using var client = factory.CreateClient();
            await Generate(client);

            using var response = await client.GetAsync(Ns + "/benchmark");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(2, (await ReadJson(response)).GetProperty("iterations").GetInt32());
        }

        [TestMethod]
        public async Task Benchmark_UnconfiguredCeiling_RejectsTheMistypedExtraZero_AndKeepsTheOldFourHundreds()
        {
            using var factory = new VolatileAppFactory();
            using var client = factory.CreateClient();
            await Generate(client);

            // The pre-fix footgun: this used to start a pass nothing could interrupt.
            using var tooMany = await client.GetAsync(Ns + "/benchmark?iterations=10001");
            Assert.AreEqual(HttpStatusCode.BadRequest, tooMany.StatusCode);
            Assert.AreEqual("application/problem+json", tooMany.Content.Headers.ContentType?.MediaType);
            StringAssert.Contains((await ReadJson(tooMany)).GetProperty("detail").GetString(), "10000");

            // Unchanged: the ceiling check is additional, the existing 400s still answer first AND
            // still name their own cause. Their STATUS is asserted verbatim by
            // Benchmark_NonPositiveOrGarbageIterations_Return400 above, so only the wording the
            // ceiling could have swallowed is asserted here - and a 200 carries no "detail" at all,
            // so these still fail if the refusal itself ever goes away.
            using var zero = await client.GetAsync(Ns + "/benchmark?iterations=0");
            StringAssert.Contains((await ReadJson(zero)).GetProperty("detail").GetString(), "greater than 0");

            using var garbage = await client.GetAsync(Ns + "/benchmark?iterations=abc");
            StringAssert.Contains((await ReadJson(garbage)).GetProperty("detail").GetString(), "not a valid");

            // And a normal small run is unaffected.
            using var ok = await client.GetAsync(Ns + "/benchmark?iterations=2");
            Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode);
            Assert.AreEqual(2, (await ReadJson(ok)).GetProperty("iterations").GetInt32());
        }

        #endregion

        [TestMethod]
        public async Task Generate_PreferentialDistribution_ProducesHubs_AndTheExactEdgeCount()
        {
            using var factory = new VolatileAppFactory();
            using var client = factory.CreateClient();

            const int nodes = 2000;
            const int edgesPerVertex = 3;
            var generate = await client.GetAsync(
                Ns + $"/generate?nodeCount={nodes}&edgeCount={edgesPerVertex}&distribution=preferential");
            Assert.AreEqual(HttpStatusCode.OK, generate.StatusCode, await generate.Content.ReadAsStringAsync());

            var engine = factory.Services
                .GetRequiredService<NoSQL.GraphDB.App.Namespaces.Fallen8Namespaces>().Default.Engine;
            Assert.AreEqual(nodes, engine.VertexCount);

            // Vertex i gets min(edgesPerVertex, i) out-edges - an exact, seed-independent count.
            var expectedEdges = nodes * edgesPerVertex - edgesPerVertex * (edgesPerVertex + 1) / 2;
            Assert.AreEqual(expectedEdges, engine.EdgeCount);

            // The response reports what was written, checked against the engine over the wire: a
            // count derived from the arguments instead of measured would read 6000 here.
            using var generated = JsonDocument.Parse(await generate.Content.ReadAsStringAsync());
            Assert.AreEqual("default", generated.RootElement.GetProperty("namespace").GetString());
            Assert.AreEqual(nodes, generated.RootElement.GetProperty("verticesCreated").GetInt32());
            Assert.AreEqual(expectedEdges, generated.RootElement.GetProperty("edgesCreated").GetInt64());
            Assert.AreEqual(engine.VertexCount, generated.RootElement.GetProperty("vertexCountAfter").GetInt32());
            Assert.AreEqual(engine.EdgeCount, generated.RootElement.GetProperty("edgeCountAfter").GetInt32());

            // The point of preferential attachment: heavy-tailed in-degrees. Uniform random
            // in-degrees are ~Poisson(3) (max ≈ 12 over 2000 draws); Barabási-Albert growth
            // gives the earliest vertices in-degrees in the hundreds - 10× the mean separates
            // the two distributions with enormous margin, keeping the assertion seed-proof.
            var maxInDegree = 0u;
            foreach (var vertex in engine.GetAllVertices())
            {
                maxInDegree = Math.Max(maxInDegree, vertex.GetInDegree());
            }
            Assert.IsTrue(maxInDegree >= 10 * edgesPerVertex,
                $"expected a hub (in-degree >= {10 * edgesPerVertex}), got max {maxInDegree}");

            // The benchmark follows every out-edge, so it counts exactly the generated edges
            // (schema-agnostic; see Bench_FollowsEveryOutEdge_RegardlessOfSchema for a non-"A" graph).
            var benchmark = await client.GetAsync(Ns + "/benchmark?iterations=1");
            Assert.AreEqual(HttpStatusCode.OK, benchmark.StatusCode);
            using var body = JsonDocument.Parse(await benchmark.Content.ReadAsStringAsync());
            Assert.AreEqual(expectedEdges, body.RootElement.GetProperty("edgesTraversed").GetInt64());
        }

        [TestMethod]
        public async Task Generate_EdgeCountExceedingNodeCount_IsCappedNotHung()
        {
            // Regression: uniform generation used to spin forever when edgeCount > nodeCount
            // (only nodeCount distinct targets exist). It must complete and cap per-vertex edges.
            using var factory = new VolatileAppFactory();
            using var client = factory.CreateClient();

            var generate = await client.GetAsync(Ns + "/generate?nodeCount=3&edgeCount=10");
            Assert.AreEqual(HttpStatusCode.OK, generate.StatusCode);

            var engine = factory.Services
                .GetRequiredService<NoSQL.GraphDB.App.Namespaces.Fallen8Namespaces>().Default.Engine;
            Assert.AreEqual(3, engine.VertexCount);
            // Each vertex gets at most nodeCount distinct targets (3), so at most 9 edges total.
            Assert.IsTrue(engine.EdgeCount <= 9, $"expected <= 9 capped edges, got {engine.EdgeCount}");
        }

        [TestMethod]
        public async Task Generate_ZeroNodes_Succeeds()
        {
            // nodeCount=0 is accepted (only negatives are 400); it must not throw on the
            // partitioner (Partitioner.Create(0,0) would). Empty graph, 200.
            using var factory = new VolatileAppFactory();
            using var client = factory.CreateClient();

            var generate = await client.GetAsync(Ns + "/generate?nodeCount=0&edgeCount=5");
            Assert.AreEqual(HttpStatusCode.OK, generate.StatusCode);
            var engine = factory.Services
                .GetRequiredService<NoSQL.GraphDB.App.Namespaces.Fallen8Namespaces>().Default.Engine;
            Assert.AreEqual(0, engine.VertexCount);
            Assert.AreEqual(0, engine.EdgeCount);
        }

        [TestMethod]
        public async Task Generate_ValidatesItsInputs_With400s()
        {
            using var factory = new VolatileAppFactory();
            using var client = factory.CreateClient();

            var unknownDistribution = await client.GetAsync(Ns + "/generate?nodeCount=10&edgeCount=1&distribution=banana");
            Assert.AreEqual(HttpStatusCode.BadRequest, unknownDistribution.StatusCode);
            StringAssert.Contains(await unknownDistribution.Content.ReadAsStringAsync(), "distribution");

            var garbageNodes = await client.GetAsync(Ns + "/generate?nodeCount=abc&edgeCount=1");
            Assert.AreEqual(HttpStatusCode.BadRequest, garbageNodes.StatusCode);

            var negativeEdges = await client.GetAsync(Ns + "/generate?nodeCount=10&edgeCount=-1");
            Assert.AreEqual(HttpStatusCode.BadRequest, negativeEdges.StatusCode);

            // Nothing was created by the rejected calls.
            var engine = factory.Services
                .GetRequiredService<NoSQL.GraphDB.App.Namespaces.Fallen8Namespaces>().Default.Engine;
            Assert.AreEqual(0, engine.VertexCount);
        }
    }
}
