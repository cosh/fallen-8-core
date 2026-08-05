// MIT License
//
// AuditDefectSubGraphTest.cs
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
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms;
using NoSQL.GraphDB.Core.Algorithms.SubGraph;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Regression tests for two subgraph defects found by the code audit:
    /// a leading edge pattern dropping its own edge-property filter during level-0 seeding,
    /// and a recalculated subgraph leaving its nested children bound to the discarded engine.
    /// </summary>
    [TestClass]
    public class AuditDefectSubGraphTest
    {
        #region graph fixtures

        /// <summary>
        /// Alice -knows-> Bob -knows-> Charlie, Alice -works_at-> TechCorp, Charlie -works_at-> TechCorp.
        /// The edge LABEL is deliberately different from the edge PROPERTY ID on every edge, so a
        /// test that filters on the property id cannot pass by accidentally matching the label.
        /// </summary>
        private static Fallen8 CreateRelationshipGraph()
        {
            var fallen8 = new Fallen8(TestLoggerFactory.Create());
            var creationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds());

            var verticesTx = new CreateVerticesTransaction();
            verticesTx.AddVertex(creationDate, "person", new Dictionary<string, object>() { { "name", "Alice" } });
            verticesTx.AddVertex(creationDate, "person", new Dictionary<string, object>() { { "name", "Bob" } });
            verticesTx.AddVertex(creationDate, "person", new Dictionary<string, object>() { { "name", "Charlie" } });
            verticesTx.AddVertex(creationDate, "company", new Dictionary<string, object>() { { "name", "TechCorp" } });
            fallen8.EnqueueTransaction(verticesTx).WaitUntilFinished();

            var vertices = verticesTx.GetCreatedVertices();

            var edgesTx = new CreateEdgesTransaction();
            edgesTx.AddEdge(vertices[0].Id, "knows", vertices[1].Id, creationDate, "edge-alice-bob");
            edgesTx.AddEdge(vertices[1].Id, "knows", vertices[2].Id, creationDate, "edge-bob-charlie");
            edgesTx.AddEdge(vertices[0].Id, "works_at", vertices[3].Id, creationDate, "edge-alice-techcorp");
            edgesTx.AddEdge(vertices[2].Id, "works_at", vertices[3].Id, creationDate, "edge-charlie-techcorp");
            fallen8.EnqueueTransaction(edgesTx).WaitUntilFinished();

            return fallen8;
        }

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

        private static void AddPerson(Fallen8 fallen8, string name, int age)
        {
            var creationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds());
            var tx = new CreateVerticesTransaction();
            tx.AddVertex(creationDate, "person", new Dictionary<string, object>() { { "name", name }, { "age", age } });
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();
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

        /// <summary>
        /// A pattern that leads with a single edge hop and closes on a vertex, which is the shape
        /// that reaches level-0 seeding with an edge pattern.
        /// </summary>
        private static SubGraphDefinition LeadingEdge(string name, Direction direction, Delegates.EdgePropertyFilter edgeProperty)
        {
            return new SubGraphDefinition
            {
                Name = name,
                Pattern = new List<APattern>
                {
                    new EdgePattern { PatternName = "e", Direction = direction, EdgeProperty = edgeProperty },
                    new VertexPattern { PatternName = "v" }
                }
            };
        }

        private static BreadthFirstSearchSubgraphAlgorithm AlgorithmOn(Fallen8 fallen8)
        {
            var algorithm = new BreadthFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null);
            return algorithm;
        }

        #endregion

        #region B25: a leading edge pattern must honor its own edge-property filter

        [TestMethod]
        public void LeadingEdgePattern_OutgoingWithEdgePropertyFilter_KeepsOnlyMatchingEdges()
        {
            // Arrange
            var fallen8 = CreateRelationshipGraph();
            var algorithm = AlgorithmOn(fallen8);

            // Act
            var created = algorithm.TryCreateSubgraph(out SubGraphResult result,
                LeadingEdge("knows-only", Direction.OutgoingEdge, p => p == "knows"));

            // Assert
            Assert.IsTrue(created, "A leading edge pattern followed by a vertex is a valid pattern");
            Assert.IsNotNull(result?.SubGraph, "SubGraph should not be null");
            Assert.AreEqual(2, result.SubGraph.EdgeCount, "Only the two 'knows' edges may seed the subgraph");
            Assert.IsTrue(result.SubGraph.GetAllEdges().All(e => e.EdgePropertyId == "knows"),
                "Every surviving edge must have the filtered edge property id");
            Assert.AreEqual(3, result.SubGraph.VertexCount, "Alice, Bob and Charlie: TechCorp has no 'knows' edge");
            Assert.IsFalse(result.SubGraph.GetAllVertices().Any(v => v.Label == "company"),
                "The company vertex is only reachable via 'works_at' and must be gone");
        }

        [TestMethod]
        public void LeadingEdgePattern_IncomingWithEdgePropertyFilter_KeepsOnlyMatchingEdges()
        {
            var fallen8 = CreateRelationshipGraph();
            var algorithm = AlgorithmOn(fallen8);

            var created = algorithm.TryCreateSubgraph(out SubGraphResult result,
                LeadingEdge("knows-incoming", Direction.IncomingEdge, p => p == "knows"));

            Assert.IsTrue(created);
            Assert.AreEqual(2, result.SubGraph.EdgeCount, "Direction does not widen the edge-property filter");
            Assert.IsTrue(result.SubGraph.GetAllEdges().All(e => e.EdgePropertyId == "knows"));
            Assert.AreEqual(3, result.SubGraph.VertexCount);
        }

        [TestMethod]
        public void LeadingEdgePattern_UndirectedWithEdgePropertyFilter_KeepsOnlyMatchingEdges()
        {
            var fallen8 = CreateRelationshipGraph();
            var algorithm = AlgorithmOn(fallen8);

            var created = algorithm.TryCreateSubgraph(out SubGraphResult result,
                LeadingEdge("knows-undirected", Direction.UndirectedEdge, p => p == "knows"));

            Assert.IsTrue(created);
            Assert.AreEqual(2, result.SubGraph.EdgeCount,
                "An undirected seed yields both orientations of the same edge, not extra edges");
            Assert.IsTrue(result.SubGraph.GetAllEdges().All(e => e.EdgePropertyId == "knows"));
            Assert.AreEqual(3, result.SubGraph.VertexCount);
        }

        [TestMethod]
        public void LeadingEdgePattern_EdgePropertyFilterOnOtherType_SelectsThatTypeOnly()
        {
            var fallen8 = CreateRelationshipGraph();
            var algorithm = AlgorithmOn(fallen8);

            var created = algorithm.TryCreateSubgraph(out SubGraphResult result,
                LeadingEdge("works-at-only", Direction.OutgoingEdge, p => p == "works_at"));

            Assert.IsTrue(created);
            Assert.AreEqual(2, result.SubGraph.EdgeCount, "Only the two 'works_at' edges");
            Assert.IsTrue(result.SubGraph.GetAllEdges().All(e => e.EdgePropertyId == "works_at"));
            Assert.AreEqual(3, result.SubGraph.VertexCount, "Alice, Charlie and TechCorp: Bob has no 'works_at' edge");
        }

        [TestMethod]
        public void LeadingEdgePattern_EdgePropertyFilterMatchesNothing_YieldsEmptySubgraph()
        {
            var fallen8 = CreateRelationshipGraph();
            var algorithm = AlgorithmOn(fallen8);

            var created = algorithm.TryCreateSubgraph(out SubGraphResult result,
                LeadingEdge("no-such-edge-type", Direction.OutgoingEdge, p => p == "does_not_exist"));

            Assert.IsTrue(created, "A valid pattern that matches nothing is an empty subgraph, not a failure");
            Assert.AreEqual(0, result.SubGraph.EdgeCount, "No edge may seed a path");
            Assert.AreEqual(0, result.SubGraph.VertexCount, "Without a seeding edge no vertex is part of a valid path");
        }

        [TestMethod]
        public void LeadingEdgePattern_EdgePropertyFilterSeesEdgePropertyIdNotLabel()
        {
            var fallen8 = CreateRelationshipGraph();
            var algorithm = AlgorithmOn(fallen8);
            var observed = new HashSet<string>();

            var created = algorithm.TryCreateSubgraph(out SubGraphResult result,
                LeadingEdge("observe", Direction.OutgoingEdge, p =>
                {
                    observed.Add(p);
                    return true;
                }));

            Assert.IsTrue(created);
            CollectionAssert.AreEquivalent(new List<string> { "knows", "works_at" }, observed.ToList(),
                "The filter must be handed the edge property id, never the edge label");
            Assert.AreEqual(4, result.SubGraph.EdgeCount, "An always-true filter keeps every edge");
        }

        [TestMethod]
        public void LeadingEdgePattern_WithoutEdgePropertyFilter_SeedsFromEveryEdge()
        {
            var fallen8 = CreateRelationshipGraph();
            var algorithm = AlgorithmOn(fallen8);

            var created = algorithm.TryCreateSubgraph(out SubGraphResult result,
                LeadingEdge("all-edges", Direction.OutgoingEdge, null));

            Assert.IsTrue(created);
            Assert.AreEqual(4, result.SubGraph.EdgeCount, "A null edge-property filter must not filter anything");
            Assert.AreEqual(4, result.SubGraph.VertexCount);
        }

        [TestMethod]
        public void LeadingEdgePattern_EdgeFilterAndEdgePropertyFilterCombine()
        {
            var fallen8 = CreateRelationshipGraph();
            var algorithm = AlgorithmOn(fallen8);

            var definition = new SubGraphDefinition
            {
                Name = "knows-and-labelled",
                Pattern = new List<APattern>
                {
                    new EdgePattern
                    {
                        PatternName = "e",
                        Direction = Direction.OutgoingEdge,
                        EdgeProperty = p => p == "knows",
                        Edge = e => e.Label == "edge-alice-bob"
                    },
                    new VertexPattern { PatternName = "v" }
                }
            };

            var created = algorithm.TryCreateSubgraph(out SubGraphResult result, definition);

            Assert.IsTrue(created);
            Assert.AreEqual(1, result.SubGraph.EdgeCount, "Both edge filters apply, they are not alternatives");
            Assert.AreEqual("edge-alice-bob", result.SubGraph.GetAllEdges().Single().Label);
            Assert.AreEqual(2, result.SubGraph.VertexCount, "Only Alice and Bob remain");
        }

        [TestMethod]
        public void VertexLeadingPattern_WithEdgePropertyFilter_IsUnchanged()
        {
            // Control: the deeper levels always honored the filter. This pins that the seeding fix
            // did not change the vertex-leading shape.
            var fallen8 = CreateRelationshipGraph();
            var algorithm = AlgorithmOn(fallen8);

            var definition = new SubGraphDefinition
            {
                Name = "vertex-leading",
                Pattern = new List<APattern>
                {
                    new VertexPattern { PatternName = "p1", Vertex = v => v.Label == "person" },
                    new EdgePattern { PatternName = "e", Direction = Direction.OutgoingEdge, EdgeProperty = p => p == "knows" },
                    new VertexPattern { PatternName = "p2", Vertex = v => v.Label == "person" }
                }
            };

            var created = algorithm.TryCreateSubgraph(out SubGraphResult result, definition);

            Assert.IsTrue(created);
            Assert.AreEqual(2, result.SubGraph.EdgeCount);
            Assert.IsTrue(result.SubGraph.GetAllEdges().All(e => e.EdgePropertyId == "knows"));
        }

        #endregion

        #region B26: recalculating a parent must rebind its nested children

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

        #endregion
    }
}
