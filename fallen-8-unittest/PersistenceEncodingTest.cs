// MIT License
//
// PersistenceEncodingTest.cs
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
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Helper;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Index.Spatial;
using NoSQL.GraphDB.Core.Index.Spatial.Implementation.Geometry;
using NoSQL.GraphDB.Core.Index.Spatial.Implementation.Metric;
using NoSQL.GraphDB.Core.Index.Spatial.Implementation.SpatialContainer;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Stage B of the persistence-hardening theme (Phase 3): the efficient payload encoding, folded
    /// into the same versioned + integrity-checked envelope Stage A shipped. These tests pin that the
    /// UTF-8 + tokenized + var-int + N1 encoding (P2/M5/P7/N1) round-trips a graph EXACTLY, that
    /// tokenization dedupes shared strings while reconstructing them faithfully, and that a spatial
    /// (R-Tree) index survives a checkpoint and is queryable on reload (C9).
    /// </summary>
    [TestClass]
    public class PersistenceEncodingTest
    {
        private ILoggerFactory _loggerFactory;
        private string _tempDir;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
            _tempDir = Path.Combine(Path.GetTempPath(), "f8_persist_encoding_" + Guid.NewGuid().ToString("N"));
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

        private string SavePath => Path.Combine(_tempDir, "encoding.f8s");

        private string Save(Fallen8 fallen8, int partitions)
        {
            var tx = new SaveTransaction { Path = SavePath, SavePartitions = partitions };
            var info = fallen8.EnqueueTransaction(tx);
            info.WaitUntilFinished();
            Assert.AreEqual(TransactionState.Finished, info.TransactionState, "The save should finish. " + info.Error);
            Assert.IsFalse(String.IsNullOrEmpty(tx.ActualPath), "The save should report a path.");
            return tx.ActualPath;
        }

        private Fallen8 Load(string path)
        {
            var loaded = new Fallen8(_loggerFactory);
            var tx = new LoadTransaction { Path = path };
            var info = loaded.EnqueueTransaction(tx);
            info.WaitUntilFinished();
            Assert.AreEqual(TransactionState.Finished, info.TransactionState, "The load should finish. " + info.Error);
            return loaded;
        }

        /// <summary>
        /// Asserts that the loaded graph is element-for-element identical to the source: same vertex
        /// and edge counts, and for every id the same label, the same properties (key, value AND
        /// runtime type), and for edges the same EdgePropertyId and endpoint ids. This is the
        /// save -> load "produces an identical graph" contract.
        /// </summary>
        private static void AssertRoundTrips(Fallen8 source, Fallen8 loaded)
        {
            Assert.AreEqual(source.VertexCount, loaded.VertexCount, "Vertex count must round-trip.");
            Assert.AreEqual(source.EdgeCount, loaded.EdgeCount, "Edge count must round-trip.");

            foreach (var sourceVertex in source.GetAllVertices())
            {
                VertexModel loadedVertex;
                Assert.IsTrue(loaded.TryGetVertex(out loadedVertex, sourceVertex.Id),
                    "Vertex " + sourceVertex.Id + " must exist after load.");
                Assert.AreEqual(sourceVertex.Label, loadedVertex.Label, "Vertex label must round-trip (id " + sourceVertex.Id + ").");
                AssertPropertiesEqual(sourceVertex, loadedVertex, "vertex " + sourceVertex.Id);

                // Outgoing adjacency degree per property id must match.
                var expectedOut = OutDegreeByProperty(sourceVertex);
                var actualOut = OutDegreeByProperty(loadedVertex);
                CollectionAssert.AreEquivalent(expectedOut, actualOut,
                    "Vertex " + sourceVertex.Id + " outgoing adjacency must round-trip.");
            }

            foreach (var sourceEdge in source.GetAllEdges())
            {
                EdgeModel loadedEdge;
                Assert.IsTrue(loaded.TryGetEdge(out loadedEdge, sourceEdge.Id),
                    "Edge " + sourceEdge.Id + " must exist after load.");
                Assert.AreEqual(sourceEdge.Label, loadedEdge.Label, "Edge label must round-trip (id " + sourceEdge.Id + ").");
                Assert.AreEqual(sourceEdge.EdgePropertyId, loadedEdge.EdgePropertyId,
                    "EdgePropertyId must round-trip exactly (id " + sourceEdge.Id + ").");
                Assert.AreEqual(sourceEdge.SourceVertex.Id, loadedEdge.SourceVertex.Id, "Edge source must round-trip.");
                Assert.AreEqual(sourceEdge.TargetVertex.Id, loadedEdge.TargetVertex.Id, "Edge target must round-trip.");
                AssertPropertiesEqual(sourceEdge, loadedEdge, "edge " + sourceEdge.Id);
            }
        }

        private static List<string> OutDegreeByProperty(VertexModel vertex)
        {
            var result = new List<string>();
            if (vertex.OutEdges != null)
            {
                foreach (var kv in vertex.OutEdges)
                {
                    result.Add(kv.Key + "=" + kv.Value.Count);
                }
            }
            return result;
        }

        private static void AssertPropertiesEqual(AGraphElementModel expected, AGraphElementModel actual, string what)
        {
            var expectedProps = expected.GetAllProperties();
            var actualProps = actual.GetAllProperties();

            Assert.AreEqual(expectedProps.Count, actualProps.Count, "Property count must round-trip (" + what + ").");
            foreach (var kv in expectedProps)
            {
                Assert.IsTrue(actualProps.ContainsKey(kv.Key), "Property \"" + kv.Key + "\" must be present (" + what + ").");
                var actualValue = actualProps[kv.Key];

                if (kv.Value == null)
                {
                    Assert.IsNull(actualValue, "Null property \"" + kv.Key + "\" must round-trip as null (" + what + ").");
                    continue;
                }

                Assert.IsNotNull(actualValue, "Property \"" + kv.Key + "\" must not become null (" + what + ").");
                Assert.AreEqual(kv.Value.GetType(), actualValue.GetType(),
                    "Property \"" + kv.Key + "\" runtime type must round-trip (" + what + ").");
                Assert.AreEqual(kv.Value, actualValue,
                    "Property \"" + kv.Key + "\" value must round-trip (" + what + ").");
            }
        }

        private string FindBunchSidecar(string actualPath)
        {
            var dir = Path.GetDirectoryName(actualPath);
            return Directory.GetFiles(dir)
                .Where(f => Path.GetFileName(f).Contains(Constants.GraphElementsSaveString))
                .Single(f => !f.Contains(Constants.TempSaveSuffix));
        }

        private static IDictionary<string, object> RTreeParameters()
        {
            return new Dictionary<string, object>
            {
                ["IMetric"] = new EuclidianMetric(),
                ["MinCount"] = 2,
                ["MaxCount"] = 5,
                ["Space"] = new List<IDimension> { new RealDimension(), new RealDimension() }
            };
        }

        #endregion

        #region P2/N1 - UTF-8 + tokenized values + all types + Unicode round-trip

        [TestMethod]
        public void Encoding_RoundTrips_AllPropertyTypes_UnicodeLabels_AndEdges()
        {
            var guid = Guid.NewGuid();
            var when = new DateTime(2026, 7, 13, 10, 30, 15, DateTimeKind.Utc);

            var source = new Fallen8(_loggerFactory);

            // Labels and property KEYS are Unicode (BMP + surrogate pair) and travel the tokenized
            // string path; the property VALUES exercise every commonly used runtime type, plus the
            // empty string and a stored null.
            var verticesTx = new CreateVerticesTransaction();
            verticesTx.AddVertex(100u, "person", new Dictionary<string, object>
            {
                { "int_small", 7 },
                { "int_neg", -98765 },
                { "int_big", 5_000_000 },       // outside the small-int de-box cache
                { "long", 9_000_000_000L },
                { "double", 3.14159265358979 },
                { "float", 2.5f },
                { "bool_true", true },
                { "bool_false", false },
                { "str_ascii", "hello" },
                { "str_bmp", "héllo wörld — 日本語" },
                { "str_surrogate", "party 😀🎉 time" },
                { "str_empty", "" },
                { "guid", guid },
                { "date_utc", when },
                { "byte", (byte)200 },
                { "short", (short)-31000 },
                { "null_value", null },
            });
            // Second vertex with a Unicode label, so the label token table carries non-ASCII too.
            verticesTx.AddVertex(100u, "人物", new Dictionary<string, object> { { "名前", "アリス" } });
            verticesTx.AddVertex(100u, "", null);   // empty label, no properties
            source.EnqueueTransaction(verticesTx).WaitUntilFinished();
            var v = source.GetAllVertices().OrderBy(x => x.Id).ToArray();

            var edgesTx = new CreateEdgesTransaction();
            edgesTx.AddEdge(v[0].Id, "knows 日本語", v[1].Id, 100u, "REL 😀",
                new Dictionary<string, object> { { "since", 2020 }, { "weight", 1.5 } });
            edgesTx.AddEdge(v[1].Id, "", v[2].Id, 100u, null);   // empty EdgePropertyId, null label
            source.EnqueueTransaction(edgesTx).WaitUntilFinished();

            // Round-trip through a single partition and through several partitions.
            foreach (var partitions in new[] { 1, 4 })
            {
                var path = Save(source, partitions);
                var loaded = Load(path);
                AssertRoundTrips(source, loaded);
                loaded.Dispose();
            }
        }

        [TestMethod]
        public void Encoding_RoundTrips_AcrossManyPartitions()
        {
            // Enough elements that a multi-partition save spreads them over several bunch files, each
            // with its OWN token table - the reload must still reconstruct every element exactly.
            var source = new Fallen8(_loggerFactory);
            var verticesTx = new CreateVerticesTransaction();
            for (int i = 0; i < 200; i++)
            {
                verticesTx.AddVertex(1u, i % 2 == 0 ? "even" : "odd",
                    new Dictionary<string, object> { { "seq", i }, { "tag", "node-" + (i % 7) } });
            }
            source.EnqueueTransaction(verticesTx).WaitUntilFinished();
            var v = source.GetAllVertices().OrderBy(x => x.Id).ToArray();

            var edgesTx = new CreateEdgesTransaction();
            for (int i = 0; i < 199; i++)
            {
                edgesTx.AddEdge(v[i].Id, "next", v[i + 1].Id, 1u, "link");
            }
            source.EnqueueTransaction(edgesTx).WaitUntilFinished();

            var path = Save(source, partitions: 8);
            var loaded = Load(path);
            AssertRoundTrips(source, loaded);
        }

        [TestMethod]
        public void N1_PropertyByteOrder_IsIndependentOfInsertionOrder_RoundTrips()
        {
            // Insert keys in a deliberately non-sorted order. N1 emits the raw store in ORDINAL key
            // order (not insertion/hash order); the loader rebuilds and re-sorts, so the exact same
            // properties come back regardless of the stored byte order.
            var source = new Fallen8(_loggerFactory);
            var tx = new CreateVerticesTransaction();
            var props = new Dictionary<string, object>();
            foreach (var key in new[] { "zeta", "alpha", "mu", "beta", "gamma", "aa", "zz" })
            {
                props[key] = key.ToUpperInvariant();
            }
            tx.AddVertex(1u, "ordered", props);
            source.EnqueueTransaction(tx).WaitUntilFinished();

            var loaded = Load(Save(source, partitions: 1));
            AssertRoundTrips(source, loaded);

            VertexModel reloaded;
            Assert.IsTrue(loaded.TryGetVertex(out reloaded, source.GetAllVertices().Single().Id));
            Assert.AreEqual(7, reloaded.GetPropertyCount());
            string alpha;
            Assert.IsTrue(reloaded.TryGetProperty(out alpha, "alpha"));
            Assert.AreEqual("ALPHA", alpha);
        }

        #endregion

        #region P2/M5 - tokenization dedupes shared strings and reloads exactly

        [TestMethod]
        public void Tokenization_ManyEdgesSharingEdgePropertyIdAndLabel_DedupeAndReloadExactly()
        {
            const int edgeCount = 300;
            // Long, distinctive strings so that storing them once (tokenized) vs. once-per-edge
            // (untokenized) is a large, measurable difference.
            var sharedEdgePropertyId = "shared-edge-property-id-" + new string('x', 80);
            var sharedLabel = "shared-edge-label-" + new string('y', 80);

            var source = new Fallen8(_loggerFactory);
            var verticesTx = new CreateVerticesTransaction();
            verticesTx.AddVertex(1u, "hub", null);          // id 0 - the shared source
            for (int i = 0; i < edgeCount; i++)
            {
                verticesTx.AddVertex(1u, "leaf", null);
            }
            source.EnqueueTransaction(verticesTx).WaitUntilFinished();
            var v = source.GetAllVertices().OrderBy(x => x.Id).ToArray();

            var edgesTx = new CreateEdgesTransaction();
            for (int i = 0; i < edgeCount; i++)
            {
                edgesTx.AddEdge(v[0].Id, sharedEdgePropertyId, v[i + 1].Id, 1u, sharedLabel);
            }
            source.EnqueueTransaction(edgesTx).WaitUntilFinished();

            // Save into a SINGLE partition so every edge shares one bunch file (one token table): the
            // shared id/label are then written exactly once, no matter how many edges reference them.
            var path = Save(source, partitions: 1);

            // Correctness first: every edge reloads with the exact shared id + label.
            var loaded = Load(path);
            AssertRoundTrips(source, loaded);
            Assert.AreEqual(edgeCount, loaded.EdgeCount);
            foreach (var e in loaded.GetAllEdges())
            {
                Assert.AreEqual(sharedEdgePropertyId, e.EdgePropertyId, "The shared EdgePropertyId must reload verbatim.");
                Assert.AreEqual(sharedLabel, e.Label, "The shared label must reload verbatim.");
            }

            // Dedup: had the shared id + label been stored once per edge (as before P2/M5), the bunch
            // would contain at least edgeCount * (len(id) + len(label)) bytes for those strings alone.
            // Tokenized, each is stored once, so the whole bunch is far below that lower bound.
            var bunchSize = new FileInfo(FindBunchSidecar(path)).Length;
            long untokenizedLowerBound = (long)edgeCount * (sharedEdgePropertyId.Length + sharedLabel.Length);
            Assert.IsTrue(bunchSize < untokenizedLowerBound / 2,
                String.Format("The bunch ({0} bytes) must be far smaller than the untokenized lower bound ({1} bytes), proving the shared id/label were deduped into the token table.",
                    bunchSize, untokenizedLowerBound));
        }

        [TestMethod]
        public void Tokenization_RepeatedPropertyValues_DedupeAndReloadExactly()
        {
            const int vertexCount = 300;
            var sharedValue = "a-repeated-property-value-" + new string('z', 80);

            var source = new Fallen8(_loggerFactory);
            var tx = new CreateVerticesTransaction();
            for (int i = 0; i < vertexCount; i++)
            {
                tx.AddVertex(1u, "node", new Dictionary<string, object> { { "shared", sharedValue }, { "i", i } });
            }
            source.EnqueueTransaction(tx).WaitUntilFinished();

            var path = Save(source, partitions: 1);
            var loaded = Load(path);
            AssertRoundTrips(source, loaded);
            foreach (var vertex in loaded.GetAllVertices())
            {
                string got;
                Assert.IsTrue(vertex.TryGetProperty(out got, "shared"));
                Assert.AreEqual(sharedValue, got, "The repeated string VALUE must reload verbatim.");
            }

            var bunchSize = new FileInfo(FindBunchSidecar(path)).Length;
            long untokenizedLowerBound = (long)vertexCount * sharedValue.Length;
            Assert.IsTrue(bunchSize < untokenizedLowerBound / 2,
                String.Format("The bunch ({0} bytes) must be far below the untokenized lower bound ({1} bytes): a string property VALUE is now tokenized, not copied per element.",
                    bunchSize, untokenizedLowerBound));
        }

        #endregion

        #region C9 - spatial (R-Tree) index survives a checkpoint and is queryable

        [TestMethod]
        public void Spatial_PointAndMbrEntries_RoundTripAndAreQueryableOnReload()
        {
            var source = new Fallen8(_loggerFactory);
            var verticesTx = new CreateVerticesTransaction();
            for (int i = 0; i < 5; i++)
            {
                verticesTx.AddVertex(1u, "geo", new Dictionary<string, object> { { "seq", i } });
            }
            source.EnqueueTransaction(verticesTx).WaitUntilFinished();
            var v = source.GetAllVertices().OrderBy(x => x.Id).ToArray();

            IIndex spatial;
            Assert.IsTrue(source.IndexFactory.TryCreateIndex(out spatial, "geoIdx", "SpatialIndex", RTreeParameters()));
            var rtree = (ISpatialIndex)spatial;

            // A mix of point entries and an MBR (rectangle) entry, so BOTH container kinds are
            // serialized and rebuilt.
            rtree.AddOrUpdate(new Point(1.0f, 1.0f), v[0]);
            rtree.AddOrUpdate(new Point(2.0f, 2.0f), v[1]);
            rtree.AddOrUpdate(new Point(3.0f, 3.0f), v[2]);
            rtree.AddOrUpdate(new Point(10.0f, 10.0f), v[3]);
            rtree.AddOrUpdate(new Rectangle(new Point(1.5f, 1.5f), new Point(2.5f, 2.5f)), v[4]);
            Assert.AreEqual(5, spatial.CountOfValues());

            var path = Save(source, partitions: 1);

            // The spatial index writes a real sidecar now (C9), not a skip.
            Assert.IsTrue(File.Exists(path + Constants.IndexSaveString + "geoIdx"),
                "The spatial index must persist its sidecar.");

            var loaded = Load(path);
            AssertRoundTrips(source, loaded);

            IIndex reloadedIndex;
            Assert.IsTrue(loaded.IndexFactory.TryGetIndex(out reloadedIndex, "geoIdx"),
                "The spatial index must be present after load (not absent-and-recreated).");
            var reloaded = (ISpatialIndex)reloadedIndex;
            Assert.AreEqual(5, reloadedIndex.CountOfValues(), "Every spatial entry must survive the checkpoint.");

            // Region query on the RELOADED index - the exact operation the pre-C9 skip-and-recreate
            // could not perform (a reloaded spatial index used to NPE). [0.5,0.5]-[2.7,2.7] covers the
            // points (1,1) and (2,2) and the rectangle (1.5,1.5)-(2.5,2.5), but not (3,3) or (10,10).
            ImmutableList<AGraphElementModel> regionHits;
            Assert.IsTrue(reloaded.SearchRegion(out regionHits, new MBR(new[] { 0.5f, 0.5f }, new[] { 2.7f, 2.7f })));
            var regionIds = regionHits.Select(e => e.Id).OrderBy(id => id).ToArray();
            CollectionAssert.AreEqual(new[] { v[0].Id, v[1].Id, v[4].Id }, regionIds,
                "The region query on the reloaded index must return exactly the entries inside the region.");

            // Exact point lookup on the reloaded index.
            ImmutableList<AGraphElementModel> pointHits;
            Assert.IsTrue(reloaded.SearchPoint(out pointHits, new Point(3.0f, 3.0f)));
            Assert.IsTrue(pointHits.Any(e => e.Id == v[2].Id), "The exact point (3,3) must be found on the reloaded index.");

            // Distance query on the reloaded index resolves a reloaded element's own container.
            VertexModel reloadedVertex;
            Assert.IsTrue(loaded.TryGetVertex(out reloadedVertex, v[0].Id));
            ImmutableList<AGraphElementModel> distanceHits;
            Assert.IsTrue(reloaded.SearchDistance(out distanceHits, 2.0f, reloadedVertex),
                "A distance query on the reloaded index must run and find near neighbours.");
        }

        [TestMethod]
        public void Spatial_EmptyIndex_RoundTripsAndStaysQueryable()
        {
            // An R-Tree with no entries must still persist its CONFIG and reload as a functional,
            // queryable (empty) index rather than a half-initialised, NPE-prone one.
            var source = new Fallen8(_loggerFactory);
            var verticesTx = new CreateVerticesTransaction();
            verticesTx.AddVertex(1u, "geo", null);
            source.EnqueueTransaction(verticesTx).WaitUntilFinished();

            IIndex spatial;
            Assert.IsTrue(source.IndexFactory.TryCreateIndex(out spatial, "emptyGeo", "SpatialIndex", RTreeParameters()));
            Assert.AreEqual(0, spatial.CountOfValues());

            var loaded = Load(Save(source, partitions: 1));

            IIndex reloadedIndex;
            Assert.IsTrue(loaded.IndexFactory.TryGetIndex(out reloadedIndex, "emptyGeo"),
                "An empty spatial index must still survive the checkpoint.");
            Assert.AreEqual(0, reloadedIndex.CountOfValues());

            // A query against the reloaded empty index must simply return nothing, not throw.
            ImmutableList<AGraphElementModel> hits;
            Assert.IsFalse(((ISpatialIndex)reloadedIndex).SearchRegion(out hits, new MBR(new[] { 0f, 0f }, new[] { 5f, 5f })),
                "A query on a reloaded empty spatial index must run and find nothing.");
        }

        #endregion
    }
}
