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
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Index.Spatial;
using NoSQL.GraphDB.Core.Model;
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

        #region B7 follow-up - a spatial index must not crash the whole checkpoint

        [TestMethod]
        public void SaveAndLoad_WithSpatialIndexPresent_DoesNotThrowAndRoundTripsGraphAndOtherIndices()
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

            // A spatial R-Tree index. Its Save/Load previously threw NotImplementedException, and
            // PersistencyFactory dereferenced the faulted saver task's .Result - so the presence of
            // this single index aborted the ENTIRE checkpoint (all graph elements, all other indices).
            IIndex spatialIndex;
            Assert.IsTrue(fallen8.IndexFactory.TryCreateIndex(out spatialIndex, "spatialIdx", "SpatialIndex", CreateRTreeParameters()),
                "The spatial index should be created.");
            spatialIndex.AddOrUpdate(new Point(1.0f, 1.0f), vertices[0]);
            Assert.AreEqual(1, spatialIndex.CountOfValues(), "Sanity: the spatial index holds one value before save.");

            var saveGameName = "SpatialCheckpointFollowupTest.f8s";
            var saveGameDirectory = ".";
            var saveGameLocation = Path.Combine(saveGameDirectory, saveGameName);
            string actualPath = null;

            try
            {
                CleanupSavegames(saveGameDirectory, saveGameName);

                // Act - save. Before the fix this rolled the whole checkpoint back.
                var saveTx = new SaveTransaction { Path = saveGameLocation, SavePartitions = 1 };
                fallen8.EnqueueTransaction(saveTx).WaitUntilFinished();

                Assert.AreEqual(TransactionState.Finished, fallen8.GetTransactionState(saveTx.TransactionId),
                    "Saving a graph that contains a spatial index must NOT roll the whole checkpoint back.");
                actualPath = saveTx.ActualPath;
                Assert.IsNotNull(actualPath, "The save must have produced a path.");

                // Load into a fresh instance.
                var reloaded = new Fallen8(_loggerFactory);
                var loadTx = new LoadTransaction { Path = actualPath };
                reloaded.EnqueueTransaction(loadTx).WaitUntilFinished();

                Assert.AreEqual(TransactionState.Finished, reloaded.GetTransactionState(loadTx.TransactionId),
                    "Loading a checkpoint that contains a spatial index must not throw.");

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

                // The spatial index is present but INTENTIONALLY EMPTY after load: full R-Tree
                // serialization is deferred to the persistence-hardening theme, so Save/Load are
                // graceful no-ops. The point added before the save is therefore gone.
                IIndex reloadedSpatial;
                Assert.IsTrue(reloaded.IndexFactory.TryGetIndex(out reloadedSpatial, "spatialIdx"),
                    "The spatial index must still be registered after load.");
                Assert.IsInstanceOfType(reloadedSpatial, typeof(ISpatialIndex));
                Assert.AreEqual(0, reloadedSpatial.CountOfValues(),
                    "The spatial index is intentionally EMPTY after load (R-Tree persistence is not yet implemented).");
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
            return new Dictionary<string, object>
            {
                ["IMetric"] = new EuclideanMetric(),
                ["MinCount"] = 2,
                ["MaxCount"] = 5,
                ["Space"] = new List<IDimension>
                {
                    new NoSQL.GraphDB.Core.Index.Spatial.Implementation.Geometry.RealDimension(),
                    new NoSQL.GraphDB.Core.Index.Spatial.Implementation.Geometry.RealDimension(),
                }
            };
        }

        private sealed class EuclideanMetric : IMetric
        {
            public float Distance(IMBP point1, IMBP point2)
            {
                var sum = 0.0f;
                for (int i = 0; i < point1.Coordinates.Length; i++)
                {
                    var diff = point1.Coordinates[i] - point2.Coordinates[i];
                    sum += diff * diff;
                }
                return (float)Math.Sqrt(sum);
            }

            public float[] TransformationOfDistance(float distance, IMBR mbr)
            {
                return new float[] { distance, distance };
            }
        }

        private static void CleanupSavegames(String saveGameDirectory, String saveGameFilePrefix)
        {
            var toBeDeleted = Path.GetFileName(saveGameFilePrefix) + "*";
            foreach (var file in Directory.GetFiles(saveGameDirectory, toBeDeleted))
            {
                File.Delete(file);
            }
        }

        #endregion
    }
}
