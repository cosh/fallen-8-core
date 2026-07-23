// MIT License
//
// NamespaceEndpointTest.cs
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
    /// Pins the namespace REST contract through the real hosted pipeline (feature
    /// graph-namespaces, Phase 2): the /ns CRUD status matrix with problem+json bodies, the
    /// /ns/{ns}/… route twins with bare-URL default aliasing, cross-namespace data isolation,
    /// and the 404-with-namespace-extension marker Studio keys its recover state on.
    /// </summary>
    [TestClass]
    public class NamespaceEndpointTest
    {
        private sealed class NamespaceFactory : WebApplicationFactory<Program>
        {
            private readonly string _maxNamespaces;

            public NamespaceFactory(string maxNamespaces = null)
            {
                _maxNamespaces = maxNamespaces;
            }

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseEnvironment("Development");
                // Volatile durability: booting the host writes no checkpoint/WAL into the test bin.
                builder.UseSetting("Fallen8:Durability:Volatile", "true");
                if (_maxNamespaces != null)
                {
                    builder.UseSetting("Fallen8:Namespaces:MaxNamespaces", _maxNamespaces);
                }
            }
        }

        #region helpers

        private static StringContent Json(string body)
        {
            return new StringContent(body, Encoding.UTF8, "application/json");
        }

        private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
        {
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        }

        private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status,
            string titleContains, string namespaceExtension = null)
        {
            Assert.AreEqual(status, response.StatusCode);
            Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var problem = await ReadJson(response);
            StringAssert.Contains(problem.GetProperty("title").GetString(), titleContains);
            if (namespaceExtension != null)
            {
                Assert.AreEqual(namespaceExtension, problem.GetProperty("namespace").GetString());
            }
        }

        private static async Task CreateNamespace(HttpClient client, string name)
        {
            using var response = await client.PutAsync("/ns/" + name, null);
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, "PUT /ns/" + name);
        }

        private static async Task CreateVertex(HttpClient client, string prefix)
        {
            using var response = await client.PutAsync(prefix + "/vertex?waitForCompletion=true",
                Json("{\"label\":\"person\",\"creationDate\":1,\"properties\":[]}"));
            Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, "PUT " + prefix + "/vertex");
        }

        private static async Task<int> VertexCount(HttpClient client, string prefix)
        {
            using var response = await client.GetAsync(prefix + "/vertex/count");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "GET " + prefix + "/vertex/count");
            return int.Parse(await response.Content.ReadAsStringAsync());
        }

        #endregion

        [TestMethod]
        public async Task FreshInstance_ListsOnlyTheDefaultNamespace_WithTheQuota()
        {
            using var factory = new NamespaceFactory();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/ns");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var list = await ReadJson(response);

            Assert.AreEqual(10000, list.GetProperty("maxNamespaces").GetInt32());
            var namespaces = list.GetProperty("namespaces");
            Assert.AreEqual(1, namespaces.GetArrayLength());
            var entry = namespaces[0];
            Assert.AreEqual("default", entry.GetProperty("name").GetString());
            Assert.AreEqual("ready", entry.GetProperty("state").GetString());
            Assert.AreEqual(0, entry.GetProperty("vertexCount").GetInt32());
            Assert.AreEqual(0, entry.GetProperty("edgeCount").GetInt32());
            Assert.IsFalse(string.IsNullOrEmpty(entry.GetProperty("createdAt").GetString()));
        }

        [TestMethod]
        public async Task Create_ThenList_ThenGetSingle_Roundtrips()
        {
            using var factory = new NamespaceFactory();
            using var client = factory.CreateClient();

            using var created = await client.PutAsync("/ns/flights", null);
            Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
            var body = await ReadJson(created);
            Assert.AreEqual("flights", body.GetProperty("name").GetString());
            Assert.AreEqual("ready", body.GetProperty("state").GetString());

            using var single = await client.GetAsync("/ns/flights");
            Assert.AreEqual(HttpStatusCode.OK, single.StatusCode);

            using var list = await client.GetAsync("/ns");
            var namespaces = (await ReadJson(list)).GetProperty("namespaces");
            Assert.AreEqual(2, namespaces.GetArrayLength());
            // Name-ordered: "default" < "flights".
            Assert.AreEqual("default", namespaces[0].GetProperty("name").GetString());
            Assert.AreEqual("flights", namespaces[1].GetProperty("name").GetString());
        }

        [TestMethod]
        public async Task Create_StatusMatrix_400_409_422()
        {
            // Quota 2 = default + one more, so the SECOND create trips the ceiling.
            using var factory = new NamespaceFactory(maxNamespaces: "2");
            using var client = factory.CreateClient();

            using var invalid = await client.PutAsync("/ns/Flights", null);
            await AssertProblem(invalid, HttpStatusCode.BadRequest, "Invalid namespace name");

            await CreateNamespace(client, "first");

            using var duplicate = await client.PutAsync("/ns/first", null);
            await AssertProblem(duplicate, HttpStatusCode.Conflict, "Namespace name in use");

            using var overQuota = await client.PutAsync("/ns/second", null);
            await AssertProblem(overQuota, (HttpStatusCode)422, "Namespace quota exceeded");
            var problem = await ReadJson(overQuota);
            Assert.AreEqual(2, problem.GetProperty("maxNamespaces").GetInt32());
        }

        [TestMethod]
        public async Task TwinRoutes_AddressIsolatedEngines_AndBareUrlsAliasDefault()
        {
            using var factory = new NamespaceFactory();
            using var client = factory.CreateClient();
            await CreateNamespace(client, "flights");
            await CreateNamespace(client, "scratch");

            await CreateVertex(client, "/ns/flights");
            await CreateVertex(client, "");            // bare = default

            Assert.AreEqual(1, await VertexCount(client, "/ns/flights"));
            Assert.AreEqual(0, await VertexCount(client, "/ns/scratch"));
            // The bare route and /ns/default are the SAME engine.
            Assert.AreEqual(1, await VertexCount(client, ""));
            Assert.AreEqual(1, await VertexCount(client, "/ns/default"));

            // Per-namespace status reports per-namespace counts.
            using var status = await client.GetAsync("/ns/scratch/status");
            Assert.AreEqual(HttpStatusCode.OK, status.StatusCode);
            Assert.AreEqual(0, (await ReadJson(status)).GetProperty("vertexCount").GetInt32());
        }

        [TestMethod]
        public async Task UnknownNamespace_Is404ProblemJson_WithTheNamespaceExtension()
        {
            using var factory = new NamespaceFactory();
            using var client = factory.CreateClient();

            using var read = await client.GetAsync("/ns/missing/vertex/count");
            await AssertProblem(read, HttpStatusCode.NotFound, "Namespace not found", namespaceExtension: "missing");

            // Mutations are refused BEFORE any action runs - nothing is created anywhere.
            using var write = await client.PutAsync("/ns/missing/vertex?waitForCompletion=true",
                Json("{\"label\":\"person\",\"creationDate\":1,\"properties\":[]}"));
            await AssertProblem(write, HttpStatusCode.NotFound, "Namespace not found", namespaceExtension: "missing");
            Assert.AreEqual(0, await VertexCount(client, ""));
        }

        [TestMethod]
        public async Task Rename_MovesTheAddress_AndPinsItsFailureMatrix()
        {
            using var factory = new NamespaceFactory();
            using var client = factory.CreateClient();
            await CreateNamespace(client, "flights");
            await CreateVertex(client, "/ns/flights");

            using var renamed = await client.PatchAsync("/ns/flights", Json("{\"name\":\"fl-eu\"}"));
            Assert.AreEqual(HttpStatusCode.OK, renamed.StatusCode);
            Assert.AreEqual("fl-eu", (await ReadJson(renamed)).GetProperty("name").GetString());

            // The data moved with the address; the old address is gone.
            Assert.AreEqual(1, await VertexCount(client, "/ns/fl-eu"));
            using var oldAddress = await client.GetAsync("/ns/flights/vertex/count");
            await AssertProblem(oldAddress, HttpStatusCode.NotFound, "Namespace not found", namespaceExtension: "flights");

            using var reserved = await client.PatchAsync("/ns/default", Json("{\"name\":\"renamed\"}"));
            await AssertProblem(reserved, HttpStatusCode.Conflict, "Reserved namespace");

            using var conflict = await client.PatchAsync("/ns/fl-eu", Json("{\"name\":\"default\"}"));
            await AssertProblem(conflict, HttpStatusCode.Conflict, "Namespace name in use");

            using var missing = await client.PatchAsync("/ns/missing", Json("{\"name\":\"target\"}"));
            await AssertProblem(missing, HttpStatusCode.NotFound, "Namespace not found");

            using var badBody = await client.PatchAsync("/ns/fl-eu", Json("{}"));
            await AssertProblem(badBody, HttpStatusCode.BadRequest, "Invalid namespace name");
        }

        [TestMethod]
        public async Task Drop_RemovesEntryAndRoutes_AndDefaultIsUndeletable()
        {
            using var factory = new NamespaceFactory();
            using var client = factory.CreateClient();
            await CreateNamespace(client, "flights");

            using var dropped = await client.DeleteAsync("/ns/flights");
            Assert.AreEqual(HttpStatusCode.NoContent, dropped.StatusCode);

            using var entry = await client.GetAsync("/ns/flights");
            await AssertProblem(entry, HttpStatusCode.NotFound, "Namespace not found");
            using var twin = await client.GetAsync("/ns/flights/vertex/count");
            await AssertProblem(twin, HttpStatusCode.NotFound, "Namespace not found", namespaceExtension: "flights");

            using var again = await client.DeleteAsync("/ns/flights");
            await AssertProblem(again, HttpStatusCode.NotFound, "Namespace not found");

            using var reserved = await client.DeleteAsync("/ns/default");
            await AssertProblem(reserved, HttpStatusCode.Conflict, "Reserved namespace");
        }
    }
}
