// MIT License
//
// ElementFulltextMatchTest.cs
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
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Expression;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Pins the element-fulltext-match contract (features/open/element-fulltext-match/):
    /// <see cref="AGraphElementModel.AnyPropertyValueMatches"/> (string property VALUES only,
    /// reserved embedding entries skipped), the safe <see cref="AGraphElementModel.TryGetProperty{T}"/>
    /// (type mismatch and stored null read as absent instead of throwing/leaking null), and the
    /// engine paths that must KEEP null-presence via the internal raw read: transaction undo,
    /// batch conflict detection, and the scan surface's null skip.
    /// </summary>
    [TestClass]
    public class ElementFulltextMatchTest
    {
        private ILoggerFactory _loggerFactory;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
        }

        private static VertexModel Vertex(Dictionary<string, object> properties)
        {
            return new VertexModel(1, 1u, "test", properties);
        }

        #region TryGetProperty is-pattern

        [TestMethod]
        public void TryGetProperty_WrongTypedTarget_IsFalse_NotThrow()
        {
            var v = Vertex(new Dictionary<string, object> { { "age", 42 }, { "name", "Ada" } });

            // The exact crash class the delegate surface hit: an NL draft guessing the wrong
            // out type threw InvalidCastException mid-traversal; now it filters the element.
            string s;
            Assert.IsFalse(v.TryGetProperty(out s, "age"), "int read as string is a miss, not a throw.");
            Assert.IsNull(s);

            int i;
            Assert.IsFalse(v.TryGetProperty(out i, "name"), "string read as int is a miss, not a throw.");
            Assert.AreEqual(0, i);

            long l;
            Assert.IsFalse(v.TryGetProperty(out l, "age"), "No numeric widening: int is not long.");

            object o;
            Assert.IsTrue(v.TryGetProperty(out o, "age"), "An object read still sees any non-null value.");
            Assert.AreEqual(42, o);
        }

        #endregion

        #region AnyPropertyValueMatches

        [TestMethod]
        public void AnyPropertyValueMatches_SeesOnlyStringValues()
        {
            var v = Vertex(new Dictionary<string, object>
            {
                { "name", "TechCorp" },
                { "industry", "Global Solutions" },
                { "age", 42 },
                { "score", 3.14 },
                { "flag", true },
            });

            Assert.IsTrue(v.AnyPropertyValueMatches(s => s.Contains("Tech")));
            Assert.IsTrue(v.AnyPropertyValueMatches(s => s.EndsWith("Solutions")));
            Assert.IsFalse(v.AnyPropertyValueMatches(s => s.Contains("42")),
                "Non-string values are never stringified into the predicate.");
            Assert.IsFalse(v.AnyPropertyValueMatches(s => s.Contains("name")),
                "Property NAMES never reach the predicate - values only.");

            // Match semantics live in the caller's BCL calls, including case-insensitivity.
            Assert.IsFalse(v.AnyPropertyValueMatches(s => s.Contains("tech")));
            Assert.IsTrue(v.AnyPropertyValueMatches(s => s.Contains("tech", StringComparison.OrdinalIgnoreCase)));
        }

        [TestMethod]
        public void AnyPropertyValueMatches_SkipsReservedEmbeddingEntries()
        {
            // The embedding model stamp IS a string ("nomic-embed-text"); a content search for
            // "text" must not false-positive on elements that merely carry an embedding.
            var v = Vertex(new Dictionary<string, object>
            {
                { "desc", "hello world" },
                { AGraphElementModel.EmbeddingPropertyPrefix + "default", new float[] { 0.1f, 0.2f } },
                { AGraphElementModel.EmbeddingModelStampPrefix + "default", "nomic-embed-text" },
            });

            Assert.IsFalse(v.AnyPropertyValueMatches(s => s.Contains("text")),
                "The reserved model-stamp string must never surface as content.");
            Assert.IsFalse(v.AnyPropertyValueMatches(s => s.Contains("nomic")));
            Assert.IsTrue(v.AnyPropertyValueMatches(s => s.Contains("hello")),
                "User property values still match.");
        }

        [TestMethod]
        public void AnyPropertyValueMatches_NullPredicate_NullValue_AndNoProperties_AreFalse()
        {
            Assert.IsFalse(Vertex(null).AnyPropertyValueMatches(s => true), "No properties is a miss.");
            Assert.IsFalse(Vertex(new Dictionary<string, object>()).AnyPropertyValueMatches(s => true));

            var v = Vertex(new Dictionary<string, object> { { "name", "Ada" }, { "nothing", null } });
            Assert.IsFalse(v.AnyPropertyValueMatches(null), "A null predicate is false, never a throw.");
            Assert.IsTrue(v.AnyPropertyValueMatches(s => s != null),
                "A stored null value never reaches the predicate (it is not a string).");
        }

        [TestMethod]
        public void AnyPropertyValueMatches_WorksOnEdges()
        {
            var source = Vertex(null);
            var target = Vertex(null);
            var edge = new EdgeModel(2, 1u, target, source, "knows",
                properties: new Dictionary<string, object> { { "since", "2024" }, { "weight", 1.5 } });

            Assert.IsTrue(edge.AnyPropertyValueMatches(s => s.StartsWith("20")));
            Assert.IsFalse(edge.AnyPropertyValueMatches(s => s.Contains("1.5")));
        }

        #endregion

        #region null-presence stays intact where it must (raw read)

        [TestMethod]
        public void Rollback_RestoresNullValuedProperty_InsteadOfRemovingIt()
        {
            // The undo journal must capture "present with null" (raw read), not "absent" (typed
            // read): otherwise this rollback would REMOVE the key instead of restoring it.
            var fallen8 = new Fallen8(_loggerFactory);
            var seed = new CreateVerticesTransaction();
            seed.AddVertex(1u, "person", new Dictionary<string, object> { { "k", null } });
            fallen8.EnqueueTransaction(seed).WaitUntilFinished();
            int id = seed.GetCreatedVertices()[0].Id;

            var tx = new DelegateTransaction(ctx =>
            {
                ctx.SetProperty(id, "k", null); // equal-value no-op, still journalled
                throw new InvalidOperationException("forced rollback");
            }, "null-restore");
            var info = fallen8.EnqueueTransaction(tx);
            info.WaitUntilFinished();

            Assert.AreEqual(TransactionState.RolledBack, info.TransactionState);
            VertexModel v;
            Assert.IsTrue(fallen8.TryGetVertex(out v, id));
            Assert.IsTrue(v.GetAllProperties().ContainsKey("k"),
                "Rollback must restore the null-valued key, not remove it.");
            Assert.IsNull(v.GetAllProperties()["k"]);

            fallen8.Dispose();
        }

        [TestMethod]
        public void Rollback_RestoresRemovedNullValuedProperty()
        {
            // Same journal, remove path (IFallen8WriterContext.RemoveProperty): the prior state
            // "present with null" must come back on rollback.
            var fallen8 = new Fallen8(_loggerFactory);
            var seed = new CreateVerticesTransaction();
            seed.AddVertex(1u, "person", new Dictionary<string, object> { { "k", null }, { "other", 1 } });
            fallen8.EnqueueTransaction(seed).WaitUntilFinished();
            int id = seed.GetCreatedVertices()[0].Id;

            var tx = new DelegateTransaction(ctx =>
            {
                ctx.RemoveProperty(id, "k");
                throw new InvalidOperationException("forced rollback");
            }, "null-remove-restore");
            var info = fallen8.EnqueueTransaction(tx);
            info.WaitUntilFinished();

            Assert.AreEqual(TransactionState.RolledBack, info.TransactionState);
            VertexModel v;
            Assert.IsTrue(fallen8.TryGetVertex(out v, id));
            Assert.IsTrue(v.GetAllProperties().ContainsKey("k"),
                "Rollback must restore the removed null-valued key.");
            Assert.IsNull(v.GetAllProperties()["k"]);

            fallen8.Dispose();
        }

        [TestMethod]
        public void BatchSet_OnNullValuedProperty_IsAConflict_NotAnOverwrite()
        {
            // Batch conflict validation must see a stored null as an EXISTING differing value
            // (raw read): a typed read would report the key absent and let the write through.
            var fallen8 = new Fallen8(_loggerFactory);
            var seed = new CreateVerticesTransaction();
            seed.AddVertex(1u, "person", new Dictionary<string, object> { { "k", null } });
            fallen8.EnqueueTransaction(seed).WaitUntilFinished();
            int id = seed.GetCreatedVertices()[0].Id;

            var batch = new AddPropertiesTransaction
            {
                Properties = new List<PropertyAddDefinition>
                {
                    new PropertyAddDefinition { GraphElementId = id, PropertyId = "k", Property = 5 }
                }
            };
            var info = fallen8.EnqueueTransaction(batch);
            info.WaitUntilFinished();

            Assert.AreEqual(TransactionState.RolledBack, info.TransactionState,
                "Setting a different value over a null-valued key is a conflict.");
            VertexModel v;
            Assert.IsTrue(fallen8.TryGetVertex(out v, id));
            Assert.IsTrue(v.GetAllProperties().ContainsKey("k"));
            Assert.IsNull(v.GetAllProperties()["k"], "The stored null survives the rejected batch.");

            fallen8.Dispose();
        }

        [TestMethod]
        public void GraphScan_SkipsNullValuedProperty_InsteadOfThrowing()
        {
            // Before the is-pattern, the scan comparator called null.Equals(...) and the parallel
            // scan surfaced a NullReferenceException; a null-valued property is now a clean skip.
            var fallen8 = new Fallen8(_loggerFactory);
            var seed = new CreateVerticesTransaction();
            seed.AddVertex(1u, "person", new Dictionary<string, object> { { "p", null } });
            seed.AddVertex(1u, "person", new Dictionary<string, object> { { "p", "x" } });
            fallen8.EnqueueTransaction(seed).WaitUntilFinished();

            List<AGraphElementModel> result;
            Assert.IsTrue(fallen8.GraphScan(out result, "p", "x", BinaryOperator.Equals));
            Assert.AreEqual(1, result.Count, "Only the non-null value participates in the comparison.");

            Assert.IsFalse(fallen8.GraphScan(out result, "p", "y", BinaryOperator.NotEquals) && result.Count > 1,
                "A null-valued property does not participate in NotEquals either.");

            fallen8.Dispose();
        }

        #endregion

        #region fragment end-to-end (REST)

        private sealed class RestFactory : WebApplicationFactory<Program>
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                // Volatile durability so booting the host writes no checkpoint/WAL.
                builder.UseSetting("Fallen8:Durability:Volatile", "true");
            }
        }

        private static Fallen8 EngineOf(RestFactory factory)
        {
            return factory.Services.GetRequiredService<NoSQL.GraphDB.App.Namespaces.Fallen8Namespaces>().Default.Engine;
        }

        private static StringContent Json(string s)
        {
            return new StringContent(s, Encoding.UTF8, "application/json");
        }

        /// <summary>
        ///   Seeds the diamond a -> b -> d / a -> c -> d used by the traversal tests: a and d are
        ///   labelled "end"; b and c carry the given properties. Returns (a, b, c, d) ids.
        /// </summary>
        private static (int a, int b, int c, int d) SeedDiamond(RestFactory factory,
            Dictionary<string, object> bProps, Dictionary<string, object> cProps)
        {
            var engine = EngineOf(factory);
            var vtx = new CreateVerticesTransaction();
            vtx.AddVertex(1u, "end");
            vtx.AddVertex(1u, "company", bProps);
            vtx.AddVertex(1u, "company", cProps);
            vtx.AddVertex(1u, "end");
            engine.EnqueueTransaction(vtx).WaitUntilFinished();
            var v = vtx.GetCreatedVertices();

            var edges = new CreateEdgesTransaction();
            edges.AddEdge(v[0].Id, "knows", v[1].Id, 1u, "knows");
            edges.AddEdge(v[1].Id, "knows", v[3].Id, 1u, "knows");
            edges.AddEdge(v[0].Id, "knows", v[2].Id, 1u, "knows");
            edges.AddEdge(v[2].Id, "knows", v[3].Id, 1u, "knows");
            engine.EnqueueTransaction(edges).WaitUntilFinished();

            return (v[0].Id, v[1].Id, v[2].Id, v[3].Id);
        }

        [TestMethod]
        public async Task Validate_FragmentsUsingAnyPropertyValueMatches_Compile()
        {
            // Proves the existing fragment compilation usings suffice: the nested lambda and
            // StringComparison resolve with no codegen change.
            using var factory = new RestFactory();
            using var client = factory.CreateClient();

            foreach (var (kind, fragment) in new[]
            {
                ("VertexFilter", "return (v) => v.AnyPropertyValueMatches(s => s.Contains(\\\"Tech\\\", StringComparison.OrdinalIgnoreCase));"),
                ("EdgeFilter", "return (e) => e.AnyPropertyValueMatches(s => s.EndsWith(\\\"Corp\\\"));"),
            })
            {
                using var response = await client.PostAsync("/delegates/validate",
                    Json($"{{\"delegateKind\":\"{kind}\",\"fragment\":\"{fragment}\"}}"));
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.IsTrue(doc.RootElement.GetProperty("valid").GetBoolean(),
                    kind + " fragment must compile: " + doc.RootElement.ToString());
            }
        }

        [TestMethod]
        public async Task Path_VertexFilterUsingNewMember_PrunesTheNonMatchingRoute()
        {
            using var factory = new RestFactory();
            using var client = factory.CreateClient();
            var (a, _, _, d) = SeedDiamond(factory,
                bProps: new Dictionary<string, object> { { "name", "TechCorp" } },
                cProps: new Dictionary<string, object> { { "name", "OtherCo" } });

            const string filter =
                "return (v) => v.Label == \\\"end\\\" || v.AnyPropertyValueMatches(s => s.Contains(\\\"Tech\\\"));";
            using var response = await client.PostAsync($"/path/{a}/to/{d}",
                Json($"{{\"filter\":{{\"vertexFilter\":\"{filter}\"}}}}"));

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual(1, doc.RootElement.GetArrayLength(),
                "Only the route through the 'Tech' vertex survives the filter.");
        }

        [TestMethod]
        public async Task Path_WrongTypedOutFragment_FiltersInsteadOfFaulting()
        {
            // b stores age as an int (passes), c stores age as a STRING: the typed read used to
            // throw InvalidCastException inside the traversal (a 500); it now filters c cleanly.
            using var factory = new RestFactory();
            using var client = factory.CreateClient();
            var (a, _, _, d) = SeedDiamond(factory,
                bProps: new Dictionary<string, object> { { "age", 40 } },
                cProps: new Dictionary<string, object> { { "age", "forty" } });

            const string filter =
                "return (v) => v.Label == \\\"end\\\" || (v.TryGetProperty(out int age, \\\"age\\\") && age > 30);";
            using var response = await client.PostAsync($"/path/{a}/to/{d}",
                Json($"{{\"filter\":{{\"vertexFilter\":\"{filter}\"}}}}"));

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                "A mis-typed out parameter filters the element, it must not fault the traversal: "
                + await response.Content.ReadAsStringAsync());
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual(1, doc.RootElement.GetArrayLength());
        }

        [TestMethod]
        public async Task SubGraph_TopLevelVertexFilterUsingNewMember_SelectsOnlyMatches()
        {
            using var factory = new RestFactory();
            using var client = factory.CreateClient();
            SeedDiamond(factory,
                bProps: new Dictionary<string, object> { { "name", "TechCorp" } },
                cProps: new Dictionary<string, object> { { "name", "OtherCo" } });

            const string filter = "return (v) => v.AnyPropertyValueMatches(s => s.EndsWith(\\\"Corp\\\"));";
            using var response = await client.PutAsync("/subgraph",
                Json($"{{\"name\":\"eft-sg\",\"vertexFilter\":\"{filter}\"}}"));

            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual(1, doc.RootElement.GetProperty("vertexCount").GetInt32(),
                "Only the 'TechCorp' vertex matches the value predicate.");
        }

        #endregion
    }
}
