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

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Pins the index-discovery contract on GET /status (feature studio-index-discovery):
    /// the live index inventory (id + plugin type), the available-plugin list the create
    /// dropdown feeds on, and the "SpatialIndex is not creatable over REST" reality the
    /// Studio's create gating relies on.
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
