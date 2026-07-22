// MIT License
//
// HostedRoutingSmokeTest.cs
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
using System.Linq;
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
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Index.Spatial;
using NoSQL.GraphDB.Core.Index.Spatial.Implementation.Geometry;
using NoSQL.GraphDB.Core.Index.Spatial.Implementation.Metric;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Routing smokes for the GraphController surface through the REAL hosted pipeline
    /// (feature structural-decomposition, Phase 3). The existing GraphController unit tests
    /// construct the controller directly and bypass routing, model binding and content
    /// negotiation - these pins go through <see cref="WebApplicationFactory{TEntryPoint}"/> so
    /// the absolute route templates, [FromRoute]/[FromBody]/[FromQuery] binding and the
    /// literal-vs-parameter route precedence stay pinned while the controller is decomposed.
    /// Seeding is via the API itself wherever the API can express it; only the spatial index
    /// (not creatable over REST - pinned by StatusIndexInventoryTest) is seeded against the
    /// hosted engine singleton.
    /// </summary>
    [TestClass]
    public class HostedRoutingSmokeTest
    {
        private sealed class RoutingFactory : WebApplicationFactory<Program>
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseEnvironment("Development");
                // Volatile durability: booting the host writes no checkpoint/WAL into the test bin.
                builder.UseSetting("Fallen8:Durability:Volatile", "true");
            }
        }

        #region helpers

        private static StringContent Json(string body)
        {
            return new StringContent(body, Encoding.UTF8, "application/json");
        }

        private static async Task<JsonElement> GetJson(HttpClient client, string url)
        {
            using var response = await client.GetAsync(url);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "GET " + url);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        }

        /// <summary>PUT /vertex with a name (string) and age (int) property; committed before returning.</summary>
        private static async Task CreateVertexViaApi(HttpClient client, string name, int age, string label = "person")
        {
            using var response = await client.PutAsync("/vertex?waitForCompletion=true", Json(
                "{\"label\":\"" + label + "\",\"creationDate\":1,\"properties\":[" +
                "{\"propertyId\":\"name\",\"propertyValue\":\"" + name + "\",\"fullQualifiedTypeName\":\"System.String\"}," +
                "{\"propertyId\":\"age\",\"propertyValue\":\"" + age + "\",\"fullQualifiedTypeName\":\"System.Int32\"}]}"));
            Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, "PUT /vertex must answer 202");
        }

        /// <summary>Resolves the seeded vertices' ids by their name property via GET /graph.</summary>
        private static async Task<Dictionary<string, int>> VerticesByName(HttpClient client)
        {
            var graph = await GetJson(client, "/graph");
            var byName = new Dictionary<string, int>();
            foreach (var vertex in graph.GetProperty("vertices").EnumerateArray())
            {
                foreach (var property in vertex.GetProperty("properties").EnumerateArray())
                {
                    if (property.GetProperty("propertyId").GetString() == "name")
                    {
                        byName[property.GetProperty("propertyValue").GetString()] = vertex.GetProperty("id").GetInt32();
                    }
                }
            }
            return byName;
        }

        private static async Task CreateIndexViaApi(HttpClient client, string uniqueId, string pluginType)
        {
            using var response = await client.PostAsync("/index",
                Json("{\"uniqueId\":\"" + uniqueId + "\",\"pluginType\":\"" + pluginType + "\"}"));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("true", await response.Content.ReadAsStringAsync(),
                "POST /index must create the " + pluginType);
        }

        private static async Task AddToIndexViaApi(HttpClient client, string indexId, int graphElementId,
            string keyValue, string keyTypeName)
        {
            using var response = await client.PutAsync("/index/" + indexId, Json(
                "{\"graphElementId\":" + graphElementId + ",\"key\":{\"propertyId\":\"key\",\"propertyValue\":\"" +
                keyValue + "\",\"fullQualifiedTypeName\":\"" + keyTypeName + "\"}}"));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("true", await response.Content.ReadAsStringAsync(), "PUT /index/" + indexId);
        }

        /// <summary>POSTs a scan and returns the id array (asserting 200).</summary>
        private static async Task<List<int>> PostScanIds(HttpClient client, string url, string body)
        {
            using var response = await client.PostAsync(url, Json(body));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "POST " + url);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync())
                .RootElement.EnumerateArray().Select(e => e.GetInt32()).ToList();
        }

        /// <summary>POST /scan/index/all with Equals over a string key.</summary>
        private static Task<List<int>> IndexScanEquals(HttpClient client, string indexId, string key)
        {
            return PostScanIds(client, "/scan/index/all",
                "{\"indexId\":\"" + indexId + "\",\"operator\":0,\"literal\":{\"value\":\"" + key +
                "\",\"fullQualifiedTypeName\":\"System.String\"},\"resultType\":\"Both\"}");
        }

        #endregion

        #region /vertex

        [TestMethod]
        public async Task VertexRoutes_PutThenGet_RoundTrip_AndBindingFailuresAre4xx()
        {
            using var factory = new RoutingFactory();
            using var client = factory.CreateClient();

            await CreateVertexViaApi(client, "Alice", 30);
            var ids = await VerticesByName(client);
            Assert.IsTrue(ids.ContainsKey("Alice"), "the committed vertex is visible via GET /graph");

            var vertex = await GetJson(client, "/vertex/" + ids["Alice"]);
            Assert.AreEqual(ids["Alice"], vertex.GetProperty("id").GetInt32());
            Assert.AreEqual("person", vertex.GetProperty("label").GetString());
            var age = vertex.GetProperty("properties").EnumerateArray()
                .Single(p => p.GetProperty("propertyId").GetString() == "age");
            Assert.AreEqual("30", age.GetProperty("propertyValue").GetString());
            Assert.AreEqual("System.Int32", age.GetProperty("fullQualifiedTypeName").GetString(),
                "the typed property survives the wire round-trip");

            using (var missing = await client.GetAsync("/vertex/424242"))
            {
                Assert.AreEqual(HttpStatusCode.NoContent, missing.StatusCode,
                    "a missing vertex is the documented 204, not an error");
            }

            using (var malformedId = await client.GetAsync("/vertex/not-a-number"))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, malformedId.StatusCode,
                    "a non-integer id fails route binding -> 400 (api-error-contract E2)");
            }

            using (var malformedBody = await client.PutAsync("/vertex", Json("{ this is not json")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, malformedBody.StatusCode,
                    "an unparsable JSON body is a 400, not a 500");
            }
        }

        #endregion

        #region /edge

        [TestMethod]
        public async Task EdgeRoutes_PutThenReadBack_AndFailuresAre4xx()
        {
            using var factory = new RoutingFactory();
            using var client = factory.CreateClient();

            await CreateVertexViaApi(client, "Source", 1);
            await CreateVertexViaApi(client, "Target", 2);
            var ids = await VerticesByName(client);

            using (var put = await client.PutAsync("/edge?waitForCompletion=true", Json(
                "{\"creationDate\":1,\"sourceVertex\":" + ids["Source"] + ",\"targetVertex\":" + ids["Target"] +
                ",\"edgePropertyId\":\"knows\",\"label\":\"knows\"}")))
            {
                Assert.AreEqual(HttpStatusCode.Accepted, put.StatusCode, "PUT /edge must answer 202");
            }

            var graph = await GetJson(client, "/graph");
            var edges = graph.GetProperty("edges").EnumerateArray().ToList();
            Assert.AreEqual(1, edges.Count);
            var edgeId = edges[0].GetProperty("id").GetInt32();

            var edge = await GetJson(client, "/edge/" + edgeId);
            Assert.AreEqual("knows", edge.GetProperty("label").GetString());
            Assert.AreEqual(ids["Source"], edge.GetProperty("sourceVertex").GetInt32());
            Assert.AreEqual(ids["Target"], edge.GetProperty("targetVertex").GetInt32());

            Assert.AreEqual(ids["Source"], (await GetJson(client, "/edge/" + edgeId + "/source")).GetInt32());
            Assert.AreEqual(ids["Target"], (await GetJson(client, "/edge/" + edgeId + "/target")).GetInt32());

            // A waited-on edge to missing endpoints rolls back as NotFound -> 404 over the wire.
            using (var dangling = await client.PutAsync("/edge?waitForCompletion=true", Json(
                "{\"creationDate\":1,\"sourceVertex\":424242,\"targetVertex\":424243,\"edgePropertyId\":\"knows\"}")))
            {
                Assert.AreEqual(HttpStatusCode.NotFound, dangling.StatusCode,
                    "a rolled-back edge create (missing endpoints) surfaces as 404, not 202");
            }

            using (var malformedId = await client.GetAsync("/edge/not-a-number"))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, malformedId.StatusCode);
            }
        }

        #endregion

        #region /graph

        [TestMethod]
        public async Task GraphRoute_MaxElementsClampsThePage_AndAMalformedQueryIs400()
        {
            using var factory = new RoutingFactory();
            using var client = factory.CreateClient();

            await CreateVertexViaApi(client, "A", 1);
            await CreateVertexViaApi(client, "B", 2);
            await CreateVertexViaApi(client, "C", 3);
            var ids = await VerticesByName(client);
            using (var put = await client.PutAsync("/edge?waitForCompletion=true", Json(
                "{\"creationDate\":1,\"sourceVertex\":" + ids["A"] + ",\"targetVertex\":" + ids["B"] +
                ",\"edgePropertyId\":\"knows\"}")))
            {
                Assert.AreEqual(HttpStatusCode.Accepted, put.StatusCode);
            }

            var full = await GetJson(client, "/graph");
            Assert.AreEqual(3, full.GetProperty("vertices").GetArrayLength());
            Assert.AreEqual(1, full.GetProperty("edges").GetArrayLength());

            var page = await GetJson(client, "/graph?maxElements=2");
            Assert.AreEqual(2, page.GetProperty("vertices").GetArrayLength(),
                "maxElements bounds the vertex page");
            Assert.AreEqual(1, page.GetProperty("edges").GetArrayLength(),
                "maxElements bounds vertices and edges independently");

            var empty = await GetJson(client, "/graph?maxElements=0");
            Assert.AreEqual(0, empty.GetProperty("vertices").GetArrayLength());
            Assert.AreEqual(0, empty.GetProperty("edges").GetArrayLength());

            var negative = await GetJson(client, "/graph?maxElements=-5");
            Assert.AreEqual(0, negative.GetProperty("vertices").GetArrayLength(),
                "a negative maxElements clamps to an empty page (api-error-contract E6)");

            using (var malformed = await client.GetAsync("/graph?maxElements=not-a-number"))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, malformed.StatusCode,
                    "a non-integer maxElements fails query binding -> 400");
            }
        }

        #endregion

        #region /scan/graph/property/{propertyId}

        [TestMethod]
        public async Task PropertyScan_MatchesEmptyAndErrorShapes()
        {
            using var factory = new RoutingFactory();
            using var client = factory.CreateClient();

            await CreateVertexViaApi(client, "A20", 20);
            await CreateVertexViaApi(client, "A30", 30);
            await CreateVertexViaApi(client, "A40", 40);
            var ids = await VerticesByName(client);

            // operator 1 = Greater (BinaryOperator serializes numerically on this surface).
            var greater = await PostScanIds(client, "/scan/graph/property/age",
                "{\"operator\":1,\"literal\":{\"value\":\"25\",\"fullQualifiedTypeName\":\"System.Int32\"},\"resultType\":\"Vertices\"}");
            CollectionAssert.AreEquivalent(new List<int> { ids["A30"], ids["A40"] }, greater,
                "age > 25 matches exactly the 30- and 40-year-olds");

            var none = await PostScanIds(client, "/scan/graph/property/age",
                "{\"operator\":0,\"literal\":{\"value\":\"99\",\"fullQualifiedTypeName\":\"System.Int32\"},\"resultType\":\"Vertices\"}");
            Assert.AreEqual(0, none.Count, "a no-match scan answers 200 with an empty array");

            using (var badType = await client.PostAsync("/scan/graph/property/age",
                Json("{\"operator\":0,\"literal\":{\"value\":\"25\",\"fullQualifiedTypeName\":\"System.Nope\"},\"resultType\":\"Vertices\"}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, badType.StatusCode,
                    "an unknown literal type name is a 400 (api-error-contract E3)");
            }

            using (var noLiteral = await client.PostAsync("/scan/graph/property/age",
                Json("{\"operator\":0,\"resultType\":\"Vertices\"}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, noLiteral.StatusCode,
                    "a scan without a literal is a 400");
            }
        }

        #endregion

        #region /scan/index/all

        [TestMethod]
        public async Task IndexScanAll_HitsMissesAndErrorShapes()
        {
            using var factory = new RoutingFactory();
            using var client = factory.CreateClient();

            await CreateVertexViaApi(client, "Alice", 30);
            await CreateVertexViaApi(client, "Bob", 40);
            var ids = await VerticesByName(client);

            await CreateIndexViaApi(client, "nameIdx", "DictionaryIndex");
            await AddToIndexViaApi(client, "nameIdx", ids["Alice"], "Alice", "System.String");
            await AddToIndexViaApi(client, "nameIdx", ids["Bob"], "Bob", "System.String");

            CollectionAssert.AreEquivalent(new List<int> { ids["Alice"] },
                await IndexScanEquals(client, "nameIdx", "Alice"));

            var unknownIndex = await IndexScanEquals(client, "no-such-index", "Alice");
            Assert.AreEqual(0, unknownIndex.Count, "an unknown index answers 200 with an empty array");

            using (var badType = await client.PostAsync("/scan/index/all",
                Json("{\"indexId\":\"nameIdx\",\"operator\":0,\"literal\":{\"value\":\"Alice\",\"fullQualifiedTypeName\":\"System.Nope\"},\"resultType\":\"Vertices\"}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, badType.StatusCode);
            }
        }

        #endregion

        #region /scan/index/range

        [TestMethod]
        public async Task RangeIndexScan_WindowAndErrorShapes()
        {
            using var factory = new RoutingFactory();
            using var client = factory.CreateClient();

            await CreateVertexViaApi(client, "A20", 20);
            await CreateVertexViaApi(client, "A30", 30);
            await CreateVertexViaApi(client, "A40", 40);
            var ids = await VerticesByName(client);

            await CreateIndexViaApi(client, "ageRange", "RangeIndex");
            await AddToIndexViaApi(client, "ageRange", ids["A20"], "20", "System.Int32");
            await AddToIndexViaApi(client, "ageRange", ids["A30"], "30", "System.Int32");
            await AddToIndexViaApi(client, "ageRange", ids["A40"], "40", "System.Int32");

            var window = await PostScanIds(client, "/scan/index/range",
                "{\"indexId\":\"ageRange\",\"leftLimit\":\"25\",\"rightLimit\":\"40\",\"fullQualifiedTypeName\":\"System.Int32\"," +
                "\"includeLeft\":true,\"includeRight\":true,\"resultType\":\"Vertices\"}");
            CollectionAssert.AreEquivalent(new List<int> { ids["A30"], ids["A40"] }, window,
                "the inclusive [25, 40] window matches exactly the 30 and 40 keys");

            using (var badLimit = await client.PostAsync("/scan/index/range",
                Json("{\"indexId\":\"ageRange\",\"leftLimit\":\"abc\",\"rightLimit\":\"40\",\"fullQualifiedTypeName\":\"System.Int32\"," +
                     "\"includeLeft\":true,\"includeRight\":true,\"resultType\":\"Vertices\"}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, badLimit.StatusCode,
                    "an unconvertible range limit is a 400, not a thrown FormatException");
            }

            using (var badType = await client.PostAsync("/scan/index/range",
                Json("{\"indexId\":\"ageRange\",\"leftLimit\":\"25\",\"rightLimit\":\"40\",\"fullQualifiedTypeName\":\"System.Nope\"," +
                     "\"includeLeft\":true,\"includeRight\":true,\"resultType\":\"Vertices\"}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, badType.StatusCode);
            }
        }

        #endregion

        #region /scan/index/fulltext

        [TestMethod]
        public async Task FulltextIndexScan_MatchMissAndMalformedBody()
        {
            using var factory = new RoutingFactory();
            using var client = factory.CreateClient();

            await CreateVertexViaApi(client, "Fox", 1);
            await CreateVertexViaApi(client, "Dog", 2);
            var ids = await VerticesByName(client);

            await CreateIndexViaApi(client, "fts", "RegExIndex");
            await AddToIndexViaApi(client, "fts", ids["Fox"], "The quick brown fox jumps", "System.String");
            await AddToIndexViaApi(client, "fts", ids["Dog"], "A lazy dog sleeps all day", "System.String");

            using (var hit = await client.PostAsync("/scan/index/fulltext",
                Json("{\"indexId\":\"fts\",\"requestString\":\"fox\"}")))
            {
                Assert.AreEqual(HttpStatusCode.OK, hit.StatusCode);
                var body = JsonDocument.Parse(await hit.Content.ReadAsStringAsync()).RootElement;
                var elements = body.GetProperty("elements").EnumerateArray().ToList();
                Assert.AreEqual(1, elements.Count, "exactly the fox sentence matches");
                Assert.AreEqual(ids["Fox"], elements[0].GetProperty("graphElementId").GetInt32());
                Assert.IsTrue(elements[0].GetProperty("highlights").GetArrayLength() >= 1,
                    "the match carries its highlight(s)");
            }

            using (var miss = await client.PostAsync("/scan/index/fulltext",
                Json("{\"indexId\":\"no-such-index\",\"requestString\":\"fox\"}")))
            {
                Assert.AreEqual(HttpStatusCode.NoContent, miss.StatusCode,
                    "an unknown fulltext index answers the null-result 204");
            }

            using (var malformed = await client.PostAsync("/scan/index/fulltext", Json("{ not json")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, malformed.StatusCode);
            }
        }

        #endregion

        #region /scan/index/spatial

        [TestMethod]
        public async Task SpatialIndexScan_DistanceSearchMissAndMalformedBody()
        {
            using var factory = new RoutingFactory();
            using var client = factory.CreateClient();

            await CreateVertexViaApi(client, "Origin", 1);
            await CreateVertexViaApi(client, "Nearby", 2);
            await CreateVertexViaApi(client, "Faraway", 3);
            var ids = await VerticesByName(client);

            // A spatial index is NOT creatable over REST (its Initialize needs live IMetric/Space
            // objects - pinned by StatusIndexInventoryTest), so seed it against the hosted engine
            // singleton; the HTTP request below still exercises the real route + body binding.
            var engine = (Fallen8)factory.Services.GetRequiredService<IFallen8>();
            Assert.IsTrue(engine.IndexFactory.TryCreateIndex(out var spatialIndex, "geo", "SpatialIndex",
                new Dictionary<string, object>
                {
                    { "IMetric", new EuclidianMetric() },
                    { "MinCount", 2 },
                    { "MaxCount", 5 },
                    { "Space", new List<IDimension> { new RealDimension(), new RealDimension() } }
                }), "the spatial index must register on the hosted engine's factory");
            Assert.IsTrue(engine.TryGetVertex(out var origin, ids["Origin"]));
            Assert.IsTrue(engine.TryGetVertex(out var nearby, ids["Nearby"]));
            Assert.IsTrue(engine.TryGetVertex(out var faraway, ids["Faraway"]));
            spatialIndex.AddOrUpdate(new Point(0f, 0f), origin);
            spatialIndex.AddOrUpdate(new Point(3f, 4f), nearby);    // distance 5 from the origin
            spatialIndex.AddOrUpdate(new Point(50f, 50f), faraway); // far outside the search radius

            var withinSix = await PostScanIds(client, "/scan/index/spatial",
                "{\"indexId\":\"geo\",\"graphElementId\":" + ids["Origin"] + ",\"distance\":6.0}");
            Assert.IsTrue(withinSix.Contains(ids["Nearby"]), "the (3,4) point lies within distance 6 of the origin");
            Assert.IsFalse(withinSix.Contains(ids["Faraway"]), "the (50,50) point does not");

            using (var missingElement = await client.PostAsync("/scan/index/spatial",
                Json("{\"indexId\":\"geo\",\"graphElementId\":424242,\"distance\":6.0}")))
            {
                Assert.AreEqual(HttpStatusCode.NoContent, missingElement.StatusCode,
                    "an unknown reference element answers the null-result 204");
            }

            using (var malformed = await client.PostAsync("/scan/index/spatial",
                Json("{\"indexId\":\"geo\",\"graphElementId\":" + ids["Origin"] + ",\"distance\":\"not-a-number\"}")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, malformed.StatusCode,
                    "a non-numeric distance fails body binding -> 400");
            }
        }

        #endregion

        #region route precedence

        [TestMethod]
        public async Task DeleteIndex_LiteralPropertyValueSegment_AndNumericSibling_EachHitTheirOwnAction()
        {
            using var factory = new RoutingFactory();
            using var client = factory.CreateClient();

            await CreateVertexViaApi(client, "Alpha", 1);
            await CreateVertexViaApi(client, "Beta", 2);
            var ids = await VerticesByName(client);

            await CreateIndexViaApi(client, "routeIdx", "DictionaryIndex");
            await AddToIndexViaApi(client, "routeIdx", ids["Alpha"], "alpha", "System.String");
            await AddToIndexViaApi(client, "routeIdx", ids["Beta"], "beta", "System.String");

            // DELETE /index/{indexId}/propertyValue must hit RemoveKeyFromIndex: the literal
            // segment outranks the {graphElementId} sibling (where "propertyValue" could never
            // bind to Int32). Pinned by effect: the KEY "alpha" disappears, "beta" is untouched.
            using (var request = new HttpRequestMessage(HttpMethod.Delete, "/index/routeIdx/propertyValue")
            {
                Content = Json("{\"propertyId\":\"key\",\"propertyValue\":\"alpha\",\"fullQualifiedTypeName\":\"System.String\"}")
            })
            using (var response = await client.SendAsync(request))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("true", await response.Content.ReadAsStringAsync(),
                    "the key removal must reach the propertyValue action and succeed");
            }

            Assert.AreEqual(0, (await IndexScanEquals(client, "routeIdx", "alpha")).Count,
                "the removed KEY no longer resolves");
            CollectionAssert.AreEquivalent(new List<int> { ids["Beta"] },
                await IndexScanEquals(client, "routeIdx", "beta"),
                "the sibling key is untouched by the key removal");

            // A NUMERIC final segment routes to the {graphElementId} sibling: removal BY ELEMENT.
            // Pinned by effect: Beta leaves its "beta" bucket - a key removal of the literal
            // string could not produce this, since the key here is "beta", not the id.
            using (var response = await client.DeleteAsync("/index/routeIdx/" + ids["Beta"]))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("true", await response.Content.ReadAsStringAsync(),
                    "the element removal must reach the graphElementId action and succeed");
            }

            Assert.AreEqual(0, (await IndexScanEquals(client, "routeIdx", "beta")).Count,
                "removing the element emptied its key bucket");
        }

        [TestMethod]
        public async Task GraphElementPut_EmbeddingSegment_AndPropertySibling_EachHitTheirOwnAction()
        {
            using var factory = new RoutingFactory();
            using var client = factory.CreateClient();

            await CreateVertexViaApi(client, "Carrier", 1);
            var id = (await VerticesByName(client))["Carrier"];

            // Three segments with the literal "embedding" must hit SetElementEmbedding, not the
            // two-parameter property sibling. Pinned by effect: a named embedding exists...
            using (var put = await client.PutAsync("/graphelement/" + id + "/embedding/e1?waitForCompletion=true",
                Json("{\"vector\":[0.25,-0.75]}")))
            {
                Assert.AreEqual(HttpStatusCode.Accepted, put.StatusCode, await put.Content.ReadAsStringAsync());
            }

            var embedding = await GetJson(client, "/graphelement/" + id + "/embedding/e1");
            Assert.AreEqual(2, embedding.GetProperty("vector").GetArrayLength());
            Assert.AreEqual(0.25f, embedding.GetProperty("vector")[0].GetSingle());

            // ...and NO property was written by it.
            var element = await GetJson(client, "/graphelement/" + id);
            Assert.IsFalse(element.GetProperty("properties").EnumerateArray()
                    .Any(p => p.GetProperty("propertyId").GetString() == "embedding"),
                "the embedding write must not surface as a property");

            // TWO segments where the property id IS the literal "embedding" must hit AddProperty:
            // a property literally named "embedding" stays writable.
            using (var put = await client.PutAsync("/graphelement/" + id + "/embedding?waitForCompletion=true",
                Json("{\"propertyId\":\"embedding\",\"propertyValue\":\"just-a-property\",\"fullQualifiedTypeName\":\"System.String\"}")))
            {
                Assert.AreEqual(HttpStatusCode.Accepted, put.StatusCode, await put.Content.ReadAsStringAsync());
            }

            element = await GetJson(client, "/graphelement/" + id);
            var embeddingProperty = element.GetProperty("properties").EnumerateArray()
                .Single(p => p.GetProperty("propertyId").GetString() == "embedding");
            Assert.AreEqual("just-a-property", embeddingProperty.GetProperty("propertyValue").GetString(),
                "the two-segment URL must reach the property action");

            // The named embedding is untouched by the property write.
            embedding = await GetJson(client, "/graphelement/" + id + "/embedding/e1");
            Assert.AreEqual(0.25f, embedding.GetProperty("vector")[0].GetSingle());
        }

        #endregion
    }
}
