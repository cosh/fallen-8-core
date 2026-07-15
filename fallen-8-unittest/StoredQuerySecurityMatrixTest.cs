// MIT License
//
// StoredQuerySecurityMatrixTest.cs
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
    /// Pipeline tests pinning the stored-query-library security matrix (spec section 3.3) through
    /// the real ASP.NET pipeline in BOTH kill-switch states: registration and inline fragments
    /// require EnableDynamicCodeExecution; stored invocation, filterless path search, and
    /// list/get/delete do not; 401 (authentication) always precedes 403 (capability).
    /// </summary>
    [TestClass]
    public class StoredQuerySecurityMatrixTest
    {
        private const string ApiKey = "matrix-test-key";

        private sealed class MatrixFactory : WebApplicationFactory<Program>
        {
            private readonly bool _enableCode;

            public MatrixFactory(bool enableCode)
            {
                _enableCode = enableCode;
            }

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseSetting("Fallen8:Durability:Volatile", "true");
                builder.UseSetting("Fallen8:Security:ApiKey", ApiKey);
                builder.UseSetting("Fallen8:Security:EnableDynamicCodeExecution", _enableCode ? "true" : "false");
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
        ///   the "registered while the switch was on (provisioning window)" state, reproducible
        ///   in a host whose switch is off.
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
                    VertexFilter = "return (ge) => true;"
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

        #region switch ON

        [TestMethod]
        public async Task SwitchOn_Registration_Returns201()
        {
            using var factory = new MatrixFactory(enableCode: true);
            using var client = Client(factory);

            using var response = await client.PostAsync("/storedquery", Json(RegisterBody));

            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        }

        [TestMethod]
        public async Task SwitchOn_InlineAndStoredAndFilterless_AllPass()
        {
            using var factory = new MatrixFactory(enableCode: true);
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
        }

        #endregion

        #region switch OFF

        [TestMethod]
        public async Task SwitchOff_Registration_Returns403()
        {
            using var factory = new MatrixFactory(enableCode: false);
            using var client = Client(factory);

            using var response = await client.PostAsync("/storedquery", Json(RegisterBody));

            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode,
                "Registration introduces code and is ALWAYS gated by the switch.");
        }

        [TestMethod]
        public async Task SwitchOff_InlineFragments_Return403()
        {
            using var factory = new MatrixFactory(enableCode: false);
            using var client = Client(factory);

            using var inlinePath = await client.PostAsync("/path/0/to/1", Json(InlinePathBody));
            Assert.AreEqual(HttpStatusCode.Forbidden, inlinePath.StatusCode,
                "Inline path fragments must stay 403 with the switch off.");

            using var inlineSubGraph = await client.PutAsync("/subgraph", Json(InlineSubGraphBody));
            Assert.AreEqual(HttpStatusCode.Forbidden, inlineSubGraph.StatusCode,
                "Inline subgraph fragments must stay 403 with the switch off.");
        }

        [TestMethod]
        public async Task SwitchOff_StoredInvocation_Succeeds()
        {
            using var factory = new MatrixFactory(enableCode: false);
            using var client = Client(factory);
            await CreateTwoVertices(client);

            RegisterDirectlyOnEngine(factory, "provisioned-path", StoredQueryKind.Path);
            RegisterDirectlyOnEngine(factory, "provisioned-subgraph", StoredQueryKind.SubGraph);

            using var storedPath = await client.PostAsync("/path/0/to/1", Json("{\"storedQuery\":\"provisioned-path\"}"));
            Assert.AreEqual(HttpStatusCode.OK, storedPath.StatusCode,
                "Invoking a stored path query must work with the switch OFF (the headline contract).");

            using var storedSubGraph = await client.PutAsync("/subgraph",
                Json("{\"name\":\"from-stored\",\"storedQuery\":\"provisioned-subgraph\"}"));
            Assert.AreEqual(HttpStatusCode.Created, storedSubGraph.StatusCode,
                "Instantiating a stored subgraph template must work with the switch OFF.");
        }

        [TestMethod]
        public async Task SwitchOff_FilterlessPath_Succeeds()
        {
            // Deliberate contract fix: a filterless path search compiles no user-supplied code
            // and no longer requires the switch.
            using var factory = new MatrixFactory(enableCode: false);
            using var client = Client(factory);
            await CreateTwoVertices(client);

            // ({} is the canonical filterless body; a literal `null` body is rejected as 400 by
            // MVC's implicit body-required validation before any gate runs - framework contract.)
            using var filterless = await client.PostAsync("/path/0/to/1", Json("{}"));
            Assert.AreEqual(HttpStatusCode.OK, filterless.StatusCode);
        }

        [TestMethod]
        public async Task SwitchOff_ListGetDelete_AreNeverGated()
        {
            using var factory = new MatrixFactory(enableCode: false);
            using var client = Client(factory);

            RegisterDirectlyOnEngine(factory, "manage-me", StoredQueryKind.Path);

            using var list = await client.GetAsync("/storedquery");
            Assert.AreEqual(HttpStatusCode.OK, list.StatusCode);

            using var get = await client.GetAsync("/storedquery/manage-me");
            Assert.AreEqual(HttpStatusCode.OK, get.StatusCode);

            using var delete = await client.DeleteAsync("/storedquery/manage-me");
            Assert.AreEqual(HttpStatusCode.NoContent, delete.StatusCode,
                "Deletion compiles nothing and must stay possible while the switch is off.");
        }

        [TestMethod]
        public async Task SwitchOff_MixedStoredAndInline_Returns403_TheCodeGateWins()
        {
            // A request that BOTH references a stored query AND carries inline fragments
            // introduces code, so with the switch off the capability gate answers first.
            using var factory = new MatrixFactory(enableCode: false);
            using var client = Client(factory);

            RegisterDirectlyOnEngine(factory, "mixed-ref", StoredQueryKind.Path);

            using var mixed = await client.PostAsync("/path/0/to/1",
                Json("{\"storedQuery\":\"mixed-ref\"," + InlinePathBody.Substring(1)));
            Assert.AreEqual(HttpStatusCode.Forbidden, mixed.StatusCode);
        }

        #endregion

        #region 401-before-403 ordering

        [TestMethod]
        public async Task Anonymous_InlineFragments_SwitchOff_Return401_NeverLeaking403()
        {
            using var factory = new MatrixFactory(enableCode: false);
            using var client = Client(factory, withKey: false);

            using var path = await client.PostAsync("/path/0/to/1", Json(InlinePathBody));
            Assert.AreEqual(HttpStatusCode.Unauthorized, path.StatusCode,
                "Authentication (401) must precede the capability answer (403).");

            using var register = await client.PostAsync("/storedquery", Json(RegisterBody));
            Assert.AreEqual(HttpStatusCode.Unauthorized, register.StatusCode);

            using var subgraph = await client.PutAsync("/subgraph", Json(InlineSubGraphBody));
            Assert.AreEqual(HttpStatusCode.Unauthorized, subgraph.StatusCode);
        }

        [TestMethod]
        public async Task Anonymous_StoredEndpoints_RequireAuthentication()
        {
            using var factory = new MatrixFactory(enableCode: false);
            using var client = Client(factory, withKey: false);

            RegisterDirectlyOnEngine(factory, "auth-check", StoredQueryKind.Path);

            using var storedPath = await client.PostAsync("/path/0/to/1", Json("{\"storedQuery\":\"auth-check\"}"));
            Assert.AreEqual(HttpStatusCode.Unauthorized, storedPath.StatusCode,
                "Stored invocation is ungated by the SWITCH, not by authentication.");

            using var list = await client.GetAsync("/storedquery");
            Assert.AreEqual(HttpStatusCode.Unauthorized, list.StatusCode);

            using var delete = await client.DeleteAsync("/storedquery/auth-check");
            Assert.AreEqual(HttpStatusCode.Unauthorized, delete.StatusCode);
        }

        #endregion
    }
}
