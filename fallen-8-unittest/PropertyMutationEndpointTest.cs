// MIT License
//
// PropertyMutationEndpointTest.cs
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

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Pipeline regression test for PUT /graphelement/{id}/{propertyId} (feature web-ui,
    /// FR-21): the endpoint used to throw a NullReferenceException because the transaction's
    /// Definition was written through a nested object initializer against a null default.
    /// Pins the whole add-property/remove-property round trip through the real pipeline.
    /// </summary>
    [TestClass]
    public class PropertyMutationEndpointTest
    {
        private sealed class VolatileFactory : WebApplicationFactory<Program>
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseSetting("Fallen8:Durability:Volatile", "true");
            }
        }

        private static StringContent Json(string payload)
        {
            return new StringContent(payload, Encoding.UTF8, "application/json");
        }

        [TestMethod]
        public async Task AddProperty_SetsAndRemoves_ThroughThePipeline()
        {
            using var factory = new VolatileFactory();
            using var client = factory.CreateClient();

            var create = await client.PutAsync("/vertex?waitForCompletion=true",
                Json("{\"label\":\"prop-test\",\"creationDate\":0}"));
            Assert.AreEqual(HttpStatusCode.Accepted, create.StatusCode);

            // The create endpoints return no id; find the vertex via the bulk view.
            var graphJson = await client.GetStringAsync("/graph?maxElements=100");
            using var graph = JsonDocument.Parse(graphJson);
            var id = -1;
            foreach (var vertex in graph.RootElement.GetProperty("vertices").EnumerateArray())
            {
                if (vertex.TryGetProperty("label", out var label) && label.GetString() == "prop-test")
                {
                    id = vertex.GetProperty("id").GetInt32();
                }
            }
            Assert.IsTrue(id >= 0, "The created vertex must be discoverable via /graph.");

            var setProperty = await client.PutAsync($"/graphelement/{id}/age?waitForCompletion=true",
                Json("{\"propertyId\":\"age\",\"propertyValue\":\"42\",\"fullQualifiedTypeName\":\"System.Int32\"}"));
            Assert.AreEqual(HttpStatusCode.Accepted, setProperty.StatusCode,
                "Setting a property must be a 202, not a 500 (regression: null Definition).");

            var element = await client.GetStringAsync($"/graphelement/{id}");
            StringAssert.Contains(element, "\"age\"");
            StringAssert.Contains(element, "42");

            var removeProperty = await client.DeleteAsync($"/graphelement/{id}/age?waitForCompletion=true");
            Assert.AreEqual(HttpStatusCode.Accepted, removeProperty.StatusCode);

            var afterRemove = await client.GetStringAsync($"/graphelement/{id}");
            Assert.IsFalse(afterRemove.Contains("\"age\""), "The removed property must be gone.");
        }

        #region the UPDATE path (feature platform-integrity-audit W2)

        /// <summary>Creates one vertex and returns its id, via the bulk view (creates return no id).</summary>
        private static async Task<int> NewVertex(HttpClient client, string label)
        {
            var create = await client.PutAsync("/vertex?waitForCompletion=true",
                Json("{\"label\":\"" + label + "\",\"creationDate\":0}"));
            Assert.AreEqual(HttpStatusCode.Accepted, create.StatusCode);

            var graphJson = await client.GetStringAsync("/graph?maxElements=1000");
            using var graph = JsonDocument.Parse(graphJson);
            var id = -1;
            foreach (var vertex in graph.RootElement.GetProperty("vertices").EnumerateArray())
            {
                if (vertex.TryGetProperty("label", out var l) && l.GetString() == label)
                {
                    id = vertex.GetProperty("id").GetInt32();
                }
            }
            Assert.IsTrue(id >= 0, "The created vertex must be discoverable via /graph.");
            return id;
        }

        private static string StringProperty(string payload, string key)
        {
            using var element = JsonDocument.Parse(payload);
            foreach (var property in element.RootElement.GetProperty("properties").EnumerateArray())
            {
                if (property.GetProperty("propertyId").GetString() == key)
                {
                    return property.GetProperty("propertyValue").GetString();
                }
            }
            return null;
        }

        [TestMethod]
        public async Task PutProperty_UpdatesAnExistingValue_RatherThanSilentlyDiscardingIt()
        {
            // THE defect W2 fixes, at the HTTP boundary. On AddPropertyTransaction this route could
            // not update: waited it answered 500, and UNWAITED (the default) it answered 202 Accepted
            // and wrote nothing. Both halves are asserted, because the unwaited one is the dangerous
            // one - a client that never passes waitForCompletion saw success and lost every update.
            using var factory = new VolatileFactory();
            using var client = factory.CreateClient();
            var id = await NewVertex(client, "w2-update");

            var first = await client.PutAsync($"/graphelement/{id}/ip?waitForCompletion=true",
                Json("{\"propertyId\":\"ip\",\"propertyValue\":\"10.0.0.5\",\"fullQualifiedTypeName\":\"System.String\"}"));
            Assert.AreEqual(HttpStatusCode.Accepted, first.StatusCode);

            var update = await client.PutAsync($"/graphelement/{id}/ip?waitForCompletion=true",
                Json("{\"propertyId\":\"ip\",\"propertyValue\":\"10.0.0.9\",\"fullQualifiedTypeName\":\"System.String\"}"));
            Assert.AreEqual(HttpStatusCode.Accepted, update.StatusCode,
                "An update must not answer 500; the route is documented as adding OR updating.");
            Assert.AreEqual("10.0.0.9", StringProperty(await client.GetStringAsync($"/graphelement/{id}"), "ip"),
                "The updated value must be stored.");

            // Unwaited: still 202, but now the write actually happened.
            var unwaited = await client.PutAsync($"/graphelement/{id}/ip",
                Json("{\"propertyId\":\"ip\",\"propertyValue\":\"10.0.0.11\",\"fullQualifiedTypeName\":\"System.String\"}"));
            Assert.AreEqual(HttpStatusCode.Accepted, unwaited.StatusCode);
            Assert.IsTrue(SpinWait.SpinUntil(
                () => StringProperty(client.GetStringAsync($"/graphelement/{id}").Result, "ip") == "10.0.0.11", 5000),
                "An unwaited update reported 202 and must actually apply - the silent-discard case.");
        }

        [TestMethod]
        public async Task PutProperties_SetsAndRemoves_InOneBatch()
        {
            using var factory = new VolatileFactory();
            using var client = factory.CreateClient();
            var id = await NewVertex(client, "w2-batch");

            await client.PutAsync($"/graphelement/{id}/stale?waitForCompletion=true",
                Json("{\"propertyId\":\"stale\",\"propertyValue\":\"yes\",\"fullQualifiedTypeName\":\"System.String\"}"));

            var batch = await client.PutAsync("/graphelements/properties?waitForCompletion=true",
                Json("[" +
                     "{\"graphElementId\":" + id + ",\"propertyId\":\"ip\",\"propertyValue\":\"10.0.0.9\",\"fullQualifiedTypeName\":\"System.String\"}," +
                     "{\"graphElementId\":" + id + ",\"propertyId\":\"stale\",\"remove\":true}" +
                     "]"));

            Assert.AreEqual(HttpStatusCode.Accepted, batch.StatusCode);
            var element = await client.GetStringAsync($"/graphelement/{id}");
            Assert.AreEqual("10.0.0.9", StringProperty(element, "ip"));
            Assert.IsNull(StringProperty(element, "stale"), "The batch removal removed.");
        }

        [TestMethod]
        public async Task PutProperties_RejectsABadValue_WithoutWritingAnything()
        {
            using var factory = new VolatileFactory();
            using var client = factory.CreateClient();
            var id = await NewVertex(client, "w2-reject");

            var batch = await client.PutAsync("/graphelements/properties?waitForCompletion=true",
                Json("[" +
                     "{\"graphElementId\":" + id + ",\"propertyId\":\"ok\",\"propertyValue\":\"1\",\"fullQualifiedTypeName\":\"System.Int32\"}," +
                     "{\"graphElementId\":" + id + ",\"propertyId\":\"bad\",\"propertyValue\":\"not-a-number\",\"fullQualifiedTypeName\":\"System.Int32\"}" +
                     "]"));

            Assert.AreEqual(HttpStatusCode.BadRequest, batch.StatusCode);
            var element = await client.GetStringAsync($"/graphelement/{id}");
            Assert.IsNull(StringProperty(element, "ok"),
                "A rejected batch is rejected BEFORE anything is enqueued, so the valid write must not land either.");
        }

        [TestMethod]
        public async Task DeleteGraphElements_RemovesManyInOneBatch()
        {
            using var factory = new VolatileFactory();
            using var client = factory.CreateClient();
            var first = await NewVertex(client, "w2-del-a");
            var second = await NewVertex(client, "w2-del-b");

            var removal = await client.SendAsync(new HttpRequestMessage(
                HttpMethod.Delete, "/graphelements?waitForCompletion=true")
            {
                Content = Json("[" + first + "," + second + "]")
            });

            Assert.AreEqual(HttpStatusCode.Accepted, removal.StatusCode);
            Assert.AreEqual(HttpStatusCode.NoContent,
                (await client.GetAsync($"/graphelement/{first}")).StatusCode, "the first is gone");
            Assert.AreEqual(HttpStatusCode.NoContent,
                (await client.GetAsync($"/graphelement/{second}")).StatusCode, "the second is gone");
        }

        [TestMethod]
        public async Task PostGraphElementsGet_ReadsManyAtOnce_AndNamesTheMissingOnes()
        {
            // The read-side mirror of the batch write path. Also pins the two properties a
            // write-only-if-changed caller depends on: the returned value form is the one that can be
            // written straight back, and a missing id is NAMED rather than silently absent.
            using var factory = new VolatileFactory();
            using var client = factory.CreateClient();
            var first = await NewVertex(client, "w6-a");
            var second = await NewVertex(client, "w6-b");
            await client.PutAsync($"/graphelement/{first}/ip?waitForCompletion=true",
                Json("{\"propertyId\":\"ip\",\"propertyValue\":\"10.0.0.5\",\"fullQualifiedTypeName\":\"System.String\"}"));

            var response = await client.PostAsync("/graphelements/get",
                Json("[" + first + "," + second + ",999999," + first + "]"));

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            var elements = body.RootElement.GetProperty("elements");
            Assert.AreEqual(2, elements.GetArrayLength(),
                "two elements exist, and the duplicated id collapses to one entry");

            var notFound = body.RootElement.GetProperty("notFound");
            Assert.AreEqual(1, notFound.GetArrayLength());
            Assert.AreEqual(999999, notFound[0].GetInt32());

            // The projection carries the kind (the singular getters are per-kind) and the writable
            // property form, and deliberately no adjacency.
            var firstElement = elements[0];
            Assert.AreEqual("vertex", firstElement.GetProperty("kind").GetString());
            Assert.IsFalse(firstElement.TryGetProperty("outEdges", out _),
                "adjacency is deliberately omitted: this route answers what an element HOLDS");
            var ip = firstElement.GetProperty("properties")[0];
            Assert.AreEqual("ip", ip.GetProperty("propertyId").GetString());
            Assert.AreEqual("10.0.0.5", ip.GetProperty("propertyValue").GetString());
            Assert.AreEqual("System.String", ip.GetProperty("fullQualifiedTypeName").GetString());
        }

        [TestMethod]
        public async Task PostGraphElementsGet_RejectsANullList()
        {
            using var factory = new VolatileFactory();
            using var client = factory.CreateClient();

            var response = await client.PostAsync("/graphelements/get", Json("null"));

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        }

        #endregion
    }
}
