// MIT License
//
// Fallen8Metrics.cs
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
using System.Diagnostics.Metrics;

namespace NoSQL.GraphDB.Core.Diagnostics
{
    /// <summary>
    ///   The engine's metric instruments (feature observability), BCL only
    ///   (<see cref="System.Diagnostics.Metrics.Meter"/> — no package reference).
    ///
    ///   <para>PER-ENGINE, not static: the observable gauges capture the engine instance
    ///   (element counts, queue depth, WAL state), so a static meter would keep disposed test
    ///   engines alive and double-report. Constructed by the <c>Fallen8</c> constructor and
    ///   disposed by <c>Fallen8.Dispose</c> BEFORE the transaction manager (a gauge callback
    ///   runs on the exporter's collection thread and must never observe torn-down state);
    ///   disposing the <see cref="Meter"/> unregisters every instrument.</para>
    ///
    ///   <para>Hot-path discipline: the <c>*Enabled</c> probes let the transaction pipeline
    ///   skip <c>Stopwatch.GetTimestamp()</c> stamps entirely when nobody listens, so the
    ///   uninstrumented cost stays ~zero. Gauge callbacks read only atomically published
    ///   values and are exception-contained.</para>
    ///
    ///   <para>TAG HYGIENE (hard invariant, pinned by test): no tag value may originate from
    ///   user input. Tags carry .NET type names and enum names only; anything user-named
    ///   (labels, index names, property keys) belongs to <c>GET /statistics</c>, never here.</para>
    /// </summary>
    internal sealed class Fallen8Metrics : IDisposable
    {
        /// <summary>The meter name (same as the trace source, per OTel convention).</summary>
        internal const String MeterName = Fallen8Diagnostics.SourceName;

        private readonly Meter _meter;

        private readonly Counter<Int64> _commits;
        private readonly Counter<Int64> _rollbacks;
        private readonly Counter<Int64> _nonDurable;
        private readonly Counter<Int64> _walFlushFailures;
        private readonly Counter<Int64> _checkpointFailures;

        private readonly Histogram<Double> _commitDuration;
        private readonly Histogram<Double> _executeDuration;
        private readonly Histogram<Int64> _groupSize;
        private readonly Histogram<Double> _walFlushDuration;
        private readonly Histogram<Double> _checkpointSaveDuration;
        private readonly Histogram<Int64> _checkpointSaveBytes;
        private readonly Histogram<Double> _checkpointLoadDuration;
        private readonly Histogram<Int64> _checkpointLoadBytes;

        internal Fallen8Metrics(Fallen8 engine)
        {
            _meter = new Meter(MeterName);

            _commits = _meter.CreateCounter<Int64>("fallen8.transaction.commits", "{transaction}",
                "Committed (Finished) transactions.");
            _rollbacks = _meter.CreateCounter<Int64>("fallen8.transaction.rollbacks", "{transaction}",
                "Rolled-back transactions, tagged with the failure reason.");
            _nonDurable = _meter.CreateCounter<Int64>("fallen8.transaction.nondurable", "{transaction}",
                "Transactions committed in memory whose WAL frame did not reach disk durably (degraded log).");
            _commitDuration = _meter.CreateHistogram<Double>("fallen8.transaction.commit.duration", "s",
                "Enqueue to durable acknowledgement (the group fsync completes before this is recorded).");
            _executeDuration = _meter.CreateHistogram<Double>("fallen8.transaction.execute.duration", "s",
                "The transaction body's execution on the single writer thread.");
            _groupSize = _meter.CreateHistogram<Int64>("fallen8.transaction.group.size", "{transaction}",
                "Group-commit batch size at flush.");

            _walFlushDuration = _meter.CreateHistogram<Double>("fallen8.wal.flush.duration", "s",
                "Duration of the write-ahead log group flush (one fsync per commit group).");
            _walFlushFailures = _meter.CreateCounter<Int64>("fallen8.wal.flush.failures", "{failure}",
                "Write-ahead log group flushes that did not reach disk durably.");

            _checkpointSaveDuration = _meter.CreateHistogram<Double>("fallen8.checkpoint.save.duration", "s",
                "Checkpoint save duration.");
            _checkpointSaveBytes = _meter.CreateHistogram<Int64>("fallen8.checkpoint.save.bytes", "By",
                "Bytes written per checkpoint save (snapshot + partitions + index sidecars).");
            _checkpointLoadDuration = _meter.CreateHistogram<Double>("fallen8.checkpoint.load.duration", "s",
                "Checkpoint load duration (including any WAL replay).");
            _checkpointLoadBytes = _meter.CreateHistogram<Int64>("fallen8.checkpoint.load.bytes", "By",
                "Bytes read per checkpoint load.");
            _checkpointFailures = _meter.CreateCounter<Int64>("fallen8.checkpoint.failures", "{failure}",
                "Failed checkpoint operations, tagged operation=save|load.");

            // Observable gauges: every callback reads only atomically published values (O(1)
            // counters, flags, file length) and is exception-contained - a collection racing a
            // teardown must never throw into the exporter.
            _meter.CreateObservableGauge("fallen8.transaction.queue.depth",
                () => Guarded(() => (Int64)engine.TransactionQueueDepthForMetrics), "{transaction}",
                "Transactions waiting for the single writer.");
            _meter.CreateObservableGauge("fallen8.wal.degraded",
                () => Guarded(() => engine.WalDegradedForMetrics ? 1L : 0L), null,
                "1 while the WAL failure fence has tripped or an anchored log awaits its paired load (the metric face of DurabilityDegraded).");
            _meter.CreateObservableGauge("fallen8.wal.size",
                () => Guarded(() => engine.WalSizeForMetrics), "By",
                "Current write-ahead log file length.");
            _meter.CreateObservableGauge("fallen8.graph.vertices",
                () => Guarded(() => (Int64)engine.VertexCount), "{vertex}",
                "Live vertices.");
            _meter.CreateObservableGauge("fallen8.graph.edges",
                () => Guarded(() => (Int64)engine.EdgeCount), "{edge}",
                "Live edges.");
            _meter.CreateObservableGauge("fallen8.index.count",
                () => Guarded(() => (Int64)engine.IndexCountForMetrics), "{index}",
                "Registered indices.");
            _meter.CreateObservableGauge("fallen8.index.entries",
                () => Guarded(() => engine.IndexEntriesForMetrics), "{entry}",
                "Total keys across all indices (aggregate only - per-index detail lives in GET /statistics behind auth).");
        }

        private static Int64 Guarded(Func<Int64> read)
        {
            try
            {
                return read();
            }
            catch
            {
                // A collection racing engine teardown reports 0 instead of faulting the exporter.
                return 0L;
            }
        }

        #region hot-path Enabled probes

        internal Boolean CommitDurationEnabled => _commitDuration.Enabled;

        internal Boolean ExecuteDurationEnabled => _executeDuration.Enabled;

        internal Boolean WalFlushDurationEnabled => _walFlushDuration.Enabled;

        #endregion

        #region record helpers (tag values are TYPE/ENUM NAMES only - never user input)

        // CONTAINMENT: Counter.Add / Histogram.Record invoke MeterListener measurement
        // callbacks INLINE and the BCL does not swallow their exceptions. Most record helpers
        // run on the single writer thread, whose containment contract (B6: the worker survives
        // any faulting transaction) must hold for a hostile/buggy listener too - so every
        // helper catches. Observability must never fault the observed.

        internal void RecordCommit(String transactionType)
        {
            try
            {
                _commits.Add(1, new KeyValuePair<String, Object>("transaction.type", transactionType));
            }
            catch { /* a throwing listener must never fault the writer */ }
        }

        internal void RecordRollback(String transactionType, Transaction.TransactionFailureReason reason)
        {
            try
            {
                _rollbacks.Add(1,
                    new KeyValuePair<String, Object>("transaction.type", transactionType),
                    new KeyValuePair<String, Object>("failure.reason", reason.ToString()));
            }
            catch { /* contained */ }
        }

        internal void RecordExecuteDuration(String transactionType, Double seconds)
        {
            try
            {
                _executeDuration.Record(seconds, new KeyValuePair<String, Object>("transaction.type", transactionType));
            }
            catch { /* contained */ }
        }

        internal void RecordCommitDuration(String transactionType, Double seconds)
        {
            try
            {
                _commitDuration.Record(seconds, new KeyValuePair<String, Object>("transaction.type", transactionType));
            }
            catch { /* contained */ }
        }

        internal void RecordGroupSize(Int32 size)
        {
            try
            {
                _groupSize.Record(size);
            }
            catch { /* contained */ }
        }

        internal void RecordNonDurable()
        {
            try
            {
                _nonDurable.Add(1);
            }
            catch { /* contained */ }
        }

        internal void RecordWalFlushDuration(Double seconds)
        {
            try
            {
                _walFlushDuration.Record(seconds);
            }
            catch { /* contained */ }
        }

        internal void RecordWalFlushFailure()
        {
            try
            {
                _walFlushFailures.Add(1);
            }
            catch { /* contained */ }
        }

        internal void RecordCheckpointSave(Double seconds, Int64 bytes)
        {
            try
            {
                _checkpointSaveDuration.Record(seconds);
                if (bytes >= 0)
                {
                    _checkpointSaveBytes.Record(bytes);
                }
            }
            catch { /* contained */ }
        }

        internal void RecordCheckpointLoad(Double seconds, Int64 bytes)
        {
            try
            {
                _checkpointLoadDuration.Record(seconds);
                if (bytes >= 0)
                {
                    _checkpointLoadBytes.Record(bytes);
                }
            }
            catch { /* contained */ }
        }

        internal void RecordCheckpointFailure(String operation)
        {
            try
            {
                _checkpointFailures.Add(1, new KeyValuePair<String, Object>("operation", operation));
            }
            catch { /* contained */ }
        }

        #endregion

        public void Dispose()
        {
            // Unregisters every instrument, including the gauge callbacks capturing the engine.
            _meter.Dispose();
        }
    }
}
