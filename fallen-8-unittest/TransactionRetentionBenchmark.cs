// MIT License
//
// TransactionRetentionBenchmark.cs
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
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Opt-in benchmark for the "transaction-retention" R1/F14 acceptance criteria. It runs a growing
    /// insert-only workload (no removals, so nothing auto-trims) and reports, per N: wall time, total
    /// bytes allocated, and the final retained transaction-bookkeeping entry count. With bounded
    /// retention the entry count stays at the bound regardless of N (the former unbounded design grew it
    /// 1:1 with N — several GB at 10M tx), and the per-transaction allocation stays roughly constant.
    ///
    /// Follows the repo benchmark convention (Benchmark category + [Ignore]); output prefix "[TXBENCH]".
    /// </summary>
    [TestClass]
    public class TransactionRetentionBenchmark
    {
        private static void Emit(string line)
        {
            Console.WriteLine("[TXBENCH] " + line);
        }

        private static int TransactionStateCount(Fallen8 fallen8)
        {
            var txManager = typeof(Fallen8).GetField("_txManager", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(fallen8);
            var dict = (IDictionary)txManager.GetType()
                .GetField("transactionState", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(txManager);
            return dict.Count;
        }

        [TestMethod]
        [TestCategory("Benchmark")]
        [Ignore("Benchmark harness; opt-in. Not part of the default suite.")]
        public void InsertOnlyWorkload_RetentionStaysBounded()
        {
            var loggerFactory = TestLoggerFactory.Create();
            foreach (var n in new[] { 200_000, 400_000, 800_000 })
            {
                var fallen8 = new Fallen8(loggerFactory);

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                var before = GC.GetTotalAllocatedBytes(true);
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < n; i++)
                {
                    var tx = new CreateVerticesTransaction();
                    tx.AddVertex(1u, "v");
                    fallen8.EnqueueTransaction(tx).WaitUntilFinished();
                }
                sw.Stop();
                var bytes = GC.GetTotalAllocatedBytes(true) - before;
                var retained = TransactionStateCount(fallen8);

                Emit(string.Format(CultureInfo.InvariantCulture,
                    "N={0,9} -> {1,9:0.000} ms, {2,7:0.0} MB total ({3:0} B/tx), retained entries={4}",
                    n, sw.Elapsed.TotalMilliseconds, bytes / (1024.0 * 1024.0), (double)bytes / n, retained));

                fallen8.Dispose();
            }
        }
    }
}
