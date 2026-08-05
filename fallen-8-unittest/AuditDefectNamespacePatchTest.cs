// MIT License
//
// AuditDefectNamespacePatchTest.cs
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
    /// Pins PATCH /ns/{name} as all-or-nothing (audit defect B31): the rename is written to the
    /// namespace catalog and survives a restart, so every field of the request must be validated
    /// before the first mutation. A rejected PATCH leaves both the name and the
    /// plugin-registration override exactly as they were. Runs through the real hosted pipeline
    /// because the window only exists in the ordering of the action's steps.
    /// </summary>
    [TestClass]
    public class AuditDefectNamespacePatchTest
    {
        private sealed class PatchFactory : WebApplicationFactory<Program>
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

        private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
        {
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        }

        private static async Task CreateNamespace(HttpClient client, string name)
        {
            using var response = await client.PutAsync("/ns/" + name, null);
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, "PUT /ns/" + name);
        }

        private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status, string title)
        {
            Assert.AreEqual(status, response.StatusCode);
            Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.AreEqual(title, (await ReadJson(response)).GetProperty("title").GetString());
        }

        /// <summary>The namespace's override as GET /ns/{name} reports it, or "gone" when it 404s.</summary>
        private static async Task<string> OverrideOf(HttpClient client, string name)
        {
            using var response = await client.GetAsync("/ns/" + name);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return "gone";
            }
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "GET /ns/" + name);
            var value = (await ReadJson(response)).GetProperty("pluginRegistrationEnabled");
            return value.ValueKind == JsonValueKind.Null ? "inherit" : value.GetBoolean().ToString();
        }

        private static async Task<List<string>> NamespaceNames(HttpClient client)
        {
            using var response = await client.GetAsync("/ns");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "GET /ns");
            return (await ReadJson(response)).GetProperty("namespaces").EnumerateArray()
                .Select(e => e.GetProperty("name").GetString()).ToList();
        }

        #endregion

        /// <summary>
        /// The defect: a valid rename plus a typo'd override answered 400 AFTER the rename had
        /// already been committed to the catalog, so the caller kept addressing a name that now 404s.
        /// </summary>
        [TestMethod]
        public async Task Patch_RenameWithInvalidOverride_Rejects_AndLeavesTheNameUntouched()
        {
            using var factory = new PatchFactory();
            using var client = factory.CreateClient();
            await CreateNamespace(client, "flights");

            using (var patch = await client.PatchAsync("/ns/flights",
                Json("{\"name\":\"flights-eu\",\"pluginRegistration\":\"on\"}")))
            {
                await AssertProblem(patch, HttpStatusCode.BadRequest, "Invalid pluginRegistration");
            }

            // 400 means "nothing changed": the old address still answers and the new one never existed.
            Assert.AreEqual("inherit", await OverrideOf(client, "flights"));
            Assert.AreEqual("gone", await OverrideOf(client, "flights-eu"));
            var names = await NamespaceNames(client);
            CollectionAssert.Contains(names, "flights");
            CollectionAssert.DoesNotContain(names, "flights-eu");
        }

        /// <summary>
        /// The counter-test to the reorder: a request valid in BOTH fields still applies both, so
        /// validating the override up front cannot silently drop it.
        /// </summary>
        [TestMethod]
        public async Task Patch_RenameWithValidOverride_AppliesBoth()
        {
            using var factory = new PatchFactory();
            using var client = factory.CreateClient();
            await CreateNamespace(client, "flights");

            using (var patch = await client.PatchAsync("/ns/flights",
                Json("{\"name\":\"flights-eu\",\"pluginRegistration\":\"disabled\"}")))
            {
                Assert.AreEqual(HttpStatusCode.OK, patch.StatusCode);
                var body = await ReadJson(patch);
                Assert.AreEqual("flights-eu", body.GetProperty("name").GetString());
                Assert.IsFalse(body.GetProperty("pluginRegistrationEnabled").GetBoolean());
            }

            Assert.AreEqual("False", await OverrideOf(client, "flights-eu"));
            Assert.AreEqual("gone", await OverrideOf(client, "flights"));

            // "inherit" on the NEW address clears the override again (both fields applied, in order).
            using (var inherit = await client.PatchAsync("/ns/flights-eu",
                Json("{\"name\":\"flights-emea\",\"pluginRegistration\":\"inherit\"}")))
            {
                Assert.AreEqual(HttpStatusCode.OK, inherit.StatusCode);
                Assert.AreEqual(JsonValueKind.Null,
                    (await ReadJson(inherit)).GetProperty("pluginRegistrationEnabled").ValueKind);
            }
            Assert.AreEqual("inherit", await OverrideOf(client, "flights-emea"));
        }

        /// <summary>
        /// An unrecognized override on its own (no rename) is still a 400 that keeps the previously
        /// set value. The accepted vocabulary is exact and case-sensitive.
        /// </summary>
        [TestMethod]
        public async Task Patch_InvalidOverrideAlone_KeepsThePreviousValue()
        {
            using var factory = new PatchFactory();
            using var client = factory.CreateClient();
            await CreateNamespace(client, "flights");

            using (var enable = await client.PatchAsync("/ns/flights", Json("{\"pluginRegistration\":\"enabled\"}")))
            {
                Assert.AreEqual(HttpStatusCode.OK, enable.StatusCode);
                Assert.IsTrue((await ReadJson(enable)).GetProperty("pluginRegistrationEnabled").GetBoolean());
            }

            using (var wrongCase = await client.PatchAsync("/ns/flights", Json("{\"pluginRegistration\":\"Enabled\"}")))
            {
                await AssertProblem(wrongCase, HttpStatusCode.BadRequest, "Invalid pluginRegistration");
            }

            // An empty string is a supplied-but-unrecognized value, not an omitted field.
            using (var empty = await client.PatchAsync("/ns/flights", Json("{\"pluginRegistration\":\"\"}")))
            {
                await AssertProblem(empty, HttpStatusCode.BadRequest, "Invalid pluginRegistration");
            }

            Assert.AreEqual("True", await OverrideOf(client, "flights"),
                "a rejected override must not disturb the value already in effect");
        }

        /// <summary>
        /// Both fields invalid: validation runs before any mutation, so the override complaint wins
        /// and neither change is applied. The empty body still reports the "supply something" 400.
        /// </summary>
        [TestMethod]
        public async Task Patch_BothFieldsInvalid_ReportsTheOverride_AndChangesNothing()
        {
            using var factory = new PatchFactory();
            using var client = factory.CreateClient();
            await CreateNamespace(client, "flights");

            var tooLong = new string('a', 64);
            using (var patch = await client.PatchAsync("/ns/flights",
                Json("{\"name\":\"" + tooLong + "\",\"pluginRegistration\":\"nope\"}")))
            {
                await AssertProblem(patch, HttpStatusCode.BadRequest, "Invalid pluginRegistration");
            }

            using (var nothing = await client.PatchAsync("/ns/flights", Json("{}")))
            {
                await AssertProblem(nothing, HttpStatusCode.BadRequest, "Invalid namespace update");
            }

            Assert.AreEqual("inherit", await OverrideOf(client, "flights"));
            Assert.AreEqual("gone", await OverrideOf(client, tooLong));
            Assert.AreEqual(2, (await NamespaceNames(client)).Count, "default + flights, nothing else");
        }

        /// <summary>
        /// A failing rename also stops the override: renaming the reserved "default" namespace is a
        /// 409, and the override that rode along in the same request is not applied either (even
        /// though "default" would accept it on its own).
        /// </summary>
        [TestMethod]
        public async Task Patch_ReservedRenameWithOverride_AppliesNeither()
        {
            using var factory = new PatchFactory();
            using var client = factory.CreateClient();

            using (var patch = await client.PatchAsync("/ns/default",
                Json("{\"name\":\"renamed\",\"pluginRegistration\":\"disabled\"}")))
            {
                await AssertProblem(patch, HttpStatusCode.Conflict, "Reserved namespace");
            }

            Assert.AreEqual("inherit", await OverrideOf(client, "default"));
            Assert.AreEqual("gone", await OverrideOf(client, "renamed"));

            // Without the rename, the reserved namespace DOES take the override.
            using (var overrideOnly = await client.PatchAsync("/ns/default", Json("{\"pluginRegistration\":\"disabled\"}")))
            {
                Assert.AreEqual(HttpStatusCode.OK, overrideOnly.StatusCode);
            }
            Assert.AreEqual("False", await OverrideOf(client, "default"));
        }

        /// <summary>
        /// A conflicting rename carrying a valid override leaves the source namespace and the
        /// occupied target both untouched.
        /// </summary>
        [TestMethod]
        public async Task Patch_ConflictingRenameWithOverride_AppliesNeither()
        {
            using var factory = new PatchFactory();
            using var client = factory.CreateClient();
            await CreateNamespace(client, "flights");
            await CreateNamespace(client, "trains");

            using (var patch = await client.PatchAsync("/ns/flights",
                Json("{\"name\":\"trains\",\"pluginRegistration\":\"enabled\"}")))
            {
                await AssertProblem(patch, HttpStatusCode.Conflict, "Namespace name in use");
            }

            Assert.AreEqual("inherit", await OverrideOf(client, "flights"));
            Assert.AreEqual("inherit", await OverrideOf(client, "trains"),
                "the occupied target must not absorb the rejected request's override");
        }

        /// <summary>An override for a namespace that does not exist is a 404 that creates nothing.</summary>
        [TestMethod]
        public async Task Patch_UnknownNamespace_Is404_AndCreatesNothing()
        {
            using var factory = new PatchFactory();
            using var client = factory.CreateClient();

            using (var patch = await client.PatchAsync("/ns/missing", Json("{\"pluginRegistration\":\"disabled\"}")))
            {
                await AssertProblem(patch, HttpStatusCode.NotFound, "Namespace not found");
            }

            // An unparseable override on a missing namespace is rejected before the lookup: still 4xx,
            // and still no namespace.
            using (var invalid = await client.PatchAsync("/ns/missing", Json("{\"pluginRegistration\":\"maybe\"}")))
            {
                await AssertProblem(invalid, HttpStatusCode.BadRequest, "Invalid pluginRegistration");
            }

            Assert.AreEqual(1, (await NamespaceNames(client)).Count, "only \"default\" exists");
        }
    }
}
