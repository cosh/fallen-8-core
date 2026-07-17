// MIT License
//
// EnginePerformanceTest.cs
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
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.App.Controllers.Cache;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Index.Range;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Plugin;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Behaviour regression tests for the "engine-performance" feature:
    ///  - P1: the compiled path-traverser cache is process-wide, so a second request/controller
    ///        reuses the traverser the first one compiled instead of recompiling it.
    ///  - P3: element removal maintains VertexCount/EdgeCount incrementally, and the counts stay
    ///        exactly correct for a committed cascade removal, a self-loop, and a rolled-back removal.
    ///  - P4: the RangeIndex's cached sorted-key structure answers ordered range queries correctly
    ///        across value-only updates, new/removed keys, and Greater/Lower bracketing.
    ///  - P5: the memoized PluginFactory discovery still resolves every plugin by name and enumerates
    ///        every index plugin, and an Assimilate-style invalidation rebuilds a fresh name map (M1).
    ///  - P6: bounded BLS reconstruction caps the result to maxResults while preserving the first-k
    ///        path order of the unbounded run.
    /// </summary>
    [TestClass]
    public class EnginePerformanceTest
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
                tx.AddVertex(1u, "test");
            }
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.GetCreatedVertices().ToArray();
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

        #region P1 - the path-compile cache is process-wide (reused across controller instances)

        [TestMethod]
        public void PathCompileCache_IsSharedAcrossControllerInstances_CompilesOnce()
        {
            // Arrange - a tiny graph and two SEPARATE controllers, standing in for two requests
            // (ASP.NET Core creates a controller per request). A distinctive spec avoids colliding
            // with any other test's cache key.
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = CreateVertices(fallen8, 2);
            var edgeTx = new CreateEdgesTransaction();
            edgeTx.AddEdge(vertices[0].Id, "e", vertices[1].Id, 1u);
            fallen8.EnqueueTransaction(edgeTx).WaitUntilFinished();

            var controller1 = new GraphController(new UnitTestLogger<GraphController>(), fallen8);
            var controller2 = new GraphController(new UnitTestLogger<GraphController>(), fallen8);

            var spec1 = new PathSpecification { PathAlgorithmName = "BLS", MaxDepth = 6, MaxResults = 54321 };
            var spec2 = new PathSpecification { PathAlgorithmName = "BLS", MaxDepth = 6, MaxResults = 54321 };
            Assert.AreEqual(spec1, spec2, "Sanity: the two specs must be value-equal (the cache key).");

            // A separate cache handle: because the backing store is process-wide, this instance sees
            // exactly the entries the controllers' own caches populate. On the pre-fix per-instance
            // cache this probe would stay empty no matter what the controllers did.
            var probe = new GeneratedCodeCache();

            // Act - first request compiles the traverser and (with the fix) publishes it process-wide.
            _ = controller1.CalculateShortestPath(vertices[0].Id, vertices[1].Id, spec1).Result;
            // Probe via the new (Filter, Cost)-keyed accessor (feature codegen-cache-keying).
            Assert.IsTrue(probe.TryGetTraverser(spec1, out var firstTraverser),
                "After the first request the compiled traverser must be visible through a DIFFERENT " +
                "cache handle - i.e. the cache is process-wide, not per controller instance (P1).");
            Assert.IsNotNull(firstTraverser, "A traverser must have been compiled and cached.");

            // Second request with a value-equal spec on a fresh controller must HIT the shared cache.
            _ = controller2.CalculateShortestPath(vertices[0].Id, vertices[1].Id, spec2).Result;
            Assert.IsTrue(probe.TryGetTraverser(spec2, out var secondTraverser),
                "The value-equal spec must resolve to the same cache entry.");

            // Assert - the SAME traverser instance is still cached. Had the second controller missed
            // the cache (the P1 bug), it would have recompiled and overwritten the entry with a new
            // instance, so reference equality here proves it was compiled once and reused.
            Assert.AreSame(firstTraverser, secondTraverser,
                "The second request must reuse the traverser compiled by the first (compiled once).");
        }

        #endregion

        #region P3 - removal maintains the counts incrementally and exactly

        [TestMethod]
        public void RemoveVertex_Committed_DecrementsVertexAndCascadedEdgeCounts()
        {
            // Arrange - a hub with two outgoing and one incoming edge (3 distinct incident edges).
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = CreateVertices(fallen8, 4);
            int hub = vertices[0].Id;
            int n1 = vertices[1].Id;
            int n2 = vertices[2].Id;
            int n3 = vertices[3].Id;

            var edgeTx = new CreateEdgesTransaction();
            edgeTx.AddEdge(hub, "e", n1, 1u);
            edgeTx.AddEdge(hub, "e", n2, 1u);
            edgeTx.AddEdge(n3, "e", hub, 1u);
            fallen8.EnqueueTransaction(edgeTx).WaitUntilFinished();

            Assert.AreEqual(4, fallen8.VertexCount, "Four vertices before the removal.");
            Assert.AreEqual(3, fallen8.EdgeCount, "Three edges before the removal.");

            // Act - remove the hub; the removal cascades to all three incident edges.
            var removeTx = new RemoveGraphElementTransaction { GraphElementId = hub };
            fallen8.EnqueueTransaction(removeTx).WaitUntilFinished();

            // Assert - counts decremented incrementally and exactly.
            Assert.AreEqual(TransactionState.Finished, fallen8.GetTransactionState(removeTx.TransactionId),
                "A valid removal must commit.");
            Assert.AreEqual(3, fallen8.VertexCount, "Exactly one vertex (the hub) must be gone.");
            Assert.AreEqual(0, fallen8.EdgeCount, "All three incident edges must be gone.");
        }

        [TestMethod]
        public void RemoveVertex_WithSelfLoop_CountsTheSelfLoopEdgeExactlyOnce()
        {
            // Arrange - a single vertex with a self-loop edge (present in BOTH OutEdges and InEdges).
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = CreateVertices(fallen8, 1);
            int v = vertices[0].Id;

            var edgeTx = new CreateEdgesTransaction();
            edgeTx.AddEdge(v, "loop", v, 1u);
            fallen8.EnqueueTransaction(edgeTx).WaitUntilFinished();

            Assert.AreEqual(1, fallen8.VertexCount, "One vertex before the removal.");
            Assert.AreEqual(1, fallen8.EdgeCount, "One (self-loop) edge before the removal.");

            // Act - remove the vertex. The self-loop must be counted as exactly one removed edge, not
            // two (it is reachable from both the out-edge and in-edge pass).
            var removeTx = new RemoveGraphElementTransaction { GraphElementId = v };
            fallen8.EnqueueTransaction(removeTx).WaitUntilFinished();

            // Assert - EdgeCount lands on exactly 0 (double-counting would drive it to -1).
            Assert.AreEqual(TransactionState.Finished, fallen8.GetTransactionState(removeTx.TransactionId));
            Assert.AreEqual(0, fallen8.VertexCount, "The vertex must be gone.");
            Assert.AreEqual(0, fallen8.EdgeCount, "The self-loop edge must be counted exactly once.");
        }

        [TestMethod]
        public void RemoveEdge_Committed_DecrementsEdgeCountOnly()
        {
            // Arrange
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = CreateVertices(fallen8, 2);
            var edgeTx = new CreateEdgesTransaction();
            edgeTx.AddEdge(vertices[0].Id, "e", vertices[1].Id, 1u);
            fallen8.EnqueueTransaction(edgeTx).WaitUntilFinished();

            VertexModel source;
            Assert.IsTrue(fallen8.TryGetVertex(out source, vertices[0].Id));
            int edgeId = source.OutEdges["e"][0].Id;

            Assert.AreEqual(2, fallen8.VertexCount);
            Assert.AreEqual(1, fallen8.EdgeCount);

            // Act - remove just the edge.
            var removeTx = new RemoveGraphElementTransaction { GraphElementId = edgeId };
            fallen8.EnqueueTransaction(removeTx).WaitUntilFinished();

            // Assert - only the edge count changes.
            Assert.AreEqual(TransactionState.Finished, fallen8.GetTransactionState(removeTx.TransactionId));
            Assert.AreEqual(2, fallen8.VertexCount, "Removing an edge must not change the vertex count.");
            Assert.AreEqual(0, fallen8.EdgeCount, "The edge must be gone.");
        }

        [TestMethod]
        public void RemoveVertex_RolledBack_LeavesVertexAndEdgeCountsIntact()
        {
            // Arrange - S --("in")--> V, then poison V's in-edge bucket so removing V faults midway.
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = CreateVertices(fallen8, 2);
            int sourceId = vertices[0].Id;
            int vId = vertices[1].Id;

            var edgeTx = new CreateEdgesTransaction();
            edgeTx.AddEdge(sourceId, "in", vId, 1u);
            fallen8.EnqueueTransaction(edgeTx).WaitUntilFinished();

            VertexModel v;
            Assert.IsTrue(fallen8.TryGetVertex(out v, vId));

            Assert.AreEqual(2, fallen8.VertexCount, "Two vertices before the faulting removal.");
            Assert.AreEqual(1, fallen8.EdgeCount, "One edge before the faulting removal.");

            // Poison: a second in-edge whose SourceVertex is null, ordered after the real one so the
            // real edge is detached (and incrementally decremented) first, then the poison throws an
            // NRE while its source-side adjacency is detached, driving the internal rollback. The
            // adjacency is now a read-only public surface, so the poison is injected through the
            // internal fault-injection hook (invoked by reflection, mirroring the former
            // v.InEdges = v.InEdges.SetItem("in", v.InEdges["in"].Add(poison)) injection exactly).
            var poison = new EdgeModel(int.MaxValue, 1, v, null, "poison", "in");
            InjectRawInEdge(v, "in", poison);

            // Act - the removal faults; the worker rolls it back.
            var removeTx = new RemoveGraphElementTransaction { GraphElementId = vId };
            fallen8.EnqueueTransaction(removeTx).WaitUntilFinished();

            // Assert - rolled back, and the counts are exactly as before (the incremental decrement is
            // undone by the full recount the rollback path runs, so it never leaks).
            Assert.AreEqual(TransactionState.RolledBack, fallen8.GetTransactionState(removeTx.TransactionId),
                "A faulting removal must be reported as rolled back.");
            Assert.AreEqual(2, fallen8.VertexCount, "Vertex count must be intact after a rolled-back removal.");
            Assert.AreEqual(1, fallen8.EdgeCount, "Edge count must be intact after a rolled-back removal.");
        }

        #endregion

        #region P5 - memoized plugin discovery still resolves every plugin by name

        [TestMethod]
        public void PluginFactory_MemoizedDiscovery_StillResolvesBothPathAlgorithmsByName()
        {
            // The name->type map (built once and reused) must still resolve both shipped path
            // algorithms by their exact plugin names.
            IShortestPathAlgorithm bls;
            Assert.IsTrue(PluginFactory.TryFindPlugin(out bls, "BLS"),
                "The BLS path algorithm must be discoverable by name.");
            Assert.AreEqual("BLS", bls.PluginName);

            IShortestPathAlgorithm dijkstra;
            Assert.IsTrue(PluginFactory.TryFindPlugin(out dijkstra, "DIJKSTRA"),
                "The DIJKSTRA path algorithm must be discoverable by name.");
            Assert.AreEqual("DIJKSTRA", dijkstra.PluginName);

            // A second call (map cached) must resolve to the same TYPE and still return a fresh
            // instance each time (the map stores types, not shared instances).
            IShortestPathAlgorithm blsAgain;
            Assert.IsTrue(PluginFactory.TryFindPlugin(out blsAgain, "BLS"));
            Assert.AreEqual(bls.GetType(), blsAgain.GetType(), "Same resolved type on a cached lookup.");
            Assert.AreNotSame(bls, blsAgain, "Each resolution still activates a fresh instance.");

            // A name that does not exist must not resolve.
            IShortestPathAlgorithm missing;
            Assert.IsFalse(PluginFactory.TryFindPlugin(out missing, "NO_SUCH_ALGORITHM"));
        }

        [TestMethod]
        public void PluginFactory_MemoizedDiscovery_EnumeratesEveryIndexPluginIncludingTestOnly()
        {
            // The available-index enumeration (used by IndexFactory / the admin endpoint) must still
            // find the production indices AND the test-only, globally-discoverable ThrowingOnLoad index.
            IEnumerable<string> indexPlugins;
            Assert.IsTrue(PluginFactory.TryGetAvailablePlugins<IIndex>(out indexPlugins),
                "At least the production index plugins must be discoverable.");

            var names = indexPlugins.ToList();
            CollectionAssert.Contains(names, "DictionaryIndex", "DictionaryIndex must be discovered.");
            CollectionAssert.Contains(names, "RangeIndex", "RangeIndex must be discovered.");
            CollectionAssert.Contains(names, "RegExIndex", "RegExIndex must be discovered.");
            CollectionAssert.Contains(names, ThrowingOnLoadIndex.TestPluginName,
                "The test-only ThrowingOnLoad index (a top-level public IIndex) must be discovered.");

            // And the index category resolves by name through the same memoized path.
            IIndex range;
            Assert.IsTrue(PluginFactory.TryFindPlugin(out range, "RangeIndex"));
            Assert.AreEqual("RangeIndex", range.PluginName);
        }

        [TestMethod]
        public void PluginFactory_AfterDiscoveryInvalidation_RebuildsFreshNameMapAndStillResolves()
        {
            // M1 regression. InvalidateDiscoveryCache (the Assimilate path) clears the memoized
            // candidate set AND every derived per-category name map under _discoveryLock, and
            // GetNameMap now BUILDS AND STORES the name map under that SAME lock. So an invalidation
            // can never be overtaken by the store of a map built from the pre-invalidation candidate
            // set: after an invalidation the very next by-name lookup must rebuild a FRESH map that
            // still resolves the full plugin set - never a lingering stale map. This pins the
            // invalidate -> rebuild -> fresh-set contract deterministically (no timing dependency).

            // Reach the private discovery cache + invalidation hook by reflection: the engine declares
            // no InternalsVisibleTo, so - as elsewhere in this suite - the test reflects rather than
            // widening that visibility.
            var nameMapsField = typeof(PluginFactory).GetField("_nameMaps",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(nameMapsField, "PluginFactory._nameMaps (the per-category name-map cache) must exist.");
            var nameMaps = (ConcurrentDictionary<Type, FrozenDictionary<String, Type>>)nameMapsField.GetValue(null);

            var invalidate = typeof(PluginFactory).GetMethod("InvalidateDiscoveryCache",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(invalidate, "PluginFactory.InvalidateDiscoveryCache (the Assimilate invalidation) must exist.");

            // Prime the path-algorithm category's name map (built + cached on first by-name lookup).
            IShortestPathAlgorithm bls;
            Assert.IsTrue(PluginFactory.TryFindPlugin(out bls, "BLS"),
                "BLS must resolve on the priming lookup that builds the name map.");
            Assert.IsTrue(nameMaps.TryGetValue(typeof(IShortestPathAlgorithm), out var mapBefore),
                "Resolving by name must have cached the category's name map.");

            // Simulate Assimilate dropping in a new plugin DLL: invalidate discovery + all name maps.
            invalidate.Invoke(null, null);

            // The invalidation must drop the cached map immediately, forcing the next lookup to
            // rebuild from a fresh scan rather than serving the stale pre-invalidation map.
            Assert.IsFalse(nameMaps.ContainsKey(typeof(IShortestPathAlgorithm)),
                "Invalidation must clear the cached per-category name maps.");

            // The next by-name lookups must rebuild the map (under the lock) and still resolve the
            // FULL, fresh set - both shipped path algorithms - not an empty or stale map.
            IShortestPathAlgorithm blsAfter;
            Assert.IsTrue(PluginFactory.TryFindPlugin(out blsAfter, "BLS"),
                "BLS must still resolve after an invalidation + rebuild.");
            Assert.AreEqual("BLS", blsAfter.PluginName);

            IShortestPathAlgorithm dijkstraAfter;
            Assert.IsTrue(PluginFactory.TryFindPlugin(out dijkstraAfter, "DIJKSTRA"),
                "DIJKSTRA must still resolve after an invalidation + rebuild.");
            Assert.AreEqual("DIJKSTRA", dijkstraAfter.PluginName);

            // The rebuilt map is a genuinely fresh instance, proving the invalidation took effect (a
            // stale store surviving the invalidation - the M1 bug - would have kept the old instance).
            Assert.IsTrue(nameMaps.TryGetValue(typeof(IShortestPathAlgorithm), out var mapAfter),
                "A rebuilt name map must be cached again after the next lookup.");
            Assert.AreNotSame(mapBefore, mapAfter,
                "The rebuild must produce a fresh map instance (the invalidation dropped the old one).");
        }

        #endregion

        #region P4 - ordered range index: sorted-key cache stays correct across mutations

        [TestMethod]
        public void RangeIndex_OrderedQueries_StayCorrectAcrossValueAndKeyMutations()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = CreateVertices(fallen8, 5);
            var index = new RangeIndex();
            index.Initialize(fallen8, null);

            index.AddOrUpdate(10, vertices[0]);
            index.AddOrUpdate(20, vertices[1]);
            index.AddOrUpdate(30, vertices[2]);

            // First range query builds the sorted-key cache; GreaterThan/LowerThan bracket correctly.
            ImmutableList<AGraphElementModel> r;
            Assert.IsTrue(index.GreaterThan(out r, 15, true));
            CollectionAssert.AreEquivalent(new AGraphElementModel[] { vertices[1], vertices[2] }, r.ToList(),
                "GreaterThan(15) must return keys 20 and 30.");

            Assert.IsTrue(index.LowerThan(out r, 20, false));
            CollectionAssert.AreEquivalent(new AGraphElementModel[] { vertices[0] }, r.ToList(),
                "LowerThan(20, exclusive) must return only key 10.");

            // Add another value under an EXISTING key (20): the key set is unchanged (cache NOT
            // invalidated), yet the range query must still surface the freshly added value.
            index.AddOrUpdate(20, vertices[3]);
            Assert.IsTrue(index.Between(out r, 20, 20, true, true));
            CollectionAssert.AreEquivalent(new AGraphElementModel[] { vertices[1], vertices[3] }, r.ToList(),
                "A value added under an existing key must appear in a later range query.");

            // Add a brand-new lower key (5): invalidates and rebuilds the cache on the next query.
            index.AddOrUpdate(5, vertices[4]);
            Assert.IsTrue(index.LowerThan(out r, 10, false));
            CollectionAssert.AreEquivalent(new AGraphElementModel[] { vertices[4] }, r.ToList(),
                "A newly added lower key must be found after the cache rebuild.");

            // Remove a key entirely: it must disappear from subsequent range queries.
            Assert.IsTrue(index.TryRemoveKey(30));
            Assert.IsTrue(index.GreaterThan(out r, 15, true));
            CollectionAssert.AreEquivalent(new AGraphElementModel[] { vertices[1], vertices[3] }, r.ToList(),
                "After removing key 30, GreaterThan(15) must return only the key-20 bucket.");

            index.Dispose();
        }

        #endregion

        #region P6 - bounded BLS reconstruction caps results and preserves the first-k order

        [TestMethod]
        public void Bls_BoundedReconstruction_CapsToMaxResultsPreservingFirstKPaths()
        {
            // Arrange - S and T joined by three DISTINCT equal-length (2-hop) paths S->Mi->T, so BLS
            // finds three shortest paths.
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = CreateVertices(fallen8, 5); // 0=S, 1=T, 2=M1, 3=M2, 4=M3
            int s = vertices[0].Id;
            int t = vertices[1].Id;

            var edgeTx = new CreateEdgesTransaction();
            for (int m = 2; m <= 4; m++)
            {
                edgeTx.AddEdge(s, "e", vertices[m].Id, 1u);   // S -> Mi
                edgeTx.AddEdge(vertices[m].Id, "e", t, 1u);   // Mi -> T
            }
            fallen8.EnqueueTransaction(edgeTx).WaitUntilFinished();

            // Unbounded run: all three 2-hop paths, in build order.
            List<Path> all;
            Assert.IsTrue(fallen8.TryCalculateShortestPath(out all, "BLS",
                new ShortestPathDefinition { SourceVertexId = s, DestinationVertexId = t, MaxDepth = 2, MaxResults = 100 }),
                "BLS must find the shortest paths.");
            Assert.AreEqual(3, all.Count, "There are three distinct 2-hop S->Mi->T paths.");

            // Bounded run: maxResults=2 must return exactly two paths, and they must be the FIRST two
            // of the unbounded run in the same order (the bounded reconstruction only stops early, it
            // never reorders).
            List<Path> capped;
            Assert.IsTrue(fallen8.TryCalculateShortestPath(out capped, "BLS",
                new ShortestPathDefinition { SourceVertexId = s, DestinationVertexId = t, MaxDepth = 2, MaxResults = 2 }));
            Assert.AreEqual(2, capped.Count, "maxResults=2 must cap the result to two paths.");

            for (int i = 0; i < capped.Count; i++)
            {
                var expected = all[i].GetPathElements();
                var actual = capped[i].GetPathElements();
                Assert.AreEqual(expected.Count, actual.Count, $"Path {i} length must match the unbounded run.");
                for (int j = 0; j < expected.Count; j++)
                {
                    Assert.AreEqual(expected[j].Edge.Id, actual[j].Edge.Id,
                        $"Bounded path {i} element {j} must equal the unbounded run's (first-k order preserved).");
                }
            }
        }

        #endregion
    }
}
