// MIT License
//
// SubGraphNestedTest.cs
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
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.SubGraph;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Tests for subgraphs sourced from other subgraphs (nested subgraphs): creation from an
    /// explicit source, recalculation of the whole dependency tree in order, and INDIVIDUAL
    /// recalculation of a single subgraph, where the defect (B26) was a recalculated parent
    /// leaving its nested children bound to the discarded engine instance.
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
                    new VertexPattern { PatternName = "p", Vertex = v => v.Label == "person" }
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

        private static void AddPerson(Fallen8 fallen8, string name, int age)
        {
            var creationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds());
            var tx = new CreateVerticesTransaction();
            tx.AddVertex(creationDate, "person", new Dictionary<string, object>() { { "name", name }, { "age", age } });
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();
        }

        [TestMethod]
        public void CreateFromSource_RegistersNestedSubgraphWithSourceDependency()
        {
            var root = CreatePeopleGraph();

            Assert.IsTrue(root.SubGraphFactory.TryCreateSubGraph<BreadthFirstSearchSubgraphAlgorithm>(
                out var a, "A", AllPersons("A")), "root subgraph A");
            Assert.AreEqual(3, a.SubGraph.VertexCount);

            // B is sourced from A (a subgraph), registered on the root factory.
            Assert.IsTrue(root.SubGraphFactory.TryCreateSubGraphFromSource<BreadthFirstSearchSubgraphAlgorithm>(
                out var b, "B", PersonsAtLeast30("B"), a.SubGraph), "nested subgraph B from A");

            Assert.AreEqual(2, b.SubGraph.VertexCount, "Alice(30) and Carol(35) from A");
            Assert.AreEqual(a.SubGraph.Id, b.SourceFallen8Id, "B's source is A, not the root");
            Assert.AreNotEqual(root.Id, b.SourceFallen8Id, "B must not be sourced from the root");
        }

        [TestMethod]
        public void RecalculateAll_RefreshesNestedSubgraphAfterSourceChange()
        {
            var root = CreatePeopleGraph();

            Assert.IsTrue(root.SubGraphFactory.TryCreateSubGraph<BreadthFirstSearchSubgraphAlgorithm>(
                out var a, "A", AllPersons("A")));
            Assert.IsTrue(root.SubGraphFactory.TryCreateSubGraphFromSource<BreadthFirstSearchSubgraphAlgorithm>(
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
            Assert.IsTrue(root.SubGraphFactory.TryCreateSubGraph<BreadthFirstSearchSubgraphAlgorithm>(
                out var a, "A", AllPersons("A")));
            Assert.IsTrue(root.SubGraphFactory.TryCreateSubGraphFromSource<BreadthFirstSearchSubgraphAlgorithm>(
                out var b, "B", PersonsAtLeast30("B"), a.SubGraph));
            Assert.IsTrue(root.SubGraphFactory.TryCreateSubGraphFromSource<BreadthFirstSearchSubgraphAlgorithm>(
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

        // ---------------------------------------------------------------------
        // Individual recalculation and reference-identity rebinding (B26).
        //
        // The tests above all go through the BULK RecalculateAllSubGraphs path and
        // assert vertex counts. These recalculate ONE named subgraph at a time and
        // assert reference identity: recalculation swaps in a NEW engine instance
        // for the recalculated subgraph, so every child sourced from it must be
        // rebound to that live instance instead of keeping the discarded one.
        // ---------------------------------------------------------------------

        [TestMethod]
        public void RecalculateParentThenChild_ChildReadsTheRefreshedParent()
        {
            // Arrange: A = all persons (from the root), B = persons aged >= 30 (from A).
            var root = CreatePeopleGraph();

            Assert.IsTrue(root.SubGraphFactory.TryCreateSubGraph<BreadthFirstSearchSubgraphAlgorithm>(
                out var a, "A", AllPersons("A")));
            Assert.IsTrue(root.SubGraphFactory.TryCreateSubGraphFromSource<BreadthFirstSearchSubgraphAlgorithm>(
                out var b, "B", PersonsAtLeast30("B"), a.SubGraph));

            Assert.AreEqual(3, a.SubGraph.VertexCount);
            Assert.AreEqual(2, b.SubGraph.VertexCount, "Alice(30) and Carol(35)");

            var staleParentInstance = a.SubGraph;

            AddPerson(root, "Dave", 40);

            // Act: recalculate the two subgraphs INDIVIDUALLY, never through RecalculateAllSubGraphs.
            Assert.IsTrue(root.SubGraphFactory.TryRecalculateSubGraph("A"));

            // The child must already point at the live parent engine, before it is recalculated.
            Assert.IsFalse(ReferenceEquals(staleParentInstance, a.SubGraph),
                "Recalculation swaps in a new engine instance for A");
            Assert.IsTrue(ReferenceEquals(a.SubGraph, b.SourceFallen8),
                "B must be rebound to A's live engine, not to the discarded one");
            Assert.AreEqual(a.SubGraph.Id, b.SourceFallen8Id, "The dependency identity (the guid) is unchanged");

            Assert.IsTrue(root.SubGraphFactory.TryRecalculateSubGraph("B"));

            // Assert
            Assert.IsTrue(root.SubGraphFactory.TryGetSubGraph(out var a2, "A"));
            Assert.IsTrue(root.SubGraphFactory.TryGetSubGraph(out var b2, "B"));
            Assert.AreEqual(4, a2.SubGraph.VertexCount, "A now holds all four persons");
            Assert.AreEqual(3, b2.SubGraph.VertexCount, "B (from A) now holds Alice, Carol and Dave");
            Assert.IsTrue(ReferenceEquals(a2.SubGraph, b2.SourceFallen8),
                "After its own recalculation B is still bound to A's live engine");
        }

        [TestMethod]
        public void RecalculateParentTwice_ChildStillReadsTheLiveParent()
        {
            var root = CreatePeopleGraph();

            Assert.IsTrue(root.SubGraphFactory.TryCreateSubGraph<BreadthFirstSearchSubgraphAlgorithm>(
                out var a, "A", AllPersons("A")));
            Assert.IsTrue(root.SubGraphFactory.TryCreateSubGraphFromSource<BreadthFirstSearchSubgraphAlgorithm>(
                out var b, "B", PersonsAtLeast30("B"), a.SubGraph));

            AddPerson(root, "Dave", 40);
            Assert.IsTrue(root.SubGraphFactory.TryRecalculateSubGraph("A"));

            AddPerson(root, "Erin", 50);
            Assert.IsTrue(root.SubGraphFactory.TryRecalculateSubGraph("A"));

            Assert.IsTrue(ReferenceEquals(a.SubGraph, b.SourceFallen8),
                "Rebinding must happen on every recalculation, not only the first");

            Assert.IsTrue(root.SubGraphFactory.TryRecalculateSubGraph("B"));
            Assert.AreEqual(4, b.SubGraph.VertexCount, "Alice, Carol, Dave and Erin are all aged >= 30");
        }

        [TestMethod]
        public void RecalculateMiddleOfChain_RebindsOnlyItsOwnChildren()
        {
            // A -> B -> C, plus S sourced from the root: recalculating B may only touch C.
            var root = CreatePeopleGraph();

            Assert.IsTrue(root.SubGraphFactory.TryCreateSubGraph<BreadthFirstSearchSubgraphAlgorithm>(
                out var a, "A", AllPersons("A")));
            Assert.IsTrue(root.SubGraphFactory.TryCreateSubGraphFromSource<BreadthFirstSearchSubgraphAlgorithm>(
                out var b, "B", PersonsAtLeast30("B"), a.SubGraph));
            Assert.IsTrue(root.SubGraphFactory.TryCreateSubGraphFromSource<BreadthFirstSearchSubgraphAlgorithm>(
                out var c, "C", PersonsAtLeast30("C"), b.SubGraph));
            Assert.IsTrue(root.SubGraphFactory.TryCreateSubGraph<BreadthFirstSearchSubgraphAlgorithm>(
                out var s, "S", AllPersons("S")));

            var aInstanceBefore = a.SubGraph;

            AddPerson(root, "Dave", 40);

            Assert.IsTrue(root.SubGraphFactory.TryRecalculateSubGraph("A"));
            Assert.IsTrue(root.SubGraphFactory.TryRecalculateSubGraph("B"));

            Assert.IsTrue(ReferenceEquals(b.SubGraph, c.SourceFallen8), "C follows B's swap");
            Assert.IsTrue(ReferenceEquals(a.SubGraph, b.SourceFallen8), "B stays bound to the live A");
            Assert.IsFalse(ReferenceEquals(aInstanceBefore, b.SourceFallen8), "B must not keep A's discarded engine");
            Assert.IsTrue(ReferenceEquals(root, s.SourceFallen8),
                "A root-sourced sibling is unrelated and must keep the root as its source");
            Assert.IsTrue(ReferenceEquals(root, a.SourceFallen8),
                "The recalculated subgraph keeps its own source: it is never rebound to itself");

            Assert.IsTrue(root.SubGraphFactory.TryRecalculateSubGraph("C"));
            Assert.AreEqual(3, c.SubGraph.VertexCount, "C reflects Dave through the refreshed A and B");
        }

        [TestMethod]
        public void RecalculateChildOnly_LeavesTheParentUntouched()
        {
            var root = CreatePeopleGraph();

            Assert.IsTrue(root.SubGraphFactory.TryCreateSubGraph<BreadthFirstSearchSubgraphAlgorithm>(
                out var a, "A", AllPersons("A")));
            Assert.IsTrue(root.SubGraphFactory.TryCreateSubGraphFromSource<BreadthFirstSearchSubgraphAlgorithm>(
                out var b, "B", PersonsAtLeast30("B"), a.SubGraph));

            var parentInstance = a.SubGraph;
            var parentId = a.SubGraph.Id;

            AddPerson(root, "Dave", 40);

            // Only the child is recalculated: it reads A, which has NOT been refreshed, so Dave
            // is legitimately absent. Recalculation does not cascade in either direction.
            Assert.IsTrue(root.SubGraphFactory.TryRecalculateSubGraph("B"));

            Assert.IsTrue(ReferenceEquals(parentInstance, a.SubGraph), "A is not recalculated by a child's recalculation");
            Assert.AreEqual(parentId, a.SubGraph.Id);
            Assert.AreEqual(3, a.SubGraph.VertexCount, "A still holds its pre-Dave contents");
            Assert.AreEqual(2, b.SubGraph.VertexCount, "B mirrors A, which does not know Dave yet");
        }

        [TestMethod]
        public void RecalculateSubGraph_UnknownName_ReturnsFalseAndChangesNothing()
        {
            var root = CreatePeopleGraph();

            Assert.IsTrue(root.SubGraphFactory.TryCreateSubGraph<BreadthFirstSearchSubgraphAlgorithm>(
                out var a, "A", AllPersons("A")));
            Assert.IsTrue(root.SubGraphFactory.TryCreateSubGraphFromSource<BreadthFirstSearchSubgraphAlgorithm>(
                out var b, "B", PersonsAtLeast30("B"), a.SubGraph));

            var parentInstance = a.SubGraph;
            var childSource = b.SourceFallen8;

            Assert.IsFalse(root.SubGraphFactory.TryRecalculateSubGraph("does-not-exist"),
                "An unknown subgraph name is a clean false");

            Assert.IsTrue(ReferenceEquals(parentInstance, a.SubGraph));
            Assert.IsTrue(ReferenceEquals(childSource, b.SourceFallen8), "No binding may change on a failed lookup");
        }

        [TestMethod]
        public void RecalculateParent_WithoutChildren_Succeeds()
        {
            // Edge case: the rebinding walk must be a no-op when nothing depends on the subgraph.
            var root = CreatePeopleGraph();

            Assert.IsTrue(root.SubGraphFactory.TryCreateSubGraph<BreadthFirstSearchSubgraphAlgorithm>(
                out var a, "A", AllPersons("A")));

            AddPerson(root, "Dave", 40);

            Assert.IsTrue(root.SubGraphFactory.TryRecalculateSubGraph("A"));
            Assert.AreEqual(4, a.SubGraph.VertexCount);
            Assert.IsTrue(ReferenceEquals(root, a.SourceFallen8));
        }
    }
}
