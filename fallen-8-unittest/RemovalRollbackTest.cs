// MIT License
//
// RemovalRollbackTest.cs
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
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// A single-element removal that faults midway must leave the graph exactly as it was, and a
    /// caller that waited for it must be told. Both branches of
    /// <c>Fallen8.TryRemoveGraphElement_private</c> are covered - the vertex branch (its in-edges are
    /// detached first) and the edge branch (the else branch, target-side detach then source-side) -
    /// each driven by a poisoned adjacency bucket so the fault happens for real, plus the
    /// <see cref="NoSQL.GraphDB.App.Controllers.GraphController"/> mapping of that rolled-back
    /// removal to a status code (an error when waited on, an unchanged 202 when not).
    /// </summary>
    [TestClass]
    public class RemovalRollbackTest
    {
        private ILoggerFactory _loggerFactory;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
        }

        /// <summary>
        /// Appends a raw (possibly poisoned/null) in-edge to a vertex through the internal
        /// fault-injection hook, bypassing the read-only public adjacency surface. Reflection is used
        /// because the engine declares no InternalsVisibleTo (same convention as ConcurrentStorageTest).
        /// This replaces the old immutable-API poison injection now that the field is read-only.
        /// </summary>
        private static void InjectRawInEdge(VertexModel vertex, string edgePropertyId, EdgeModel poison)
        {
            typeof(VertexModel)
                .GetMethod("InjectRawInEdgeForTesting", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(vertex, new object[] { edgePropertyId, poison });
        }

        /// <summary>
        /// Appends a raw (possibly null) out-edge to a vertex through the internal fault-injection
        /// hook, bypassing the read-only public adjacency surface. Reflection is used because the
        /// engine declares no InternalsVisibleTo (same convention as ConcurrentStorageTest). This
        /// replaces the old immutable-API poison injection now that the field is read-only.
        /// </summary>
        private static void InjectRawOutEdge(VertexModel vertex, string edgePropertyId, EdgeModel poison)
        {
            typeof(VertexModel)
                .GetMethod("InjectRawOutEdgeForTesting", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(vertex, new object[] { edgePropertyId, poison });
        }

        private static int StatusCodeOf(IActionResult result)
        {
            var statusResult = result as IStatusCodeActionResult;
            Assert.IsNotNull(statusResult,
                "Expected a status-code action result but got " + (result?.GetType().Name ?? "null") + ".");
            Assert.IsTrue(statusResult.StatusCode.HasValue,
                "Expected the action result to carry an explicit status code.");
            return statusResult.StatusCode.Value;
        }

        #region vertex branch of TryRemoveGraphElement_private

        [TestMethod]
        public void RemoveGraphElement_WhenRemovalFaultsMidway_ShouldRollBackVertexAndItsInEdge()
        {
            // Arrange - S --("in")--> V . We then give V a second, poisoned in-edge (null source)
            // under the same edge-property key so that removing V throws while its in-edges are being
            // detached, driving the internal restore/rollback path.
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = TestVertices.Create(fallen8, 2, "test", "idx");
            int sourceId = vertices[0].Id;
            int vId = vertices[1].Id;

            var edgeTx = new CreateEdgeTransaction
            {
                Definition = new EdgeDefinition
                {
                    CreationDate = 1,
                    SourceVertexId = sourceId,
                    TargetVertexId = vId,
                    EdgePropertyId = "in"
                }
            };
            fallen8.EnqueueTransaction(edgeTx).WaitUntilFinished();

            VertexModel v;
            Assert.IsTrue(fallen8.TryGetVertex(out v, vId));
            var realInEdge = v.InEdges["in"][0];
            int inEdgeId = realInEdge.Id;

            Assert.AreEqual(2, fallen8.VertexCount, "Two vertices before the faulting removal.");
            Assert.AreEqual(1, fallen8.EdgeCount, "One edge before the faulting removal.");

            // Poison: an in-edge whose SourceVertex is null. It is appended after the real in-edge so
            // the real one is detached first, then the poison throws. The adjacency is now a read-only
            // public surface, so the poison is injected through the internal fault-injection hook
            // (invoked by reflection, mirroring the former
            // v.InEdges = v.InEdges.SetItem("in", v.InEdges["in"].Add(poison)) injection exactly).
            var poison = new EdgeModel(int.MaxValue, 1, v, null, "poison", "in");
            InjectRawInEdge(v, "in", poison);

            // Act - the removal faults and the transaction manager rolls it back.
            var removeTx = new RemoveGraphElementTransaction { GraphElementId = vId };
            fallen8.EnqueueTransaction(removeTx).WaitUntilFinished();

            // Assert - the removal did not succeed...
            Assert.AreEqual(TransactionState.RolledBack, fallen8.GetTransactionState(removeTx.TransactionId),
                "A faulting removal must be reported as rolled back.");

            // ...and the graph state is restored: the vertex and its in-edge are present again.
            VertexModel restoredVertex;
            Assert.IsTrue(fallen8.TryGetVertex(out restoredVertex, vId),
                "The vertex must be restored (not left flagged as removed) after a rolled-back removal.");

            EdgeModel restoredEdge;
            Assert.IsTrue(fallen8.TryGetEdge(out restoredEdge, inEdgeId),
                "The in-edge must be restored - the in-edge restore branch must read InEdges, not OutEdges.");

            Assert.AreEqual(2, fallen8.VertexCount, "Vertex count must be restored.");
            Assert.AreEqual(1, fallen8.EdgeCount, "Edge count must be restored.");

            // ...and the SOURCE-side adjacency is restored correctly. Removal detached the in-edge from
            // the source vertex via RemoveOutGoingEdge, so the rollback must re-file it through the
            // inverse, AddOutEdge - i.e. back into the source's OUTgoing edges. The buggy restore called
            // AddIncomingEdge, which left OutEdges empty and mis-filed the edge into the source's InEdges.
            // (The poisoned in-edge is ordered last, so the real in-edge is restored before the poison
            // faults the restore loop; the hardened restore still runs the counter recompute.)
            VertexModel restoredSource;
            Assert.IsTrue(fallen8.TryGetVertex(out restoredSource, sourceId),
                "The source vertex must still be present.");

            IReadOnlyList<EdgeModel> sourceOutEdges;
            Assert.IsTrue(restoredSource.TryGetOutEdge(out sourceOutEdges, "in"),
                "The source vertex must still expose its outgoing-edge bucket for property \"in\".");
            Assert.IsTrue(sourceOutEdges.Any(e => e.Id == inEdgeId),
                "The restored in-edge must be back in the SOURCE vertex's OutEdges (restore must call AddOutEdge, not AddIncomingEdge).");

            IReadOnlyList<EdgeModel> sourceInEdges;
            bool sourceHasInBucket = restoredSource.TryGetInEdge(out sourceInEdges, "in");
            Assert.IsFalse(sourceHasInBucket && sourceInEdges.Any(e => e.Id == inEdgeId),
                "The restored in-edge must NOT be mis-filed into the source vertex's InEdges.");
        }

        #endregion

        #region edge branch (else branch of TryRemoveGraphElement_private)

        [TestMethod]
        public void RemoveEdge_WhenDetachFaultsMidway_ShouldRollBackEdgeAndRestoreAdjacency()
        {
            // Arrange - a normal edge S --("knows")--> T.
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = TestVertices.Create(fallen8, 2, "test", "idx");
            int sourceId = vertices[0].Id;
            int targetId = vertices[1].Id;

            var edgeTx = new CreateEdgeTransaction
            {
                Definition = new EdgeDefinition
                {
                    CreationDate = 1,
                    SourceVertexId = sourceId,
                    TargetVertexId = targetId,
                    EdgePropertyId = "knows"
                }
            };
            fallen8.EnqueueTransaction(edgeTx).WaitUntilFinished();

            VertexModel source, target;
            Assert.IsTrue(fallen8.TryGetVertex(out source, sourceId));
            Assert.IsTrue(fallen8.TryGetVertex(out target, targetId));
            int edgeId = source.OutEdges["knows"][0].Id;
            Assert.AreEqual(1, fallen8.EdgeCount, "One edge before the faulting removal.");

            // Poison the SOURCE vertex's out-edge bucket with a null entry. Removing the edge takes
            // the edge (else) branch of TryRemoveGraphElement_private: the target-side detach
            // (RemoveIncomingEdge) succeeds and populates inEdgeRemovals, then the source-side detach
            // (RemoveOutGoingEdge) throws an NRE on the null while iterating - before it mutates
            // OutEdges - driving the internal rollback. The adjacency is now a read-only public
            // surface, so the null is injected through the internal fault-injection hook (invoked by
            // reflection, mirroring the former
            // source.OutEdges = source.OutEdges.SetItem("knows", source.OutEdges["knows"].Add(null))).
            InjectRawOutEdge(source, "knows", null);

            // Act - the removal faults and the transaction manager rolls it back.
            var removeTx = new RemoveGraphElementTransaction { GraphElementId = edgeId };
            fallen8.EnqueueTransaction(removeTx).WaitUntilFinished();

            // Assert - the removal did not succeed...
            Assert.AreEqual(TransactionState.RolledBack, fallen8.GetTransactionState(removeTx.TransactionId),
                "A faulting edge removal must be reported as rolled back.");

            // ...the edge itself is restored (MarkAsNotRemoved) and the counter recomputed...
            EdgeModel restoredEdge;
            Assert.IsTrue(fallen8.TryGetEdge(out restoredEdge, edgeId),
                "The edge must be restored (not left flagged as removed) after a rolled-back removal.");
            Assert.AreEqual(1, fallen8.EdgeCount, "Edge count must be restored.");

            // ...the TARGET vertex's incoming adjacency is restored via the inEdgeRemovals replay...
            IReadOnlyList<EdgeModel> targetInEdges;
            Assert.IsTrue(target.TryGetInEdge(out targetInEdges, "knows"),
                "The target vertex must still expose its incoming-edge bucket for \"knows\".");
            Assert.IsTrue(targetInEdges.Any(e => e != null && e.Id == edgeId),
                "The edge must be back in the target vertex's InEdges (inEdgeRemovals replay).");

            // ...and the SOURCE vertex's outgoing adjacency still contains the edge (the source
            // detach threw before mutating OutEdges, so the edge was never removed there).
            IReadOnlyList<EdgeModel> sourceOutEdges;
            Assert.IsTrue(source.TryGetOutEdge(out sourceOutEdges, "knows"),
                "The source vertex must still expose its outgoing-edge bucket for \"knows\".");
            Assert.IsTrue(sourceOutEdges.Any(e => e != null && e.Id == edgeId),
                "The edge must still be in the source vertex's OutEdges after rollback.");
        }

        #endregion

        #region controllers surface a rolled-back removal

        [TestMethod]
        public async Task TryRemoveGraphElement_WhenWaitingAndTransactionRollsBack_ReturnsError()
        {
            // Arrange
            var fallen8 = new Fallen8(_loggerFactory);
            var controller = new GraphController(_loggerFactory.CreateLogger<GraphController>(), fallen8);

            // A removal of a non-existent (out-of-range) id throws inside the worker and is rolled back.
            // Act
            var result = await controller.TryRemoveGraphElement(int.MaxValue, waitForCompletion: true);

            // Assert
            Assert.AreEqual(StatusCodes.Status500InternalServerError, StatusCodeOf(result),
                "A waited-on mutation that rolled back must be reported as an error, not success.");
        }

        [TestMethod]
        public async Task TryRemoveGraphElement_WhenNotWaiting_StaysAcceptedEvenThoughItWouldRollBack()
        {
            // Arrange
            var fallen8 = new Fallen8(_loggerFactory);
            var controller = new GraphController(_loggerFactory.CreateLogger<GraphController>(), fallen8);

            // Act - fire-and-forget: the same removal that rolls back, but without waiting.
            var result = await controller.TryRemoveGraphElement(int.MaxValue, waitForCompletion: false);

            // Assert
            Assert.AreEqual(StatusCodes.Status202Accepted, StatusCodeOf(result),
                "The fire-and-forget path must be unchanged - the outcome is unknowable when not waiting.");
        }

        #endregion
    }
}
