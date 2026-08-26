// MIT License
//
// NamespaceEndpointTest.cs
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
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App;
using NoSQL.GraphDB.App.Namespaces;

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
            private readonly string _storageDir;
            private readonly string _metaDir;

            public NamespaceFactory(string maxNamespaces = null, string storageDir = null, string metaDir = null)
            {
                _maxNamespaces = maxNamespaces;
                _storageDir = storageDir;
                _metaDir = metaDir;
            }

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseEnvironment("Development");
                if (_storageDir == null)
                {
                    // Volatile durability: booting the host writes no checkpoint/WAL into the test bin.
                    builder.UseSetting("Fallen8:Durability:Volatile", "true");
                }
                else
                {
                    // Durable, into a temp directory: the only way to have a CATALOG, which is what
                    // makes a namespace exist without being loaded (feature namespace-startup-load).
                    builder.UseSetting("Fallen8:Durability:Volatile", "false");
                    builder.UseSetting("Fallen8:Durability:StorageDirectory", _storageDir);
                    builder.UseSetting("Fallen8:Durability:SaveOnShutdown", "false");
                    builder.UseSetting("Fallen8:Metadata:Directory", _metaDir);
                }

                if (_maxNamespaces != null)
                {
                    builder.UseSetting("Fallen8:Namespaces:MaxNamespaces", _maxNamespaces);
                }
            }
        }

        #region helpers

        /// <summary>
        ///   A host whose catalog already holds one EXCLUDED namespace, written the way an operator's
        ///   PATCH persists it. No first boot and no data: the point is the wire contract of a
        ///   namespace that exists with no engine, and the real catalog reader takes the decision.
        /// </summary>
        private NamespaceFactory NotLoadedHost(string name = "archived")
        {
            _storageDir = Path.Combine(Path.GetTempPath(), "f8_nsx_" + Guid.NewGuid().ToString("N"));
            _metaDir = Path.Combine(_storageDir, "metadata");
            Directory.CreateDirectory(_metaDir);
            File.WriteAllText(Path.Combine(_metaDir, "namespaces.json"),
                "{\"schemaVersion\":1,\"namespaces\":[{\"id\":\"ns-20260101-000000-abcd\",\"name\":\"" + name +
                "\",\"createdAt\":\"2026-01-01T00:00:00.000Z\",\"loadOnStartupEnabled\":false}]}");

            return new NamespaceFactory(storageDir: _storageDir, metaDir: _metaDir);
        }

        private string _storageDir;
        private string _metaDir;

        [TestCleanup]
        public void TestCleanup()
        {
            try
            {
                if (_storageDir != null && Directory.Exists(_storageDir))
                {
                    Directory.Delete(_storageDir, true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }

        private static StringContent Json(string body)
        {
            return new StringContent(body, Encoding.UTF8, "application/json");
        }

        private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
        {
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        }

        private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status,
            string titleContains, string namespaceExtension = null, string detailEquals = null)
        {
            Assert.AreEqual(status, response.StatusCode);
            Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var problem = await ReadJson(response);
            StringAssert.Contains(problem.GetProperty("title").GetString(), titleContains);
            if (namespaceExtension != null)
            {
                Assert.AreEqual(namespaceExtension, problem.GetProperty("namespace").GetString());
            }
            if (detailEquals != null)
            {
                Assert.AreEqual(detailEquals, problem.GetProperty("detail").GetString());
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
            // The reserved default namespace is always loaded and cannot be excluded (feature
            // namespace-startup-load §4.9), so it reports the policy in force rather than "inherit".
            Assert.IsTrue(entry.GetProperty("loadOnStartupEnabled").GetBoolean());
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

            // Over the 63-char cap: the one invalid-name shape that still reaches the action over
            // HTTP (a '/'-bearing name can't route; '.'/'..' get normalized away by the client).
            using var invalid = await client.PutAsync("/ns/" + new string('a', 64), null);
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
        public async Task PermissiveName_WithSpacesAndCase_CreatesAndRoutesThroughItsEncodedTwin()
        {
            using var factory = new NamespaceFactory();
            using var client = factory.CreateClient();

            const string name = "Flights EU #2";
            var seg = Uri.EscapeDataString(name); // Flights%20EU%20%232

            using (var created = await client.PutAsync("/ns/" + seg, null))
            {
                Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
                Assert.AreEqual(name, (await ReadJson(created)).GetProperty("name").GetString());
            }

            // The encoded twin routes to THIS namespace's engine (Kestrel decodes the segment).
            await CreateVertex(client, "/ns/" + seg);
            Assert.AreEqual(1, await VertexCount(client, "/ns/" + seg));
            Assert.AreEqual(0, await VertexCount(client, ""), "the spaced namespace is isolated from default");

            // It shows up in the listing under its exact name.
            using var list = await client.GetAsync("/ns");
            var names = (await ReadJson(list)).GetProperty("namespaces").EnumerateArray()
                .Select(e => e.GetProperty("name").GetString()).ToList();
            CollectionAssert.Contains(names, name);
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
            // The detail wording is asserted exactly ONCE, here: all three emit sites build the
            // body via NamespaceProblems.NotFound, so one end-to-end pin covers them all.
            await AssertProblem(read, HttpStatusCode.NotFound, "Namespace not found", namespaceExtension: "missing",
                detailEquals: "No namespace named \"missing\" exists on this Fallen-8.");

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

            // PATCH now accepts a rename AND/OR a plugin-registration override (feature
            // plugin-registration); an empty body supplies neither.
            using var badBody = await client.PatchAsync("/ns/fl-eu", Json("{}"));
            await AssertProblem(badBody, HttpStatusCode.BadRequest, "Invalid namespace update");
        }

        /// <summary>
        /// Pins the twin/exclusion invariant structurally: every namespace-scoped path in the
        /// served OpenAPI document has its /ns/{ns} twin, and every [Fallen8Level] path has none.
        /// A new endpoint that forgets the attribute (or loses it) fails here, not in a manual
        /// snapshot review.
        /// </summary>
        [TestMethod]
        public async Task OpenApi_EveryPathIsEitherTwinned_OrDeclaredFallen8Level()
        {
            // Fallen-8-level paths (spec §5.1): these exist once and must never gain a twin.
            var fallen8Level = new HashSet<string>
            {
                "/save/all",
                "/tabularasa/all",
                // NOTE: /generate and /benchmark are NOT here. They are namespace-scoped (they act on
                // exactly one graph) and additionally [NamespaceRequired], which refuses their bare
                // form at request time rather than removing it - so they are twinned like any scoped
                // route and the pinning below applies to them unchanged.
                "/delegates/validate",
                "/plugin",
                "/ns",
                "/ns/{name}",
                // Activation is a management route like every other /ns/{name} one (its controller is
                // [Fallen8Level] and its route parameter is "name", not "ns"): a twin would spell
                // /ns/{ns}/ns/{name}/activate, and it must stay OUTSIDE the residency filter so a
                // not-loaded namespace can be activated at all.
                "/ns/{name}/activate",
                "/chat",
                "/config",
            };

            using var factory = new NamespaceFactory();
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/openapi/v0.1.json");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var paths = (await ReadJson(response)).GetProperty("paths").EnumerateObject()
                .Select(p => p.Name).ToHashSet();

            foreach (var path in paths)
            {
                if (path.StartsWith("/ns/{ns}", StringComparison.Ordinal))
                {
                    continue; // a twin itself
                }

                // The whole /integrations prefix is instance-wide (feature integrations): one runtime
                // serves the whole instance and a job names the namespace it writes into, so twinning
                // would offer a second way to say the same thing and let the two disagree.
                var isFallen8Level = fallen8Level.Contains(path)
                    || path.StartsWith("/savegames", StringComparison.Ordinal)
                    || path.StartsWith("/integrations", StringComparison.Ordinal);
                var hasTwin = paths.Contains("/ns/{ns}" + path);
                if (isFallen8Level)
                {
                    Assert.IsFalse(hasTwin, path + " is Fallen-8-level and must not have a /ns twin");
                }
                else
                {
                    Assert.IsTrue(hasTwin, path + " is namespace-scoped and must have a /ns/{ns} twin");
                }
            }
        }

        /// <summary>
        /// The MACHINE-READABLE half of the no-bare-alias contract: a bare [NamespaceRequired] path is
        /// described as deprecated with no success response, so a client generated from this document
        /// cannot expose a typed method that fails every call. Its twin keeps the full contract.
        /// </summary>
        [TestMethod]
        public async Task OpenApi_BareNamespaceRequiredPaths_AdvertiseOnlyTheirRefusal()
        {
            using var factory = new NamespaceFactory();
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/openapi/v0.1.json");
            var paths = (await ReadJson(response)).GetProperty("paths");

            foreach (var bare in new[] { "/generate", "/benchmark" })
            {
                var operation = paths.GetProperty(bare).GetProperty("get");
                Assert.IsTrue(operation.GetProperty("deprecated").GetBoolean(), bare + " must be deprecated");
                var responses = operation.GetProperty("responses");
                Assert.IsFalse(responses.TryGetProperty("200", out _), bare + " must advertise no 200");
                Assert.IsTrue(responses.TryGetProperty("400", out _), bare + " must advertise its refusal");

                var twin = paths.GetProperty("/ns/{ns}" + bare).GetProperty("get");
                Assert.IsFalse(twin.TryGetProperty("deprecated", out var flag) && flag.GetBoolean(),
                    "the twin is the supported form and must not be deprecated");
                Assert.IsTrue(twin.GetProperty("responses").TryGetProperty("200", out _),
                    "the twin must keep its success response");
            }
        }

        /// <summary>
        /// Benchmark generation writes the ADDRESSED namespace and says so in its structured result.
        /// The regression this pins: /generate was [Fallen8Level], so it had no twin and every
        /// generation landed in "default" no matter which namespace the caller was working in.
        /// </summary>
        [TestMethod]
        public async Task Generate_WritesTheAddressedNamespace_AndNamesItInTheResult()
        {
            using var factory = new NamespaceFactory();
            using var client = factory.CreateClient();
            await CreateNamespace(client, "flights");

            using var response = await client.GetAsync("/ns/flights/generate?nodeCount=20&edgeCount=2");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var result = await ReadJson(response);

            Assert.AreEqual("flights", result.GetProperty("namespace").GetString());
            Assert.AreEqual(20, result.GetProperty("verticesCreated").GetInt32());
            // Counted, not derived: 2 distinct targets are available for every one of the 20 vertices.
            Assert.AreEqual(40, result.GetProperty("edgesCreated").GetInt64());
            Assert.AreEqual("uniform", result.GetProperty("distribution").GetString());
            Assert.AreEqual(20, result.GetProperty("vertexCountAfter").GetInt32());
            Assert.AreEqual(40, result.GetProperty("edgeCountAfter").GetInt32());
            Assert.IsTrue(result.GetProperty("elapsedMilliseconds").GetDouble() >= 0.0);

            // The addressed namespace grew and the default one did NOT - the whole point.
            Assert.AreEqual(20, await VertexCount(client, "/ns/flights"));
            Assert.AreEqual(0, await VertexCount(client, "/ns/default"));
        }

        /// <summary>
        /// Preferential attachment reports FEWER edges than nodeCount * edgeCount, because vertex i
        /// can only attach to the i vertices before it. The count is measured, so the result cannot
        /// drift from what was written.
        /// </summary>
        [TestMethod]
        public async Task Generate_Preferential_ReportsTheEdgesItActuallyCreated()
        {
            using var factory = new NamespaceFactory();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync(
                "/ns/default/generate?nodeCount=10&edgeCount=3&distribution=preferential");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var result = await ReadJson(response);

            Assert.AreEqual("preferential", result.GetProperty("distribution").GetString());
            // sum(min(3, i)) for i in 0..9 = 0+1+2+3*7 = 24, never the naive 30.
            Assert.AreEqual(24, result.GetProperty("edgesCreated").GetInt64());
            Assert.AreEqual(24, result.GetProperty("edgeCountAfter").GetInt32());
            Assert.AreEqual(10, await VertexCount(client, "/ns/default"));
        }

        /// <summary>
        /// Generation and the benchmark have NO bare-URL alias to "default": the bare route is
        /// registered (an unrouted path would be answered by the SPA fallback with HTTP 200) and
        /// refuses with 400 problem+json naming the scoped URL. Nothing is written on the way.
        /// </summary>
        [TestMethod]
        public async Task BareUrls_OfTheNamespaceRequiredRoutes_Are400_AndWriteNothing()
        {
            using var factory = new NamespaceFactory();
            using var client = factory.CreateClient();

            using var generate = await client.GetAsync("/generate?nodeCount=20&edgeCount=2");
            await AssertProblem(generate, HttpStatusCode.BadRequest, "Namespace required");
            StringAssert.Contains((await ReadJson(generate)).GetProperty("detail").GetString(),
                "/ns/{namespace}/generate");

            using var benchmark = await client.GetAsync("/benchmark?iterations=1");
            await AssertProblem(benchmark, HttpStatusCode.BadRequest, "Namespace required");
            StringAssert.Contains((await ReadJson(benchmark)).GetProperty("detail").GetString(),
                "/ns/{namespace}/benchmark");

            // The refusal happens BEFORE the action, so the default namespace is untouched.
            Assert.AreEqual(0, await VertexCount(client, "/ns/default"));
        }

        /// <summary>
        /// The refusal carries no <c>namespace</c> extension member: that member is the marker Studio
        /// turns into its "namespace is gone - recreate or switch" recover state, and there is no
        /// namespace here to be gone.
        /// </summary>
        [TestMethod]
        public async Task NamespaceRequiredRefusal_CarriesNoNamespaceExtension()
        {
            using var factory = new NamespaceFactory();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/generate");
            var problem = await ReadJson(response);
            Assert.IsFalse(problem.TryGetProperty("namespace", out _),
                "the namespace-required refusal must not look like a missing-namespace 404");
        }

        /// <summary>
        /// The benchmark measures the addressed namespace: run against an empty one it reports an
        /// empty graph even while another namespace holds a generated one.
        /// </summary>
        [TestMethod]
        public async Task Benchmark_MeasuresTheAddressedNamespace()
        {
            using var factory = new NamespaceFactory();
            using var client = factory.CreateClient();
            await CreateNamespace(client, "flights");

            using var generated = await client.GetAsync("/ns/flights/generate?nodeCount=20&edgeCount=2");
            Assert.AreEqual(HttpStatusCode.OK, generated.StatusCode);

            using var measured = await client.GetAsync("/ns/flights/benchmark?iterations=2");
            Assert.AreEqual(HttpStatusCode.OK, measured.StatusCode);
            var result = await ReadJson(measured);
            Assert.AreEqual(2, result.GetProperty("iterations").GetInt32());
            Assert.AreEqual(40, result.GetProperty("edgesTraversed").GetInt64());

            // "default" is still empty, so measuring it reports an empty graph rather than flights'
            // numbers - which is what a Fallen-8-level benchmark could not distinguish.
            using var empty = await client.GetAsync("/ns/default/benchmark?iterations=2");
            await AssertProblem(empty, HttpStatusCode.BadRequest, "Bad Request",
                detailEquals: "No vertices found in the graph.");
        }

        /// <summary>
        /// A namespace dropped or excluded MID-request must answer 404/503 even when the engine
        /// dereference happened on a worker thread: benchmark generation resolves the engine inside a
        /// <c>Parallel.ForEach</c> body, which reports the failure wrapped in an
        /// <c>AggregateException</c>. Testing the wrapped shape directly, because the race itself is
        /// not reproducible on demand - and without the unwrap both filters miss it and the contracted
        /// problem+json becomes a bare 500.
        /// </summary>
        [TestMethod]
        public void MidRequestNamespaceFailure_FromAWorkerThread_StillMapsToItsContract()
        {
            foreach (var wrapped in new Exception[]
            {
                new UnknownNamespaceException("flights"),
                // Two levels deep: Parallel.ForEach can hand back an aggregate of aggregates.
                new AggregateException(new AggregateException(new UnknownNamespaceException("flights"))),
                new AggregateException(new UnknownNamespaceException("flights")),
            })
            {
                var context = ExceptionContextFor(wrapped);
                new UnknownNamespaceExceptionFilter().OnException(context);

                Assert.IsTrue(context.ExceptionHandled, wrapped.GetType().Name + " must be handled");
                var problem = (ProblemDetails)((ObjectResult)context.Result).Value;
                Assert.AreEqual(StatusCodes.Status404NotFound, problem.Status);
                Assert.AreEqual("flights", problem.Extensions["namespace"]);
            }

            var notLoaded = ExceptionContextFor(
                new AggregateException(new NamespaceNotLoadedException("archived")));
            new NamespaceNotLoadedExceptionFilter().OnException(notLoaded);

            Assert.IsTrue(notLoaded.ExceptionHandled);
            var notLoadedProblem = (ProblemDetails)((ObjectResult)notLoaded.Result).Value;
            Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, notLoadedProblem.Status);
            Assert.AreEqual("notLoaded", notLoadedProblem.Extensions["namespaceState"]);

            // An unrelated aggregate is NOT unwrapped into a namespace problem: this is a mapping
            // fix, not a blanket "any wrapped exception becomes a 404".
            var unrelated = ExceptionContextFor(new AggregateException(new InvalidOperationException("boom")));
            new UnknownNamespaceExceptionFilter().OnException(unrelated);
            new NamespaceNotLoadedExceptionFilter().OnException(unrelated);
            Assert.IsFalse(unrelated.ExceptionHandled, "only the namespace refusals are unwrapped");
        }

        private static ExceptionContext ExceptionContextFor(Exception exception)
        {
            var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(),
                new ActionDescriptor());
            return new ExceptionContext(actionContext, new List<IFilterMetadata>())
            {
                Exception = exception
            };
        }

        /// <summary>
        /// Now that they are twinned, the newly scoped routes inherit the namespace guard: an unknown
        /// namespace is the same 404-with-marker every other scoped route answers.
        /// </summary>
        [TestMethod]
        public async Task Generate_UnknownNamespace_Is404WithTheStudioMarker()
        {
            using var factory = new NamespaceFactory();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/ns/nope/generate?nodeCount=5&edgeCount=1");
            await AssertProblem(response, HttpStatusCode.NotFound, "Namespace not found", "nope");
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

        #region feature namespace-startup-load: the REST surface (spec sections 4.6 / 4.7)

        /// <summary>
        ///   GET /ns LISTS a not-loaded namespace, with state "notLoaded" and ABSENT counts. Both
        ///   halves are load-bearing: hiding it reaches Studio's recover state by absence (whose
        ///   primary action recreates the namespace empty), and a zero count reaches the first-run
        ///   walkthrough - "get started" over a namespace that holds data.
        /// </summary>
        [TestMethod]
        public async Task List_IncludesANotLoadedNamespace_WithNullCounts_AndStateNotLoaded()
        {
            using var factory = NotLoadedHost();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/ns");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "one excluded namespace must not 503 the whole list");
            var namespaces = (await ReadJson(response)).GetProperty("namespaces").EnumerateArray().ToList();
            Assert.AreEqual(2, namespaces.Count);

            var archived = namespaces.Single(n => n.GetProperty("name").GetString() == "archived");
            Assert.AreEqual("notLoaded", archived.GetProperty("state").GetString());
            Assert.AreEqual(JsonValueKind.Null, archived.GetProperty("vertexCount").ValueKind,
                "absent, never 0");
            Assert.AreEqual(JsonValueKind.Null, archived.GetProperty("edgeCount").ValueKind);
            Assert.IsFalse(archived.GetProperty("loadOnStartupEnabled").GetBoolean(),
                "the policy that excluded it is visible, so an operator can undo it");

            // The loaded default in the same list still reports real numbers.
            var byDefault = namespaces.Single(n => n.GetProperty("name").GetString() == "default");
            Assert.AreEqual("ready", byDefault.GetProperty("state").GetString());
            Assert.AreEqual(0, byDefault.GetProperty("vertexCount").GetInt32());

            // GET /ns/{name} agrees with the list entry.
            using var single = await client.GetAsync("/ns/archived");
            Assert.AreEqual(HttpStatusCode.OK, single.StatusCode);
            Assert.AreEqual("notLoaded", (await ReadJson(single)).GetProperty("state").GetString());
        }

        /// <summary>
        ///   The exact 503 body, pinned once: it is built in one home
        ///   (<c>NamespaceProblems.NotLoaded</c>) and refused pre-action, so a read and a write answer
        ///   identically and neither reaches an engine.
        /// </summary>
        [TestMethod]
        public async Task DataRoute_OnANotLoadedNamespace_Answers503_WithNamespaceStateNotLoaded()
        {
            using var factory = NotLoadedHost();
            using var client = factory.CreateClient();

            using (var read = await client.GetAsync("/ns/archived/vertex/count"))
            {
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, read.StatusCode);
                Assert.AreEqual("application/problem+json", read.Content.Headers.ContentType?.MediaType);
                var problem = await ReadJson(read);
                Assert.AreEqual(503, problem.GetProperty("status").GetInt32());
                Assert.AreEqual("Namespace not loaded", problem.GetProperty("title").GetString());
                Assert.AreEqual("archived", problem.GetProperty("namespace").GetString());
                Assert.AreEqual("notLoaded", problem.GetProperty("namespaceState").GetString());
                // The detail names BOTH ways out, in the order an operator wants them: activate this
                // process now, and set the policy so the next boot loads it too. Naming only the policy
                // would send someone mid-incident to a restart they do not need; naming only activation
                // would have them repeat it after every boot.
                Assert.AreEqual(
                    "The namespace \"archived\" exists on this Fallen-8 but is not loaded in this process, so it " +
                    "cannot serve requests. Its data on disk is untouched. Load it now with POST " +
                    "/ns/archived/activate; to have every boot load it, also set its startup-load policy " +
                    "(PATCH /ns/archived with \"loadOnStartup\": \"enabled\").",
                    problem.GetProperty("detail").GetString());
            }

            // A mutation is refused before any action runs, and nothing lands anywhere.
            using (var write = await client.PutAsync("/ns/archived/vertex?waitForCompletion=true",
                Json("{\"label\":\"person\",\"creationDate\":1,\"properties\":[]}")))
            {
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, write.StatusCode);
                Assert.AreEqual("notLoaded", (await ReadJson(write)).GetProperty("namespaceState").GetString());
            }
            Assert.AreEqual(0, await VertexCount(client, ""));
        }

        /// <summary>
        ///   PUT /save addressed at a not-loaded namespace is refused, the third of the spec's data-loss
        ///   enforcement points: the alternative is a checkpoint of an engine that never loaded, written
        ///   over the one file that holds that namespace's data. The loaded half of the test is what
        ///   makes the refusal meaningful rather than a route that saves nothing anywhere.
        /// </summary>
        [TestMethod]
        public async Task Save_AddressingANotLoadedNamespace_Refuses()
        {
            using var factory = NotLoadedHost();
            using var client = factory.CreateClient();

            using (var refused = await client.PutAsync("/ns/archived/save", Json("{}")))
            {
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, refused.StatusCode,
                    "a save that ran here would checkpoint an engine that never loaded");
                var problem = await ReadJson(refused);
                Assert.AreEqual("Namespace not loaded", problem.GetProperty("title").GetString());
                Assert.AreEqual("archived", problem.GetProperty("namespace").GetString());
                Assert.AreEqual("notLoaded", problem.GetProperty("namespaceState").GetString());
            }

            Assert.AreEqual(0, Checkpoints().Length, "the refusal wrote no checkpoint anywhere");

            using (var saved = await client.PutAsync("/save", Json("{}")))
            {
                Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
            }

            Assert.IsTrue(Checkpoints().Length > 0,
                "the same route on a loaded namespace does write one, so the count above was a refusal "
                    + "and not a search that can never find anything");
        }

        private string[] Checkpoints()
        {
            return Directory.GetFiles(_storageDir, "*.f8s*", SearchOption.AllDirectories);
        }

        /// <summary>
        ///   The 404 body is byte-for-byte what it always was. The two refusals must stay
        ///   distinguishable: Studio turns a 404 carrying a string "namespace" extension into its
        ///   recover state, and that state's button recreates the namespace EMPTY.
        /// </summary>
        [TestMethod]
        public async Task UnknownNamespace_404Body_IsUnchangedBesideTheNotLoaded503()
        {
            using var factory = NotLoadedHost();
            using var client = factory.CreateClient();

            using var missing = await client.GetAsync("/ns/nowhere/vertex/count");
            Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
            var problem = await ReadJson(missing);
            Assert.AreEqual(404, problem.GetProperty("status").GetInt32());
            Assert.AreEqual("Namespace not found", problem.GetProperty("title").GetString());
            Assert.AreEqual("No namespace named \"nowhere\" exists on this Fallen-8.",
                problem.GetProperty("detail").GetString());
            Assert.AreEqual("nowhere", problem.GetProperty("namespace").GetString());
            Assert.IsFalse(problem.TryGetProperty("namespaceState", out _),
                "the 404 gained no new members - a client keying on namespaceState must not see one here");
        }

        /// <summary>
        ///   The namespace MANAGEMENT routes are never refused for a not-loaded namespace: without
        ///   this, a wrong exclusion could not be undone over REST at all. Structural rather than a
        ///   special case - the controller is Fallen-8-level, so it has no /ns/{ns} twin and its route
        ///   parameter is "name", never "ns".
        /// </summary>
        [TestMethod]
        public async Task ManagementRoutes_StayOpen_ForANotLoadedNamespace()
        {
            using var factory = NotLoadedHost();
            using var client = factory.CreateClient();

            using (var patched = await client.PatchAsync("/ns/archived", Json("{\"loadOnStartup\":\"enabled\"}")))
            {
                Assert.AreEqual(HttpStatusCode.OK, patched.StatusCode, "the policy must be reconfigurable");
                var body = await ReadJson(patched);
                Assert.IsTrue(body.GetProperty("loadOnStartupEnabled").GetBoolean());
                Assert.AreEqual("notLoaded", body.GetProperty("state").GetString(),
                    "the policy is about the next boot; this process is unchanged");
                Assert.AreEqual(JsonValueKind.Null, body.GetProperty("vertexCount").ValueKind);
            }

            using (var renamed = await client.PatchAsync("/ns/archived", Json("{\"name\":\"archived-eu\"}")))
            {
                Assert.AreEqual(HttpStatusCode.OK, renamed.StatusCode);
                Assert.AreEqual("archived-eu", (await ReadJson(renamed)).GetProperty("name").GetString());
            }

            using (var dropped = await client.DeleteAsync("/ns/archived-eu"))
            {
                Assert.AreEqual(HttpStatusCode.NoContent, dropped.StatusCode, "a not-loaded namespace must be droppable");
            }
        }

        /// <summary>
        ///   GET /status is the anonymous connection probe, so it is the ONE namespace-scoped route
        ///   that still answers for a not-loaded namespace - reporting residency, and omitting every
        ///   engine-derived field rather than reporting zeros and empty plugin lists.
        /// </summary>
        [TestMethod]
        public async Task Status_OnANotLoadedNamespace_ReportsResidency_AndOmitsDerivedNumbers()
        {
            using var factory = NotLoadedHost();
            using var client = factory.CreateClient();

            using var status = await client.GetAsync("/ns/archived/status");
            Assert.AreEqual(HttpStatusCode.OK, status.StatusCode);
            var body = await ReadJson(status);
            Assert.AreEqual("notLoaded", body.GetProperty("namespaceState").GetString());
            Assert.AreEqual(JsonValueKind.Null, body.GetProperty("vertexCount").ValueKind);
            Assert.AreEqual(JsonValueKind.Null, body.GetProperty("edgeCount").ValueKind);
            Assert.AreEqual(JsonValueKind.Null, body.GetProperty("indices").ValueKind);
            Assert.AreEqual(JsonValueKind.Null, body.GetProperty("availableIndexPlugins").ValueKind,
                "an empty list would claim this namespace can create nothing");
            Assert.AreEqual(JsonValueKind.Null, body.GetProperty("durability").ValueKind);
            // The host-level half is still true and still usable as the connection probe.
            Assert.IsTrue(body.GetProperty("usedMemory").GetInt64() > 0);
            Assert.IsFalse(body.GetProperty("apiKeyRequired").GetBoolean());

            // A loaded namespace is unaffected: real counts, real inventory, state "ready".
            using var loaded = await client.GetAsync("/status");
            var loadedBody = await ReadJson(loaded);
            Assert.AreEqual("ready", loadedBody.GetProperty("namespaceState").GetString());
            Assert.AreEqual(0, loadedBody.GetProperty("vertexCount").GetInt32());
            Assert.AreNotEqual(JsonValueKind.Null, loadedBody.GetProperty("availableIndexPlugins").ValueKind);
        }

        /// <summary>
        ///   The PATCH round-trip of the new field on a normal, loaded namespace: the tri-state
        ///   vocabulary is the one "pluginRegistration" already ships, both fields can ride one
        ///   request, and an unrecognized value is rejected by name.
        /// </summary>
        [TestMethod]
        public async Task Patch_LoadOnStartup_RoundTripsTheTriState_AndRejectsAnythingElse()
        {
            using var factory = new NamespaceFactory();
            using var client = factory.CreateClient();
            await CreateNamespace(client, "flights");

            using (var response = await client.GetAsync("/ns/flights"))
            {
                Assert.AreEqual(JsonValueKind.Null, (await ReadJson(response)).GetProperty("loadOnStartupEnabled").ValueKind,
                    "a fresh namespace inherits the global default");
            }

            foreach (var (wire, expected) in new[] { ("disabled", false), ("enabled", true) })
            {
                using var response = await client.PatchAsync("/ns/flights", Json("{\"loadOnStartup\":\"" + wire + "\"}"));
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(expected, (await ReadJson(response)).GetProperty("loadOnStartupEnabled").GetBoolean());
            }

            using (var response = await client.PatchAsync("/ns/flights", Json("{\"loadOnStartup\":\"inherit\"}")))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(JsonValueKind.Null, (await ReadJson(response)).GetProperty("loadOnStartupEnabled").ValueKind);
            }

            // One request, all three fields, one atomic update.
            using (var response = await client.PatchAsync("/ns/flights",
                Json("{\"name\":\"fl-eu\",\"pluginRegistration\":\"disabled\",\"loadOnStartup\":\"disabled\"}")))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                var body = await ReadJson(response);
                Assert.AreEqual("fl-eu", body.GetProperty("name").GetString());
                Assert.IsFalse(body.GetProperty("pluginRegistrationEnabled").GetBoolean());
                Assert.IsFalse(body.GetProperty("loadOnStartupEnabled").GetBoolean());
            }

            // An unrecognized value is refused by its own name, and changes nothing.
            using (var response = await client.PatchAsync("/ns/fl-eu", Json("{\"loadOnStartup\":\"maybe\"}")))
            {
                await AssertProblem(response, HttpStatusCode.BadRequest, "Invalid loadOnStartup");
            }
            using (var response = await client.PatchAsync("/ns/fl-eu", Json("{\"loadOnStartup\":\"Disabled\"}")))
            {
                await AssertProblem(response, HttpStatusCode.BadRequest, "Invalid loadOnStartup",
                    detailEquals: "Expected \"enabled\", \"disabled\", or \"inherit\".");
            }
            using (var response = await client.GetAsync("/ns/fl-eu"))
            {
                Assert.IsFalse((await ReadJson(response)).GetProperty("loadOnStartupEnabled").GetBoolean(),
                    "a rejected value must not disturb the policy in effect");
            }

            // A rename that rides along with a rejected policy is not applied either (B31's ordering).
            using (var response = await client.PatchAsync("/ns/fl-eu",
                Json("{\"name\":\"fl-emea\",\"loadOnStartup\":\"nope\"}")))
            {
                await AssertProblem(response, HttpStatusCode.BadRequest, "Invalid loadOnStartup");
            }
            using (var response = await client.GetAsync("/ns/fl-emea"))
            {
                Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, "the rename must not have committed");
            }

            // The new field joins the "supply at least one field" guard: an empty body is still a 400,
            // and its detail now names all three fields.
            using (var response = await client.PatchAsync("/ns/fl-eu", Json("{}")))
            {
                await AssertProblem(response, HttpStatusCode.BadRequest, "Invalid namespace update");
                StringAssert.Contains((await ReadJson(response)).GetProperty("detail").GetString(), "loadOnStartup");
            }
        }

        #endregion
    }
}
