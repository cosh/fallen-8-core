// MIT License
//
// SupernodeAdjacencyBuildTest.cs
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
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Tests for the "supernode-adjacency-build" feature: per-edge adjacency append is now amortised
    /// O(1), so building or loading a high-degree hub is O(d), not O(d²). These pin the two composing
    /// changes' observable behaviour — batch-group wiring (k appends to one vertex/direction collapse to
    /// one publish) and amortised ×2 capacity (spare-slot append) — plus a machine-independent
    /// linear-scaling guard that would fail on the former O(d²) whole-group-copy-per-edge build. The
    /// lock-free-reader race for the shared, growing backing array lives in
    /// <c>AdjacencyConcurrencyTest.ConcurrentReaders_DuringMonotonicHubGrowth_*</c>; the wall-clock/
    /// allocation numbers are captured by the opt-in <c>SupernodeAdjacencyBuildBenchmark</c>.
    /// </summary>
    [TestClass]
    public class SupernodeAdjacencyBuildTest
    {
        private const string EdgeKey = "A";
        private const string EdgeKeyB = "B";

        private ILoggerFactory _loggerFactory;
        private string _tempDir;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
            _tempDir = Path.Combine(Path.GetTempPath(), "f8_supernode_" + Guid.NewGuid().ToString("N"));
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
                // best-effort
            }
        }

        private static VertexModel[] CreateVertices(Fallen8 fallen8, int count)
        {
            var tx = new CreateVerticesTransaction();
            for (var i = 0; i < count; i++)
            {
                tx.AddVertex(1u, "v");
            }
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.GetCreatedVertices().ToArray();
        }

        // ---- Step 1: batch-group wiring -----------------------------------------------------------

        /// <summary>
        /// k edges landing on one hub under one key in a SINGLE transaction must produce a group with
        /// exactly those k edges, in the order they were added, each rooted at the hub — and each leaf
        /// must see exactly one mirrored in-edge. This is the batch-group wiring: one publish per
        /// vertex/direction instead of k.
        /// </summary>
        [TestMethod]
        public void BatchCreate_ManyEdgesToOneHubUnderOneKey_BuildsTheGroupInOrder()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            const int degree = 500;
            var v = CreateVertices(fallen8, degree + 1);
            var hub = v[0];

            var edgeTx = new CreateEdgesTransaction();
            for (var i = 1; i <= degree; i++)
            {
                edgeTx.AddEdge(hub.Id, EdgeKey, v[i].Id, 1u, "e");
            }
            fallen8.EnqueueTransaction(edgeTx).WaitUntilFinished();

            Assert.AreEqual((uint)degree, hub.GetOutDegree(), "The hub must carry every batched out-edge.");
            Assert.IsTrue(hub.TryGetOutEdge(out var outEdges, EdgeKey));
            Assert.AreEqual(degree, outEdges.Count);

            // Encounter order preserved: the i-th out-edge targets the i-th leaf.
            for (var i = 0; i < degree; i++)
            {
                Assert.AreSame(hub, outEdges[i].SourceVertex);
                Assert.AreEqual(v[i + 1].Id, outEdges[i].TargetVertex.Id, "Batch append must preserve encounter order.");
            }

            // Every leaf has exactly the one mirrored in-edge from the hub.
            for (var i = 1; i <= degree; i++)
            {
                Assert.AreEqual(1u, v[i].GetInDegree(), "Each leaf must have exactly one in-edge from the hub.");
                Assert.IsTrue(v[i].TryGetInEdge(out var inEdges, EdgeKey));
                Assert.AreEqual(1, inEdges.Count);
                Assert.AreSame(hub, inEdges[0].SourceVertex);
            }
        }

        /// <summary>
        /// A vertex receiving edges under TWO keys in one transaction must end up in the multi-group
        /// (map) shape with both groups correct — chaining the per-key builds and publishing once.
        /// </summary>
        [TestMethod]
        public void BatchCreate_EdgesUnderTwoKeysInOneTransaction_PromotesToMultiGroup()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var v = CreateVertices(fallen8, 7); // hub + 6 leaves
            var hub = v[0];

            var edgeTx = new CreateEdgesTransaction();
            edgeTx.AddEdge(hub.Id, EdgeKey, v[1].Id, 1u, "e");
            edgeTx.AddEdge(hub.Id, EdgeKeyB, v[2].Id, 1u, "e");
            edgeTx.AddEdge(hub.Id, EdgeKey, v[3].Id, 1u, "e");
            edgeTx.AddEdge(hub.Id, EdgeKeyB, v[4].Id, 1u, "e");
            edgeTx.AddEdge(hub.Id, EdgeKey, v[5].Id, 1u, "e");
            fallen8.EnqueueTransaction(edgeTx).WaitUntilFinished();

            Assert.AreEqual(5u, hub.GetOutDegree());

            Assert.IsTrue(hub.TryGetOutEdge(out var groupA, EdgeKey));
            CollectionAssert.AreEqual(new[] { v[1].Id, v[3].Id, v[5].Id }, groupA.Select(e => e.TargetVertex.Id).ToArray(),
                "The A group must hold its three edges in encounter order.");

            Assert.IsTrue(hub.TryGetOutEdge(out var groupB, EdgeKeyB));
            CollectionAssert.AreEqual(new[] { v[2].Id, v[4].Id }, groupB.Select(e => e.TargetVertex.Id).ToArray(),
                "The B group must hold its two edges in encounter order.");

            var keys = hub.GetOutgoingEdgeIds();
            CollectionAssert.AreEquivalent(new[] { EdgeKey, EdgeKeyB }, keys, "Both groups must be advertised.");
        }

        // ---- Step 2: amortised capacity (single-edge appends) -------------------------------------

        /// <summary>
        /// The single-edge path (one edge per transaction) must build the same correct, ordered group as
        /// the batch path — the spare-capacity append must not drop, reorder, or leak a slot as the group
        /// grows through several ×2 reallocations.
        /// </summary>
        [TestMethod]
        public void SingleEdgeAppends_GrowingAHub_PreserveEveryEdgeInOrder()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            const int degree = 300;
            var v = CreateVertices(fallen8, degree + 1);
            var hub = v[0];

            for (var i = 1; i <= degree; i++)
            {
                var edgeTx = new CreateEdgesTransaction();
                edgeTx.AddEdge(hub.Id, EdgeKey, v[i].Id, 1u, "e");
                fallen8.EnqueueTransaction(edgeTx).WaitUntilFinished();

                // The out-degree is exact after every single append (no spare slot leaks into the count).
                Assert.AreEqual((uint)i, hub.GetOutDegree(), "Out-degree must equal the number of edges appended so far.");
            }

            Assert.IsTrue(hub.TryGetOutEdge(out var outEdges, EdgeKey));
            Assert.AreEqual(degree, outEdges.Count);
            for (var i = 0; i < degree; i++)
            {
                Assert.IsNotNull(outEdges[i], "No slot may be null — a spare capacity slot must never be exposed.");
                Assert.AreEqual(v[i + 1].Id, outEdges[i].TargetVertex.Id, "Single-edge appends must preserve order.");
            }
        }

        /// <summary>
        /// Machine-independent linear-scaling guard. Building a degree-2d hub must not allocate ~4× the
        /// bytes of a degree-d hub — that 4× is the signature of the former O(d²) whole-group-copy-per-
        /// edge build (Σ 8·i ≈ 4·d² bytes copied). Amortised capacity makes the build O(d), so doubling
        /// the degree roughly doubles the bytes. The threshold is deliberately loose (&lt; 3×) so it is
        /// a regression tripwire, not a tight benchmark.
        /// </summary>
        [TestMethod]
        public void HubBuild_AllocatedBytes_ScaleLinearlyNotQuadratically()
        {
            const int d = 4000;

            // Warm the paths once so first-touch JIT/allocation is not attributed to a measured build.
            MeasureBatchBuildBytes(1000);

            var bytesD = MeasureBatchBuildBytes(d);
            var bytes2D = MeasureBatchBuildBytes(2 * d);

            Assert.IsTrue(bytesD > 0, "The degree-d build must allocate a measurable amount.");
            double ratio = (double)bytes2D / bytesD;
            Assert.IsTrue(ratio < 3.0,
                $"Doubling the degree allocated {ratio:0.00}× the bytes (d={d}: {bytesD}, 2d: {bytes2D}); " +
                "a value near 4× indicates the O(d²) whole-group-copy build has regressed.");
        }

        /// <summary>
        /// Builds a degree-<paramref name="degree"/> hub with one batch <see cref="CreateEdgesTransaction"/>
        /// and returns the process-wide bytes allocated by that wiring. <c>GetTotalAllocatedBytes</c> (not
        /// the thread-local counter) is required because the adjacency build runs on the TransactionManager
        /// writer thread, not the calling thread.
        /// </summary>
        private long MeasureBatchBuildBytes(int degree)
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var v = CreateVertices(fallen8, degree + 1);
            var hub = v[0];

            var edgeTx = new CreateEdgesTransaction();
            for (var i = 1; i <= degree; i++)
            {
                edgeTx.AddEdge(hub.Id, EdgeKey, v[i].Id, 1u, "e");
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetTotalAllocatedBytes(true);
            fallen8.EnqueueTransaction(edgeTx).WaitUntilFinished();
            var after = GC.GetTotalAllocatedBytes(true);

            Assert.AreEqual((uint)degree, hub.GetOutDegree(), "The measured build must actually wire the hub.");
            fallen8.Dispose();
            return after - before;
        }

        // ---- Load round-trip ----------------------------------------------------------------------

        /// <summary>
        /// A high-degree hub must save and reload with the exact same in/out degree and edge SET (same
        /// endpoints) — exercising the batched deferred-edge load fix-up (which turned the hub's O(d²)
        /// per-edge rehydration into O(d)). The SET, not the order, is the guarantee: a multi-partition
        /// load reconstructs cross-bunch deferred edges in parallel-bunch / ConcurrentDictionary
        /// enumeration order, which is not deterministic across environments (this is why the assertion
        /// is order-insensitive).
        /// </summary>
        [TestMethod]
        public void SaveLoad_SupernodeRoundTrips_WithIdenticalAdjacency()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            const int degree = 800;
            var v = CreateVertices(fallen8, degree + 1);
            var hub = v[0];

            // The hub gets out-edges to every leaf AND in-edges from every leaf, all under one key.
            var edgeTx = new CreateEdgesTransaction();
            for (var i = 1; i <= degree; i++)
            {
                edgeTx.AddEdge(hub.Id, EdgeKey, v[i].Id, 1u, "e");   // hub -> leaf
                edgeTx.AddEdge(v[i].Id, EdgeKey, hub.Id, 1u, "e");   // leaf -> hub
            }
            fallen8.EnqueueTransaction(edgeTx).WaitUntilFinished();

            Assert.AreEqual((uint)degree, hub.GetOutDegree());
            Assert.AreEqual((uint)degree, hub.GetInDegree());

            var expectedOut = FarEndpointIds(hub, outgoing: true);
            var expectedIn = FarEndpointIds(hub, outgoing: false);

            // Save with several partitions so a large fraction of the hub's edges are deferred cross-bunch
            // fix-ups on load (the O(d²) path this feature batches).
            var path = Save(fallen8, partitions: 4);
            var loaded = Load(path);

            Assert.IsTrue(loaded.TryGetVertex(out var reloadedHub, hub.Id), "The hub must reload.");
            Assert.AreEqual((uint)degree, reloadedHub.GetOutDegree(), "Reloaded out-degree must match.");
            Assert.AreEqual((uint)degree, reloadedHub.GetInDegree(), "Reloaded in-degree must match.");

            // The edge SET must match; the reloaded order of cross-bunch deferred edges is not
            // deterministic across environments, so the comparison is order-insensitive.
            CollectionAssert.AreEquivalent(expectedOut, FarEndpointIds(reloadedHub, outgoing: true),
                "The reloaded hub's out-edge target set must match the source.");
            CollectionAssert.AreEquivalent(expectedIn, FarEndpointIds(reloadedHub, outgoing: false),
                "The reloaded hub's in-edge source set must match the source.");

            loaded.Dispose();
            fallen8.Dispose();
        }

        private static int[] FarEndpointIds(VertexModel vertex, bool outgoing)
        {
            IReadOnlyList<EdgeModel> edges;
            var found = outgoing
                ? vertex.TryGetOutEdge(out edges, EdgeKey)
                : vertex.TryGetInEdge(out edges, EdgeKey);
            Assert.IsTrue(found, "The hub must have the expected group.");
            // The "far" endpoint (the leaf) in both directions: an out-edge's target, an in-edge's source.
            return edges.Select(e => outgoing ? e.TargetVertex.Id : e.SourceVertex.Id).ToArray();
        }

        // ---- Empty-but-present group (removal edge case) ------------------------------------------

        /// <summary>
        /// Removing the last edge of a group must leave a degree-0 group and never leak a spare slot: the
        /// count-aware removal produces a compacted (empty) array, and the count-bounded accessors treat
        /// it as an empty slice.
        /// </summary>
        [TestMethod]
        public void RemovingTheLastEdge_LeavesADegreeZeroGroup_NotASpareSlot()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var v = CreateVertices(fallen8, 2);
            var hub = v[0];

            // Append two then remove both, so the backing array carries spare capacity when the count
            // hits zero (the array is not shrunk).
            var edgeTx = new CreateEdgesTransaction();
            edgeTx.AddEdge(hub.Id, EdgeKey, v[1].Id, 1u, "e");
            fallen8.EnqueueTransaction(edgeTx).WaitUntilFinished();
            var edge = edgeTx.GetCreatedEdges()[0];

            Assert.AreEqual(1u, hub.GetOutDegree());

            fallen8.EnqueueTransaction(new RemoveGraphElementTransaction { GraphElementId = edge.Id }).WaitUntilFinished();

            Assert.AreEqual(0u, hub.GetOutDegree(), "The out-degree must be zero after removing the only edge.");
            if (hub.TryGetOutEdge(out var edges, EdgeKey))
            {
                Assert.AreEqual(0, edges.Count, "An emptied group must present as an empty slice, never a spare slot.");
            }
        }

        #region persistence helpers

        private string SavePath => Path.Combine(_tempDir, "supernode.f8s");

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

        #endregion
    }
}
