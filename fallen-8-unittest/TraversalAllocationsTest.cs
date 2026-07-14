// MIT License
//
// TraversalAllocationsTest.cs
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
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Tests for the "traversal-allocations" feature. This pass landed the allocation-free public read
    /// (C1 <c>TryGetOut/InEdgesSpan</c>), the Dijkstra neighbour memoisation (B1, guarded by the
    /// unchanged <c>WeightedDijkstraPathTest</c>), the dead-code deletion (D1 <c>PathHelper.GetValidEdges</c>)
    /// and the presized <c>GetAllNeighbors</c> (D2). The BLS per-edge struct rewrite (A1–A4) is deferred.
    /// These pin the C1 span read: it returns exactly the group's edges, is count-bounded (never exposes
    /// supernode spare capacity), and agrees with <see cref="VertexModel.TryGetOutEdge"/>.
    /// </summary>
    [TestClass]
    public class TraversalAllocationsTest
    {
        private ILoggerFactory _loggerFactory;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
        }

        private VertexModel[] BuildHub(int leafCount)
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var vtx = new CreateVerticesTransaction();
            vtx.AddVertex(1u, "hub");
            for (var i = 0; i < leafCount; i++)
            {
                vtx.AddVertex(1u, "leaf");
            }
            fallen8.EnqueueTransaction(vtx).WaitUntilFinished();
            var v = vtx.GetCreatedVertices().ToArray();

            // One edge per leaf, all under the same key, grown one small transaction at a time so the
            // hub's group carries supernode-adjacency-build spare capacity (count < backing length).
            for (var i = 1; i <= leafCount; i++)
            {
                var edgeTx = new CreateEdgesTransaction();
                edgeTx.AddEdge(v[0].Id, "e", v[i].Id, 1u, "e");
                fallen8.EnqueueTransaction(edgeTx).WaitUntilFinished();
            }
            return v;
        }

        [TestMethod]
        public void TryGetOutEdgesSpan_ReturnsTheGroup_CountBounded_AndMatchesTryGetOutEdge()
        {
            var v = BuildHub(10);
            var hub = v[0];

            Assert.IsTrue(hub.TryGetOutEdgesSpan(out var span, "e"), "The hub has an 'e' out-group.");
            Assert.AreEqual(10, span.Length, "The span must be bounded to the logical count (no spare slots).");

            // No spare slot leaks in as null, and the span agrees element-for-element with the
            // count-bounded IReadOnlyList accessor.
            Assert.IsTrue(hub.TryGetOutEdge(out var list, "e"));
            Assert.AreEqual(list.Count, span.Length);
            for (var i = 0; i < span.Length; i++)
            {
                Assert.IsNotNull(span[i], "No span slot may be null (spare capacity must never be exposed).");
                Assert.AreSame(list[i], span[i], "The span and the read-only list must agree element-for-element.");
            }

            // A missing group returns false with an empty span.
            Assert.IsFalse(hub.TryGetOutEdgesSpan(out var empty, "no-such-key"));
            Assert.AreEqual(0, empty.Length);
        }

        [TestMethod]
        public void TryGetInEdgesSpan_ReturnsTheGroup_ForALeaf()
        {
            var v = BuildHub(5);

            // Each leaf has exactly one incoming edge from the hub, under key "e".
            var leaf = v[1];
            Assert.IsTrue(leaf.TryGetInEdgesSpan(out var span, "e"));
            Assert.AreEqual(1, span.Length);
            Assert.AreSame(v[0], span[0].SourceVertex, "The in-edge's source must be the hub.");

            Assert.IsFalse(leaf.TryGetInEdgesSpan(out var missing, "other"));
            Assert.AreEqual(0, missing.Length);
        }
    }
}
