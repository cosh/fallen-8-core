// MIT License
//
// VertexNeighbourProjectionTest.cs
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
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// The directional projection of <c>VertexModel.GetAllNeighbors</c>. The contract these pin is
    /// stated on the member: every edge is projected onto its far endpoint, out-neighbours first,
    /// one entry per connecting edge.
    /// <para>History (audit defect B10): the member projected BOTH directions through the edge's
    /// target vertex, so an in-edge yielded the vertex itself instead of its predecessor.</para>
    /// </summary>
    [TestClass]
    public class VertexNeighbourProjectionTest
    {
        private const string VertexLabel = "person";
        private const string EdgeLabel = "knows";
        private const string EdgePropertyId = "friend";
        private const string OtherEdgePropertyId = "colleague";

        private ILoggerFactory _loggerFactory;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
        }

        private static VertexModel[] CreateVertices(Fallen8 fallen8, int count)
        {
            var tx = new CreateVerticesTransaction();
            for (var i = 0; i < count; i++)
            {
                tx.AddVertex(1u, VertexLabel, new Dictionary<string, object> { { "seq", i } });
            }
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();

            var created = tx.GetCreatedVertices().ToArray();
            Assert.AreEqual(count, created.Length, "Arrange failed: not every vertex was created.");
            return created;
        }

        /// <summary>
        /// Wires the given (source, edge-property-id, target) triples in ONE transaction and returns the
        /// created edges in definition order.
        /// </summary>
        private static EdgeModel[] CreateEdges(Fallen8 fallen8, params (VertexModel Source, string EdgePropertyId, VertexModel Target)[] definitions)
        {
            var tx = new CreateEdgesTransaction();
            foreach (var definition in definitions)
            {
                tx.AddEdge(definition.Source.Id, definition.EdgePropertyId, definition.Target.Id, 1u, EdgeLabel);
            }
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();

            var created = tx.GetCreatedEdges().ToArray();
            Assert.AreEqual(definitions.Length, created.Length, "Arrange failed: not every edge was created.");
            return created;
        }

        private static void Remove(Fallen8 fallen8, int graphElementId)
        {
            fallen8.EnqueueTransaction(new RemoveGraphElementTransaction { GraphElementId = graphElementId }).WaitUntilFinished();
        }

        /// <summary>
        /// The neighbour list must always hold exactly one entry per connecting edge, so its size is the
        /// sum of the two degrees, and no entry may be null.
        /// </summary>
        private static List<VertexModel> Neighbours(VertexModel vertex)
        {
            var neighbours = vertex.GetAllNeighbors();

            Assert.IsNotNull(neighbours, "GetAllNeighbors must never return null.");
            Assert.AreEqual(
                (int)(vertex.GetOutDegree() + vertex.GetInDegree()),
                neighbours.Count,
                "The neighbour count must be OutDegree + InDegree (one entry per connecting edge).");
            foreach (var neighbour in neighbours)
            {
                Assert.IsNotNull(neighbour, "GetAllNeighbors must never yield a null neighbour.");
            }

            return neighbours;
        }

        private static int[] NeighbourIds(VertexModel vertex)
        {
            return Neighbours(vertex).Select(n => n.Id).ToArray();
        }

        [TestMethod]
        public void GetAllNeighbors_WithOneOutAndOneInEdge_ReturnsTheFarEndpointsAndNeverTheVertexItself()
        {
            // Arrange: b <- a, and c -> a. So a has one successor (b) and one predecessor (c).
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = CreateVertices(fallen8, 3);
            var a = vertices[0];
            var b = vertices[1];
            var c = vertices[2];
            CreateEdges(fallen8, (a, EdgePropertyId, b), (c, EdgePropertyId, a));

            // Act
            var neighbours = Neighbours(a);

            // Assert: exactly {b, c} - the defect returned {b, a}, i.e. a self-reference for the in-edge.
            CollectionAssert.AreEquivalent(
                new[] { b.Id, c.Id },
                neighbours.Select(n => n.Id).ToArray(),
                "A vertex's neighbours are the far endpoints of its edges in both directions.");
            Assert.IsFalse(
                neighbours.Any(n => ReferenceEquals(n, a)),
                "A vertex must never be listed as its own neighbour when it has no self-loop.");

            // The documented order is out-neighbours first, then in-neighbours.
            Assert.AreSame(b, neighbours[0], "The out-neighbour must come first.");
            Assert.AreSame(c, neighbours[1], "The in-neighbour must come after the out-neighbours.");
        }

        [TestMethod]
        public void GetAllNeighbors_ForAVertexWithOnlyIncomingEdges_ReturnsItsPredecessors()
        {
            // Arrange: a -> sink and b -> sink. sink has no outgoing edge at all, so before the fix its
            // neighbour list was two copies of sink itself.
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = CreateVertices(fallen8, 3);
            var a = vertices[0];
            var b = vertices[1];
            var sink = vertices[2];
            CreateEdges(fallen8, (a, EdgePropertyId, sink), (b, EdgePropertyId, sink));

            // Act
            var neighbourIds = NeighbourIds(sink);

            // Assert
            CollectionAssert.AreEquivalent(
                new[] { a.Id, b.Id },
                neighbourIds,
                "A pure sink's neighbours are its predecessors.");
            Assert.IsFalse(neighbourIds.Contains(sink.Id), "The sink must not appear in its own neighbour list.");
        }

        [TestMethod]
        public void GetAllNeighbors_ForAnIsolatedVertex_IsEmptyAndNotNull()
        {
            // Arrange
            var fallen8 = new Fallen8(_loggerFactory);
            var isolated = CreateVertices(fallen8, 1)[0];

            // Act
            var neighbours = Neighbours(isolated);

            // Assert
            Assert.AreEqual(0, neighbours.Count, "A vertex with no edges has no neighbours.");
        }

        [TestMethod]
        public void GetAllNeighbors_ForASelfLoop_ListsTheVertexOncePerDirection()
        {
            // Arrange: loop -> loop. This is the ONE case where a vertex is legitimately its own
            // neighbour, and it must appear twice (once via the out-edge, once via the in-edge).
            var fallen8 = new Fallen8(_loggerFactory);
            var loop = CreateVertices(fallen8, 1)[0];
            CreateEdges(fallen8, (loop, EdgePropertyId, loop));

            // Act
            var neighbours = Neighbours(loop);

            // Assert
            Assert.AreEqual(2, neighbours.Count, "A self-loop contributes to both directions.");
            Assert.IsTrue(neighbours.All(n => ReferenceEquals(n, loop)), "Both entries must be the vertex itself.");
        }

        [TestMethod]
        public void GetAllNeighbors_WithParallelEdges_RepeatsTheNeighbourOncePerEdge()
        {
            // Arrange: two distinct a -> b edges under the same edge-property-id. The result is a list,
            // not a set, so b appears twice from a's side and a twice from b's side.
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = CreateVertices(fallen8, 2);
            var a = vertices[0];
            var b = vertices[1];
            CreateEdges(fallen8, (a, EdgePropertyId, b), (a, EdgePropertyId, b));

            // Act
            var fromA = Neighbours(a);
            var fromB = Neighbours(b);

            // Assert
            Assert.AreEqual(2, fromA.Count, "Two parallel edges list the target twice.");
            Assert.IsTrue(fromA.All(n => ReferenceEquals(n, b)), "Both of a's neighbours are b.");
            Assert.AreEqual(2, fromB.Count, "Two parallel in-edges list the source twice.");
            Assert.IsTrue(fromB.All(n => ReferenceEquals(n, a)), "Both of b's neighbours are a.");
        }

        [TestMethod]
        public void GetAllNeighbors_WithEdgesUnderSeveralEdgePropertyIds_ProjectsEveryGroup()
        {
            // Arrange: a hub whose in-edges live in TWO groups (which promotes the adjacency from the
            // inline single-group shape to the dictionary-backed one) plus one out-edge.
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = CreateVertices(fallen8, 4);
            var hub = vertices[0];
            var friend = vertices[1];
            var colleague = vertices[2];
            var successor = vertices[3];
            CreateEdges(
                fallen8,
                (friend, EdgePropertyId, hub),
                (colleague, OtherEdgePropertyId, hub),
                (hub, EdgePropertyId, successor));

            // Act
            var neighbourIds = NeighbourIds(hub);

            // Assert
            CollectionAssert.AreEquivalent(
                new[] { successor.Id, friend.Id, colleague.Id },
                neighbourIds,
                "Every in-edge group must be projected onto its source, not swallowed or self-referenced.");
            Assert.IsFalse(neighbourIds.Contains(hub.Id), "The hub must not appear in its own neighbour list.");
        }

        [TestMethod]
        public void GetAllNeighbors_AfterTheIncomingEdgeIsRemoved_DropsThePredecessor()
        {
            // Arrange: a -> b and c -> a, then remove the in-edge.
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = CreateVertices(fallen8, 3);
            var a = vertices[0];
            var b = vertices[1];
            var c = vertices[2];
            var edges = CreateEdges(fallen8, (a, EdgePropertyId, b), (c, EdgePropertyId, a));

            // Act
            Remove(fallen8, edges[1].Id);

            // Assert
            CollectionAssert.AreEquivalent(
                new[] { b.Id },
                NeighbourIds(a),
                "Removing the in-edge must drop exactly the predecessor it carried.");
            Assert.AreEqual(0, Neighbours(c).Count, "c lost its only edge, so it has no neighbours left.");
        }

        [TestMethod]
        public void GetAllNeighbors_AfterAPredecessorVertexIsRemoved_DropsIt()
        {
            // Arrange: a -> b and c -> a, then remove the predecessor vertex c (the removal cascade
            // detaches its edge from a's incoming adjacency).
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = CreateVertices(fallen8, 3);
            var a = vertices[0];
            var b = vertices[1];
            var c = vertices[2];
            CreateEdges(fallen8, (a, EdgePropertyId, b), (c, EdgePropertyId, a));

            // Act
            Remove(fallen8, c.Id);

            // Assert
            var neighbourIds = NeighbourIds(a);
            CollectionAssert.AreEquivalent(new[] { b.Id }, neighbourIds, "Only the surviving successor remains.");
            Assert.IsFalse(neighbourIds.Contains(c.Id), "A removed predecessor must not stay a neighbour.");
        }

        [TestMethod]
        public void GetAllNeighbors_OnADirectedRing_YieldsBothTheSuccessorAndThePredecessorOfEveryVertex()
        {
            // Arrange: a directed ring v0 -> v1 -> ... -> v0. Every vertex has exactly one successor and
            // one predecessor, and they are different vertices - so the defect (which returned the
            // vertex itself for the in-edge) is visible at every single vertex.
            var fallen8 = new Fallen8(_loggerFactory);
            const int ringSize = 5;
            var ring = CreateVertices(fallen8, ringSize);
            var definitions = new (VertexModel, string, VertexModel)[ringSize];
            for (var i = 0; i < ringSize; i++)
            {
                definitions[i] = (ring[i], EdgePropertyId, ring[(i + 1) % ringSize]);
            }
            CreateEdges(fallen8, definitions);

            // Act & assert
            for (var i = 0; i < ringSize; i++)
            {
                var successor = ring[(i + 1) % ringSize];
                var predecessor = ring[(i + ringSize - 1) % ringSize];

                var neighbours = Neighbours(ring[i]);

                Assert.AreEqual(2, neighbours.Count, "Every ring vertex has degree 1 in each direction.");
                Assert.AreSame(successor, neighbours[0], "The successor is the out-edge's target.");
                Assert.AreSame(predecessor, neighbours[1], "The predecessor is the in-edge's source.");
            }
        }
    }
}
