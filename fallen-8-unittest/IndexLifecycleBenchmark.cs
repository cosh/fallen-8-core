// MIT License
//
// IndexLifecycleBenchmark.cs
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
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Opt-in benchmark for the "index-lifecycle" 3.4 acceptance criterion: with the reverse map,
    /// <c>RemoveValue</c> scales with the number of keys the element appears under, NOT the total number
    /// of keys in the index. It builds a DictionaryIndex with a growing number of DISTINCT keys (each
    /// holding one element) plus one extra element under a single key, then times removing that one
    /// element. The old implementation scanned every key (O(total keys) with a per-key allocation), so
    /// its time grew with the key count; the reverse-map version stays roughly flat.
    ///
    /// Follows the repo benchmark convention (Benchmark category + [Ignore]); output prefix "[IDXBENCH]".
    /// </summary>
    [TestClass]
    public class IndexLifecycleBenchmark
    {
        private static void Emit(string line)
        {
            Console.WriteLine("[IDXBENCH] " + line);
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

        [TestMethod]
        [TestCategory("Benchmark")]
        [Ignore("Benchmark harness; opt-in. Not part of the default suite.")]
        public void RemoveValue_ScalesWithAffectedKeys_NotTotalKeys()
        {
            var loggerFactory = TestLoggerFactory.Create();
            foreach (var totalKeys in new[] { 10_000, 20_000, 40_000, 80_000 })
            {
                var fallen8 = new Fallen8(loggerFactory);
                // One "extra" vertex under a single key + one filler vertex per distinct key.
                var vertices = CreateVertices(fallen8, totalKeys + 1);

                var index = new DictionaryIndex();
                index.Initialize(fallen8, null);
                for (var k = 0; k < totalKeys; k++)
                {
                    index.AddOrUpdate("key_" + k.ToString(CultureInfo.InvariantCulture), vertices[k]);
                }
                var target = vertices[totalKeys];
                index.AddOrUpdate("target_key", target);

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                var before = GC.GetTotalAllocatedBytes(true);
                var sw = Stopwatch.StartNew();
                index.RemoveValue(target); // present under exactly ONE key
                sw.Stop();
                var bytes = GC.GetTotalAllocatedBytes(true) - before;

                Emit(string.Format(CultureInfo.InvariantCulture,
                    "totalKeys={0,8} -> RemoveValue(1 affected key) {1:0.0000} ms, {2} bytes",
                    totalKeys, sw.Elapsed.TotalMilliseconds, bytes));

                index.Dispose();
                fallen8.Dispose();
            }
        }
    }
}
