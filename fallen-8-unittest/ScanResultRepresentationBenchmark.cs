// MIT License
//
// ScanResultRepresentationBenchmark.cs
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
using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Opt-in Stopwatch/allocation benchmark for the "scan-result-representation" acceptance criteria.
    /// It measures a full-graph <see cref="Fallen8.GetAllVertices"/>/<see cref="Fallen8.GetAllGraphElements"/>
    /// scan at 1,000,000 and 2,500,000 elements, reporting per-call wall time and thread-local bytes
    /// allocated. The change replaces a per-call <c>ImmutableList</c> (an AVL tree, ~63 B/visited element
    /// at 2.5M = ~158 MB recorded in <c>core-storage-representation/plan.md</c>) with a right-sized
    /// reference array (~8 B/slot on 64-bit), so the allocation should drop by roughly an order of
    /// magnitude with no per-node allocation, and iteration should be a flat contiguous walk.
    ///
    /// Follows the repo benchmark convention (Benchmark category + [Ignore]) so it is NOT part of the
    /// default run; remove the [Ignore] (or run the method explicitly) to capture numbers on this box.
    /// Output is prefixed "[SCANBENCH]".
    /// </summary>
    [TestClass]
    public class ScanResultRepresentationBenchmark
    {
        private static void Emit(string line)
        {
            Console.WriteLine("[SCANBENCH] " + line);
        }

        private static void SeedVertices(Fallen8 fallen8, int count)
        {
            // Build the store in modest batches so the seeding transaction set stays bounded; the
            // measured operation is the READ scan, not this setup.
            const int batch = 100_000;
            var remaining = count;
            while (remaining > 0)
            {
                var thisBatch = Math.Min(batch, remaining);
                var tx = new CreateVerticesTransaction();
                for (var i = 0; i < thisBatch; i++)
                {
                    tx.AddVertex(1u, "v");
                }
                fallen8.EnqueueTransaction(tx).WaitUntilFinished();
                remaining -= thisBatch;
            }
        }

        private static void MeasureScan(string label, Func<int> scan)
        {
            // Warm up once (JIT + first-touch) so the reported figures reflect steady state.
            var warmCount = scan();

            var before = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            var count = scan();
            sw.Stop();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Emit(string.Format(CultureInfo.InvariantCulture,
                "{0}: {1} elements -> {2:0.000} ms, {3:0.0} MB allocated ({4:0.0} B/element) [warm={5}]",
                label, count, sw.Elapsed.TotalMilliseconds, allocated / (1024.0 * 1024.0),
                count > 0 ? (double)allocated / count : 0.0, warmCount));
        }

        private void RunAt(int elementCount)
        {
            var loggerFactory = TestLoggerFactory.Create();
            var fallen8 = new Fallen8(loggerFactory);
            SeedVertices(fallen8, elementCount);

            Emit($"--- {elementCount} elements ---");
            MeasureScan("GetAllVertices", () => fallen8.GetAllVertices().Count);
            MeasureScan("GetAllGraphElements", () => fallen8.GetAllGraphElements().Count);

            fallen8.Dispose();
        }

        [TestMethod]
        [TestCategory("Benchmark")]
        [Ignore("Benchmark harness; opt-in. Not part of the default suite.")]
        public void Scan_1M_Elements()
        {
            RunAt(1_000_000);
        }

        [TestMethod]
        [TestCategory("Benchmark")]
        [Ignore("Benchmark harness; opt-in. Not part of the default suite.")]
        public void Scan_2_5M_Elements()
        {
            RunAt(2_500_000);
        }
    }
}
