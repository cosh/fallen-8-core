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

using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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
    }
}
