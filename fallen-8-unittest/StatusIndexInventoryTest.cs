// MIT License
//
// StatusIndexInventoryTest.cs
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
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.Core.Index.Spatial.Implementation.RTree;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Pins the index-discovery contract on GET /status (features studio-index-discovery,
    /// index-workspace): the live index inventory (id, plugin type, capabilities, counts),
    /// the available-plugin list the create dropdown feeds on, and the "SpatialIndex is not
    /// creatable over REST" reality the Studio's create gating relies on.
    /// </summary>
    [TestClass]
    public class StatusIndexInventoryTest
    {
        private sealed class TestFactory : WebApplicationFactory<Program>
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

        private static async Task<JsonElement> Status(HttpClient client)
        {
            using var response = await client.GetAsync("/status");
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        }

        private static Dictionary<string, string> Inventory(JsonElement status)
        {
            return status.GetProperty("indices").EnumerateArray().ToDictionary(
                e => e.GetProperty("indexId").GetString(),
                e => e.GetProperty("pluginType").GetString());
        }

        [TestMethod]
        public async Task Status_ListsIndexInventory_AndTracksCreateDelete()
        {
            using var factory = new TestFactory();
            using var client = factory.CreateClient();

            var empty = Inventory(await Status(client));
            Assert.AreEqual(0, empty.Count, "a fresh instance has no indices");

            using (var create = await client.PostAsync("/index",
                Json("{\"uniqueId\":\"nameIndex\",\"pluginType\":\"DictionaryIndex\"}")))
            {
                Assert.AreEqual(HttpStatusCode.OK, create.StatusCode);
                Assert.AreEqual("true", await create.Content.ReadAsStringAsync());
            }
            using (var create = await client.PostAsync("/index", Json(
                "{\"uniqueId\":\"embeddings\",\"pluginType\":\"VectorIndex\",\"pluginOptions\":{" +
                "\"dimension\":{\"propertyId\":\"dimension\",\"propertyValue\":\"3\",\"fullQualifiedTypeName\":\"System.Int32\"}}}")))
            {
                Assert.AreEqual("true", await create.Content.ReadAsStringAsync());
            }

            var two = Inventory(await Status(client));
            Assert.AreEqual(2, two.Count);
            Assert.AreEqual("DictionaryIndex", two["nameIndex"]);
            Assert.AreEqual("VectorIndex", two["embeddings"]);

            using (var delete = await client.DeleteAsync("/index/nameIndex"))
            {
                Assert.AreEqual(HttpStatusCode.OK, delete.StatusCode);
            }

            var one = Inventory(await Status(client));
            Assert.AreEqual(1, one.Count);
            Assert.IsTrue(one.ContainsKey("embeddings"), "the deleted index must leave the inventory");
        }

        [TestMethod]
        public async Task Status_AvailableIndexPlugins_ListTheBuiltins()
        {
            using var factory = new TestFactory();
            using var client = factory.CreateClient();

            var status = await Status(client);
            var plugins = status.GetProperty("availableIndexPlugins").EnumerateArray()
                .Select(e => e.GetString()).ToHashSet();

            foreach (var name in new[]
            {
                "DictionaryIndex", "SingleValueIndex", "RangeIndex",
                "RegExIndex", "VectorIndex", "SpatialIndex"
            })
            {
                Assert.IsTrue(plugins.Contains(name), name + " must be discoverable");
            }
        }

        private static JsonElement InventoryEntry(JsonElement status, string indexId)
        {
            return status.GetProperty("indices").EnumerateArray()
                .Single(e => e.GetProperty("indexId").GetString() == indexId);
        }

        private static List<string> Capabilities(JsonElement entry)
        {
            return entry.GetProperty("capabilities").EnumerateArray()
                .Select(e => e.GetString()).ToList();
        }

        [TestMethod]
        public async Task Status_ReportsCapabilities_PerIndexFamily()
        {
            using var factory = new TestFactory();
            using var client = factory.CreateClient();

            foreach (var (id, type) in new[]
            {
                ("names", "DictionaryIndex"), ("ages", "RangeIndex"), ("docs", "RegExIndex")
            })
            {
                using var create = await client.PostAsync("/index",
                    Json($"{{\"uniqueId\":\"{id}\",\"pluginType\":\"{type}\"}}"));
                Assert.AreEqual("true", await create.Content.ReadAsStringAsync(), type + " must be created");
            }
            using (var create = await client.PostAsync("/index", Json(
                "{\"uniqueId\":\"embeddings\",\"pluginType\":\"VectorIndex\",\"pluginOptions\":{" +
                "\"dimension\":{\"propertyId\":\"dimension\",\"propertyValue\":\"3\",\"fullQualifiedTypeName\":\"System.Int32\"}}}")))
            {
                Assert.AreEqual("true", await create.Content.ReadAsStringAsync());
            }

            var status = await Status(client);
            CollectionAssert.AreEqual(new[] { "equality" }, Capabilities(InventoryEntry(status, "names")));
            CollectionAssert.AreEqual(new[] { "equality", "range" }, Capabilities(InventoryEntry(status, "ages")));
            CollectionAssert.AreEqual(new[] { "equality", "fulltext" }, Capabilities(InventoryEntry(status, "docs")));
            CollectionAssert.AreEqual(new[] { "vector" }, Capabilities(InventoryEntry(status, "embeddings")));
        }

        [TestMethod]
        public void SpatialIndex_Capabilities_AreSpatialOnly()
        {
            // SpatialIndex cannot be created over REST (pinned below), so the derivation is
            // pinned directly: geometry keys cannot travel as the wire literal -> no equality.
            CollectionAssert.AreEqual(new[] { "spatial" }, IndexCapabilities.Describe(new RTree()));
        }

        [TestMethod]
        public void SpatialIndex_CountOfKeysSentinel_IsNegative()
        {
            // The R-Tree answers a negative "count not supported" sentinel; Status() maps
            // negative counts to null on the wire (AdminController.NonNegativeCount) so no
            // client ever renders "-1 keys". This pins the sentinel that mapping guards.
            Assert.IsTrue(new RTree().CountOfKeys() < 0, "the R-Tree count sentinel must be negative");
        }

        [TestMethod]
        public async Task Status_ReportsCounts_ThatTrackIndexContent()
        {
            using var factory = new TestFactory();
            using var client = factory.CreateClient();

            using (var create = await client.PostAsync("/index",
                Json("{\"uniqueId\":\"names\",\"pluginType\":\"DictionaryIndex\"}")))
            {
                Assert.AreEqual("true", await create.Content.ReadAsStringAsync());
            }

            var fresh = InventoryEntry(await Status(client), "names");
            Assert.AreEqual(0, fresh.GetProperty("keys").GetInt32(), "a fresh index has no keys");
            Assert.AreEqual(0, fresh.GetProperty("values").GetInt32(), "a fresh index has no values");

            using (var vertex = await client.PutAsync("/vertex?waitForCompletion=true",
                Json("{\"label\":\"person\",\"creationDate\":0}")))
            {
                Assert.AreEqual(HttpStatusCode.Accepted, vertex.StatusCode);
            }
            using (var add = await client.PutAsync("/index/names", Json(
                "{\"graphElementId\":0,\"key\":{\"propertyValue\":\"John\",\"fullQualifiedTypeName\":\"System.String\"}}")))
            {
                Assert.AreEqual("true", await add.Content.ReadAsStringAsync());
            }

            var populated = InventoryEntry(await Status(client), "names");
            Assert.AreEqual(1, populated.GetProperty("keys").GetInt32());
            Assert.AreEqual(1, populated.GetProperty("values").GetInt32());
        }

        [TestMethod]
        public async Task CreateSpatialIndex_OverRest_AnswersFalse_AndStaysOffTheInventory()
        {
            using var factory = new TestFactory();
            using var client = factory.CreateClient();

            // SpatialIndex.Initialize needs live CLR objects (IMetric, Space) that the
            // primitive-literal pluginOptions cannot carry - the Studio disables Create for
            // this type and this test pins the behaviour that gating documents.
            using var create = await client.PostAsync("/index",
                Json("{\"uniqueId\":\"geo\",\"pluginType\":\"SpatialIndex\"}"));
            Assert.AreEqual(HttpStatusCode.OK, create.StatusCode);
            Assert.AreEqual("false", await create.Content.ReadAsStringAsync());

            var inventory = Inventory(await Status(client));
            Assert.IsFalse(inventory.ContainsKey("geo"));
        }
    }
}
