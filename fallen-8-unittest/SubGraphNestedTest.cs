// MIT License
//
// SubGraphNestedTest.cs
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
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.SubGraph;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Tests for subgraphs sourced from other subgraphs (nested subgraphs): creation from an
    /// explicit source, and recalculation of the whole dependency tree in order.
    /// </summary>
    [TestClass]
    public class SubGraphNestedTest
    {
        private static Fallen8 CreatePeopleGraph()
        {
            var fallen8 = new Fallen8(TestLoggerFactory.Create());
            var creationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds());
            var tx = new CreateVerticesTransaction();
            tx.AddVertex(creationDate, "person", new Dictionary<string, object>() { { "name", "Alice" }, { "age", 30 } });
            tx.AddVertex(creationDate, "person", new Dictionary<string, object>() { { "name", "Bob" }, { "age", 25 } });
            tx.AddVertex(creationDate, "person", new Dictionary<string, object>() { { "name", "Carol" }, { "age", 35 } });
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            return fallen8;
        }

        private static SubGraphDefinition AllPersons(string name)
        {
            return new SubGraphDefinition
            {
                Name = name,
                Pattern = new List<APattern>
                {
                    new VertexPattern { PatternName = "p", GraphElement = ge => ge.Label == "person" }
                }
            };
        }

        private static SubGraphDefinition PersonsAtLeast30(string name)
        {
            return new SubGraphDefinition
            {
                Name = name,
                Pattern = new List<APattern>
                {
                    new VertexPattern
                    {
                        PatternName = "p",
                        Vertex = v => v.TryGetProperty(out object age, "age") && (int)age >= 30
                    }
                }
            };
        }

        [TestMethod]
        public void CreateFromSource_RegistersNestedSubgraphWithSourceDependency()
        {
            var root = CreatePeopleGraph();

            Assert.IsTrue(root.SubGraphFactory.TryCreateSubGraph<BreathFirstSearchSubgraphAlgorithm>(
                out var a, "A", AllPersons("A")), "root subgraph A");
            Assert.AreEqual(3, a.SubGraph.VertexCount);

            // B is sourced from A (a subgraph), registered on the root factory.
            Assert.IsTrue(root.SubGraphFactory.TryCreateSubGraphFromSource<BreathFirstSearchSubgraphAlgorithm>(
                out var b, "B", PersonsAtLeast30("B"), a.SubGraph), "nested subgraph B from A");

            Assert.AreEqual(2, b.SubGraph.VertexCount, "Alice(30) and Carol(35) from A");
            Assert.AreEqual(a.SubGraph.Id, b.SourceFallen8Id, "B's source is A, not the root");
            Assert.AreNotEqual(root.Id, b.SourceFallen8Id, "B must not be sourced from the root");
        }

        [TestMethod]
        public void RecalculateAll_RefreshesNestedSubgraphAfterSourceChange()
        {
            var root = CreatePeopleGraph();

            Assert.IsTrue(root.SubGraphFactory.TryCreateSubGraph<BreathFirstSearchSubgraphAlgorithm>(
                out var a, "A", AllPersons("A")));
            Assert.IsTrue(root.SubGraphFactory.TryCreateSubGraphFromSource<BreathFirstSearchSubgraphAlgorithm>(
                out var b, "B", PersonsAtLeast30("B"), a.SubGraph));

            Assert.AreEqual(3, a.SubGraph.VertexCount);
            Assert.AreEqual(2, b.SubGraph.VertexCount);

            // Add a 4th person (age 40) to the ROOT.
            var creationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds());
            var tx = new CreateVerticesTransaction();
            tx.AddVertex(creationDate, "person", new Dictionary<string, object>() { { "name", "Dave" }, { "age", 40 } });
            root.EnqueueTransaction(tx).WaitUntilFinished();

            // Recalculate the whole tree.
            var recalculated = root.SubGraphFactory.RecalculateAllSubGraphs();

            Assert.AreEqual(2, recalculated, "Both A and B (nested) should be recalculated");

            Assert.IsTrue(root.SubGraphFactory.TryGetSubGraph(out var a2, "A"));
            Assert.IsTrue(root.SubGraphFactory.TryGetSubGraph(out var b2, "B"));
            Assert.AreEqual(4, a2.SubGraph.VertexCount, "A now has all 4 persons");
            Assert.AreEqual(3, b2.SubGraph.VertexCount, "B (from A) now has the 3 persons aged >= 30: Alice, Carol, Dave");
        }

        [TestMethod]
        public void RecalculateAll_ThreeLevelChain_RefreshesEntireChain()
        {
            var root = CreatePeopleGraph();

            // A: all persons; B: age >= 30 from A; C: age >= 30 from B (same filter, deeper).
            Assert.IsTrue(root.SubGraphFactory.TryCreateSubGraph<BreathFirstSearchSubgraphAlgorithm>(
                out var a, "A", AllPersons("A")));
            Assert.IsTrue(root.SubGraphFactory.TryCreateSubGraphFromSource<BreathFirstSearchSubgraphAlgorithm>(
                out var b, "B", PersonsAtLeast30("B"), a.SubGraph));
            Assert.IsTrue(root.SubGraphFactory.TryCreateSubGraphFromSource<BreathFirstSearchSubgraphAlgorithm>(
                out var c, "C", PersonsAtLeast30("C"), b.SubGraph));

            Assert.AreEqual(2, c.SubGraph.VertexCount);

            // Add Dave(40) to root; the change must propagate A -> B -> C.
            var creationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds());
            var tx = new CreateVerticesTransaction();
            tx.AddVertex(creationDate, "person", new Dictionary<string, object>() { { "name", "Dave" }, { "age", 40 } });
            root.EnqueueTransaction(tx).WaitUntilFinished();

            var recalculated = root.SubGraphFactory.RecalculateAllSubGraphs();
            Assert.AreEqual(3, recalculated, "A, B and C all recalculated");

            Assert.IsTrue(root.SubGraphFactory.TryGetSubGraph(out var c2, "C"));
            Assert.AreEqual(3, c2.SubGraph.VertexCount, "C (deepest) reflects Dave via A -> B -> C");
        }
    }
}
