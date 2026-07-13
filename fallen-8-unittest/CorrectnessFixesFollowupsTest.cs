// MIT License
//
// CorrectnessFixesFollowupsTest.cs
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
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Helper;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Index.Spatial;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Serializer;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Regression tests for the "correctness-fixes-followups" feature:
    ///  - B6 follow-up: a rolled-back, waited-on mutation is reported to the client as an error.
    ///  - B7 follow-up: a spatial (R-Tree) index must not crash the whole checkpoint.
    ///  - the edge-removal branch of the internal removal rollback.
    /// </summary>
    [TestClass]
    public class CorrectnessFixesFollowupsTest
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

        private static int StatusCodeOf(IActionResult result)
        {
            var statusResult = result as IStatusCodeActionResult;
            Assert.IsNotNull(statusResult,
                "Expected a status-code action result but got " + (result?.GetType().Name ?? "null") + ".");
            Assert.IsTrue(statusResult.StatusCode.HasValue,
                "Expected the action result to carry an explicit status code.");
            return statusResult.StatusCode.Value;
        }

        #region B6 follow-up - controllers surface a rolled-back transaction

        [TestMethod]
        public async Task TryRemoveGraphElement_WhenWaitingAndTransactionRollsBack_ReturnsError()
        {
            // Arrange
            var fallen8 = new Fallen8(_loggerFactory);
            var controller = new GraphController(_loggerFactory.CreateLogger<GraphController>(), fallen8);

            // A removal of a non-existent (out-of-range) id throws inside the worker and is rolled back.
            // Act
            var result = await controller.TryRemoveGraphElement(int.MaxValue.ToString(), waitForCompletion: true);

            // Assert
            Assert.AreEqual(StatusCodes.Status500InternalServerError, StatusCodeOf(result),
                "A waited-on mutation that rolled back must be reported as an error, not success.");
        }

        [TestMethod]
        public async Task AddEdge_WhenWaitingAndReferencingNonExistentVertex_ReturnsError()
        {
            // Arrange - one real vertex so the source resolves but the target does not.
            var fallen8 = new Fallen8(_loggerFactory);
            var controller = new GraphController(_loggerFactory.CreateLogger<GraphController>(), fallen8);
            var vertices = CreateVertices(fallen8, 1);

            var edgeSpec = new EdgeSpecification
            {
                CreationDate = 1,
                Label = "knows",
                SourceVertex = vertices[0].Id,
                TargetVertex = int.MaxValue, // non-existent
                EdgePropertyId = "knows",
                Properties = new List<PropertySpecification>()
            };

            // Act
            var result = await controller.AddEdge(edgeSpec, waitForCompletion: true);

            // Assert
            Assert.AreEqual(StatusCodes.Status500InternalServerError, StatusCodeOf(result),
                "Creating an edge to a non-existent vertex rolls back and must be reported as an error.");
        }

        [TestMethod]
        public async Task AddVertex_WhenWaitingAndTransactionSucceeds_ReturnsSuccess()
        {
            // Arrange
            var fallen8 = new Fallen8(_loggerFactory);
            var controller = new GraphController(_loggerFactory.CreateLogger<GraphController>(), fallen8);

            var vertexSpec = new VertexSpecification
            {
                CreationDate = 1,
                Label = "person",
                Properties = new List<PropertySpecification>()
            };

            // Act
            var result = await controller.AddVertex(vertexSpec, waitForCompletion: true);

            // Assert
            Assert.AreEqual(StatusCodes.Status202Accepted, StatusCodeOf(result),
                "A normal, successful mutation must still be reported as success.");
            Assert.AreEqual(1, fallen8.VertexCount, "The vertex should have been created.");
        }

        [TestMethod]
        public async Task TryRemoveGraphElement_WhenNotWaiting_StaysAcceptedEvenThoughItWouldRollBack()
        {
            // Arrange
            var fallen8 = new Fallen8(_loggerFactory);
            var controller = new GraphController(_loggerFactory.CreateLogger<GraphController>(), fallen8);

            // Act - fire-and-forget: the same removal that rolls back, but without waiting.
            var result = await controller.TryRemoveGraphElement(int.MaxValue.ToString(), waitForCompletion: false);

            // Assert
            Assert.AreEqual(StatusCodes.Status202Accepted, StatusCodeOf(result),
                "The fire-and-forget path must be unchanged - the outcome is unknowable when not waiting.");
        }

        #endregion

        #region B6 follow-up (engine level) - the real worker records the fault on TransactionInformation.Error

        // These pin the PRODUCTION wiring in TransactionManager.ProcessTransaction directly (not via a
        // controller decorator that fabricates the outcome). The worker maps BOTH a thrown exception and
        // a clean TryExecute()==false to TransactionState.RolledBack, and it is the manager's single
        // "faultedInfo.Error = ex" line that lets a waited-on caller tell the two apart. The whole
        // SubGraphController fault->500 vs clean->400 split rests on exactly this distinction, yet the
        // existing controller tests would still pass if that line were deleted (RollbackForcingFallen8
        // sets Error itself). We drive it through REAL transactions rather than a bespoke throwing
        // ATransaction subclass: ATransaction's TryExecute/Rollback/Cleanup are internal abstract and the
        // test assembly has no InternalsVisibleTo, so a cross-assembly subclass cannot compile
        // (CS0115/CS0534).

        [TestMethod]
        public void ProcessTransaction_WhenTryExecuteThrows_RecordsThrownExceptionAndRollsBack()
        {
            // Arrange - a real transaction whose TryExecute throws: removing an out-of-range id makes
            // Fallen8.TryRemoveGraphElement_private call GetGraphElementForMutation, whose bounds check
            // throws an ArgumentOutOfRangeException that escapes TryExecute up to the worker's catch.
            var fallen8 = new Fallen8(_loggerFactory);
            var tx = new RemoveGraphElementTransaction { GraphElementId = int.MaxValue };

            // Act
            var txInfo = fallen8.EnqueueTransaction(tx);
            txInfo.WaitUntilFinished();

            // Assert - the terminal state is RolledBack (as for a clean false) AND the fault is recorded.
            // The Error assertions are load-bearing: delete "faultedInfo.Error = ex" from
            // TransactionManager.ProcessTransaction and this test fails while the state assertion alone
            // would still pass.
            Assert.AreEqual(TransactionState.RolledBack, txInfo.TransactionState,
                "A transaction whose TryExecute throws must end up RolledBack.");
            Assert.IsNotNull(txInfo.Error,
                "The real worker must record the thrown exception on TransactionInformation.Error.");
            Assert.IsInstanceOfType(txInfo.Error, typeof(ArgumentOutOfRangeException),
                "Error must be the exact exception that escaped TryExecute, not a substitute.");
        }

        [TestMethod]
        public void ProcessTransaction_WhenTryExecuteReturnsFalseCleanly_RollsBackWithNullError()
        {
            // Arrange - a real transaction whose TryExecute returns false WITHOUT throwing: removing a
            // subgraph that does not exist makes RemoveSubGraphTransaction return a clean false.
            var fallen8 = new Fallen8(_loggerFactory);
            var tx = new RemoveSubGraphTransaction { SubGraphName = "does-not-exist" };

            // Act
            var txInfo = fallen8.EnqueueTransaction(tx);
            txInfo.WaitUntilFinished();

            // Assert - same terminal state as a genuine fault, but Error stays null. This is the exact
            // distinction SubGraphController.CreateSubGraph relies on to map a clean rollback to 400
            // rather than 500.
            Assert.AreEqual(TransactionState.RolledBack, txInfo.TransactionState,
                "A clean TryExecute()==false must end up RolledBack.");
            Assert.IsNull(txInfo.Error,
                "A clean rollback (no thrown exception) must leave TransactionInformation.Error null.");
        }

        #endregion

        #region B7 follow-up - a spatial index survives a checkpoint (C9)

        [TestMethod]
        public void SaveAndLoad_WithSpatialIndexPresent_SpatialIndexSurvivesAndIsQueryable()
        {
            // Arrange
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = CreateVertices(fallen8, 3);

            // A non-spatial index that MUST round-trip across the checkpoint.
            IIndex dictIndex;
            Assert.IsTrue(fallen8.IndexFactory.TryCreateIndex(out dictIndex, "nameIdx", "DictionaryIndex"),
                "The dictionary index should be created.");
            dictIndex.AddOrUpdate("alice", vertices[0]);
            dictIndex.AddOrUpdate("bob", vertices[1]);

            // A spatial R-Tree index holding points. With C9 it is now FULLY persistable: it must
            // survive the checkpoint and come back queryable (before, Save/Load threw and the index
            // was skipped, so it was absent after load and had to be recreated by the caller).
            IIndex spatialIndex;
            Assert.IsTrue(fallen8.IndexFactory.TryCreateIndex(out spatialIndex, "spatialIdx", "SpatialIndex", CreateRTreeParameters()),
                "The spatial index should be created.");
            spatialIndex.AddOrUpdate(new Point(1.0f, 1.0f), vertices[0]);
            spatialIndex.AddOrUpdate(new Point(2.0f, 2.0f), vertices[1]);
            spatialIndex.AddOrUpdate(new Point(3.0f, 3.0f), vertices[2]);
            Assert.AreEqual(3, spatialIndex.CountOfValues(), "Sanity: the spatial index holds three values before save.");

            var saveGameName = "SpatialCheckpointFollowupTest.f8s";
            var saveGameDirectory = ".";
            var saveGameLocation = Path.Combine(saveGameDirectory, saveGameName);
            string actualPath = null;

            try
            {
                CleanupSavegames(saveGameDirectory, saveGameName);

                // Act - save. The spatial index is persisted like any other.
                var saveTx = new SaveTransaction { Path = saveGameLocation, SavePartitions = 1 };
                fallen8.EnqueueTransaction(saveTx).WaitUntilFinished();

                Assert.AreEqual(TransactionState.Finished, fallen8.GetTransactionState(saveTx.TransactionId),
                    "Saving a graph that contains a spatial index must succeed.");
                actualPath = saveTx.ActualPath;
                Assert.IsNotNull(actualPath, "The save must have produced a path.");

                // The spatial index now WRITES its sidecar (C9), unlike the former skip-on-save.
                Assert.IsTrue(File.Exists(actualPath + Constants.IndexSaveString + "spatialIdx"),
                    "A persistable spatial index must write its sidecar.");

                // Load into a fresh instance.
                var reloaded = new Fallen8(_loggerFactory);
                var loadTx = new LoadTransaction { Path = actualPath };
                reloaded.EnqueueTransaction(loadTx).WaitUntilFinished();

                Assert.AreEqual(TransactionState.Finished, reloaded.GetTransactionState(loadTx.TransactionId),
                    "Loading a checkpoint that contained a spatial index must not throw.");

                // The graph elements round-trip.
                Assert.AreEqual(3, reloaded.VertexCount, "All vertices must round-trip.");

                // The non-spatial index round-trips with its data intact.
                IIndex reloadedDict;
                Assert.IsTrue(reloaded.IndexFactory.TryGetIndex(out reloadedDict, "nameIdx"),
                    "The dictionary index must be present after load.");
                ImmutableList<AGraphElementModel> aliceHits;
                Assert.IsTrue(reloadedDict.TryGetValue(out aliceHits, "alice"),
                    "The dictionary index must retain its keys across the checkpoint.");
                Assert.AreEqual(1, aliceHits.Count, "The dictionary index must retain its values across the checkpoint.");

                // C9 headline: the spatial index is PRESENT after load (not absent-and-recreated) and
                // holds its entries...
                IIndex reloadedSpatial;
                Assert.IsTrue(reloaded.IndexFactory.TryGetIndex(out reloadedSpatial, "spatialIdx"),
                    "The spatial index must survive the checkpoint and be present after load.");
                Assert.AreEqual(3, reloadedSpatial.CountOfValues(),
                    "The reloaded spatial index must retain all three entries.");

                // ...and a spatial QUERY on the RELOADED index runs and returns the correct subset -
                // the exact thing the pre-C9 skip-and-recreate could not do (a reloaded spatial index
                // used to NPE). A region covering (1,1) and (2,2) but not (3,3):
                ImmutableList<AGraphElementModel> regionHits;
                Assert.IsTrue(((ISpatialIndex)reloadedSpatial).SearchRegion(out regionHits,
                        new NoSQL.GraphDB.Core.Index.Spatial.Implementation.SpatialContainer.MBR(
                            new[] { 0.5f, 0.5f }, new[] { 2.5f, 2.5f })),
                    "A region query on the reloaded index must run and find matches.");
                var hitIds = regionHits.Select(e => e.Id).OrderBy(id => id).ToArray();
                CollectionAssert.AreEqual(new[] { vertices[0].Id, vertices[1].Id }, hitIds,
                    "The region query must return exactly the two vertices inside the region.");

                // A distance query on the reloaded index also works against a reloaded vertex.
                VertexModel reloadedVertex;
                Assert.IsTrue(reloaded.TryGetVertex(out reloadedVertex, vertices[0].Id), "The indexed vertex must exist after load.");
                ImmutableList<AGraphElementModel> distanceHits;
                Assert.IsTrue(((ISpatialIndex)reloadedSpatial).SearchDistance(out distanceHits, 5.0f, reloadedVertex),
                    "A distance query on the reloaded index must run and find neighbours.");
            }
            finally
            {
                CleanupSavegames(saveGameDirectory, actualPath != null ? Path.GetFileName(actualPath) : saveGameName);
            }
        }

        [TestMethod]
        public void SaveAndLoad_WithThrowingIndexPresent_SkipsItAndCompletesCheckpoint()
        {
            // Arrange - a graph, a good dictionary index that MUST round-trip, and a deliberately
            // broken index whose Save throws. This is the headline B7 promise proven directly: the
            // per-index guard in PersistencyFactory.SaveIndex (return null on failure) plus the
            // per-index catch in LoadIndices mean one throwing index is skipped, never fatal to the
            // whole checkpoint.
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = CreateVertices(fallen8, 2);

            IIndex dictIndex;
            Assert.IsTrue(fallen8.IndexFactory.TryCreateIndex(out dictIndex, "goodIdx", "DictionaryIndex"),
                "The good dictionary index should be created.");
            dictIndex.AddOrUpdate("alice", vertices[0]);

            // Register a throwing stub directly (it is not a discoverable plugin).
            fallen8.IndexFactory.Indices.Add("throwingIdx", new ThrowingOnSaveIndex());

            var saveGameName = "ThrowingIndexCheckpointTest.f8s";
            var saveGameDirectory = ".";
            var saveGameLocation = Path.Combine(saveGameDirectory, saveGameName);
            string actualPath = null;

            try
            {
                CleanupSavegames(saveGameDirectory, saveGameName);

                var saveTx = new SaveTransaction { Path = saveGameLocation, SavePartitions = 1 };
                fallen8.EnqueueTransaction(saveTx).WaitUntilFinished();

                Assert.AreEqual(TransactionState.Finished, fallen8.GetTransactionState(saveTx.TransactionId),
                    "A single throwing index must NOT roll the whole checkpoint back.");
                actualPath = saveTx.ActualPath;
                Assert.IsNotNull(actualPath, "The save must have produced a path.");

                // The throwing index must not leave an orphaned partial sidecar behind (fix 5).
                Assert.IsFalse(File.Exists(actualPath + Constants.IndexSaveString + "throwingIdx"),
                    "A throwing index must not leave a partial index sidecar behind.");

                var reloaded = new Fallen8(_loggerFactory);
                var loadTx = new LoadTransaction { Path = actualPath };
                reloaded.EnqueueTransaction(loadTx).WaitUntilFinished();

                Assert.AreEqual(TransactionState.Finished, reloaded.GetTransactionState(loadTx.TransactionId),
                    "Loading a checkpoint whose save skipped a throwing index must not throw.");

                // Graph elements and the good index round-trip; the throwing index was skipped.
                Assert.AreEqual(2, reloaded.VertexCount, "All vertices must round-trip.");

                IIndex reloadedGood;
                Assert.IsTrue(reloaded.IndexFactory.TryGetIndex(out reloadedGood, "goodIdx"),
                    "The good index must be present after load.");
                ImmutableList<AGraphElementModel> aliceHits;
                Assert.IsTrue(reloadedGood.TryGetValue(out aliceHits, "alice"),
                    "The good index must retain its data across the checkpoint.");
                Assert.AreEqual(1, aliceHits.Count, "The good index must retain its values.");

                IIndex skipped;
                Assert.IsFalse(reloaded.IndexFactory.TryGetIndex(out skipped, "throwingIdx"),
                    "The throwing index must simply be absent after load (skipped, not fatal).");
            }
            finally
            {
                CleanupSavegames(saveGameDirectory, actualPath != null ? Path.GetFileName(actualPath) : saveGameName);
            }
        }

        [TestMethod]
        public void SaveAndLoad_WithThrowingOnLoadIndexPresent_SkipsItOnLoadAndCompletes()
        {
            // Arrange - a graph, a good dictionary index that MUST round-trip, and an index that
            // SAVES fine but throws on Load. This proves the LOAD-side guard directly: the index
            // reaches the manifest and its sidecar, so on load IndexFactory.OpenIndex finds the
            // plugin and calls Load (which throws); the per-index catch in LoadIndices skips it
            // rather than aborting the whole load. (This is the "older save point still lists a
            // spatial sidecar / a throwing Load is skipped" path.)
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = CreateVertices(fallen8, 2);

            IIndex dictIndex;
            Assert.IsTrue(fallen8.IndexFactory.TryCreateIndex(out dictIndex, "goodIdx", "DictionaryIndex"),
                "The good dictionary index should be created.");
            dictIndex.AddOrUpdate("alice", vertices[0]);

            // ThrowingOnLoadIndex is a discoverable (top-level public) plugin, so OpenIndex can
            // instantiate it by plugin name on load and actually invoke its throwing Load.
            fallen8.IndexFactory.Indices.Add("throwingLoadIdx", new ThrowingOnLoadIndex());

            var saveGameName = "ThrowingOnLoadIndexCheckpointTest.f8s";
            var saveGameDirectory = ".";
            var saveGameLocation = Path.Combine(saveGameDirectory, saveGameName);
            string actualPath = null;

            try
            {
                CleanupSavegames(saveGameDirectory, saveGameName);

                var saveTx = new SaveTransaction { Path = saveGameLocation, SavePartitions = 1 };
                fallen8.EnqueueTransaction(saveTx).WaitUntilFinished();

                Assert.AreEqual(TransactionState.Finished, fallen8.GetTransactionState(saveTx.TransactionId),
                    "Saving must succeed - this index serializes fine; it only fails on load.");
                actualPath = saveTx.ActualPath;
                Assert.IsNotNull(actualPath, "The save must have produced a path.");

                // Unlike a save-throwing index, this one saved fine, so its sidecar IS present and
                // it IS referenced by the manifest - which is exactly what forces the load path.
                Assert.IsTrue(File.Exists(actualPath + Constants.IndexSaveString + "throwingLoadIdx"),
                    "The save-fine index must have written its sidecar.");

                var reloaded = new Fallen8(_loggerFactory);
                var loadTx = new LoadTransaction { Path = actualPath };
                reloaded.EnqueueTransaction(loadTx).WaitUntilFinished();

                Assert.AreEqual(TransactionState.Finished, reloaded.GetTransactionState(loadTx.TransactionId),
                    "A single index whose Load throws must NOT abort the whole checkpoint load.");

                // Graph elements and the good index round-trip; the throwing-on-load index is skipped.
                Assert.AreEqual(2, reloaded.VertexCount, "All vertices must round-trip.");

                IIndex reloadedGood;
                Assert.IsTrue(reloaded.IndexFactory.TryGetIndex(out reloadedGood, "goodIdx"),
                    "The good index must be present after load.");
                ImmutableList<AGraphElementModel> aliceHits;
                Assert.IsTrue(reloadedGood.TryGetValue(out aliceHits, "alice"),
                    "The good index must retain its data across the checkpoint.");
                Assert.AreEqual(1, aliceHits.Count, "The good index must retain its values.");

                IIndex skipped;
                Assert.IsFalse(reloaded.IndexFactory.TryGetIndex(out skipped, "throwingLoadIdx"),
                    "The index whose Load threw must be absent after load (skipped, not registered).");
            }
            finally
            {
                CleanupSavegames(saveGameDirectory, actualPath != null ? Path.GetFileName(actualPath) : saveGameName);
            }
        }

        #endregion

        #region edge-removal rollback regression (else branch of TryRemoveGraphElement_private)

        [TestMethod]
        public void RemoveEdge_WhenDetachFaultsMidway_ShouldRollBackEdgeAndRestoreAdjacency()
        {
            // Arrange - a normal edge S --("knows")--> T.
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = CreateVertices(fallen8, 2);
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
            // OutEdges - driving the internal rollback.
            source.OutEdges = source.OutEdges.SetItem("knows", source.OutEdges["knows"].Add(null));

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
            ImmutableList<EdgeModel> targetInEdges;
            Assert.IsTrue(target.TryGetInEdge(out targetInEdges, "knows"),
                "The target vertex must still expose its incoming-edge bucket for \"knows\".");
            Assert.IsTrue(targetInEdges.Any(e => e != null && e.Id == edgeId),
                "The edge must be back in the target vertex's InEdges (inEdgeRemovals replay).");

            // ...and the SOURCE vertex's outgoing adjacency still contains the edge (the source
            // detach threw before mutating OutEdges, so the edge was never removed there).
            ImmutableList<EdgeModel> sourceOutEdges;
            Assert.IsTrue(source.TryGetOutEdge(out sourceOutEdges, "knows"),
                "The source vertex must still expose its outgoing-edge bucket for \"knows\".");
            Assert.IsTrue(sourceOutEdges.Any(e => e != null && e.Id == edgeId),
                "The edge must still be in the source vertex's OutEdges after rollback.");
        }

        #endregion

        #region helpers

        private static IDictionary<string, object> CreateRTreeParameters()
        {
            // Use the framework's public, stateless EuclidianMetric (not a private test metric): C9
            // reconstructs the metric and dimensions from their assembly-qualified type names on load,
            // so they must be publicly resolvable + parameterless-constructible for the reloaded index
            // to be functional.
            return new Dictionary<string, object>
            {
                ["IMetric"] = new NoSQL.GraphDB.Core.Index.Spatial.Implementation.Metric.EuclidianMetric(),
                ["MinCount"] = 2,
                ["MaxCount"] = 5,
                ["Space"] = new List<IDimension>
                {
                    new NoSQL.GraphDB.Core.Index.Spatial.Implementation.Geometry.RealDimension(),
                    new NoSQL.GraphDB.Core.Index.Spatial.Implementation.Geometry.RealDimension(),
                }
            };
        }

        private static void CleanupSavegames(String saveGameDirectory, String saveGameFilePrefix)
        {
            var toBeDeleted = Path.GetFileName(saveGameFilePrefix) + "*";
            foreach (var file in Directory.GetFiles(saveGameDirectory, toBeDeleted))
            {
                File.Delete(file);
            }
        }

        /// <summary>
        /// A minimal, deliberately-broken index whose Save always throws. Used to prove that the
        /// per-index guards in PersistencyFactory skip a failing index instead of aborting the
        /// whole checkpoint. Everything else is an inert no-op - it never needs to hold data.
        /// </summary>
        private sealed class ThrowingOnSaveIndex : IIndex
        {
            public string PluginName => "ThrowingTestIndex";
            public Type PluginCategory => typeof(IIndex);
            public string Description => "A test index whose Save throws.";
            public string Manufacturer => "fallen-8 tests";

            // CLAIMS to be persistable (so it is NOT skipped silently by the CanPersist gate) but then
            // its Save throws - exactly the "genuine, unexpected serialization failure" path that must
            // be caught, logged at Error level and skipped without aborting the checkpoint.
            public bool CanPersist => true;

            public void Initialize(IFallen8 fallen8, IDictionary<string, object> parameter) { }

            public void Save(SerializationWriter writer)
            {
                throw new InvalidOperationException("This index deliberately fails to serialize.");
            }

            public void Load(SerializationReader reader, IFallen8 fallen8) { }

            public int CountOfKeys() => 0;
            public int CountOfValues() => 0;
            public void AddOrUpdate(object key, AGraphElementModel graphElement) { }
            public bool TryRemoveKey(object key) => false;
            public void RemoveValue(AGraphElementModel graphElement) { }
            public void Wipe() { }
            public IEnumerable<object> GetKeys() => Enumerable.Empty<object>();

            public IEnumerable<KeyValuePair<object, ImmutableList<AGraphElementModel>>> GetKeyValues()
                => Enumerable.Empty<KeyValuePair<object, ImmutableList<AGraphElementModel>>>();

            public bool TryGetValue(out ImmutableList<AGraphElementModel> result, object key)
            {
                result = ImmutableList<AGraphElementModel>.Empty;
                return false;
            }

            public void Dispose() { }
        }

        #endregion
    }

    /// <summary>
    /// A minimal index that SAVES fine but always throws on Load. It is deliberately a top-level
    /// public type with a public parameterless constructor so <c>PluginFactory</c> discovers it
    /// (nested types report <c>IsNestedPublic</c>, not <c>IsPublic</c>, so PluginFactory skips them):
    /// only then can <c>IndexFactory.OpenIndex</c> instantiate it by plugin name on load and actually
    /// invoke the throwing <see cref="Load"/>, exercising the per-index catch in
    /// <c>PersistencyFactory.LoadIndices</c>. Everything else is an inert no-op - it never needs to
    /// hold data.
    /// </summary>
    /// <remarks>
    /// Consequence of being globally discoverable: this double - and any other top-level public
    /// <see cref="IIndex"/> added to the test assembly - is enumerated by
    /// <c>PluginFactory.TryGetAvailablePlugins&lt;IIndex&gt;()</c> during test runs (that is what
    /// <c>IndexFactory</c> and the admin endpoint use to list index plugins). Any FUTURE test that
    /// asserts an exact set or count of available index plugins must therefore filter out these
    /// test-manufacturer doubles (e.g. by <see cref="Manufacturer"/> == "fallen-8 tests") rather than
    /// expecting only the production indices.
    /// </remarks>
    public sealed class ThrowingOnLoadIndex : IIndex
    {
        public const string TestPluginName = "ThrowingOnLoadTestIndex";

        public string PluginName => TestPluginName;
        public Type PluginCategory => typeof(IIndex);
        public string Description => "A test index that saves fine but throws on load.";
        public string Manufacturer => "fallen-8 tests";

        // Persistable: it serializes fine and reaches the manifest + its sidecar; the failure is on
        // the LOAD side, exercising the per-index catch in PersistencyFactory.LoadIndices.
        public bool CanPersist => true;

        public void Initialize(IFallen8 fallen8, IDictionary<string, object> parameter) { }

        public void Save(SerializationWriter writer)
        {
            // Serializes cleanly (writes no payload), so it reaches the manifest and its sidecar.
        }

        public void Load(SerializationReader reader, IFallen8 fallen8)
        {
            throw new InvalidOperationException("This index deliberately fails to deserialize.");
        }

        public int CountOfKeys() => 0;
        public int CountOfValues() => 0;
        public void AddOrUpdate(object key, AGraphElementModel graphElement) { }
        public bool TryRemoveKey(object key) => false;
        public void RemoveValue(AGraphElementModel graphElement) { }
        public void Wipe() { }
        public IEnumerable<object> GetKeys() => Enumerable.Empty<object>();

        public IEnumerable<KeyValuePair<object, ImmutableList<AGraphElementModel>>> GetKeyValues()
            => Enumerable.Empty<KeyValuePair<object, ImmutableList<AGraphElementModel>>>();

        public bool TryGetValue(out ImmutableList<AGraphElementModel> result, object key)
        {
            result = ImmutableList<AGraphElementModel>.Empty;
            return false;
        }

        public void Dispose() { }
    }
}
