// MIT License
//
// CorrectnessFixesTest.cs
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
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Index.Fulltext;
using NoSQL.GraphDB.Core.Index.Range;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Regression tests for the "correctness-fixes" feature (defects B1-B6).
    /// Each test reproduces a specific latent defect surfaced by the repository review.
    /// </summary>
    [TestClass]
    public class CorrectnessFixesTest
    {
        private ILoggerFactory _loggerFactory;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
        }

        private VertexModel[] CreateVertices(Fallen8 fallen8, int count)
        {
            var tx = new CreateVerticesTransaction();
            for (int i = 0; i < count; i++)
            {
                tx.AddVertex(1, "test", new Dictionary<string, object> { { "idx", i } });
            }
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.GetCreatedVertices().ToArray();
        }

        #region B1 - DictionaryIndex discards the ImmutableList return

        [TestMethod]
        public void DictionaryIndex_WhenAddingMultipleValuesUnderOneKey_ShouldReturnAllOfThem()
        {
            // Arrange
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = CreateVertices(fallen8, 3);
            var index = new DictionaryIndex();
            index.Initialize(fallen8, null);

            // Act
            index.AddOrUpdate("key", vertices[0]);
            index.AddOrUpdate("key", vertices[1]);
            index.AddOrUpdate("key", vertices[2]);

            // Assert
            ImmutableList<AGraphElementModel> result;
            bool found = index.TryGetValue(out result, "key");
            Assert.IsTrue(found, "The key should be present in the index.");
            Assert.AreEqual(3, result.Count, "All three values added under one key must be retained.");
            CollectionAssert.AreEquivalent(
                new AGraphElementModel[] { vertices[0], vertices[1], vertices[2] },
                result.ToList(),
                "The bucket must contain exactly the three added elements.");
        }

        [TestMethod]
        public void DictionaryIndex_WhenRemovingOneValueFromAKey_ShouldKeepTheRest()
        {
            // Arrange
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = CreateVertices(fallen8, 3);
            var index = new DictionaryIndex();
            index.Initialize(fallen8, null);
            index.AddOrUpdate("key", vertices[0]);
            index.AddOrUpdate("key", vertices[1]);
            index.AddOrUpdate("key", vertices[2]);

            // Act
            index.RemoveValue(vertices[1]);

            // Assert
            ImmutableList<AGraphElementModel> result;
            bool found = index.TryGetValue(out result, "key");
            Assert.IsTrue(found, "The key should still be present after removing one of its values.");
            Assert.AreEqual(2, result.Count, "Exactly the removed value should be gone.");
            Assert.IsTrue(result.Contains(vertices[0]), "The first value must remain.");
            Assert.IsTrue(result.Contains(vertices[2]), "The third value must remain.");
            Assert.IsFalse(result.Contains(vertices[1]), "The removed value must be gone.");
        }

        #endregion

        #region B2 - RegExIndex discards the ImmutableList return

        [TestMethod]
        public void RegExIndex_WhenAddingMultipleValuesUnderOneKey_ShouldReturnAllOfThem()
        {
            // Arrange
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = CreateVertices(fallen8, 3);
            var index = new RegExIndex();
            index.Initialize(fallen8, null);

            // Act
            index.AddOrUpdate("the quick brown fox", vertices[0]);
            index.AddOrUpdate("the quick brown fox", vertices[1]);
            index.AddOrUpdate("the quick brown fox", vertices[2]);

            // Assert
            ImmutableList<AGraphElementModel> result;
            bool found = index.TryGetValue(out result, "the quick brown fox");
            Assert.IsTrue(found, "The key should be present in the index.");
            Assert.AreEqual(3, result.Count, "All three values added under one key must be retained.");
            CollectionAssert.AreEquivalent(
                new AGraphElementModel[] { vertices[0], vertices[1], vertices[2] },
                result.ToList(),
                "The bucket must contain exactly the three added elements.");
        }

        [TestMethod]
        public void RegExIndex_WhenRemovingOneValueFromAKey_ShouldKeepTheRest()
        {
            // Arrange
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = CreateVertices(fallen8, 3);
            var index = new RegExIndex();
            index.Initialize(fallen8, null);
            index.AddOrUpdate("the quick brown fox", vertices[0]);
            index.AddOrUpdate("the quick brown fox", vertices[1]);
            index.AddOrUpdate("the quick brown fox", vertices[2]);

            // Act
            index.RemoveValue(vertices[1]);

            // Assert
            ImmutableList<AGraphElementModel> result;
            bool found = index.TryGetValue(out result, "the quick brown fox");
            Assert.IsTrue(found, "The key should still be present after removing one of its values.");
            Assert.AreEqual(2, result.Count, "Exactly the removed value should be gone.");
            Assert.IsTrue(result.Contains(vertices[0]), "The first value must remain.");
            Assert.IsTrue(result.Contains(vertices[2]), "The third value must remain.");
            Assert.IsFalse(result.Contains(vertices[1]), "The removed value must be gone.");
        }

        #endregion

        #region B3 - RangeIndex.Between predicate inverted

        [TestMethod]
        public void RangeIndex_Between_WithLowerBelowUpper_ShouldReturnInRangeElements()
        {
            // Arrange
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = CreateVertices(fallen8, 3);
            var index = new RangeIndex();
            index.Initialize(fallen8, null);
            index.AddOrUpdate(10, vertices[0]);
            index.AddOrUpdate(20, vertices[1]);
            index.AddOrUpdate(30, vertices[2]);

            // Act - inclusive range [15, 25] should catch only key 20
            ImmutableList<AGraphElementModel> result;
            bool found = index.Between(out result, 15, 25, true, true);

            // Assert
            Assert.IsTrue(found, "Between should report success.");
            Assert.AreEqual(1, result.Count, "Only the element at key 20 is inside [15, 25].");
            Assert.AreSame(vertices[1], result[0], "The in-range element must be the one at key 20.");
        }

        [TestMethod]
        public void RangeIndex_Between_InclusiveBounds_ShouldReturnAllElementsInRange()
        {
            // Arrange
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = CreateVertices(fallen8, 3);
            var index = new RangeIndex();
            index.Initialize(fallen8, null);
            index.AddOrUpdate(10, vertices[0]);
            index.AddOrUpdate(20, vertices[1]);
            index.AddOrUpdate(30, vertices[2]);

            // Act - inclusive range [10, 30] should catch all three keys
            ImmutableList<AGraphElementModel> result;
            bool found = index.Between(out result, 10, 30, true, true);

            // Assert
            Assert.IsTrue(found);
            Assert.AreEqual(3, result.Count, "All three elements are inside the inclusive range [10, 30].");
            CollectionAssert.AreEquivalent(
                new AGraphElementModel[] { vertices[0], vertices[1], vertices[2] },
                result.ToList());
        }

        [TestMethod]
        public void RangeIndex_Between_ExclusiveBounds_ShouldHonorBoundaryFlags()
        {
            // Arrange
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = CreateVertices(fallen8, 3);
            var index = new RangeIndex();
            index.Initialize(fallen8, null);
            index.AddOrUpdate(10, vertices[0]);
            index.AddOrUpdate(20, vertices[1]);
            index.AddOrUpdate(30, vertices[2]);

            // Act - exclusive range (10, 30) should catch only key 20
            ImmutableList<AGraphElementModel> result;
            bool found = index.Between(out result, 10, 30, false, false);

            // Assert
            Assert.IsTrue(found);
            Assert.AreEqual(1, result.Count, "With both boundaries excluded only key 20 remains.");
            Assert.AreSame(vertices[1], result[0]);
        }

        [TestMethod]
        public void RangeIndexScan_ViaFallen8_ShouldReturnInRangeElements()
        {
            // Arrange - reach the Between predicate through the public Fallen8 surface.
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = CreateVertices(fallen8, 3);

            IIndex index;
            Assert.IsTrue(fallen8.IndexFactory.TryCreateIndex(out index, "ageRange", "RangeIndex"),
                "The range index should be created.");
            index.AddOrUpdate(10, vertices[0]);
            index.AddOrUpdate(20, vertices[1]);
            index.AddOrUpdate(30, vertices[2]);

            // Act
            ImmutableList<AGraphElementModel> result;
            bool found = fallen8.RangeIndexScan(out result, "ageRange", 15, 25, true, true);

            // Assert
            Assert.IsTrue(found, "RangeIndexScan should report success.");
            Assert.AreEqual(1, result.Count, "Only the element at key 20 is inside [15, 25].");
            Assert.AreSame(vertices[1], result[0]);
        }

        #endregion

        #region RangeIndex - discards the ImmutableList return (same defect class as B1/B2)

        [TestMethod]
        public void RangeIndex_WhenAddingMultipleValuesUnderOneKey_RangeQueryShouldReturnAllOfThem()
        {
            // Arrange
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = CreateVertices(fallen8, 3);
            var index = new RangeIndex();
            index.Initialize(fallen8, null);

            // Act - three elements share the single range key 20.
            index.AddOrUpdate(20, vertices[0]);
            index.AddOrUpdate(20, vertices[1]);
            index.AddOrUpdate(20, vertices[2]);

            // Assert - a range that covers key 20 must return ALL three, not just the first.
            ImmutableList<AGraphElementModel> result;
            bool found = index.Between(out result, 10, 30, true, true);
            Assert.IsTrue(found, "The range scan should report success.");
            Assert.AreEqual(3, result.Count, "All three values under the covered range key must be retained.");
            CollectionAssert.AreEquivalent(
                new AGraphElementModel[] { vertices[0], vertices[1], vertices[2] },
                result.ToList(),
                "The range bucket must contain exactly the three added elements.");

            // ...and a direct key lookup must agree.
            ImmutableList<AGraphElementModel> byKey;
            Assert.IsTrue(index.TryGetValue(out byKey, 20), "The key should be present in the index.");
            Assert.AreEqual(3, byKey.Count, "All three values must be retained under the key.");
        }

        [TestMethod]
        public void RangeIndex_WhenRemovingOneValueFromAKey_RangeQueryShouldKeepTheRest()
        {
            // Arrange
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = CreateVertices(fallen8, 3);
            var index = new RangeIndex();
            index.Initialize(fallen8, null);
            index.AddOrUpdate(20, vertices[0]);
            index.AddOrUpdate(20, vertices[1]);
            index.AddOrUpdate(20, vertices[2]);

            // Act
            index.RemoveValue(vertices[1]);

            // Assert - the removed value is gone, the rest remain and are still range-queryable.
            ImmutableList<AGraphElementModel> result;
            bool found = index.Between(out result, 10, 30, true, true);
            Assert.IsTrue(found, "The range scan should still report success.");
            Assert.AreEqual(2, result.Count, "Exactly the removed value should be gone.");
            Assert.IsTrue(result.Contains(vertices[0]), "The first value must remain.");
            Assert.IsTrue(result.Contains(vertices[2]), "The third value must remain.");
            Assert.IsFalse(result.Contains(vertices[1]), "The removed value must be gone.");
        }

        #endregion

        #region B4 - TryRemoveGraphElement_private rollback path

        [TestMethod]
        public void RemoveGraphElement_WhenRemovalFaultsMidway_ShouldRollBackVertexAndItsInEdge()
        {
            // Arrange - S --("in")--> V . We then give V a second, poisoned in-edge (null source)
            // under the same edge-property key so that removing V throws while its in-edges are being
            // detached, driving the internal restore/rollback path.
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = CreateVertices(fallen8, 2);
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
            // the real one is detached first, then the poison throws.
            var poison = new EdgeModel(int.MaxValue, 1, v, null, "poison", "in");
            v.InEdges = v.InEdges.SetItem("in", v.InEdges["in"].Add(poison));

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

            ImmutableList<EdgeModel> sourceOutEdges;
            Assert.IsTrue(restoredSource.TryGetOutEdge(out sourceOutEdges, "in"),
                "The source vertex must still expose its outgoing-edge bucket for property \"in\".");
            Assert.IsTrue(sourceOutEdges.Any(e => e.Id == inEdgeId),
                "The restored in-edge must be back in the SOURCE vertex's OutEdges (restore must call AddOutEdge, not AddIncomingEdge).");

            ImmutableList<EdgeModel> sourceInEdges;
            bool sourceHasInBucket = restoredSource.TryGetInEdge(out sourceInEdges, "in");
            Assert.IsFalse(sourceHasInBucket && sourceInEdges.Any(e => e.Id == inEdgeId),
                "The restored in-edge must NOT be mis-filed into the source vertex's InEdges.");
        }

        #endregion

        #region B5 - GetPropertyCount NRE on a property-less element

        [TestMethod]
        public void GetPropertyCount_OnElementCreatedWithoutProperties_ShouldReturnZero()
        {
            // Arrange
            var fallen8 = new Fallen8(_loggerFactory);
            var tx = new CreateVertexTransaction
            {
                Definition = new VertexDefinition { CreationDate = 1, Properties = null }
            };
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            var vertex = tx.VertexCreated;
            Assert.IsNotNull(vertex, "The vertex should have been created.");

            // Act
            int count = vertex.GetPropertyCount();

            // Assert
            Assert.AreEqual(0, count, "A property-less element must report a property count of zero.");
        }

        #endregion

        #region B6 - Transaction worker must survive a faulting transaction

        [TestMethod]
        public void TransactionWorker_WhenATransactionThrows_ShouldStillProcessSubsequentTransactions()
        {
            // Arrange
            var fallen8 = new Fallen8(_loggerFactory);

            // A RemoveGraphElementTransaction with an out-of-range id makes TryExecute throw
            // (the immutable list indexer throws ArgumentOutOfRangeException before the internal
            // try/catch), which faults the worker task.
            var faultingTx = new RemoveGraphElementTransaction { GraphElementId = int.MaxValue };
            var faultingInfo = fallen8.EnqueueTransaction(faultingTx);
            try
            {
                // On the buggy code this observes the faulted task; on fixed code it returns cleanly.
                faultingInfo.WaitUntilFinished();
            }
            catch (Exception)
            {
                // Swallow the observed fault so it does not fail the test directly - the point of the
                // test is whether the worker survives.
            }

            // Act - enqueue a normal transaction after the faulting one.
            var normalTx = new CreateVertexTransaction
            {
                Definition = new VertexDefinition { CreationDate = 1, Properties = null }
            };
            fallen8.EnqueueTransaction(normalTx);

            // Poll (do not WaitUntilFinished - that would hang forever if the worker died).
            var stopwatch = Stopwatch.StartNew();
            bool finished = false;
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(10))
            {
                if (fallen8.GetTransactionState(normalTx.TransactionId) == TransactionState.Finished)
                {
                    finished = true;
                    break;
                }
                Thread.Sleep(20);
            }

            // Assert
            Assert.IsTrue(finished,
                "The worker thread must survive a faulting transaction and process subsequent ones.");
            Assert.AreEqual(1, fallen8.VertexCount, "The follow-up vertex should have been created.");
        }

        #endregion
    }
}
