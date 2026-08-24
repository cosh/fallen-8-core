// MIT License
//
// McpReadToolsTest.cs
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
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Mcp.Configuration;
using NoSQL.GraphDB.Mcp.Tools;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The Phase 1 read tier (f8_get / f8_search / f8_paths / f8_analytics) exercised
    ///   end-to-end through the ToolCatalog into a real hosted apiApp seeded a-priori via its own
    ///   REST (independent of the write tier, which arrives in Phase 2) — spec §3.11. One shared
    ///   volatile apiApp is seeded once for the class.
    /// </summary>
    [TestClass]
    public class McpReadToolsTest
    {
        private sealed class ApiAppFactory : WebApplicationFactory<NoSQL.GraphDB.App.Program>
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseEnvironment("Development");
                builder.UseSetting("Fallen8:Durability:Volatile", "true");
            }
        }

        private static ApiAppFactory _api = null!;
        private static Dictionary<String, Int32> _ids = new();

        [ClassInitialize]
        public static async Task ClassInitialize(TestContext context)
        {
            _api = new ApiAppFactory();
            using var seed = _api.CreateClient();

            await CreateVertex(seed, "Alice", 30);
            await CreateVertex(seed, "Bob", 40);
            await CreateVertex(seed, "Carol", 20);
            _ids = await VerticesByName(seed);

            await Put(seed, "/edge?waitForCompletion=true",
                $"{{\"creationDate\":1,\"sourceVertex\":{_ids["Alice"]},\"targetVertex\":{_ids["Bob"]},\"edgePropertyId\":\"knows\",\"label\":\"friendship\"}}");

            await CreateIndex(seed, "nameIdx", "DictionaryIndex");
            foreach (var name in new[] { "Alice", "Bob", "Carol" })
            {
                await AddToIndex(seed, "nameIdx", _ids[name], name, "System.String");
            }

            await CreateIndex(seed, "fts", "RegExIndex");
            await AddToIndex(seed, "fts", _ids["Alice"], "Alice studies graph databases", "System.String");
            await AddToIndex(seed, "fts", _ids["Bob"], "Bob prefers relational systems", "System.String");

            // A BOUND vector index over element embeddings, written as raw vectors so this fixture needs
            // no embedding provider. Bound rather than raw on purpose: it is the shape the product now
            // steers every user towards, and its projection is maintained by the writer thread rather
            // than by explicit adds - so a defect there is invisible to a raw-index test.
            await SetEmbedding(seed, _ids["Alice"], "default", "[1,0,0]");
            await SetEmbedding(seed, _ids["Bob"], "default", "[0.9,0.1,0]");
            await SetEmbedding(seed, _ids["Carol"], "default", "[0,0,1]");
            await CreateBoundVectorIndex(seed, "vecIdx", 3, "Cosine", "default");
        }

        [ClassCleanup]
        public static void ClassCleanup() => _api?.Dispose();

        private ToolCatalog Catalog()
        {
            var bridge = McpTestSupport.Bridge(_api.Server.CreateHandler());
            return McpTestSupport.Catalog(new McpToolsOptions(), McpTestSupport.ReadTools(bridge));
        }

        private static IReadOnlyDictionary<String, JsonElement> Args(String json)
        {
            return JsonSerializer.Deserialize<Dictionary<String, JsonElement>>(json)!;
        }

        private static JsonElement Structured(ModelContextProtocol.Protocol.CallToolResult result)
        {
            Assert.IsFalse(result.IsError, "tool returned an error: " +
                (result.Content.Count > 0 ? ((ModelContextProtocol.Protocol.TextContentBlock)result.Content[0]).Text : ""));
            Assert.IsNotNull(result.StructuredContent);
            return result.StructuredContent!.Value;
        }

        // --- f8_get -------------------------------------------------------------------------

        [TestMethod]
        public async Task Get_Vertex_ReturnsScalarPropertyValues()
        {
            var result = await Catalog().CallAsync("f8_get", Args($"{{\"kind\":\"vertex\",\"id\":{_ids["Alice"]}}}"), CancellationToken.None);
            var element = Structured(result);

            Assert.AreEqual(_ids["Alice"], element.GetProperty("id").GetInt32());
            Assert.AreEqual("person", element.GetProperty("label").GetString());
            // age is a scalar → rendered as a native number (not a stringly-typed FQTN pair).
            Assert.AreEqual(30, element.GetProperty("properties").GetProperty("age").GetInt32());
            Assert.AreEqual("Alice", element.GetProperty("properties").GetProperty("name").GetString());
        }

        [TestMethod]
        public async Task Get_MissingVertex_IsSoftNotFound()
        {
            var result = await Catalog().CallAsync("f8_get", Args("{\"kind\":\"vertex\",\"id\":999999}"), CancellationToken.None);
            var element = Structured(result);
            Assert.IsFalse(element.GetProperty("found").GetBoolean());
        }

        [TestMethod]
        public async Task Get_Vertex_IncludeDegree_CountsEdges()
        {
            var result = await Catalog().CallAsync("f8_get",
                Args($"{{\"kind\":\"vertex\",\"id\":{_ids["Alice"]},\"include\":[\"degree\"]}}"), CancellationToken.None);
            var element = Structured(result);
            Assert.IsTrue(element.GetProperty("degree").GetInt32() >= 1, "Alice knows Bob → degree >= 1");
        }

        [TestMethod]
        public async Task Get_Edge_ReturnsTypeAndLabel()
        {
            // Locate the seeded edge via Alice's adjacency (grouped by the edge's type).
            var alice = Structured(await Catalog().CallAsync("f8_get",
                Args($"{{\"kind\":\"vertex\",\"id\":{_ids["Alice"]},\"include\":[\"out_edges\"]}}"), CancellationToken.None));
            var edgeId = alice.GetProperty("outEdges").GetProperty("knows").EnumerateArray().First().GetInt32();

            var edge = Structured(await Catalog().CallAsync("f8_get",
                Args($"{{\"kind\":\"edge\",\"id\":{edgeId}}}"), CancellationToken.None));

            // The projection carries both classifiers: the type (edgePropertyId) and the label.
            Assert.AreEqual("knows", edge.GetProperty("edgePropertyId").GetString());
            Assert.AreEqual("friendship", edge.GetProperty("label").GetString());
        }

        // --- f8_search, vector and semantic modes -------------------------------------------
        //
        // These two arms are the WHOLE of the agent-side embedding capability and were executed by no
        // test: the underlying REST routes are covered, the bridge through them was not.

        [TestMethod]
        public async Task Search_VectorMode_RanksByCosineOverTheBoundIndex()
        {
            var result = await Catalog().CallAsync("f8_search",
                Args("{\"mode\":\"vector\",\"indexId\":\"vecIdx\",\"vector\":[1,0,0],\"limit\":2}"),
                CancellationToken.None);
            var structured = Structured(result);

            var ids = structured.GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("id").GetInt32()).ToList();
            Assert.AreEqual(_ids["Alice"], ids[0], "[1,0,0] is Alice's own vector, so Alice ranks first");
            Assert.AreEqual(_ids["Bob"], ids[1], "[0.9,0.1,0] is the next nearest; Carol is orthogonal");
            Assert.IsFalse(ids.Contains(_ids["Carol"]), "an orthogonal vector must not make the top 2");
        }

        [TestMethod]
        public async Task Search_VectorMode_HonoursTheLabelConstraint()
        {
            // The constraint is applied BEFORE scoring, so the returned k are k MATCHING elements. A
            // label nothing carries must therefore come back empty rather than ignored.
            var result = await Catalog().CallAsync("f8_search",
                Args("{\"mode\":\"vector\",\"indexId\":\"vecIdx\",\"vector\":[1,0,0],\"label\":\"nothing-carries-this\"}"),
                CancellationToken.None);

            Assert.AreEqual(0, Structured(result).GetProperty("items").GetArrayLength(),
                "a label constraint that matches nothing yields nothing, not the unconstrained answer");
        }

        [TestMethod]
        public async Task Search_VectorMode_HonoursTheKindConstraint()
        {
            var result = await Catalog().CallAsync("f8_search",
                Args("{\"mode\":\"vector\",\"indexId\":\"vecIdx\",\"vector\":[1,0,0],\"kind\":\"edge\"}"),
                CancellationToken.None);

            Assert.AreEqual(0, Structured(result).GetProperty("items").GetArrayLength(),
                "only vertices carry an embedding in this fixture, so an edge-kind query is empty");
        }

        [TestMethod]
        public async Task Search_VectorMode_WithoutAVector_IsAnArgumentErrorAndNotAnEmptyAnswer()
        {
            var result = await Catalog().CallAsync("f8_search",
                Args("{\"mode\":\"vector\",\"indexId\":\"vecIdx\"}"), CancellationToken.None);

            Assert.IsTrue(result.IsError, "an agent that forgot the vector must be told, not handed 0 hits");
        }

        [TestMethod]
        public async Task Search_VectorMode_WithoutAnIndex_IsAnArgumentError()
        {
            var result = await Catalog().CallAsync("f8_search",
                Args("{\"mode\":\"vector\",\"vector\":[1,0,0]}"), CancellationToken.None);

            Assert.IsTrue(result.IsError, "the index is not guessable: kNN has no default corpus");
        }

        [TestMethod]
        public async Task Search_SemanticMode_WithNoEmbeddingProvider_SaysSoRatherThanReturningNothing()
        {
            // The default deployment is model-free, so this is the state most agents meet first. It must
            // surface as an error: an empty result set reads as "nothing is similar", which is a claim
            // about the graph rather than about the missing capability.
            var result = await Catalog().CallAsync("f8_search",
                Args("{\"mode\":\"semantic\",\"indexId\":\"vecIdx\",\"query\":\"graph databases\"}"),
                CancellationToken.None);

            Assert.IsTrue(result.IsError,
                "text-in search without a provider is a capability failure, not an empty answer");
        }

        [TestMethod]
        public async Task Search_SemanticMode_WithoutQueryText_IsAnArgumentError()
        {
            var result = await Catalog().CallAsync("f8_search",
                Args("{\"mode\":\"semantic\",\"indexId\":\"vecIdx\"}"), CancellationToken.None);

            Assert.IsTrue(result.IsError, "semantic mode with no query is an argument error");
        }

        // --- f8_search ----------------------------------------------------------------------

        [TestMethod]
        public async Task Search_PropertyMode_UnindexedColdGraphScan()
        {
            var result = await Catalog().CallAsync("f8_search",
                Args("{\"mode\":\"property\",\"key\":\"age\",\"operator\":\"greater\",\"value\":25,\"kind\":\"vertex\"}"),
                CancellationToken.None);
            var structured = Structured(result);

            var ids = structured.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToHashSet();
            Assert.IsTrue(ids.Contains(_ids["Alice"]) && ids.Contains(_ids["Bob"]), "age>25 matches Alice(30) and Bob(40)");
            Assert.IsFalse(ids.Contains(_ids["Carol"]), "Carol(20) does not match");
        }

        [TestMethod]
        public async Task Search_IndexMode_EqualityHit()
        {
            var result = await Catalog().CallAsync("f8_search",
                Args("{\"mode\":\"index\",\"indexId\":\"nameIdx\",\"value\":\"Bob\"}"), CancellationToken.None);
            var structured = Structured(result);

            var ids = structured.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToList();
            CollectionAssert.AreEquivalent(new List<Int32> { _ids["Bob"] }, ids);
        }

        [TestMethod]
        public async Task Search_PropertyMode_LabelRestrictor_PassesThrough()
        {
            var matching = Structured(await Catalog().CallAsync("f8_search",
                Args("{\"mode\":\"property\",\"key\":\"age\",\"operator\":\"greater\",\"value\":0,\"label\":\"person\"}"),
                CancellationToken.None));
            var ids = matching.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToHashSet();
            Assert.IsTrue(ids.Contains(_ids["Alice"]), "person-labeled vertices pass the label restrictor");

            var none = Structured(await Catalog().CallAsync("f8_search",
                Args("{\"mode\":\"property\",\"key\":\"age\",\"operator\":\"greater\",\"value\":0,\"label\":\"no-such-label\"}"),
                CancellationToken.None));
            Assert.AreEqual(0, none.GetProperty("count").GetInt32(), "an unmatched label filters every hit out");
        }

        [TestMethod]
        public async Task Search_PropertiesMode_ContainsAcrossAllValues_CaseInsensitive()
        {
            // "ALI" is a case-insensitive substring of Alice's name value.
            var byName = Structured(await Catalog().CallAsync("f8_search",
                Args("{\"mode\":\"properties\",\"query\":\"ALI\",\"kind\":\"vertex\"}"), CancellationToken.None));
            var nameIds = byName.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToHashSet();
            Assert.IsTrue(nameIds.Contains(_ids["Alice"]), "a case-insensitive substring of a string value matches");
            Assert.IsFalse(nameIds.Contains(_ids["Carol"]), "Carol matches nothing");

            // The int age value is searchable by its text form (all values stringified).
            var byAge = Structured(await Catalog().CallAsync("f8_search",
                Args("{\"mode\":\"properties\",\"query\":\"40\",\"kind\":\"vertex\"}"), CancellationToken.None));
            var ageIds = byAge.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToHashSet();
            Assert.IsTrue(ageIds.Contains(_ids["Bob"]), "a numeric property value is searchable by its text form");
        }

        [TestMethod]
        public async Task Search_PropertiesMode_LabelRestrictor_PassesThrough()
        {
            var person = Structured(await Catalog().CallAsync("f8_search",
                Args("{\"mode\":\"properties\",\"query\":\"ali\",\"label\":\"person\"}"), CancellationToken.None));
            Assert.IsTrue(person.GetProperty("count").GetInt32() >= 1, "the person label passes Alice through");

            var none = Structured(await Catalog().CallAsync("f8_search",
                Args("{\"mode\":\"properties\",\"query\":\"ali\",\"label\":\"no-such-label\"}"), CancellationToken.None));
            Assert.AreEqual(0, none.GetProperty("count").GetInt32(), "an unmatched label filters every hit out");
        }

        [TestMethod]
        public async Task Search_PropertiesMode_BlankQuery_IsError()
        {
            var result = await Catalog().CallAsync("f8_search",
                Args("{\"mode\":\"properties\",\"query\":\"   \"}"), CancellationToken.None);
            Assert.IsTrue(result.IsError, "a blank properties-mode term is a 400 tool error");
        }

        [TestMethod]
        public async Task Search_IndexMode_LabelRestrictor_FiltersHits()
        {
            // Pins the REST fix too: /scan/index/all used to accept "label" and silently ignore it.
            var matching = Structured(await Catalog().CallAsync("f8_search",
                Args("{\"mode\":\"index\",\"indexId\":\"nameIdx\",\"value\":\"Bob\",\"label\":\"person\"}"),
                CancellationToken.None));
            Assert.AreEqual(1, matching.GetProperty("count").GetInt32(), "the person-labeled hit survives");

            var none = Structured(await Catalog().CallAsync("f8_search",
                Args("{\"mode\":\"index\",\"indexId\":\"nameIdx\",\"value\":\"Bob\",\"label\":\"no-such-label\"}"),
                CancellationToken.None));
            Assert.AreEqual(0, none.GetProperty("count").GetInt32(), "an unmatched label filters the indexed hit out");
        }

        [TestMethod]
        public async Task Search_IndexMode_WithFields_EnrichesHits()
        {
            var result = await Catalog().CallAsync("f8_search",
                Args("{\"mode\":\"index\",\"indexId\":\"nameIdx\",\"value\":\"Alice\",\"fields\":[\"name\"]}"),
                CancellationToken.None);
            var structured = Structured(result);
            var first = structured.GetProperty("items").EnumerateArray().First();
            Assert.AreEqual("Alice", first.GetProperty("element").GetProperty("properties").GetProperty("name").GetString());
        }

        [TestMethod]
        public async Task Search_Fulltext_ReturnsScoredHit()
        {
            var result = await Catalog().CallAsync("f8_search",
                Args("{\"mode\":\"fulltext\",\"indexId\":\"fts\",\"query\":\"graph\"}"), CancellationToken.None);
            var structured = Structured(result);

            var items = structured.GetProperty("items").EnumerateArray().ToList();
            Assert.IsTrue(items.Any(i => i.GetProperty("id").GetInt32() == _ids["Alice"]), "the graph sentence matches Alice");
            Assert.IsTrue(items.First().TryGetProperty("score", out _), "fulltext hits carry a score");
        }

        [TestMethod]
        public async Task Search_Fulltext_ZeroMatchOnRealIndex_IsEmptyPageNotError()
        {
            // The engine returns the same null for "no such index" and "index exists, zero hits",
            // so f8_search reports an honest empty page (not a misleading "index not found").
            var result = await Catalog().CallAsync("f8_search",
                Args("{\"mode\":\"fulltext\",\"indexId\":\"fts\",\"query\":\"zzzznomatchhere\"}"), CancellationToken.None);
            var structured = Structured(result);
            Assert.AreEqual(0, structured.GetProperty("count").GetInt32(), "a real index with no matches is an empty page");
            Assert.IsFalse(structured.GetProperty("hasMore").GetBoolean());
        }

        [TestMethod]
        public async Task Search_Fulltext_UnknownIndex_IsEmptyPage()
        {
            // Consistent with the index/property modes' silent-empty on a missing index.
            var result = await Catalog().CallAsync("f8_search",
                Args("{\"mode\":\"fulltext\",\"indexId\":\"no-such-index\",\"query\":\"graph\"}"), CancellationToken.None);
            Assert.AreEqual(0, Structured(result).GetProperty("count").GetInt32());
        }

        // --- f8_paths -----------------------------------------------------------------------

        [TestMethod]
        public async Task Paths_FindsTheSeededPath()
        {
            var result = await Catalog().CallAsync("f8_paths",
                Args($"{{\"from\":{_ids["Alice"]},\"to\":{_ids["Bob"]}}}"), CancellationToken.None);
            var structured = Structured(result);
            Assert.IsTrue(structured.GetProperty("count").GetInt32() >= 1, "Alice→Bob has at least one path");
        }

        // --- f8_analytics -------------------------------------------------------------------

        [TestMethod]
        public async Task Analytics_ListsAvailableAlgorithms()
        {
            var result = await Catalog().CallAsync("f8_analytics", Args("{}"), CancellationToken.None);
            var structured = Structured(result);
            var algorithms = structured.GetProperty("algorithms");
            Assert.IsTrue(algorithms.EnumerateObject().Any(p => p.Name == "PAGERANK"), "PAGERANK is available");
        }

        [TestMethod]
        public async Task Analytics_RunsPageRank()
        {
            var result = await Catalog().CallAsync("f8_analytics", Args("{\"algorithm\":\"PAGERANK\"}"), CancellationToken.None);
            var structured = Structured(result);
            Assert.AreEqual("PAGERANK", structured.GetProperty("algorithm").GetString());
            Assert.IsTrue(structured.TryGetProperty("results", out var scored) && scored.GetArrayLength() >= 1,
                "PageRank returns scored vertices");
        }

        // --- seeding helpers (via the apiApp's own REST) ------------------------------------

        private static StringContent Json(String body) => new(body, Encoding.UTF8, "application/json");

        private static async Task Put(HttpClient client, String url, String body)
        {
            using var response = await client.PutAsync(url, Json(body));
            Assert.IsTrue(response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK,
                $"seed PUT {url} → {response.StatusCode}");
        }

        private static Task CreateVertex(HttpClient client, String name, Int32 age)
        {
            return Put(client, "/vertex?waitForCompletion=true",
                "{\"label\":\"person\",\"creationDate\":1,\"properties\":[" +
                $"{{\"propertyId\":\"name\",\"propertyValue\":\"{name}\",\"fullQualifiedTypeName\":\"System.String\"}}," +
                $"{{\"propertyId\":\"age\",\"propertyValue\":\"{age}\",\"fullQualifiedTypeName\":\"System.Int32\"}}]}}");
        }

        private static async Task<Dictionary<String, Int32>> VerticesByName(HttpClient client)
        {
            using var response = await client.GetAsync("/graph");
            var graph = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            var byName = new Dictionary<String, Int32>();
            foreach (var vertex in graph.GetProperty("vertices").EnumerateArray())
            {
                foreach (var property in vertex.GetProperty("properties").EnumerateArray())
                {
                    if (property.GetProperty("propertyId").GetString() == "name")
                    {
                        byName[property.GetProperty("propertyValue").GetString()!] = vertex.GetProperty("id").GetInt32();
                    }
                }
            }
            return byName;
        }

        private static async Task CreateIndex(HttpClient client, String uniqueId, String pluginType)
        {
            using var response = await client.PostAsync("/index",
                Json($"{{\"uniqueId\":\"{uniqueId}\",\"pluginType\":\"{pluginType}\"}}"));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"create index {uniqueId}");
        }

        private static async Task SetEmbedding(HttpClient client, Int32 elementId, String name, String vector)
        {
            // waitForCompletion, because the bound index created next materialises its projection from
            // COMMITTED element state: an accepted-but-unapplied write would build an empty index.
            using var response = await client.PutAsync(
                $"/graphelement/{elementId}/embedding/{name}?waitForCompletion=true",
                Json($"{{\"vector\":{vector}}}"));
            Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, $"set embedding on {elementId}");
        }

        private static async Task CreateBoundVectorIndex(HttpClient client, String uniqueId, Int32 dimension,
            String metric, String embeddingName)
        {
            var body =
                $"{{\"uniqueId\":\"{uniqueId}\",\"pluginType\":\"VectorIndex\",\"pluginOptions\":{{" +
                $"\"dimension\":{{\"propertyId\":\"dimension\",\"propertyValue\":\"{dimension}\",\"fullQualifiedTypeName\":\"System.Int32\"}}," +
                $"\"metric\":{{\"propertyId\":\"metric\",\"propertyValue\":\"{metric}\",\"fullQualifiedTypeName\":\"System.String\"}}," +
                $"\"embeddingName\":{{\"propertyId\":\"embeddingName\",\"propertyValue\":\"{embeddingName}\",\"fullQualifiedTypeName\":\"System.String\"}}}}}}";
            using var response = await client.PostAsync("/index", Json(body));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"create bound index {uniqueId}");
        }

        private static async Task AddToIndex(HttpClient client, String indexId, Int32 elementId, String key, String typeName)
        {
            using var response = await client.PutAsync($"/index/{indexId}",
                Json($"{{\"graphElementId\":{elementId},\"key\":{{\"propertyId\":\"key\",\"propertyValue\":\"{key}\",\"fullQualifiedTypeName\":\"{typeName}\"}}}}"));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"add {key} to {indexId}");
        }
    }
}
