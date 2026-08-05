// MIT License
//
// StoredQuerySecurityMatrixTest.cs
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
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.StoredQueries;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Pipeline tests pinning the stored-query-library security matrix through the real ASP.NET
    /// pipeline. Dynamic code execution is ALWAYS ON (there is no kill switch), so registration,
    /// inline fragments, stored invocation, filterless path search, and list/get/delete all work;
    /// authentication is still enforced when an API key is configured (401 for an anonymous caller).
    /// </summary>
    [TestClass]
    public class StoredQuerySecurityMatrixTest
    {
        private const string ApiKey = "matrix-test-key";

        private sealed class MatrixFactory : WebApplicationFactory<Program>
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseSetting("Fallen8:Durability:Volatile", "true");
                builder.UseSetting("Fallen8:Security:ApiKey", ApiKey);
            }
        }

        private static HttpClient Client(MatrixFactory factory, bool withKey = true)
        {
            var client = factory.CreateClient();
            if (withKey)
            {
                client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
            }
            return client;
        }

        private static StringContent Json(string body)
        {
            return new StringContent(body, Encoding.UTF8, "application/json");
        }

        private const string RegisterBody =
            "{\"name\":\"matrix-path\",\"kind\":\"Path\",\"path\":{\"filter\":{\"vertexFilter\":\"return (v) => true;\"}}}";

        private const string InlinePathBody =
            "{\"filter\":{\"vertexFilter\":\"return (v) => true;\"}}";

        private const string InlineSubGraphBody =
            "{\"name\":\"matrix-inline-sg\",\"vertexFilter\":\"return (ge) => true;\"}";

        /// <summary>
        ///   Registers a compiled stored query directly on the hosted engine, bypassing REST -
        ///   the state an operator provisions out of band (a library/plugin host, a restored
        ///   save game), so invocation is exercised without a REST registration call.
        /// </summary>
        private static void RegisterDirectlyOnEngine(MatrixFactory factory, string name, StoredQueryKind kind)
        {
            var engine = factory.Services.GetRequiredService<IFallen8>();

            string specificationJson;
            if (kind == StoredQueryKind.Path)
            {
                specificationJson = JsonSerializer.Serialize(new StoredPathQueryBlock
                {
                    Filter = new PathFilterSpecification { Vertex = "return (v) => true;" }
                }, AppJsonContext.Default.StoredPathQueryBlock);
            }
            else
            {
                specificationJson = JsonSerializer.Serialize(new StoredSubGraphQueryBlock
                {
                    VertexFilter = "return (v) => true;"
                }, AppJsonContext.Default.StoredSubGraphQueryBlock);
            }

            var definition = new StoredQueryDefinition
            {
                Name = name,
                Kind = kind,
                SpecificationJson = specificationJson,
                CreatedAt = DateTime.UtcNow
            };

            var compiler = new StoredQueryCompiler();
            Assert.IsTrue(compiler.TryCompile(definition, out var artifact, out var error), error);

            var tx = new RegisterStoredQueryTransaction
            {
                Entry = new StoredQueryEntry(definition, StoredQueryCompileState.Compiled, artifact)
            };
            var txInfo = engine.EnqueueTransaction(tx);
            txInfo.WaitUntilFinished();
            Assert.AreEqual(TransactionState.Finished, txInfo.TransactionState);
        }

        private static async Task CreateTwoVertices(HttpClient client)
        {
            const string vertex = "{\"label\":\"person\",\"creationDate\":1}";
            (await client.PutAsync("/vertex?waitForCompletion=true", Json(vertex))).EnsureSuccessStatusCode();
            (await client.PutAsync("/vertex?waitForCompletion=true", Json(vertex))).EnsureSuccessStatusCode();
        }

        #region registration and invocation over REST

        [TestMethod]
        public async Task Registration_Returns201()
        {
            using var factory = new MatrixFactory();
            using var client = Client(factory);

            using var response = await client.PostAsync("/storedquery", Json(RegisterBody));

            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        }

        [TestMethod]
        public async Task InlineAndStoredAndFilterless_AllPass()
        {
            using var factory = new MatrixFactory();
            using var client = Client(factory);
            await CreateTwoVertices(client);

            using var register = await client.PostAsync("/storedquery", Json(RegisterBody));
            Assert.AreEqual(HttpStatusCode.Created, register.StatusCode);

            using var inlinePath = await client.PostAsync("/path/0/to/1", Json(InlinePathBody));
            Assert.AreEqual(HttpStatusCode.OK, inlinePath.StatusCode);

            using var storedPath = await client.PostAsync("/path/0/to/1", Json("{\"storedQuery\":\"matrix-path\"}"));
            Assert.AreEqual(HttpStatusCode.OK, storedPath.StatusCode);

            using var filterless = await client.PostAsync("/path/0/to/1", Json("{}"));
            Assert.AreEqual(HttpStatusCode.OK, filterless.StatusCode);

            using var inlineSubGraph = await client.PutAsync("/subgraph", Json(InlineSubGraphBody));
            Assert.AreEqual(HttpStatusCode.Created, inlineSubGraph.StatusCode);

            // The stored-subgraph and list/get/delete rows of the matrix.
            const string registerSubGraph =
                "{\"name\":\"matrix-subgraph\",\"kind\":\"SubGraph\",\"subGraph\":{\"vertexFilter\":\"return (ge) => true;\"}}";
            using var registerSg = await client.PostAsync("/storedquery", Json(registerSubGraph));
            Assert.AreEqual(HttpStatusCode.Created, registerSg.StatusCode);

            using var storedSubGraph = await client.PutAsync("/subgraph",
                Json("{\"name\":\"sg-on\",\"storedQuery\":\"matrix-subgraph\"}"));
            Assert.AreEqual(HttpStatusCode.Created, storedSubGraph.StatusCode);

            using var list = await client.GetAsync("/storedquery");
            Assert.AreEqual(HttpStatusCode.OK, list.StatusCode);

            using var get = await client.GetAsync("/storedquery/matrix-subgraph");
            Assert.AreEqual(HttpStatusCode.OK, get.StatusCode);

            using var delete = await client.DeleteAsync("/storedquery/matrix-subgraph");
            Assert.AreEqual(HttpStatusCode.NoContent, delete.StatusCode);
        }

        #endregion

        #region no gate exists: only authentication applies

        [TestMethod]
        public async Task StoredInvocation_Succeeds()
        {
            using var factory = new MatrixFactory();
            using var client = Client(factory);
            await CreateTwoVertices(client);

            RegisterDirectlyOnEngine(factory, "provisioned-path", StoredQueryKind.Path);
            RegisterDirectlyOnEngine(factory, "provisioned-subgraph", StoredQueryKind.SubGraph);

            using var storedPath = await client.PostAsync("/path/0/to/1", Json("{\"storedQuery\":\"provisioned-path\"}"));
            Assert.AreEqual(HttpStatusCode.OK, storedPath.StatusCode,
                "Invoking a stored path query registered out of band must work (the headline contract).");

            using var storedSubGraph = await client.PutAsync("/subgraph",
                Json("{\"name\":\"from-stored\",\"storedQuery\":\"provisioned-subgraph\"}"));
            Assert.AreEqual(HttpStatusCode.Created, storedSubGraph.StatusCode,
                "Instantiating a stored subgraph template registered out of band must work.");
        }

        [TestMethod]
        public async Task FilterlessPath_Succeeds()
        {
            // A filterless path search compiles no user-supplied code at all, so nothing but
            // authentication stands between the caller and a 200.
            using var factory = new MatrixFactory();
            using var client = Client(factory);
            await CreateTwoVertices(client);

            // ({} is the canonical filterless body; a literal `null` body is rejected as 400 by
            // MVC's implicit body-required validation before the action runs - framework contract.)
            using var filterless = await client.PostAsync("/path/0/to/1", Json("{}"));
            Assert.AreEqual(HttpStatusCode.OK, filterless.StatusCode);
        }

        [TestMethod]
        public async Task ListGetDelete_AreNeverGated()
        {
            using var factory = new MatrixFactory();
            using var client = Client(factory);

            RegisterDirectlyOnEngine(factory, "manage-me", StoredQueryKind.Path);

            using var list = await client.GetAsync("/storedquery");
            Assert.AreEqual(HttpStatusCode.OK, list.StatusCode);

            using var get = await client.GetAsync("/storedquery/manage-me");
            Assert.AreEqual(HttpStatusCode.OK, get.StatusCode);

            using var delete = await client.DeleteAsync("/storedquery/manage-me");
            Assert.AreEqual(HttpStatusCode.NoContent, delete.StatusCode,
                "Deletion compiles nothing and is never refused for a code-execution reason.");
        }

        #endregion

        #region authentication still applies (when a key is configured)

        [TestMethod]
        public async Task Anonymous_InlineFragments_Return401_WhenKeyConfigured()
        {
            using var factory = new MatrixFactory();
            using var client = Client(factory, withKey: false);

            using var path = await client.PostAsync("/path/0/to/1", Json(InlinePathBody));
            Assert.AreEqual(HttpStatusCode.Unauthorized, path.StatusCode,
                "With a key configured, an anonymous caller is 401: auth is layered on the code endpoints like any other.");

            using var register = await client.PostAsync("/storedquery", Json(RegisterBody));
            Assert.AreEqual(HttpStatusCode.Unauthorized, register.StatusCode);

            using var subgraph = await client.PutAsync("/subgraph", Json(InlineSubGraphBody));
            Assert.AreEqual(HttpStatusCode.Unauthorized, subgraph.StatusCode);
        }

        [TestMethod]
        public async Task Anonymous_StoredEndpoints_RequireAuthentication()
        {
            using var factory = new MatrixFactory();
            using var client = Client(factory, withKey: false);

            RegisterDirectlyOnEngine(factory, "auth-check", StoredQueryKind.Path);

            using var storedPath = await client.PostAsync("/path/0/to/1", Json("{\"storedQuery\":\"auth-check\"}"));
            Assert.AreEqual(HttpStatusCode.Unauthorized, storedPath.StatusCode,
                "Stored invocation carries no code-execution gate, but authentication still applies.");

            using var list = await client.GetAsync("/storedquery");
            Assert.AreEqual(HttpStatusCode.Unauthorized, list.StatusCode);

            using var delete = await client.DeleteAsync("/storedquery/auth-check");
            Assert.AreEqual(HttpStatusCode.Unauthorized, delete.StatusCode);
        }

        #endregion
    }
}
