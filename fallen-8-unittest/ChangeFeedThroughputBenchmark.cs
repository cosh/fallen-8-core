// MIT License
//
// ChangeFeedThroughputBenchmark.cs
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
using NoSQL.GraphDB.Core.ChangeFeed;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Opt-in throughput benchmark pinning the change feed's non-regression requirement (feature
    /// change-feed, spec requirement 2): committed tx/s on a WAL-enabled engine for feed-off /
    /// feed-on-no-subscriber / feed-on-one-draining-subscriber / feed-on-with-stalled-subscriber.
    /// Follows the repo convention (Benchmark category + [Ignore]); remove the [Ignore] to
    /// capture numbers. Results are recorded in features/change-feed/README.md.
    /// </summary>
    [TestClass]
    public class ChangeFeedThroughputBenchmark
    {
        private const int TransactionCount = 20_000;

        [TestMethod]
        [TestCategory("Benchmark")]
        [Ignore("Benchmark harness; opt-in. Not part of the default suite.")]
        public void CommittedWriteThroughput_FeedOff_vs_FeedOn()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "f8_cfbench_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var results = new List<string>
                {
                    Run("feed-off", tempDir, changeFeed: null, subscriber: SubscriberMode.None),
                    Run("feed-on-no-subscriber", tempDir, new ChangeFeedOptions(), SubscriberMode.None),
                    Run("feed-on-one-subscriber", tempDir, new ChangeFeedOptions(), SubscriberMode.Draining),
                    Run("feed-on-stalled-subscriber", tempDir, new ChangeFeedOptions(), SubscriberMode.Stalled)
                };

                foreach (var line in results)
                {
                    Console.WriteLine("[change-feed benchmark] " + line);
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
            }
        }

        private enum SubscriberMode { None, Draining, Stalled }

        private static string Run(string name, string tempDir, ChangeFeedOptions changeFeed, SubscriberMode subscriber)
        {
            var walPath = Path.Combine(tempDir, name + ".wal");
            using var engine = new Fallen8(TestLoggerFactory.Create(),
                new WriteAheadLogOptions(walPath), null, null, changeFeed);

            ChangeFeedSubscription subscription = null;
            Task drainer = null;
            if (subscriber != SubscriberMode.None)
            {
                Assert.IsTrue(engine.ChangeFeed.TrySubscribe(ChangeFeedFilter.MatchAll, null, null, out subscription));
                if (subscriber == SubscriberMode.Draining)
                {
                    var reader = subscription.Reader;
                    drainer = Task.Run(async () =>
                    {
                        await foreach (var _ in reader.ReadAllAsync())
                        {
                        }
                    });
                }
                // Stalled: never read - the queue overflows and the drop+resync path is exercised.
            }

            // Warm-up outside the measurement.
            EnqueueVertices(engine, 1_000);

            var stopwatch = Stopwatch.StartNew();
            EnqueueVertices(engine, TransactionCount);
            stopwatch.Stop();

            subscription?.Dispose();
            drainer?.Wait(2000);

            var txPerSecond = TransactionCount / stopwatch.Elapsed.TotalSeconds;
            return $"{name}: {TransactionCount} committed tx in {stopwatch.ElapsedMilliseconds} ms = {txPerSecond:F0} tx/s";
        }

        /// <summary>Enqueues single-vertex transactions and waits for the LAST one (group commit
        /// batches the rest), mirroring the write-path-throughput measurement shape.</summary>
        private static void EnqueueVertices(Fallen8 engine, int count)
        {
            TransactionInformation last = null;
            for (var i = 0; i < count; i++)
            {
                var tx = new CreateVertexTransaction
                {
                    Definition = new NoSQL.GraphDB.Core.Model.VertexDefinition { CreationDate = 1u, Label = "person" }
                };
                last = engine.EnqueueTransaction(tx);
            }

            last.WaitUntilFinished();
        }
    }
}
