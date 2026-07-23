// MIT License
//
// SubGraphPersistenceTest.cs
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
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.SubGraph;
using NoSQL.GraphDB.Core.SubGraph;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Round-trips registered subgraphs through a save/load cycle: a subgraph created from a
    /// declarative specification is persisted as a recipe and recomputed on load.
    /// </summary>
    [TestClass]
    public class SubGraphPersistenceTest
    {
        private string _tempDir;

        [TestInitialize]
        public void TestInitialize()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "f8_subgraph_persist_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        [TestCleanup]
        public void TestCleanup()
        {
            try
            {
                if (_tempDir != null && Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }

        private static Fallen8 CreateGraphWithData(bool withCompiler)
        {
            var fallen8 = new Fallen8(TestLoggerFactory.Create());
            if (withCompiler)
            {
                fallen8.SubGraphRecipeCompiler = new RecipeSubGraphCompiler();
            }

            var creationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds());
            var verticesTx = new CreateVerticesTransaction();
            verticesTx.AddVertex(creationDate, "person", new Dictionary<string, object>() { { "name", "Alice" } });
            verticesTx.AddVertex(creationDate, "person", new Dictionary<string, object>() { { "name", "Bob" } });
            verticesTx.AddVertex(creationDate, "company", new Dictionary<string, object>() { { "name", "TechCorp" } });
            fallen8.EnqueueTransaction(verticesTx).WaitUntilFinished();
            var v = verticesTx.GetCreatedVertices();

            var edgesTx = new CreateEdgesTransaction();
            edgesTx.AddEdge(v[0].Id, "knows", v[1].Id, creationDate, "knows");
            edgesTx.AddEdge(v[0].Id, "works_at", v[2].Id, creationDate, "works_at");
            fallen8.EnqueueTransaction(edgesTx).WaitUntilFinished();

            return fallen8;
        }

        private static SubGraphSpecification PersonKnowsPerson()
        {
            return new SubGraphSpecification
            {
                Name = "people",
                Patterns = new List<PatternSpecification>
                {
                    new PatternSpecification { Type = "Vertex", PatternName = "p1", VertexFilter = "return (v) => v.Label == \"person\";" },
                    new PatternSpecification { Type = "Edge", PatternName = "knows", Direction = "OutgoingEdge", EdgePropertyFilter = "return (p) => p == \"knows\";" },
                    new PatternSpecification { Type = "Vertex", PatternName = "p2", VertexFilter = "return (v) => v.Label == \"person\";" }
                }
            };
        }

        private static SubGraphSpecification AllPersons(string name)
        {
            return new SubGraphSpecification
            {
                Name = name,
                Patterns = new List<PatternSpecification>
                {
                    new PatternSpecification { Type = "Vertex", PatternName = "p", VertexFilter = "return (v) => v.Label == \"person\";" }
                }
            };
        }

        private string SaveGraph(Fallen8 fallen8)
        {
            var savePath = Path.Combine(_tempDir, "savegame.f8s");
            var saveTx = new SaveTransaction { Path = savePath, SavePartitions = 1 };
            fallen8.EnqueueTransaction(saveTx).WaitUntilFinished();
            Assert.IsFalse(String.IsNullOrEmpty(saveTx.ActualPath), "Save should report the actual path");
            return saveTx.ActualPath;
        }

        [TestMethod]
        public void SaveThenLoad_SubgraphWithRecipe_IsRehydrated()
        {
            // Arrange: create a graph, register a subgraph via the controller (attaches a recipe).
            var source = CreateGraphWithData(withCompiler: true);
            var controller = new SubGraphController(TestLoggerFactory.Create().CreateLogger<SubGraphController>(), source);
            Assert.IsInstanceOfType(controller.CreateSubGraph(PersonKnowsPerson()).Result,
                typeof(Microsoft.AspNetCore.Mvc.CreatedResult));

            // Act: save, then load into a fresh graph that has a recipe compiler registered.
            var actualPath = SaveGraph(source);

            var loaded = new Fallen8(TestLoggerFactory.Create());
            loaded.SubGraphRecipeCompiler = new RecipeSubGraphCompiler();
            loaded.EnqueueTransaction(new LoadTransaction { Path = actualPath }).WaitUntilFinished();

            // Assert: the base graph and the subgraph both came back.
            Assert.AreEqual(3, loaded.VertexCount, "The source graph should be restored");
            Assert.IsTrue(loaded.SubGraphFactory.TryGetSubGraph(out SubGraphResult result, "people"),
                "The subgraph should be rehydrated after load");
            Assert.AreEqual(2, result.SubGraph.VertexCount, "Rehydrated subgraph keeps Alice and Bob");
            Assert.AreEqual(1, result.SubGraph.EdgeCount, "Rehydrated subgraph keeps the knows edge");
            Assert.IsNotNull(result.Recipe, "The recipe should be retained so it can be persisted again");
        }

        [TestMethod]
        public void SaveThenLoad_SemanticThresholdRecipe_IsRehydrated()
        {
            // A purely declarative semantic subgraph (feature subgraph-semantic-thresholds):
            // top-level minScore + per-pattern thresholds, no fragments. The recipe must
            // round-trip the bound vector and thresholds so load recomputes membership
            // without any provider or compiled code.
            var source = CreateGraphWithData(withCompiler: true);
            var vertices = source.GetAllVertices();
            var alice = vertices.First(v => v.TryGetProperty<string>(out var n, "name") && n == "Alice");
            var bob = vertices.First(v => v.TryGetProperty<string>(out var n, "name") && n == "Bob");
            var techCorp = vertices.First(v => v.TryGetProperty<string>(out var n, "name") && n == "TechCorp");
            source.EnqueueTransaction(new SetEmbeddingsTransaction()
                    .SetEmbedding(alice.Id, "default", new[] { 1f, 0f })
                    .SetEmbedding(bob.Id, "default", new[] { 0.9f, 0.1f })
                    .SetEmbedding(techCorp.Id, "default", new[] { 0f, 1f }))
                .WaitUntilFinished();

            var specification = new SubGraphSpecification
            {
                Name = "close",
                Semantic = new SemanticTraversalSpecification { QueryVector = new[] { 1f, 0f } },
                Patterns = new List<PatternSpecification>
                {
                    new PatternSpecification { Type = "Vertex", SemanticMinScore = 0.5 },
                    new PatternSpecification { Type = "Edge" },
                    new PatternSpecification { Type = "Vertex", SemanticMinScore = 0.5 }
                }
            };
            var controller = new SubGraphController(TestLoggerFactory.Create().CreateLogger<SubGraphController>(), source);
            Assert.IsInstanceOfType(controller.CreateSubGraph(specification).Result,
                typeof(Microsoft.AspNetCore.Mvc.CreatedResult));

            var actualPath = SaveGraph(source);

            var loaded = new Fallen8(TestLoggerFactory.Create());
            loaded.SubGraphRecipeCompiler = new RecipeSubGraphCompiler();
            loaded.EnqueueTransaction(new LoadTransaction { Path = actualPath }).WaitUntilFinished();

            Assert.IsTrue(loaded.SubGraphFactory.TryGetSubGraph(out SubGraphResult result, "close"),
                "The semantic subgraph should be rehydrated after load");
            Assert.AreEqual(2, result.SubGraph.VertexCount,
                "Only Alice knows Bob matches the thresholded pattern; TechCorp is orthogonal to the query");
            Assert.AreEqual(1, result.SubGraph.EdgeCount, "works_at is pruned - its target fails the threshold");
            Assert.IsNotNull(result.Recipe, "The recipe should be retained so it can be persisted again");
            StringAssert.Contains(result.Recipe.SpecificationJson, "semanticMinScore",
                "The thresholds ride the persisted recipe");
        }

        [TestMethod]
        public void SaveThenLoad_ReflectsSourceChangesOnRehydration()
        {
            // A rehydrated subgraph is recomputed against the loaded graph, not restored
            // verbatim - so data present at save time is reflected.
            var source = CreateGraphWithData(withCompiler: true);
            var controller = new SubGraphController(TestLoggerFactory.Create().CreateLogger<SubGraphController>(), source);

            // "persons": every person vertex.
            var spec = new SubGraphSpecification
            {
                Name = "persons",
                Patterns = new List<PatternSpecification>
                {
                    new PatternSpecification { Type = "Vertex", PatternName = "p", VertexFilter = "return (v) => v.Label == \"person\";" }
                }
            };
            Assert.IsInstanceOfType(controller.CreateSubGraph(spec).Result, typeof(Microsoft.AspNetCore.Mvc.CreatedResult));

            var actualPath = SaveGraph(source);

            var loaded = new Fallen8(TestLoggerFactory.Create());
            loaded.SubGraphRecipeCompiler = new RecipeSubGraphCompiler();
            loaded.EnqueueTransaction(new LoadTransaction { Path = actualPath }).WaitUntilFinished();

            Assert.IsTrue(loaded.SubGraphFactory.TryGetSubGraph(out SubGraphResult persons, "persons"));
            Assert.AreEqual(2, persons.SubGraph.VertexCount, "Alice and Bob are the two persons at save time");
        }

        [TestMethod]
        public void Load_WithoutCompiler_SkipsSubgraphsButStillLoadsGraph()
        {
            var source = CreateGraphWithData(withCompiler: true);
            var controller = new SubGraphController(TestLoggerFactory.Create().CreateLogger<SubGraphController>(), source);
            _ = controller.CreateSubGraph(PersonKnowsPerson()).Result;
            var actualPath = SaveGraph(source);

            // No recipe compiler registered on the loading graph.
            var loaded = new Fallen8(TestLoggerFactory.Create());
            loaded.EnqueueTransaction(new LoadTransaction { Path = actualPath }).WaitUntilFinished();

            Assert.AreEqual(3, loaded.VertexCount, "The base graph must still load");
            Assert.IsFalse(loaded.SubGraphFactory.TryGetSubGraph(out _, "people"),
                "Without a compiler, persisted subgraphs are skipped rather than failing the load");
        }

        [TestMethod]
        public void GetPersistableRecipes_ExcludesDelegateOnlySubgraphs()
        {
            // A subgraph created directly from delegates (no recipe) must not be persisted.
            var fallen8 = CreateGraphWithData(withCompiler: false);
            var definition = new SubGraphDefinition
            {
                Name = "delegate-only",
                Pattern = new List<APattern>
                {
                    new VertexPattern { PatternName = "p", Vertex = v => v.Label == "person" }
                }
            };

            Assert.IsTrue(fallen8.SubGraphFactory.TryCreateSubGraph<BreadthFirstSearchSubgraphAlgorithm>(
                out _, "delegate-only", definition));

            var recipes = new List<SubGraphRecipe>(fallen8.SubGraphFactory.GetPersistableRecipes());
            Assert.AreEqual(0, recipes.Count, "A subgraph without a recipe is not persistable");
        }

        [TestMethod]
        public void SaveThenLoad_NestedSubgraph_IsRehydratedFromItsParent()
        {
            // A is a root subgraph; B is nested (sourced from A). Both must survive a
            // save/load cycle, with B rebuilt from the rehydrated A rather than the root.
            var source = CreateGraphWithData(withCompiler: true);
            var controller = new SubGraphController(TestLoggerFactory.Create().CreateLogger<SubGraphController>(), source);

            Assert.IsInstanceOfType(controller.CreateSubGraph(AllPersons("A")).Result,
                typeof(Microsoft.AspNetCore.Mvc.CreatedResult), "root subgraph A");
            Assert.IsInstanceOfType(controller.CreateSubGraph(AllPersons("B"), "A").Result,
                typeof(Microsoft.AspNetCore.Mvc.CreatedResult), "nested subgraph B from A");

            Assert.IsTrue(source.SubGraphFactory.TryGetSubGraph(out var aBefore, "A"));
            Assert.IsTrue(source.SubGraphFactory.TryGetSubGraph(out var bBefore, "B"));
            Assert.AreEqual(aBefore.SubGraph.Id, bBefore.SourceFallen8Id, "B is sourced from A before save");

            var actualPath = SaveGraph(source);

            var loaded = new Fallen8(TestLoggerFactory.Create());
            loaded.SubGraphRecipeCompiler = new RecipeSubGraphCompiler();
            loaded.EnqueueTransaction(new LoadTransaction { Path = actualPath }).WaitUntilFinished();

            Assert.IsTrue(loaded.SubGraphFactory.TryGetSubGraph(out var a, "A"), "A rehydrated");
            Assert.IsTrue(loaded.SubGraphFactory.TryGetSubGraph(out var b, "B"), "B (nested) rehydrated");
            Assert.AreEqual(2, a.SubGraph.VertexCount);
            Assert.AreEqual(2, b.SubGraph.VertexCount);
            Assert.AreEqual(loaded.Id, a.SourceFallen8Id, "A is sourced from the loaded root");
            Assert.AreEqual(a.SubGraph.Id, b.SourceFallen8Id, "B is sourced from the rehydrated A, not the root");
        }
    }
}
