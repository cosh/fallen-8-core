// MIT License
//
// BenchmarkEndpointTest.cs
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
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Pipeline tests for GET /benchmark (feature web-ui): the endpoint returns structured
    /// JSON statistics instead of a formatted (locale-dependent) string, defaults to 1000
    /// iterations when the parameter is omitted, and maps the empty-graph and bad-input
    /// cases to 400 instead of a 200 with an error sentence.
    /// </summary>
    [TestClass]
    public class BenchmarkEndpointTest
    {
        private sealed class VolatileFactory : WebApplicationFactory<Program>
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseSetting("Fallen8:Durability:Volatile", "true");
            }
        }

        [TestMethod]
        public async Task Benchmark_OnGeneratedGraph_ReturnsStructuredStatistics()
        {
            using var factory = new VolatileFactory();
            using var client = factory.CreateClient();

            var generate = await client.GetAsync("/generate?nodeCount=50&edgeCount=2");
            Assert.AreEqual(HttpStatusCode.OK, generate.StatusCode);

            var response = await client.GetAsync("/benchmark?iterations=3");
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
            using var factory = new VolatileFactory();
            using var client = factory.CreateClient();

            var response = await client.GetAsync("/benchmark?iterations=1");
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            StringAssert.Contains(await response.Content.ReadAsStringAsync(), "No vertices");
        }

        [TestMethod]
        public async Task Benchmark_NonPositiveOrGarbageIterations_Return400()
        {
            using var factory = new VolatileFactory();
            using var client = factory.CreateClient();

            var generate = await client.GetAsync("/generate?nodeCount=10&edgeCount=1");
            Assert.AreEqual(HttpStatusCode.OK, generate.StatusCode);

            var zero = await client.GetAsync("/benchmark?iterations=0");
            Assert.AreEqual(HttpStatusCode.BadRequest, zero.StatusCode);

            var garbage = await client.GetAsync("/benchmark?iterations=abc");
            Assert.AreEqual(HttpStatusCode.BadRequest, garbage.StatusCode);
        }

        [TestMethod]
        public async Task Generate_PreferentialDistribution_ProducesHubs_AndTheExactEdgeCount()
        {
            using var factory = new VolatileFactory();
            using var client = factory.CreateClient();

            const int nodes = 2000;
            const int edgesPerVertex = 3;
            var generate = await client.GetAsync(
                $"/generate?nodeCount={nodes}&edgeCount={edgesPerVertex}&distribution=preferential");
            Assert.AreEqual(HttpStatusCode.OK, generate.StatusCode, await generate.Content.ReadAsStringAsync());

            var engine = factory.Services
                .GetRequiredService<NoSQL.GraphDB.App.Namespaces.Fallen8Namespaces>().Default.Engine;
            Assert.AreEqual(nodes, engine.VertexCount);

            // Vertex i gets min(edgesPerVertex, i) out-edges - an exact, seed-independent count.
            var expectedEdges = nodes * edgesPerVertex - edgesPerVertex * (edgesPerVertex + 1) / 2;
            Assert.AreEqual(expectedEdges, engine.EdgeCount);

            // The point of preferential attachment: heavy-tailed in-degrees. Uniform random
            // in-degrees are ~Poisson(3) (max ≈ 12 over 2000 draws); Barabási–Albert growth
            // gives the earliest vertices in-degrees in the hundreds - 10× the mean separates
            // the two distributions with enormous margin, keeping the assertion seed-proof.
            var maxInDegree = 0u;
            foreach (var vertex in engine.GetAllVertices())
            {
                maxInDegree = Math.Max(maxInDegree, vertex.GetInDegree());
            }
            Assert.IsTrue(maxInDegree >= 10 * edgesPerVertex,
                $"expected a hub (in-degree >= {10 * edgesPerVertex}), got max {maxInDegree}");

            // The generated edges carry property "A", so the benchmark pairing holds.
            var benchmark = await client.GetAsync("/benchmark?iterations=1");
            Assert.AreEqual(HttpStatusCode.OK, benchmark.StatusCode);
            using var body = JsonDocument.Parse(await benchmark.Content.ReadAsStringAsync());
            Assert.AreEqual(expectedEdges, body.RootElement.GetProperty("edgesTraversed").GetInt64());
        }

        [TestMethod]
        public async Task Generate_EdgeCountExceedingNodeCount_IsCappedNotHung()
        {
            // Regression: uniform generation used to spin forever when edgeCount > nodeCount
            // (only nodeCount distinct targets exist). It must complete and cap per-vertex edges.
            using var factory = new VolatileFactory();
            using var client = factory.CreateClient();

            var generate = await client.GetAsync("/generate?nodeCount=3&edgeCount=10");
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
            using var factory = new VolatileFactory();
            using var client = factory.CreateClient();

            var generate = await client.GetAsync("/generate?nodeCount=0&edgeCount=5");
            Assert.AreEqual(HttpStatusCode.OK, generate.StatusCode);
            var engine = factory.Services
                .GetRequiredService<NoSQL.GraphDB.App.Namespaces.Fallen8Namespaces>().Default.Engine;
            Assert.AreEqual(0, engine.VertexCount);
            Assert.AreEqual(0, engine.EdgeCount);
        }

        [TestMethod]
        public async Task Generate_ValidatesItsInputs_With400s()
        {
            using var factory = new VolatileFactory();
            using var client = factory.CreateClient();

            var unknownDistribution = await client.GetAsync("/generate?nodeCount=10&edgeCount=1&distribution=banana");
            Assert.AreEqual(HttpStatusCode.BadRequest, unknownDistribution.StatusCode);
            StringAssert.Contains(await unknownDistribution.Content.ReadAsStringAsync(), "distribution");

            var garbageNodes = await client.GetAsync("/generate?nodeCount=abc&edgeCount=1");
            Assert.AreEqual(HttpStatusCode.BadRequest, garbageNodes.StatusCode);

            var negativeEdges = await client.GetAsync("/generate?nodeCount=10&edgeCount=-1");
            Assert.AreEqual(HttpStatusCode.BadRequest, negativeEdges.StatusCode);

            // Nothing was created by the rejected calls.
            var engine = factory.Services
                .GetRequiredService<NoSQL.GraphDB.App.Namespaces.Fallen8Namespaces>().Default.Engine;
            Assert.AreEqual(0, engine.VertexCount);
        }
    }
}
