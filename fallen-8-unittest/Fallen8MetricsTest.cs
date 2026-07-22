// MIT License
//
// Fallen8MetricsTest.cs
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
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Persistency;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// MeterListener tests for the engine's metric instruments (feature observability): the
    /// transaction pipeline counters/histograms, WAL degradation, checkpoint metrics, gauge
    /// lifecycle across engine disposal, and the no-user-strings-in-tags invariant.
    /// </summary>
    [TestClass]
    public class Fallen8MetricsTest
    {
        private sealed record Measurement(String Instrument, Double Value, Dictionary<String, Object> Tags);

        /// <summary>Collects every measurement from the named meters (default: the engine's).</summary>
        private sealed class Collector : IDisposable
        {
            private readonly MeterListener _listener = new MeterListener();
            public readonly ConcurrentQueue<Measurement> Measurements = new ConcurrentQueue<Measurement>();

            public Collector(params string[] meterNames)
            {
                var names = meterNames.Length == 0 ? new[] { "NoSQL.GraphDB.Core" } : meterNames;
                _listener.InstrumentPublished = (instrument, listener) =>
                {
                    if (Array.IndexOf(names, instrument.Meter.Name) >= 0)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                };
                _listener.SetMeasurementEventCallback<Int64>((instrument, value, tags, state) => Record(instrument, value, tags));
                _listener.SetMeasurementEventCallback<Double>((instrument, value, tags, state) => Record(instrument, value, tags));
                _listener.SetMeasurementEventCallback<Int32>((instrument, value, tags, state) => Record(instrument, value, tags));
                _listener.Start();
            }

            private void Record(Instrument instrument, Double value, ReadOnlySpan<KeyValuePair<String, Object>> tags)
            {
                var tagMap = new Dictionary<String, Object>();
                foreach (var tag in tags)
                {
                    tagMap[tag.Key] = tag.Value;
                }
                Measurements.Enqueue(new Measurement(instrument.Name, value, tagMap));
            }

            public void CollectGauges()
            {
                _listener.RecordObservableInstruments();
            }

            public Double Sum(String instrument) =>
                Measurements.Where(m => m.Instrument == instrument).Sum(m => m.Value);

            public Int32 Count(String instrument) =>
                Measurements.Count(m => m.Instrument == instrument);

            public List<Measurement> Of(String instrument) =>
                Measurements.Where(m => m.Instrument == instrument).ToList();

            public void Dispose()
            {
                _listener.Dispose();
            }
        }

        private static Int32 CreateVertex(Fallen8 engine, string label = "person")
        {
            var tx = new CreateVertexTransaction
            {
                Definition = new VertexDefinition { CreationDate = 1u, Label = label }
            };
            engine.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.VertexCreated.Id;
        }

        [TestMethod]
        public void Commits_Rollbacks_Durations_AndQueueGauge_AreRecorded()
        {
            using var collector = new Collector();
            using var engine = new Fallen8(TestLoggerFactory.Create());

            CreateVertex(engine);
            CreateVertex(engine);

            // A clean rollback with a classified reason: removing a non-existent element.
            var doomed = engine.EnqueueTransaction(new RemoveGraphElementTransaction { GraphElementId = 424242 });
            doomed.WaitUntilFinished();
            Assert.AreEqual(TransactionState.RolledBack, doomed.TransactionState);

            Assert.AreEqual(2d, collector.Of("fallen8.transaction.commits")
                    .Where(m => (String)m.Tags["transaction.type"] == nameof(CreateVertexTransaction))
                    .Sum(m => m.Value),
                "two commits, tagged with the transaction TYPE name");

            var rollbacks = collector.Of("fallen8.transaction.rollbacks");
            Assert.AreEqual(1, rollbacks.Count);
            Assert.AreEqual(nameof(RemoveGraphElementTransaction), rollbacks[0].Tags["transaction.type"]);
            Assert.IsTrue(rollbacks[0].Tags.ContainsKey("failure.reason"), "rollbacks carry the failure reason");

            Assert.AreEqual(2, collector.Of("fallen8.transaction.commit.duration")
                    .Count(m => m.Value >= 0d && (String)m.Tags["transaction.type"] == nameof(CreateVertexTransaction)),
                "commit latency (enqueue -> durable ack) recorded per committed transaction");
            Assert.IsTrue(collector.Count("fallen8.transaction.execute.duration") >= 3,
                "execute duration recorded for commits AND rollbacks");
            Assert.IsTrue(collector.Count("fallen8.transaction.group.size") >= 1);

            collector.CollectGauges();
            Assert.IsTrue(collector.Count("fallen8.transaction.queue.depth") >= 1, "the queue gauge is readable");
            Assert.AreEqual(2d, collector.Of("fallen8.graph.vertices").Last().Value);
        }

        [TestMethod]
        public void WalFailure_FlipsDegradedGauge_CountsNonDurable_AndSaveClearsIt()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "f8_obs_wal_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var walPath = Path.Combine(tempDir, "graph.f8s.wal");
            try
            {
                using var collector = new Collector();
                using var engine = new Fallen8(TestLoggerFactory.Create(), new WriteAheadLogOptions(walPath));

                CreateVertex(engine);
                Assert.IsTrue(collector.Count("fallen8.wal.flush.duration") >= 1,
                    "the group fsync duration is recorded");

                collector.CollectGauges();
                Assert.AreEqual(0d, collector.Of("fallen8.wal.degraded").Last().Value, "healthy log");
                Assert.IsTrue(collector.Of("fallen8.wal.size").Last().Value > 0d, "the log holds the commit");

                // Inject a flush failure: the WAL opens the file per flush, so replacing it with
                // a DIRECTORY makes the next append fail and trips the D1 sticky fence.
                File.Delete(walPath);
                Directory.CreateDirectory(walPath);

                var degradedCommit = engine.EnqueueTransaction(new CreateVertexTransaction
                {
                    Definition = new VertexDefinition { CreationDate = 1u, Label = "person" }
                });
                degradedCommit.WaitUntilFinished();
                Assert.AreEqual(TransactionState.Finished, degradedCommit.TransactionState,
                    "the commit stays applied in memory");
                Assert.IsFalse(degradedCommit.Durable, "but it is not durable in the log");

                Assert.IsTrue(collector.Sum("fallen8.transaction.nondurable") >= 1d);
                Assert.IsTrue(collector.Sum("fallen8.wal.flush.failures") >= 1d);
                collector.CollectGauges();
                Assert.AreEqual(1d, collector.Of("fallen8.wal.degraded").Last().Value,
                    "the D1 fence is visible without reading a single log line");

                // A successful Save re-establishes durability (ResetToSnapshot clears the fence).
                Directory.Delete(walPath);
                var save = new SaveTransaction { Path = Path.Combine(tempDir, "snap.f8s"), SavePartitions = 1 };
                engine.EnqueueTransaction(save).WaitUntilFinished();

                collector.CollectGauges();
                Assert.AreEqual(0d, collector.Of("fallen8.wal.degraded").Last().Value,
                    "a save returns the gauge to 0");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
            }
        }

        [TestMethod]
        public void CheckpointSaveAndLoad_RecordDurationsAndBytes()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "f8_obs_ckpt_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                using var collector = new Collector();
                using var engine = new Fallen8(TestLoggerFactory.Create());
                CreateVertex(engine);

                var save = new SaveTransaction { Path = Path.Combine(tempDir, "snap.f8s"), SavePartitions = 1 };
                engine.EnqueueTransaction(save).WaitUntilFinished();

                Assert.AreEqual(1, collector.Count("fallen8.checkpoint.save.duration"));
                Assert.IsTrue(collector.Of("fallen8.checkpoint.save.bytes").Single().Value > 0d,
                    "the checkpoint's on-disk bytes are measured");

                using var restored = new Fallen8(TestLoggerFactory.Create());
                restored.EnqueueTransaction(new LoadTransaction { Path = save.ActualPath }).WaitUntilFinished();

                Assert.AreEqual(1, collector.Count("fallen8.checkpoint.load.duration"));
                Assert.IsTrue(collector.Of("fallen8.checkpoint.load.bytes").Single().Value > 0d);
                Assert.AreEqual(0, collector.Count("fallen8.checkpoint.failures"));
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
            }
        }

        [TestMethod]
        public void DisposedEngine_UnregistersItsGauges_ASecondEngineReportsOnlyItself()
        {
            // Other (undisposed) engines from the surrounding suite may also report on this
            // meter name, so the pin is the DELTA: disposing one engine removes exactly one
            // reporter, and the disposed engine's value disappears while the live one's stays.
            using var collector = new Collector();

            var engine1 = new Fallen8(TestLoggerFactory.Create());
            using var engine2 = new Fallen8(TestLoggerFactory.Create());
            for (var i = 0; i < 41; i++)
            {
                CreateVertex(engine1);
            }
            for (var i = 0; i < 43; i++)
            {
                CreateVertex(engine2);
            }

            collector.CollectGauges();
            var before = collector.Of("fallen8.graph.vertices").Select(m => m.Value).ToList();
            Assert.IsTrue(before.Contains(41d) && before.Contains(43d),
                "both engines' per-instance gauges report (distinctive counts)");

            engine1.Dispose();

            var seen = collector.Count("fallen8.graph.vertices");
            collector.CollectGauges();
            var after = collector.Of("fallen8.graph.vertices").Skip(seen).Select(m => m.Value).ToList();
            Assert.AreEqual(before.Count - 1, after.Count,
                "the disposed engine's meter is unregistered - exactly one reporter fewer");
            Assert.IsFalse(after.Contains(41d), "the disposed engine no longer reports");
            Assert.IsTrue(after.Contains(43d), "the live engine still does");
        }

        [TestMethod]
        public void EnabledGate_UnlistenedInstruments_RecordNothing()
        {
            // A listener that enables ONLY the commits counter: the duration histograms stay
            // Enabled == false, so their record paths (and the timestamp stamps behind them)
            // are skipped entirely - the observable half of the hot-path Enabled gate.
            using var listener = new MeterListener();
            var commitCount = 0L;
            var durationCount = 0;
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "NoSQL.GraphDB.Core" &&
                    instrument.Name == "fallen8.transaction.commits")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<Int64>((i, v, t, s) => System.Threading.Interlocked.Add(ref commitCount, v));
            listener.SetMeasurementEventCallback<Double>((i, v, t, s) => System.Threading.Interlocked.Increment(ref durationCount));
            listener.Start();

            using var engine = new Fallen8(TestLoggerFactory.Create());
            CreateVertex(engine);
            CreateVertex(engine);

            Assert.AreEqual(2L, System.Threading.Interlocked.Read(ref commitCount), "the enabled counter records");
            Assert.AreEqual(0, durationCount,
                "no duration histogram has a listener, so nothing is recorded (and no enqueue/execute timestamps are taken)");
        }

        [TestMethod]
        public void EngineAssembly_ReferencesNoOpenTelemetryPackage()
        {
            // The BCL-only acceptance criterion, pinned at runtime: the engine assembly's
            // reference list must never contain an OpenTelemetry assembly (the instruments are
            // System.Diagnostics.* from the shared framework).
            var references = typeof(Fallen8).Assembly.GetReferencedAssemblies();
            foreach (var reference in references)
            {
                Assert.IsFalse(reference.Name.StartsWith("OpenTelemetry", StringComparison.OrdinalIgnoreCase),
                    "fallen-8-core must stay dependency-clean; found " + reference.Name);
            }
        }

        #region gauge accessors + MeasureCheckpointBytes (structural-decomposition Phase 3 pins)

        // The index gauge accessors and MeasureCheckpointBytes are internal/private engine
        // members (the engine declares no InternalsVisibleTo), so - as elsewhere in this suite -
        // the tests reach them by reflection rather than widening visibility.

        private static Int32 IndexCountOf(Fallen8 engine)
        {
            var property = typeof(Fallen8).GetProperty("IndexCountForMetrics",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(property, "Fallen8.IndexCountForMetrics must exist (fallen8.index.count reads it)");
            return (Int32)property.GetValue(engine);
        }

        private static Int64 IndexEntriesOf(Fallen8 engine)
        {
            var property = typeof(Fallen8).GetProperty("IndexEntriesForMetrics",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(property, "Fallen8.IndexEntriesForMetrics must exist (fallen8.index.entries reads it)");
            return (Int64)property.GetValue(engine);
        }

        private static Int64 MeasureCheckpointBytes(String checkpointPath)
        {
            var method = typeof(Fallen8).GetMethod("MeasureCheckpointBytes",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(method, "Fallen8.MeasureCheckpointBytes must exist (checkpoint save/load bytes read it)");
            return (Int64)method.Invoke(null, new Object[] { checkpointPath });
        }

        private static Int64 SumOfFilesWithPrefix(String directory, String namePrefix)
        {
            return Directory.EnumerateFiles(directory, namePrefix + "*").Sum(file => new FileInfo(file).Length);
        }

        [TestMethod]
        public void IndexGauges_TrackIndexCreate_EntryAdd_AndIndexDelete()
        {
            using var engine = new Fallen8(TestLoggerFactory.Create());
            Assert.AreEqual(0, IndexCountOf(engine), "a fresh engine registers no index");
            Assert.AreEqual(0L, IndexEntriesOf(engine));

            Assert.IsTrue(engine.IndexFactory.TryCreateIndex(out var dictionary, "gaugeDict", "DictionaryIndex"));
            Assert.IsTrue(engine.IndexFactory.TryCreateIndex(out var range, "gaugeRange", "RangeIndex"));
            Assert.AreEqual(2, IndexCountOf(engine), "a live create is visible immediately");
            Assert.AreEqual(0L, IndexEntriesOf(engine), "empty indices contribute no keys");

            var id = CreateVertex(engine);
            Assert.IsTrue(engine.TryGetVertex(out var vertex, id));
            dictionary.AddOrUpdate("k1", vertex);
            dictionary.AddOrUpdate("k2", vertex);
            dictionary.AddOrUpdate("k2", vertex); // a re-add under the same key adds no KEY
            range.AddOrUpdate(10, vertex);
            range.AddOrUpdate(20, vertex);
            range.AddOrUpdate(30, vertex);
            Assert.AreEqual(2, IndexCountOf(engine), "entry adds do not change the index count");
            Assert.AreEqual(5L, IndexEntriesOf(engine),
                "the aggregate sums CountOfKeys across all registered indices (2 dict keys + 3 range keys)");

            Assert.IsTrue(engine.IndexFactory.TryDeleteIndex("gaugeDict"));
            Assert.AreEqual(1, IndexCountOf(engine), "a live delete is visible immediately");
            Assert.AreEqual(3L, IndexEntriesOf(engine), "the deleted index's keys leave the aggregate");
        }

        [TestMethod]
        public void MeasureCheckpointBytes_ExcludesTheVersionStampedSiblings_OfALaterSaveToTheSameBasePath()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "f8_obs_bytes_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                using var engine = new Fallen8(TestLoggerFactory.Create());
                CreateVertex(engine);

                var requestedPath = Path.Combine(tempDir, "snap.f8s");
                var firstSave = new SaveTransaction { Path = requestedPath, SavePartitions = 1 };
                engine.EnqueueTransaction(firstSave).WaitUntilFinished();
                Assert.AreEqual(requestedPath, firstSave.ActualPath, "the first save takes the requested path verbatim");

                // With only the first checkpoint on disk, the measurement is the plain sum of every
                // prefix-sharing file (snapshot + partition/index sidecars).
                var firstCheckpointBytes = SumOfFilesWithPrefix(tempDir, "snap.f8s");
                Assert.IsTrue(firstCheckpointBytes > 0L, "the checkpoint wrote measurable files");
                Assert.AreEqual(firstCheckpointBytes, MeasureCheckpointBytes(firstSave.ActualPath));

                // A second save to the SAME base path gets a '#' version stamp instead of
                // overwriting (PersistencyFactory) - its files SHARE the first checkpoint's prefix.
                CreateVertex(engine);
                var secondSave = new SaveTransaction { Path = requestedPath, SavePartitions = 1 };
                engine.EnqueueTransaction(secondSave).WaitUntilFinished();
                StringAssert.StartsWith(secondSave.ActualPath, requestedPath + "#",
                    "a save onto an existing checkpoint version-stamps with '#'");
                Assert.IsTrue(SumOfFilesWithPrefix(tempDir, "snap.f8s") > firstCheckpointBytes,
                    "sanity: the sibling checkpoint's files really extend the first checkpoint's prefix");

                // THE branch pin: re-measuring the FIRST checkpoint still counts only its own
                // files - the '#'-stamped sibling is a DIFFERENT checkpoint and is excluded.
                Assert.AreEqual(firstCheckpointBytes, MeasureCheckpointBytes(firstSave.ActualPath),
                    "the '#'-stamped sibling checkpoint must not be summed into the first checkpoint's size");

                // The second checkpoint measures as exactly its own files.
                var secondPrefix = Path.GetFileName(secondSave.ActualPath);
                var secondCheckpointBytes = SumOfFilesWithPrefix(tempDir, secondPrefix);
                Assert.IsTrue(secondCheckpointBytes > 0L);
                Assert.AreEqual(secondCheckpointBytes, MeasureCheckpointBytes(secondSave.ActualPath));

                // Best-effort contract: an unmeasurable path answers the -1 sentinel, never throws
                // (a metrics detail must not fault a save/load).
                Assert.AreEqual(-1L, MeasureCheckpointBytes(Path.Combine(tempDir, "no-such-dir", "x.f8s")));
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
            }
        }

        #endregion

        [TestMethod]
        public void TagHygiene_NoUserSuppliedStringEverBecomesATagValue()
        {
            const string userLabel = "userSuppliedLabelXYZZY";
            const string userProperty = "userSuppliedKeyXYZZY";
            const string userIndexName = "userIndexNameXYZZY";
            const string userFragmentMarker = "userFragmentXYZZY";

            // BOTH meters: the engine's and the app's (codegen) - the invariant covers every
            // emitted measurement.
            using var collector = new Collector("NoSQL.GraphDB.Core", "NoSQL.GraphDB.App");
            using var engine = new Fallen8(TestLoggerFactory.Create());

            // A battery of operations carrying user strings everywhere the API accepts them.
            var id = CreateVertex(engine, userLabel);
            engine.EnqueueTransaction(new AddPropertyTransaction
            {
                Definition = new PropertyAddDefinition { GraphElementId = id, PropertyId = userProperty, Property = 42 }
            }).WaitUntilFinished();
            Assert.IsTrue(engine.IndexFactory.TryCreateIndex(out _, userIndexName, "DictionaryIndex",
                new Dictionary<string, object>()));
            engine.EnqueueTransaction(new RemoveGraphElementTransaction { GraphElementId = 424242 }).WaitUntilFinished();

            // A codegen compile whose FRAGMENT carries a user string (the App-meter side).
            NoSQL.GraphDB.Core.App.Helper.CodeGenerationHelper.GeneratePathTraverser(out _,
                new NoSQL.GraphDB.App.Controllers.Model.PathSpecification
                {
                    Filter = new NoSQL.GraphDB.App.Controllers.Model.PathFilterSpecification
                    {
                        Vertex = "return (v) => v.Label != \"" + userFragmentMarker + "\";"
                    }
                });

            collector.CollectGauges();

            Assert.IsTrue(collector.Measurements.Count > 0);
            foreach (var measurement in collector.Measurements)
            {
                foreach (var tag in measurement.Tags)
                {
                    var value = tag.Value?.ToString() ?? "";
                    Assert.IsFalse(
                        value.Contains(userLabel) || value.Contains(userProperty) ||
                        value.Contains(userIndexName) || value.Contains(userFragmentMarker),
                        $"metric tag {tag.Key}='{value}' on {measurement.Instrument} must never carry user input");
                }
            }
        }
    }
}
