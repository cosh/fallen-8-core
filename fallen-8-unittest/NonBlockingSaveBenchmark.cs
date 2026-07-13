// MIT License
//
// NonBlockingSaveBenchmark.cs
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
using System.Diagnostics;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Opt-in benchmarks for the non-blocking-save (P3) decision: they measure the writer-thread
    /// hold time of a blocking <see cref="SaveTransaction"/> (≈ the worst-case stall any concurrent
    /// write pays) and demonstrate that stall directly. Follows the repo convention
    /// (Benchmark category + [Ignore]) so they are NOT part of the default run; remove the [Ignore]
    /// (or run the method explicitly) to capture numbers. Output is prefixed "[NBSBENCH]".
    /// </summary>
    [TestClass]
    public class NonBlockingSaveBenchmark
    {
        private static void Emit(string line)
        {
            Console.WriteLine("[NBSBENCH] " + line);
        }

        /// <summary>
        /// Builds a graph of <paramref name="vertexCount"/> vertices (each carrying two properties,
        /// so serialization does realistic per-element work) chained by <paramref name="vertexCount"/>-1
        /// edges. Batched so building the graph is not itself the bottleneck.
        /// </summary>
        private static void BuildGraph(Fallen8 fallen8, int vertexCount)
        {
            const int batch = 50_000;
            var created = new List<int>(vertexCount);

            var i = 0;
            while (i < vertexCount)
            {
                var end = Math.Min(i + batch, vertexCount);
                var vtx = new CreateVerticesTransaction();
                for (var j = i; j < end; j++)
                {
                    vtx.AddVertex(1u, "person", new Dictionary<string, object>
                    {
                        { "name", "v" + j },
                        { "age", j % 100 }
                    });
                }
                fallen8.EnqueueTransaction(vtx).WaitUntilFinished();
                foreach (var v in vtx.GetCreatedVertices())
                {
                    created.Add(v.Id);
                }
                i = end;
            }

            // A chain of edges: v[k] --knows--> v[k+1].
            i = 0;
            while (i < created.Count - 1)
            {
                var end = Math.Min(i + batch, created.Count - 1);
                var etx = new CreateEdgesTransaction();
                for (var k = i; k < end; k++)
                {
                    etx.AddEdge(created[k], "knows", created[k + 1], 1u, "knows");
                }
                fallen8.EnqueueTransaction(etx).WaitUntilFinished();
                i = end;
            }
        }

        private static long TimeSave(Fallen8 fallen8, string path)
        {
            var sw = Stopwatch.StartNew();
            var tx = new SaveTransaction { Path = path, SavePartitions = (int)SaveTransaction.GetOptimalNumberOfPartitions() };
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            sw.Stop();
            return sw.ElapsedMilliseconds;
        }

        [TestMethod]
        [TestCategory("Benchmark")]
        [Ignore("Benchmark harness; opt-in. Not part of the default suite.")]
        public void SaveWriterHoldTime_ScalesWithGraphSize()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "f8_nbs_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                int[] sizes = { 50_000, 200_000, 1_000_000 };
                foreach (var size in sizes)
                {
                    var fallen8 = new Fallen8(TestLoggerFactory.Create());
                    BuildGraph(fallen8, size);
                    var elements = fallen8.VertexCount + fallen8.EdgeCount;

                    // Warm one save (JIT, file-system), then time a second.
                    var path = Path.Combine(tempDir, "sg_" + size + ".f8s");
                    TimeSave(fallen8, path);
                    var ms = TimeSave(fallen8, path);

                    Emit(string.Format("size={0,10} elements ({1} vertices + {2} edges): save writer-hold = {3} ms",
                        elements, fallen8.VertexCount, fallen8.EdgeCount, ms));
                    fallen8.Dispose();
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { /* best-effort */ }
            }
        }

        [TestMethod]
        [TestCategory("Benchmark")]
        [Ignore("Benchmark harness; opt-in. Not part of the default suite.")]
        public void ConcurrentWriteStall_DuringSave_IsAboutSaveDuration()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "f8_nbs_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                const int size = 500_000;
                var fallen8 = new Fallen8(TestLoggerFactory.Create());
                BuildGraph(fallen8, size);
                var path = Path.Combine(tempDir, "concurrent.f8s");

                // Warm.
                TimeSave(fallen8, path);

                // Enqueue a save but DO NOT wait; then immediately enqueue a tiny write. Because there
                // is exactly one writer and the queue is FIFO, the write runs only AFTER the save, so
                // its WaitUntilFinished measures the stall a concurrent writer observes.
                var saveTx = new SaveTransaction { Path = path, SavePartitions = (int)SaveTransaction.GetOptimalNumberOfPartitions() };
                var saveSw = Stopwatch.StartNew();
                var saveInfo = fallen8.EnqueueTransaction(saveTx);

                var probe = new CreateVerticesTransaction();
                probe.AddVertex(1u, "late");
                var stallSw = Stopwatch.StartNew();
                var probeInfo = fallen8.EnqueueTransaction(probe);
                probeInfo.WaitUntilFinished();
                stallSw.Stop();

                saveInfo.WaitUntilFinished();
                saveSw.Stop();

                Emit(string.Format("graph={0} vertices + {1} edges: save = {2} ms; a concurrent one-vertex write stalled {3} ms behind it",
                    fallen8.VertexCount - 1, fallen8.EdgeCount, saveSw.ElapsedMilliseconds, stallSw.ElapsedMilliseconds));
                fallen8.Dispose();
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { /* best-effort */ }
            }
        }
    }
}
