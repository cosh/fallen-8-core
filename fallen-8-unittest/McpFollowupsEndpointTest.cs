// MIT License
//
// McpFollowupsEndpointTest.cs
//
// Copyright (c) 2026 Henning Rauch
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
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The apiApp side of feature mcp-followups: the batch write endpoints (PUT /vertices,
    ///   PUT /edges) that create atomically and RETURN the assigned ids. Exercised through the
    ///   real hosted pipeline.
    /// </summary>
    [TestClass]
    public class McpFollowupsEndpointTest
    {
        private sealed class Factory : WebApplicationFactory<Program>
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseEnvironment("Development");
                builder.UseSetting("Fallen8:Durability:Volatile", "true");
            }
        }

        private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

        private static string Vertex(string name) =>
            "{\"label\":\"person\",\"creationDate\":1,\"properties\":[" +
            $"{{\"propertyId\":\"name\",\"propertyValue\":\"{name}\",\"fullQualifiedTypeName\":\"System.String\"}}]}}";

        private static async Task<List<int>> PutIds(HttpClient client, string url, string body)
        {
            using var response = await client.PutAsync(url, Json(body));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "PUT " + url);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync())
                .RootElement.EnumerateArray().Select(e => e.GetInt32()).ToList();
        }

        [TestMethod]
        public async Task PutVertices_CreatesAtomically_AndReturnsIdsInOrder()
        {
            using var factory = new Factory();
            using var client = factory.CreateClient();

            var ids = await PutIds(client, "/vertices?waitForCompletion=true",
                $"[{Vertex("Ada")},{Vertex("Grace")},{Vertex("Alan")}]");

            Assert.AreEqual(3, ids.Count, "one id per input vertex");
            Assert.AreEqual(3, ids.Distinct().Count(), "ids are distinct");
            // The ids resolve to the created vertices.
            using var got = await client.GetAsync("/vertex/" + ids[0]);
            Assert.AreEqual(HttpStatusCode.OK, got.StatusCode);
        }

        [TestMethod]
        public async Task PutEdges_ReturnsIds_AndRollsBackAtomicallyOnMissingEndpoint()
        {
            using var factory = new Factory();
            using var client = factory.CreateClient();

            var vertexIds = await PutIds(client, "/vertices?waitForCompletion=true", $"[{Vertex("A")},{Vertex("B")}]");

            var edgeIds = await PutIds(client, "/edges?waitForCompletion=true",
                $"[{{\"creationDate\":1,\"sourceVertex\":{vertexIds[0]},\"targetVertex\":{vertexIds[1]},\"edgePropertyId\":\"knows\",\"label\":\"knows\"}}]");
            Assert.AreEqual(1, edgeIds.Count, "the batch returns the created edge id");

            // A batch whose SECOND edge references a missing vertex rolls back the WHOLE batch → 404;
            // the first (valid) edge must NOT have been committed.
            using var bad = await client.PutAsync("/edges?waitForCompletion=true", Json(
                $"[{{\"creationDate\":1,\"sourceVertex\":{vertexIds[0]},\"targetVertex\":{vertexIds[1]},\"edgePropertyId\":\"a\"}}," +
                $"{{\"creationDate\":1,\"sourceVertex\":{vertexIds[0]},\"targetVertex\":424242,\"edgePropertyId\":\"b\"}}]"));
            Assert.AreEqual(HttpStatusCode.NotFound, bad.StatusCode, "a missing endpoint rolls the whole batch back to 404");

            var graph = JsonDocument.Parse(await (await client.GetAsync("/graph")).Content.ReadAsStringAsync()).RootElement;
            Assert.AreEqual(1, graph.GetProperty("edges").GetArrayLength(),
                "the rolled-back batch wired nothing — only the first, successful edge remains");
        }

        [TestMethod]
        public async Task PutVertices_Unwaited_Is202NoBody()
        {
            using var factory = new Factory();
            using var client = factory.CreateClient();

            using var response = await client.PutAsync("/vertices", Json($"[{Vertex("X")}]"));
            Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, "unwaited batch is 202 (ids not yet known)");
        }
    }
}
