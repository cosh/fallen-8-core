// MIT License
//
// PropertyMutationEndpointTest.cs
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
    }
}
