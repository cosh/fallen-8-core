// MIT License
//
// TransactionAtomicityTest.cs
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
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Persistency;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Pins the transaction-atomicity contract (features/transaction-atomicity/): a transaction
    /// whose terminal state is RolledBack has ZERO observable effect. Covers the batch create
    /// (id-space corruption), batch property (partial update), and batch remove (partial removal)
    /// violations, the restored id == index invariant after every failure, and - with the WAL
    /// enabled - that a rolled-back batch is never logged, so crash recovery reproduces exactly
    /// the state clients observed.
    /// </summary>
    [TestClass]
    public class TransactionAtomicityTest
    {
        private ILoggerFactory _loggerFactory;
        private string _tempDir;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
            _tempDir = Path.Combine(Path.GetTempPath(), "f8_txatomic_" + Guid.NewGuid().ToString("N"));
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

        #region helpers

        private string WalPath => Path.Combine(_tempDir, "atomicity.f8s.wal");

        private static VertexModel[] CreateVertices(Fallen8 fallen8, int count, string label = "node")
        {
            var tx = new CreateVerticesTransaction();
            for (var i = 0; i < count; i++)
            {
                tx.AddVertex(1u, label, new Dictionary<string, object> { { "idx", i } });
            }
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.GetCreatedVertices().ToArray();
        }

        private static EdgeModel CreateEdge(Fallen8 fallen8, int sourceId, int targetId, string edgePropertyId = "knows")
        {
            var tx = new CreateEdgesTransaction();
            tx.AddEdge(sourceId, edgePropertyId, targetId, 1u);
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.GetCreatedEdges().Single();
        }

        /// <summary>
        /// Asserts the single writer is still alive by driving one more transaction to a committed
        /// terminal state.
        /// </summary>
        private static void AssertWorkerSurvives(Fallen8 fallen8)
        {
            var probe = new CreateVertexTransaction
            {
                Definition = new VertexDefinition { CreationDate = 1u, Label = "survivor" }
            };
            var info = fallen8.EnqueueTransaction(probe);
            info.WaitUntilFinished();
            Assert.AreEqual(TransactionState.Finished, info.TransactionState,
                "The worker must keep processing transactions after a rolled-back batch.");
        }

        /// <summary>
        /// Creates one more vertex and asserts the id == index master-store invariant still holds:
        /// the new vertex's Id resolves to exactly that vertex. This is the direct probe for the
        /// "_currentId drifted past Count" corruption a partially-failed batch create used to leave.
        /// </summary>
        private static VertexModel AssertIdSpaceIntact(Fallen8 fallen8)
        {
            var tx = new CreateVertexTransaction
            {
                Definition = new VertexDefinition { CreationDate = 1u, Label = "probe" }
            };
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            var created = tx.VertexCreated;
            Assert.IsNotNull(created, "The probe vertex must be created.");

            Assert.IsTrue(fallen8.TryGetVertex(out var fetched, created.Id),
                "TryGetVertex(newId) must succeed - the created vertex's Id must be within the published Count (id == index).");
            Assert.AreSame(created, fetched,
                "TryGetVertex(newId) must return exactly the created vertex - not a shifted neighbour (id == index).");
            return created;
        }

        /// <summary>
        /// Poisons a vertex's raw out-edge bucket with a null entry via the internal
        /// fault-injection hook (reflection, same pattern as CorrectnessFixesFollowupsTest), so a
        /// subsequent removal of an edge in that bucket faults deterministically inside
        /// RemoveOutGoingEdge (RemoveById dereferences the null).
        /// </summary>
        private static void InjectRawOutEdge(VertexModel vertex, string edgePropertyId, EdgeModel poison)
        {
            typeof(VertexModel)
                .GetMethod("InjectRawOutEdgeForTesting", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(vertex, new object[] { edgePropertyId, poison });
        }

        #endregion

        #region batch create - id-space corruption (spec section 1, create)

        [TestMethod]
        public void CreateVerticesBatch_WithNullDefinitionMidBatch_RollsBackCleanAndLeavesIdSpaceIntact()
        {
            // Arrange - two committed baseline vertices (ids 0 and 1).
            var fallen8 = new Fallen8(_loggerFactory);
            var baseline = CreateVertices(fallen8, 2);

            // Act - a batch whose middle definition is null (a JSON array element can be null).
            var failing = new CreateVerticesTransaction();
            failing.AddVertex(1u, "valid-before");
            failing.Vertices.Add(null);
            failing.AddVertex(1u, "valid-after");
            var info = fallen8.EnqueueTransaction(failing);
            info.WaitUntilFinished();

            // Assert - reported honestly as a clean, client-attributable rollback...
            Assert.AreEqual(TransactionState.RolledBack, info.TransactionState);
            Assert.AreEqual(TransactionFailureReason.InvalidInput, info.FailureReason,
                "A structurally invalid batch (null definition) must be classified as InvalidInput.");
            Assert.IsNull(info.Error, "A pre-validated invalid batch is a clean rollback, not a thrown fault.");

            // ...with zero observable effect...
            Assert.AreEqual(2, fallen8.VertexCount, "No vertex of the failed batch may be counted.");
            Assert.AreEqual(2, fallen8.GetAllVertices().Count, "No vertex of the failed batch may be visible.");
            Assert.AreEqual(0, failing.GetCreatedVertices().Count, "A rolled-back batch reports no created vertices.");

            // ...the id space is NOT corrupted (this used to fail: _currentId had drifted past Count,
            // so the next created vertex's Id no longer matched its slot)...
            var probe = AssertIdSpaceIntact(fallen8);
            Assert.AreEqual(2, probe.Id, "The next id after two committed vertices must be 2 - the failed batch must not consume ids.");

            // ...the baseline is untouched, and the worker survives.
            foreach (var v in baseline)
            {
                Assert.IsTrue(fallen8.TryGetVertex(out var fetched, v.Id));
                Assert.AreSame(v, fetched, "Pre-existing ids must still resolve to the same elements.");
            }
            AssertWorkerSurvives(fallen8);
        }

        [TestMethod]
        public void CreateEdgesBatch_WithNullDefinitionMidBatch_RollsBackCleanAndLeavesIdSpaceIntact()
        {
            // Arrange - two vertices to connect.
            var fallen8 = new Fallen8(_loggerFactory);
            var v = CreateVertices(fallen8, 2);

            // Act - a batch whose second definition is null.
            var failing = new CreateEdgesTransaction();
            failing.AddEdge(v[0].Id, "knows", v[1].Id, 1u);
            failing.Edges.Add(null);
            var info = fallen8.EnqueueTransaction(failing);
            info.WaitUntilFinished();

            // Assert - clean InvalidInput (this used to surface as an escaped NRE / InternalError)...
            Assert.AreEqual(TransactionState.RolledBack, info.TransactionState);
            Assert.AreEqual(TransactionFailureReason.InvalidInput, info.FailureReason,
                "A structurally invalid edge batch (null definition) must be classified as InvalidInput.");
            Assert.IsNull(info.Error, "A pre-validated invalid batch is a clean rollback, not a thrown fault.");

            // ...zero observable effect: no edge, no adjacency, no consumed ids.
            Assert.AreEqual(0, fallen8.EdgeCount);
            Assert.AreEqual(0, fallen8.GetAllEdges().Count);
            Assert.IsFalse(v[0].TryGetOutEdge(out _, "knows"), "No adjacency may be wired for a rolled-back edge batch.");
            Assert.IsFalse(v[1].TryGetInEdge(out _, "knows"), "No adjacency may be wired for a rolled-back edge batch.");
            Assert.AreEqual(0, failing.GetCreatedEdges().Count);

            var edge = CreateEdge(fallen8, v[0].Id, v[1].Id);
            Assert.AreEqual(2, edge.Id, "The next id after two vertices must be 2 - the failed batch must not consume ids.");
            Assert.IsTrue(fallen8.TryGetEdge(out var fetchedEdge, edge.Id));
            Assert.AreSame(edge, fetchedEdge, "TryGetEdge(newId) must return exactly the created edge (id == index).");

            AssertWorkerSurvives(fallen8);
        }

        #endregion

        #region batch properties - partial update (spec section 1, remove/property)

        [TestMethod]
        public void AddPropertiesBatch_WithConflictingUpdate_AppliesNothing()
        {
            // Arrange - a vertex whose "b" already differs from the batch's value.
            var fallen8 = new Fallen8(_loggerFactory);
            var v = CreateVertices(fallen8, 1)[0];
            fallen8.EnqueueTransaction(new AddPropertyTransaction
            {
                Definition = new PropertyAddDefinition { GraphElementId = v.Id, PropertyId = "b", Property = 2 }
            }).WaitUntilFinished();

            // Act - [a = 1 (new key), b = 99 (conflicts with the existing b = 2)].
            var failing = new AddPropertiesTransaction();
            failing.AddProperty(v.Id, "a", 1);
            failing.AddProperty(v.Id, "b", 99);
            var info = fallen8.EnqueueTransaction(failing);
            info.WaitUntilFinished();

            // Assert - a routine conflicting update is a clean Conflict (it used to escape as an
            // ArgumentException AFTER applying the earlier sets)...
            Assert.AreEqual(TransactionState.RolledBack, info.TransactionState);
            Assert.AreEqual(TransactionFailureReason.Conflict, info.FailureReason,
                "A conflicting property update must be classified as Conflict.");
            Assert.IsNull(info.Error, "A pre-validated conflict is a clean rollback, not a thrown fault.");

            // ...and NOTHING of the batch was applied.
            Assert.IsFalse(v.TryGetProperty<object>(out _, "a"),
                "The earlier set of a rolled-back property batch must NOT stay applied.");
            Assert.IsTrue(v.TryGetProperty(out int b, "b"));
            Assert.AreEqual(2, b, "The conflicting key must keep its pre-batch value.");

            AssertWorkerSurvives(fallen8);
        }

        [TestMethod]
        public void AddPropertiesBatch_WithBatchInternalConflict_AppliesNothing()
        {
            // Arrange.
            var fallen8 = new Fallen8(_loggerFactory);
            var v = CreateVertices(fallen8, 1)[0];

            // Act - the batch conflicts with ITSELF: two different values for the same new key.
            var failing = new AddPropertiesTransaction();
            failing.AddProperty(v.Id, "c", 1);
            failing.AddProperty(v.Id, "c", 2);
            var info = fallen8.EnqueueTransaction(failing);
            info.WaitUntilFinished();

            // Assert.
            Assert.AreEqual(TransactionState.RolledBack, info.TransactionState);
            Assert.AreEqual(TransactionFailureReason.Conflict, info.FailureReason,
                "A batch-internal conflicting duplicate must be classified as Conflict.");
            Assert.IsNull(info.Error);
            Assert.IsFalse(v.TryGetProperty<object>(out _, "c"),
                "Neither value of a self-conflicting batch may be applied.");

            AssertWorkerSurvives(fallen8);
        }

        [TestMethod]
        public void AddPropertiesBatch_WithEqualValueDuplicate_Commits()
        {
            // Guards the conflict pre-check against being over-strict: setting the same key to an
            // EQUAL value twice is the existing no-op-update semantics and must still commit.
            var fallen8 = new Fallen8(_loggerFactory);
            var v = CreateVertices(fallen8, 1)[0];

            var tx = new AddPropertiesTransaction();
            tx.AddProperty(v.Id, "d", 5);
            tx.AddProperty(v.Id, "d", 5);
            var info = fallen8.EnqueueTransaction(tx);
            info.WaitUntilFinished();

            Assert.AreEqual(TransactionState.Finished, info.TransactionState,
                "An equal-value duplicate is the documented no-op update and must commit.");
            Assert.IsTrue(v.TryGetProperty(out int d, "d"));
            Assert.AreEqual(5, d);
        }

        [TestMethod]
        public void AddPropertiesBatch_WithNullDefinitionMidBatch_AppliesNothing()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var v = CreateVertices(fallen8, 1)[0];

            var failing = new AddPropertiesTransaction();
            failing.AddProperty(v.Id, "e", 1);
            failing.Properties.Add(null);
            var info = fallen8.EnqueueTransaction(failing);
            info.WaitUntilFinished();

            Assert.AreEqual(TransactionState.RolledBack, info.TransactionState);
            Assert.AreEqual(TransactionFailureReason.InvalidInput, info.FailureReason,
                "A structurally invalid property batch (null definition) must be classified as InvalidInput.");
            Assert.IsNull(info.Error);
            Assert.IsFalse(v.TryGetProperty<object>(out _, "e"),
                "The earlier set of a rolled-back property batch must NOT stay applied.");

            AssertWorkerSurvives(fallen8);
        }

        #endregion

        #region batch remove - partial removal (spec section 1, remove/property)

        [TestMethod]
        public void RemoveGraphElementsBatch_WithOutOfRangeIdMidBatch_RemovesNothing()
        {
            // Arrange.
            var fallen8 = new Fallen8(_loggerFactory);
            var v = CreateVertices(fallen8, 2);

            // Act - the first id is valid, the second is out of range.
            var failing = new RemoveGraphElementsTransaction
            {
                GraphElementIds = new List<int> { v[0].Id, int.MaxValue }
            };
            var info = fallen8.EnqueueTransaction(failing);
            info.WaitUntilFinished();

            // Assert - the historical out-of-range contract is preserved (an escaped
            // ArgumentOutOfRangeException classified as InternalError; the B6 boundary from
            // transaction-failure-reasons is deliberately NOT changed here)...
            Assert.AreEqual(TransactionState.RolledBack, info.TransactionState);
            Assert.AreEqual(TransactionFailureReason.InternalError, info.FailureReason);
            Assert.IsInstanceOfType(info.Error, typeof(ArgumentOutOfRangeException),
                "The out-of-range throw contract (B6) must be preserved.");

            // ...but the batch is now ATOMIC: the valid earlier id must NOT stay removed.
            Assert.IsTrue(fallen8.TryGetVertex(out var stillAlive, v[0].Id),
                "An earlier valid removal of a rolled-back batch must NOT stay committed.");
            Assert.AreSame(v[0], stillAlive);
            Assert.AreEqual(2, fallen8.VertexCount, "The vertex count must be unchanged by the rolled-back batch.");

            AssertWorkerSurvives(fallen8);
        }

        [TestMethod]
        public void RemoveGraphElementsBatch_WhenLaterRemovalFaults_RestoresEarlierRemovals()
        {
            // Arrange - two disjoint edges; the SECOND one's source out-bucket is poisoned so its
            // removal faults mid-detach (after the first edge was already removed).
            var fallen8 = new Fallen8(_loggerFactory);
            var v = CreateVertices(fallen8, 4);
            var edgeA = CreateEdge(fallen8, v[0].Id, v[1].Id);
            var edgeB = CreateEdge(fallen8, v[2].Id, v[3].Id);
            InjectRawOutEdge(v[2], "knows", null);

            // Act.
            var failing = new RemoveGraphElementsTransaction
            {
                GraphElementIds = new List<int> { edgeA.Id, edgeB.Id }
            };
            var info = fallen8.EnqueueTransaction(failing);
            info.WaitUntilFinished();

            // Assert - a genuine fault (the poison) is reported as such...
            Assert.AreEqual(TransactionState.RolledBack, info.TransactionState);
            Assert.IsNotNull(info.Error, "The poisoned detach is a genuine fault.");

            // ...and the EARLIER, already-applied removal is undone by the batch rollback.
            Assert.IsTrue(fallen8.TryGetEdge(out var restoredA, edgeA.Id),
                "The earlier removal of a rolled-back batch must be restored.");
            Assert.AreSame(edgeA, restoredA);
            Assert.IsTrue(fallen8.TryGetEdge(out _, edgeB.Id), "The faulting removal restored itself (existing contract).");
            Assert.AreEqual(2, fallen8.EdgeCount, "The edge count must be restored exactly.");

            // The restored edge is fully re-wired on both endpoints.
            Assert.IsTrue(v[0].TryGetOutEdge(out var outEdges, "knows"));
            Assert.IsTrue(outEdges.Any(e => e != null && e.Id == edgeA.Id),
                "The restored edge must be back in its source vertex's OutEdges.");
            Assert.IsTrue(v[1].TryGetInEdge(out var inEdges, "knows"));
            Assert.IsTrue(inEdges.Any(e => e != null && e.Id == edgeA.Id),
                "The restored edge must be back in its target vertex's InEdges.");

            AssertWorkerSurvives(fallen8);
        }

        [TestMethod]
        public void RemoveGraphElementsBatch_VertexWithSelfLoopAndEdge_RolledBack_RestoresCascadedAdjacency()
        {
            // Arrange - the subtlest undo: an already-applied VERTEX removal (which cascades into
            // its edges, including a self-loop) must be fully reversed when a later id in the batch
            // faults. Layout: V --self--> V, V --knows--> W, plus a poisoned edge X --knows--> Y.
            var fallen8 = new Fallen8(_loggerFactory);
            var v = CreateVertices(fallen8, 4);
            VertexModel vertexV = v[0], vertexW = v[1], vertexX = v[2], vertexY = v[3];
            var selfLoop = CreateEdge(fallen8, vertexV.Id, vertexV.Id, "self");
            var edgeVw = CreateEdge(fallen8, vertexV.Id, vertexW.Id);
            var edgeXy = CreateEdge(fallen8, vertexX.Id, vertexY.Id);
            InjectRawOutEdge(vertexX, "knows", null);

            // Act - remove V (applies, cascading self-loop + edgeVw), then the poisoned edge (faults).
            var failing = new RemoveGraphElementsTransaction
            {
                GraphElementIds = new List<int> { vertexV.Id, edgeXy.Id }
            };
            var info = fallen8.EnqueueTransaction(failing);
            info.WaitUntilFinished();

            // Assert - rolled back with the fault reported...
            Assert.AreEqual(TransactionState.RolledBack, info.TransactionState);
            Assert.IsNotNull(info.Error);

            // ...V and its cascaded edges are alive again...
            Assert.IsTrue(fallen8.TryGetVertex(out _, vertexV.Id), "The removed vertex must be restored.");
            Assert.IsTrue(fallen8.TryGetEdge(out _, selfLoop.Id), "The cascaded self-loop must be restored.");
            Assert.IsTrue(fallen8.TryGetEdge(out _, edgeVw.Id), "The cascaded edge must be restored.");
            Assert.AreEqual(4, fallen8.VertexCount);
            Assert.AreEqual(3, fallen8.EdgeCount);

            // ...with the adjacency exactly as before: the self-loop once in V's out AND once in
            // V's in (no duplicates), and edgeVw back in W's InEdges.
            Assert.IsTrue(vertexV.TryGetOutEdge(out var vOutSelf, "self"));
            Assert.AreEqual(1, vOutSelf.Count(e => e != null && e.Id == selfLoop.Id),
                "The self-loop must appear exactly once in V's OutEdges (no duplicate from the restore).");
            Assert.IsTrue(vertexV.TryGetInEdge(out var vInSelf, "self"));
            Assert.AreEqual(1, vInSelf.Count(e => e != null && e.Id == selfLoop.Id),
                "The self-loop must appear exactly once in V's InEdges (no duplicate from the restore).");
            Assert.IsTrue(vertexV.TryGetOutEdge(out var vOutKnows, "knows"));
            Assert.AreEqual(1, vOutKnows.Count(e => e != null && e.Id == edgeVw.Id));
            Assert.IsTrue(vertexW.TryGetInEdge(out var wIn, "knows"));
            Assert.AreEqual(1, wIn.Count(e => e != null && e.Id == edgeVw.Id),
                "The cascaded edge must be re-attached to its target's InEdges exactly once.");

            AssertWorkerSurvives(fallen8);
        }

        [TestMethod]
        public void RemoveGraphElementsBatch_WithNullIdList_RollsBackCleanWithInvalidInput()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            CreateVertices(fallen8, 1);

            var failing = new RemoveGraphElementsTransaction { GraphElementIds = null };
            var info = fallen8.EnqueueTransaction(failing);
            info.WaitUntilFinished();

            Assert.AreEqual(TransactionState.RolledBack, info.TransactionState);
            Assert.AreEqual(TransactionFailureReason.InvalidInput, info.FailureReason,
                "A null id list is structurally invalid input, not an internal fault.");
            Assert.IsNull(info.Error);
            Assert.AreEqual(1, fallen8.VertexCount);

            AssertWorkerSurvives(fallen8);
        }

        [TestMethod]
        public void RemoveGraphElementsBatch_HappyPath_StillCommits()
        {
            // Guards the pre-validation against regressing the normal path: a valid batch (including
            // an in-range but already-removed id, which stays a documented no-op) commits.
            var fallen8 = new Fallen8(_loggerFactory);
            var v = CreateVertices(fallen8, 3);
            fallen8.EnqueueTransaction(new RemoveGraphElementTransaction { GraphElementId = v[1].Id }).WaitUntilFinished();

            var tx = new RemoveGraphElementsTransaction
            {
                GraphElementIds = new List<int> { v[0].Id, v[1].Id, v[2].Id }
            };
            var info = fallen8.EnqueueTransaction(tx);
            info.WaitUntilFinished();

            Assert.AreEqual(TransactionState.Finished, info.TransactionState,
                "A valid remove batch (with an already-removed no-op id) must commit.");
            Assert.AreEqual(0, fallen8.VertexCount);
        }

        #endregion

        #region WAL - a rolled-back batch is never logged (spec acceptance 4 + 5)

        [TestMethod]
        public void WAL_RolledBackBatch_LogsNothing_RecoveryReproducesPreTxState()
        {
            // Arrange - a WAL-enabled engine with a committed baseline.
            VertexModel[] baseline;
            int probeIdBefore;
            using (var fallen8 = new Fallen8(_loggerFactory, new WriteAheadLogOptions(WalPath)))
            {
                baseline = CreateVertices(fallen8, 2);
                CreateEdge(fallen8, baseline[0].Id, baseline[1].Id);

                // Act - three failing batches of each kind, then one more committed write.
                var failingCreate = new CreateVerticesTransaction();
                failingCreate.AddVertex(1u, "doomed");
                failingCreate.Vertices.Add(null);
                fallen8.EnqueueTransaction(failingCreate).WaitUntilFinished();

                var failingProps = new AddPropertiesTransaction();
                failingProps.AddProperty(baseline[0].Id, "idx", 999); // conflicts with idx = 0
                fallen8.EnqueueTransaction(failingProps).WaitUntilFinished();

                var failingRemove = new RemoveGraphElementsTransaction
                {
                    GraphElementIds = new List<int> { baseline[0].Id, int.MaxValue }
                };
                fallen8.EnqueueTransaction(failingRemove).WaitUntilFinished();

                var committed = CreateVertices(fallen8, 1, "after-failures");
                probeIdBefore = committed[0].Id;

                Assert.AreEqual(3, fallen8.VertexCount, "Live state before the crash: 2 baseline + 1 after-failures.");
                Assert.AreEqual(1, fallen8.EdgeCount);
            }

            // "Crash" - recover a fresh engine purely from the write-ahead log.
            var recovered = new Fallen8(_loggerFactory, new WriteAheadLogOptions(WalPath));

            // Assert - recovery reproduces EXACTLY the observed pre-crash state: the rolled-back
            // batches left no entry (nothing resurrected, nothing missing, no id drift).
            Assert.AreEqual(3, recovered.VertexCount,
                "Recovery must reproduce the committed state - a rolled-back batch must not be replayed.");
            Assert.AreEqual(1, recovered.EdgeCount);
            Assert.IsTrue(recovered.TryGetVertex(out var recoveredBaseline0, baseline[0].Id),
                "The vertex whose failed batch-removal rolled back must still exist after recovery.");
            Assert.IsTrue(recoveredBaseline0.TryGetProperty(out int idx, "idx"));
            Assert.AreEqual(0, idx, "The rolled-back conflicting property update must not be replayed.");
            Assert.IsTrue(recovered.TryGetVertex(out var afterFailures, probeIdBefore),
                "The write committed AFTER the failed batches must recover under the same id (no id drift in the log).");
            Assert.AreEqual("after-failures", afterFailures.Label);
            Assert.IsFalse(recovered.GetAllVertices().Any(x => x.Label == "doomed"),
                "No vertex of the rolled-back create batch may be resurrected by replay.");

            // The recovered engine's id space is aligned too.
            AssertIdSpaceIntact(recovered);
        }

        #endregion
    }
}
