// MIT License
//
// CheckpointResilienceTest.cs
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
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Helper;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Index.Spatial;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Serializer;
using NoSQL.GraphDB.Core.Service;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// An optional sidecar must never decide the fate of a whole checkpoint. One index whose Save
    /// throws, one index whose Load throws and one service whose Save throws are each skipped -
    /// logged, no partial sidecar left behind - while the graph elements and the healthy indices
    /// still round-trip; and a spatial index backed by the one STATEFUL built-in metric
    /// (<c>GeoMetric</c>) genuinely survives the round trip instead of being skipped on load.
    /// Every test therefore also asserts which per-index and per-service sidecar files exist after
    /// the save, since that is where "skipped" and "persisted" are observable.
    /// </summary>
    [TestClass]
    public class CheckpointResilienceTest
    {
        private ILoggerFactory _loggerFactory;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
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
            var vertices = TestVertices.Create(fallen8, 2, "test", "idx");

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
            var vertices = TestVertices.Create(fallen8, 2, "test", "idx");

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

        [TestMethod]
        public void SaveAndLoad_WithThrowingServicePresent_SkipsItAndCompletesCheckpoint()
        {
            // Fix 2: SaveService is now best-effort, symmetric with SaveIndex and with the load side
            // (ValidateOptionalSidecars already skips a bad service sidecar). A service whose Save
            // throws must be skipped - logged at Error, no partial sidecar left behind - while the
            // rest of the checkpoint (graph elements + a good index) still completes and reloads.
            // Before the fix, the throw propagated via serviceEntries.Add(saver.Result) and aborted
            // the WHOLE checkpoint.
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = TestVertices.Create(fallen8, 2, "test", "idx");

            IIndex dictIndex;
            Assert.IsTrue(fallen8.IndexFactory.TryCreateIndex(out dictIndex, "goodIdx", "DictionaryIndex"),
                "The good dictionary index should be created.");
            dictIndex.AddOrUpdate("alice", vertices[0]);

            // Register a throwing service double directly (it is not a discoverable plugin).
            fallen8.ServiceFactory.Services.Add("throwingSvc", new ThrowingOnSaveService());

            var saveGameName = "ThrowingServiceCheckpointTest.f8s";
            var saveGameDirectory = ".";
            var saveGameLocation = Path.Combine(saveGameDirectory, saveGameName);
            string actualPath = null;

            try
            {
                CleanupSavegames(saveGameDirectory, saveGameName);

                var saveTx = new SaveTransaction { Path = saveGameLocation, SavePartitions = 1 };
                fallen8.EnqueueTransaction(saveTx).WaitUntilFinished();

                Assert.AreEqual(TransactionState.Finished, fallen8.GetTransactionState(saveTx.TransactionId),
                    "A single throwing service must NOT roll the whole checkpoint back.");
                actualPath = saveTx.ActualPath;
                Assert.IsNotNull(actualPath, "The save must have produced a path.");

                // The throwing service must not leave an orphaned partial sidecar behind.
                Assert.IsFalse(File.Exists(actualPath + Constants.ServiceSaveString + "throwingSvc"),
                    "A throwing service must not leave a partial service sidecar behind.");

                var reloaded = new Fallen8(_loggerFactory);
                var loadTx = new LoadTransaction { Path = actualPath };
                reloaded.EnqueueTransaction(loadTx).WaitUntilFinished();

                Assert.AreEqual(TransactionState.Finished, reloaded.GetTransactionState(loadTx.TransactionId),
                    "Loading a checkpoint whose save skipped a throwing service must not throw.");

                // Graph elements and the good index round-trip; the throwing service was skipped.
                Assert.AreEqual(2, reloaded.VertexCount, "All vertices must round-trip.");

                IIndex reloadedGood;
                Assert.IsTrue(reloaded.IndexFactory.TryGetIndex(out reloadedGood, "goodIdx"),
                    "The good index must be present after load (the checkpoint completed).");
                ImmutableList<AGraphElementModel> aliceHits;
                Assert.IsTrue(reloadedGood.TryGetValue(out aliceHits, "alice"),
                    "The good index must retain its data across the checkpoint.");
                Assert.AreEqual(1, aliceHits.Count, "The good index must retain its values.");

                Assert.IsFalse(reloaded.ServiceFactory.Services.ContainsKey("throwingSvc"),
                    "The throwing service must simply be absent after load (skipped, not fatal).");
            }
            finally
            {
                CleanupSavegames(saveGameDirectory, actualPath != null ? Path.GetFileName(actualPath) : saveGameName);
            }
        }

        [TestMethod]
        public void SaveAndLoad_WithGeoMetricSpatialIndex_RoundTripsMetricStateAndQueries()
        {
            // Fix 3 (preferred, round-trip): GeoMetric is a STATEFUL metric (it carries an earth
            // radius and has NO public parameterless ctor). The R-Tree now serializes the metric's
            // STATE, not just its type name, and reconstructs it via a non-public parameterless ctor
            // + IMetric.RestoreState - so a GeoMetric-backed R-Tree genuinely round-trips and stays
            // queryable, rather than throwing on load (Activator on a ctor-less type) and being
            // skipped. The distance query below only returns the right subset if the earth radius was
            // actually restored (a default/zero radius would make every point equidistant).
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = TestVertices.Create(fallen8, 3, "test", "idx");

            IIndex geoIndex;
            Assert.IsTrue(fallen8.IndexFactory.TryCreateIndex(out geoIndex, "geoIdx", "SpatialIndex", CreateGeoRTreeParameters()),
                "The GeoMetric spatial index should be created.");
            // Geo points as (latitude, longitude): two near Munich, one far north-east.
            geoIndex.AddOrUpdate(new Point(48.00f, 11.00f), vertices[0]);
            geoIndex.AddOrUpdate(new Point(48.05f, 11.05f), vertices[1]); // ~6 km from the first
            geoIndex.AddOrUpdate(new Point(60.00f, 25.00f), vertices[2]); // ~1500 km away
            Assert.AreEqual(3, geoIndex.CountOfValues(), "Sanity: the geo index holds three values before save.");

            var saveGameName = "GeoSpatialCheckpointTest.f8s";
            var saveGameDirectory = ".";
            var saveGameLocation = Path.Combine(saveGameDirectory, saveGameName);
            string actualPath = null;

            try
            {
                CleanupSavegames(saveGameDirectory, saveGameName);

                var saveTx = new SaveTransaction { Path = saveGameLocation, SavePartitions = 1 };
                fallen8.EnqueueTransaction(saveTx).WaitUntilFinished();

                Assert.AreEqual(TransactionState.Finished, fallen8.GetTransactionState(saveTx.TransactionId),
                    "Saving a graph with a GeoMetric spatial index must succeed.");
                actualPath = saveTx.ActualPath;
                Assert.IsNotNull(actualPath, "The save must have produced a path.");
                Assert.IsTrue(File.Exists(actualPath + Constants.IndexSaveString + "geoIdx"),
                    "A GeoMetric R-Tree is persistable and must write its sidecar.");

                var reloaded = new Fallen8(_loggerFactory);
                var loadTx = new LoadTransaction { Path = actualPath };
                reloaded.EnqueueTransaction(loadTx).WaitUntilFinished();

                Assert.AreEqual(TransactionState.Finished, reloaded.GetTransactionState(loadTx.TransactionId),
                    "Loading a GeoMetric R-Tree must not throw - the metric state round-trips (no ctor-less Activator failure).");

                IIndex reloadedGeo;
                Assert.IsTrue(reloaded.IndexFactory.TryGetIndex(out reloadedGeo, "geoIdx"),
                    "The GeoMetric spatial index must survive the checkpoint (not be skipped on load).");
                Assert.AreEqual(3, reloadedGeo.CountOfValues(), "All three entries round-trip.");

                // A distance query on the RELOADED index uses the reconstructed GeoMetric. Within
                // 100 km of (48.00, 11.00): the two nearby points, NOT the far one. If the earth
                // radius had not been restored (zero), TransformationOfDistance would blow the search
                // region up and the far point would wrongly match - so this pins the state round-trip.
                VertexModel anchor;
                Assert.IsTrue(reloaded.TryGetVertex(out anchor, vertices[0].Id), "The anchor vertex must exist after load.");
                ImmutableList<AGraphElementModel> near;
                Assert.IsTrue(((ISpatialIndex)reloadedGeo).SearchDistance(out near, 100.0f, anchor),
                    "A distance query on the reloaded GeoMetric index must run and find neighbours.");
                var nearIds = near.Select(e => e.Id).OrderBy(id => id).ToArray();
                CollectionAssert.AreEqual(new[] { vertices[0].Id, vertices[1].Id }, nearIds,
                    "Within 100 km of (48.00,11.00): exactly the two nearby points - proves the earth radius (metric state) was restored.");
            }
            finally
            {
                CleanupSavegames(saveGameDirectory, actualPath != null ? Path.GetFileName(actualPath) : saveGameName);
            }
        }

        #region helpers

        private static IDictionary<string, object> CreateGeoRTreeParameters()
        {
            // GeoMetric is the framework's one STATEFUL built-in metric (it carries an earth radius,
            // here km, and has no public parameterless ctor). Fix 3 serializes that state so this
            // metric reconstructs faithfully on load.
            return new Dictionary<string, object>
            {
                ["IMetric"] = new NoSQL.GraphDB.Core.Index.Spatial.Implementation.Metric.GeoMetric(6371.0f),
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
        /// A minimal, deliberately-broken service whose Save always throws. Used to prove that
        /// PersistencyFactory.SaveService skips a failing service instead of aborting the whole
        /// checkpoint (fix 2). Everything else is an inert no-op - it never needs to run or hold data,
        /// and it is a nested private type so it is not enumerated by plugin discovery.
        /// </summary>
        private sealed class ThrowingOnSaveService : IService
        {
            public string PluginName => "ThrowingTestService";
            public Type PluginCategory => typeof(IService);
            public string Description => "A test service whose Save throws.";
            public string Manufacturer => "fallen-8 tests";

            public DateTime StartTime => DateTime.MinValue;
            public bool IsRunning => false;
            public IDictionary<string, string> Metadata => new Dictionary<string, string>();

            public void Initialize(IFallen8 fallen8, IDictionary<string, object> parameter) { }

            public void Save(SerializationWriter writer)
            {
                throw new InvalidOperationException("This service deliberately fails to serialize.");
            }

            public void Load(SerializationReader reader, IFallen8 fallen8) { }

            public bool TryStop() => true;
            public bool TryStart() => true;
            public void OnServiceRestart() { }

            public void Dispose() { }
        }

        #endregion
    }
}
