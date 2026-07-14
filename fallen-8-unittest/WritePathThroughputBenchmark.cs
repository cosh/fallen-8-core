// MIT License
//
// WritePathThroughputBenchmark.cs
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
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Opt-in benchmark for write-path-throughput group commit: measures committed single-element
    /// writes/second against a WAL-enabled engine under N concurrent producers, where group commit
    /// amortises the fsync across the drained batch, versus a single serial producer (a group of one,
    /// which still fsyncs per commit so its latency is the floor). Follows the repo convention
    /// (Benchmark category + [Ignore]) so it is NOT part of the default run; output prefix "[WPTBENCH]".
    /// </summary>
    [TestClass]
    public class WritePathThroughputBenchmark
    {
        private static void Emit(string line) => Console.WriteLine("[WPTBENCH] " + line);

        private static int EnvInt(string name, int fallback)
        {
            var raw = Environment.GetEnvironmentVariable(name);
            return int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : fallback;
        }

        [TestMethod]
        [TestCategory("Benchmark")]
        [Ignore("Opt-in benchmark: remove [Ignore] or run explicitly to capture numbers.")]
        public void GroupCommitThroughput()
        {
            var loggerFactory = TestLoggerFactory.Create();
            var totalWrites = EnvInt("WPTBENCH_WRITES", 20_000);
            var producers = EnvInt("WPTBENCH_PRODUCERS", 32);
            var tempDir = Path.Combine(Path.GetTempPath(), "f8_wptbench_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                // Serial baseline (a group of one per commit: one fsync each).
                RunScenario(loggerFactory, Path.Combine(tempDir, "serial.wal"), totalWrites, producers: 1, label: "serial (1 producer)");

                // Concurrent producers: the single writer drains ready writes into groups, amortising
                // the fsync.
                RunScenario(loggerFactory, Path.Combine(tempDir, "grouped.wal"), totalWrites, producers, label: producers + " producers");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static void RunScenario(ILoggerFactory loggerFactory, string walPath, int totalWrites, int producers, string label)
        {
            using var fallen8 = new Fallen8(loggerFactory, new WriteAheadLogOptions(walPath));

            var sw = Stopwatch.StartNew();
            Parallel.For(0, producers, new ParallelOptions { MaxDegreeOfParallelism = producers }, p =>
            {
                var mine = totalWrites / producers;
                for (var i = 0; i < mine; i++)
                {
                    var tx = new CreateVerticesTransaction();
                    tx.AddVertex(1u, "n");
                    fallen8.EnqueueTransaction(tx).WaitUntilFinished();
                }
            });
            sw.Stop();

            var committed = (totalWrites / producers) * producers;
            Emit($"{label}: {committed:N0} committed writes in {sw.ElapsedMilliseconds:N0} ms " +
                 $"({committed * 1000.0 / Math.Max(1, sw.ElapsedMilliseconds):N0}/s)");
        }
    }
}
