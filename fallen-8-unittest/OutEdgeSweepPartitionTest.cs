// MIT License
//
// OutEdgeSweepPartitionTest.cs
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
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.Traversal;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Pins the SHAPE of <see cref="OutEdgeSweep.DefaultPartitionSize" /> (feature
    ///   traversal-sweep-partitioning), not any throughput: rates are not testable, shapes are. The
    ///   predecessor formula produced ProcessorCount / 1.5 ranges, quietly idling a third of the
    ///   machine, and nothing pinned it, which is how it survived from the original benchmark code
    ///   into the shared sweep. These tests make that regression impossible to reintroduce silently.
    /// </summary>
    [TestClass]
    public class OutEdgeSweepPartitionTest
    {
        private static Int32 RangeCount(Int32 vertexCount)
        {
            var size = OutEdgeSweep.DefaultPartitionSize(vertexCount);
            return (vertexCount + size - 1) / size;
        }

        [TestMethod]
        public void DefaultPartitionSize_KeepsEveryWorkerBusy_OnceTheGraphOutgrowsTheFloor()
        {
            // The property that was broken: on a graph past the floor threshold, the range count must
            // reach the worker count, or cores sit idle for the whole sweep.
            var processors = Environment.ProcessorCount;

            foreach (var vertexCount in new[] { 256 * processors, 4096 * processors, 1_000_000, 10_000_000 })
            {
                if (vertexCount < 256 * processors)
                {
                    continue; // 1M can be under the threshold on a very wide host
                }

                Assert.IsTrue(RangeCount(vertexCount) >= processors,
                    String.Format("{0} vertices split into {1} ranges for {2} workers - cores would idle",
                        vertexCount, RangeCount(vertexCount), processors));
            }
        }

        [TestMethod]
        public void DefaultPartitionSize_IsAlwaysAValidPartitionerArgument()
        {
            // Partitioner.Create rejects a range below one; the floor must hold on every input a
            // caller can produce, including the empty graph the sweep never actually partitions.
            foreach (var vertexCount in new[] { 0, 1, 255, 256, 257 })
            {
                Assert.IsTrue(OutEdgeSweep.DefaultPartitionSize(vertexCount) >= 1,
                    "partition size for " + vertexCount + " vertices must be at least one");
            }
        }

        [TestMethod]
        public void DefaultPartitionSize_DoesNotShatterATinyGraph()
        {
            // The floor's other half: a small graph must not dissolve into per-vertex ranges whose
            // dispatch overhead outweighs the microseconds of sweeping them.
            Assert.IsTrue(OutEdgeSweep.DefaultPartitionSize(1_000) >= 256,
                "a 1,000-vertex graph must be swept in a few coarse ranges, not shattered");
        }

        [TestMethod]
        public void Sweep_WithTheDefaultPartition_CountsEveryOutEdgeExactly()
        {
            // Behaviour must not move with the partitioning: the traversed count is exact whatever
            // the range layout. Isolated vertices contribute nothing; a self-loop counts once.
            var loggerFactory = TestLoggerFactory.Create();
            var fallen8 = new Fallen8(loggerFactory);

            var verticesTx = new CreateVerticesTransaction();
            for (var i = 0; i < 1_000; i++)
            {
                verticesTx.AddVertex(1u, null);
            }
            fallen8.EnqueueTransaction(verticesTx).WaitUntilFinished();
            var vertices = verticesTx.GetCreatedVertices();

            var edgesTx = new CreateEdgesTransaction();
            for (var i = 0; i < 500; i++)
            {
                edgesTx.AddEdge(vertices[i].Id, "sweep", vertices[(i + 7) % 1_000].Id, 1u);
            }
            edgesTx.AddEdge(vertices[0].Id, "sweep", vertices[0].Id, 1u); // self-loop
            fallen8.EnqueueTransaction(edgesTx).WaitUntilFinished();

            var traversed = OutEdgeSweep.Sweep(fallen8.GetAllVertices());

            Assert.AreEqual(501L, traversed, "every out-edge exactly once, half the vertices isolated");
        }

        [TestMethod]
        public void Sweep_OnAnEmptyVertexList_ReturnsZero()
        {
            Assert.AreEqual(0L, OutEdgeSweep.Sweep(new List<NoSQL.GraphDB.Core.Model.VertexModel>()));
        }
    }
}
