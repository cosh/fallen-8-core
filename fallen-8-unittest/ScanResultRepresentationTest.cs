// MIT License
//
// ScanResultRepresentationTest.cs
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

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Expression;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Characterization tests for the "scan-result-representation" feature. The whole-graph read
    /// surface (<see cref="Fallen8.GetAllVertices"/>/<see cref="Fallen8.GetAllEdges"/>/
    /// <see cref="Fallen8.GetAllGraphElements"/>) and the built index-scan branches now hand back a
    /// right-sized <c>List&lt;T&gt;</c> typed as <see cref="IReadOnlyList{T}"/> instead of packing the
    /// scan into a per-call <see cref="ImmutableList{T}"/> (an AVL tree the caller drops immediately).
    ///
    /// These tests pin the two contracts the change must preserve:
    ///   1. Result <b>parity</b> - the same live elements, in the same id order, under mixed labels,
    ///      removals and label filters.
    ///   2. The <b>de-treed representation</b> - the built paths return a plain list (no tree), while the
    ///      <c>Equals</c> fast path keeps returning the index's own retained (copy-on-write) bucket.
    ///
    /// Operator-level IndexScan parity and cross-bucket de-dup are covered by the P4 tests in
    /// <c>EnginePerformanceFollowupsTest</c>; this file does not duplicate them.
    /// </summary>
    [TestClass]
    public class ScanResultRepresentationTest
    {
        private ILoggerFactory _loggerFactory;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
        }

        private static VertexModel[] CreateLabelledVertices(Fallen8 fallen8, string label, int count)
        {
            var tx = new CreateVerticesTransaction();
            for (var i = 0; i < count; i++)
            {
                tx.AddVertex(1u, label, new Dictionary<string, object> { { "n", i } });
            }
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.GetCreatedVertices().ToArray();
        }

        private static void Remove(Fallen8 fallen8, params int[] ids)
        {
            fallen8.EnqueueTransaction(new RemoveGraphElementsTransaction { GraphElementIds = ids.ToList() })
                .WaitUntilFinished();
        }

        private static int[] IdsOf<T>(IReadOnlyList<T> elements) where T : AGraphElementModel
        {
            return elements.Select(e => e.Id).ToArray();
        }

        /// <summary>
        /// Builds a mixed graph (two vertex labels + edges), removes a vertex of each label and an edge,
        /// then asserts every GetAll* projection returns exactly the surviving elements in ascending id
        /// order. The removed vertices are edge-free by construction, so no incident-edge cascade can
        /// perturb the expected edge set.
        /// </summary>
        [TestMethod]
        public void GetAll_UnderMixedLabelsAndRemovals_ReturnsExactlyTheLiveElementsInIdOrder()
        {
            // Arrange - v0..v2 are "person", v3..v5 are "city" (ids 0..5). Edges (ids 6..8) touch only
            // v0/v2/v3/v5; v1 and v4 (the vertices we remove) stay edge-free.
            var fallen8 = new Fallen8(_loggerFactory);
            var persons = CreateLabelledVertices(fallen8, "person", 3);
            var cities = CreateLabelledVertices(fallen8, "city", 3);

            var edgeTx = new CreateEdgesTransaction();
            edgeTx.AddEdge(persons[0].Id, "road", persons[2].Id, 1u, "road");
            edgeTx.AddEdge(persons[2].Id, "road", cities[0].Id, 1u, "road");
            edgeTx.AddEdge(cities[0].Id, "road", cities[2].Id, 1u, "road");
            fallen8.EnqueueTransaction(edgeTx).WaitUntilFinished();
            var edges = edgeTx.GetCreatedEdges().ToArray();

            // Act - remove one person (v1), one city (v4) and the middle edge; all are edge-free removals.
            Remove(fallen8, persons[1].Id, cities[1].Id, edges[1].Id);

            var liveVertexIds = new[] { persons[0].Id, persons[2].Id, cities[0].Id, cities[2].Id }.OrderBy(x => x).ToArray();
            var livePersonIds = new[] { persons[0].Id, persons[2].Id }.OrderBy(x => x).ToArray();
            var liveCityIds = new[] { cities[0].Id, cities[2].Id }.OrderBy(x => x).ToArray();
            var liveEdgeIds = new[] { edges[0].Id, edges[2].Id }.OrderBy(x => x).ToArray();

            // Assert - GetAllVertices: exactly the four survivors, ascending id order.
            var allVertices = fallen8.GetAllVertices();
            CollectionAssert.AreEqual(liveVertexIds, IdsOf(allVertices),
                "GetAllVertices must return exactly the live vertices in ascending id order.");

            // Label filters project the same store to the matching label only.
            CollectionAssert.AreEqual(livePersonIds, IdsOf(fallen8.GetAllVertices("person")),
                "GetAllVertices(\"person\") must return only the live person vertices.");
            CollectionAssert.AreEqual(liveCityIds, IdsOf(fallen8.GetAllVertices("city")),
                "GetAllVertices(\"city\") must return only the live city vertices.");
            Assert.AreEqual(0, fallen8.GetAllVertices("does-not-exist").Count,
                "A label matched by nothing must yield an empty (non-null) list.");

            // GetAllEdges: the two surviving edges, ascending id order.
            CollectionAssert.AreEqual(liveEdgeIds, IdsOf(fallen8.GetAllEdges()),
                "GetAllEdges must return exactly the live edges in ascending id order.");

            // GetAllGraphElements: the union of live vertices and edges, still globally id-ordered.
            var expectedAll = liveVertexIds.Concat(liveEdgeIds).OrderBy(x => x).ToArray();
            var allElements = fallen8.GetAllGraphElements();
            CollectionAssert.AreEqual(expectedAll, IdsOf(allElements),
                "GetAllGraphElements must return every live element (vertices + edges) in ascending id order.");
        }

        /// <summary>
        /// An empty graph returns empty (never null) lists from every GetAll* projection.
        /// </summary>
        [TestMethod]
        public void GetAll_OnEmptyGraph_ReturnsEmptyNonNullLists()
        {
            var fallen8 = new Fallen8(_loggerFactory);

            var vertices = fallen8.GetAllVertices();
            var edges = fallen8.GetAllEdges();
            var elements = fallen8.GetAllGraphElements();

            Assert.IsNotNull(vertices);
            Assert.IsNotNull(edges);
            Assert.IsNotNull(elements);
            Assert.AreEqual(0, vertices.Count);
            Assert.AreEqual(0, edges.Count);
            Assert.AreEqual(0, elements.Count);
        }

        /// <summary>
        /// The headline of the feature: the GetAll* projections no longer allocate an
        /// <see cref="ImmutableList{T}"/> (an AVL tree) - they fill and return a plain list. Retaining
        /// the exact concrete type is not the contract; NOT being the tree is.
        /// </summary>
        [TestMethod]
        public void GetAll_ResultsAreNotBackedByAnImmutableTree()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            CreateLabelledVertices(fallen8, "person", 4);

            var vertices = fallen8.GetAllVertices();
            var elements = fallen8.GetAllGraphElements();

            Assert.IsFalse(vertices is ImmutableList<VertexModel>,
                "GetAllVertices must hand back a right-sized list, not a per-call ImmutableList tree.");
            Assert.IsFalse(elements is ImmutableList<AGraphElementModel>,
                "GetAllGraphElements must hand back a right-sized list, not a per-call ImmutableList tree.");
        }

        /// <summary>
        /// The representation split on the index-scan surface:
        ///   - the <c>Equals</c> fast path returns the index's OWN retained posting-list bucket (its
        ///     copy-on-write is load-bearing, so it is kept - and is therefore still an ImmutableList);
        ///   - every built branch (here <c>NotEquals</c>) returns a freshly de-treed list, NOT a tree.
        /// Both are exposed only as <see cref="IReadOnlyList{T}"/>.
        /// </summary>
        [TestMethod]
        public void IndexScan_KeepsTheSharedBucketOnEquals_ButDeTreesTheBuiltBranches()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var v = CreateLabelledVertices(fallen8, "person", 3);

            IIndex index;
            Assert.IsTrue(fallen8.IndexFactory.TryCreateIndex(out index, "byName", "DictionaryIndex"));
            // v0 sits under two keys so the NotEquals branch must dedup it across buckets.
            index.AddOrUpdate("alice", v[0]);
            index.AddOrUpdate("bob", v[0]);
            index.AddOrUpdate("alice", v[1]);
            index.AddOrUpdate("carol", v[2]);

            // Equals fast path: the index's shared {alice} bucket, kept as-is (still an ImmutableList).
            Assert.IsTrue(fallen8.IndexScan(out var equalsResult, "byName", "alice", BinaryOperator.Equals));
            CollectionAssert.AreEquivalent(new AGraphElementModel[] { v[0], v[1] }, equalsResult.ToList());
            Assert.IsTrue(equalsResult is ImmutableList<AGraphElementModel>,
                "The Equals fast path must keep returning the index's own retained bucket, not a copy.");
            index.TryGetValue(out var sharedBucket, "alice");
            Assert.IsTrue(ReferenceEquals(sharedBucket, equalsResult),
                "The Equals result must be the very same shared bucket instance the index holds.");

            // Built branch (NotEquals matches alice+bob+carol): freshly de-treed, deduped list.
            Assert.IsTrue(fallen8.IndexScan(out var notEqualsResult, "byName", "zzz", BinaryOperator.NotEquals));
            CollectionAssert.AreEquivalent(new AGraphElementModel[] { v[0], v[1], v[2] }, notEqualsResult.ToList());
            Assert.AreEqual(3, notEqualsResult.Count,
                "v0 is indexed under two matching keys but must appear exactly once (cross-bucket de-dup).");
            Assert.IsFalse(notEqualsResult is ImmutableList<AGraphElementModel>,
                "A built (non-Equals) branch must return a right-sized list, not a per-call ImmutableList tree.");
        }
    }
}
