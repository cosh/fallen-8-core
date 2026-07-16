// MIT License
//
// TransactionManager.cs
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
using System.Diagnostics;
using System.Text;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace NoSQL.GraphDB.Core.Transaction
{
    internal sealed class TransactionManager : IDisposable
    {
        /// <summary>
        ///   The queue of pending transaction tasks. A <see cref="BlockingCollection{T}" /> lets the
        ///   single consumer thread BLOCK while the queue is empty (finding P2) instead of the old
        ///   idle <c>Thread.Sleep(1)</c> spin, which imposed a ~1 ms per-transaction latency floor and
        ///   kept a thread busy-waiting even when the engine was idle.
        /// </summary>
        private readonly BlockingCollection<WorkItem> _transactions = new BlockingCollection<WorkItem>();

        /// <summary>Upper bound on how many transactions a single commit group buffers before it is
        /// flushed, so a long backlog cannot grow the in-memory frame buffer without bound.</summary>
        private const int MaxGroupSize = 4096;

        /// <summary>
        ///   One queued unit of work: the transaction, the completion source the writer completes
        ///   after the group fsync (durable-before-ack - see <see cref="ConsumeLoop"/>), the
        ///   caller's <see cref="TransactionInformation"/>, and whether the transaction buffered a
        ///   durable WAL frame (ANDed with the group flush result to set
        ///   <see cref="TransactionInformation.Durable"/>).
        /// </summary>
        private sealed class WorkItem
        {
            public readonly ATransaction Tx;
            public readonly TaskCompletionSource Completion;
            public readonly TransactionInformation Info;
            public bool BufferedDurable;

            /// <summary>The committed transaction's change descriptor (feature change-feed), captured
            /// at execute time (before the input payload is released) and published by the group
            /// flush after the fsync. Null when the feed is off or the transaction changed nothing.</summary>
            public ChangeFeed.ChangeDescriptor Changes;

            /// <summary>Enqueue timestamp for the commit-latency histogram (feature observability);
            /// 0 when nobody listens - the Enabled gate skips the clock read entirely.</summary>
            public long EnqueueTimestamp;

            /// <summary>The enqueuing request's activity context (feature observability), so the
            /// execute span started on the decoupled writer thread parents to the HTTP request
            /// that enqueued the transaction. Default when no trace listener is attached.</summary>
            public ActivityContext ParentContext;

            /// <summary>The execute span, started in <see cref="ExecuteTransactionBody"/> and
            /// finished by the group flush AFTER the durable tag is known - so its duration covers
            /// execute through durable acknowledgement. Null when unsampled.</summary>
            public Activity Span;

            public WorkItem(ATransaction tx, TaskCompletionSource completion, TransactionInformation info)
            {
                Tx = tx;
                Completion = completion;
                Info = info;
            }
        }

        private readonly ConcurrentDictionary<Guid, TransactionInformation> transactionState = new ConcurrentDictionary<Guid, TransactionInformation>();

        /// <summary>Default ceiling on retained TERMINAL transaction entries (feature
        /// transaction-retention R1): large enough that a caller polling <c>GetTransactionState</c>
        /// straight after completion still finds its entry, small enough to bound memory on an
        /// insert-only workload that never removes (and so never auto-trims).</summary>
        private const int DefaultMaxRetainedTerminalTransactions = 100_000;

        /// <summary>
        ///   FIFO of terminal (<c>Finished</c>/<c>RolledBack</c>) transaction ids in completion order
        ///   (feature transaction-retention R1). Touched ONLY on the single writer thread - terminal
        ///   transitions run in <see cref="SetTransactionState"/> (which the worker calls from
        ///   <see cref="ExecuteTransactionBody"/>) and <see cref="Trim"/> - so it needs no lock of its
        ///   own. When it exceeds <see cref="MaxRetainedTerminalTransactions"/> the oldest ids are popped
        ///   and evicted from <see cref="transactionState"/>, bounding the bookkeeping without a client
        ///   having to call <c>/trim</c>.
        /// </summary>
        private readonly Queue<Guid> _terminalFifo = new Queue<Guid>();

        /// <summary>The active terminal-retention bound; defaults to
        /// <see cref="DefaultMaxRetainedTerminalTransactions"/>. Settable (internal) so a test can lower
        /// it without enqueuing hundreds of thousands of transactions.</summary>
        internal int MaxRetainedTerminalTransactions { get; set; } = DefaultMaxRetainedTerminalTransactions;

        private readonly Fallen8 _f8;

        private readonly ILogger<TransactionManager> _logger;

        /// <summary>
        ///   The single writer thread. Every transaction body runs here and ONLY here, which is what
        ///   upholds the engine's single-writer invariant.
        /// </summary>
        private readonly Thread _worker;

        /// <summary>Guards <see cref="Dispose" /> so it is idempotent.</summary>
        private Boolean _disposed;

        public TransactionManager(Fallen8 f8)
        {
            _f8 = f8;
            _logger = f8.CreateLogger<TransactionManager>();

            _logger.LogInformation("TransactionManager initialized");

            // ONE consumer thread that BLOCKS on the queue (no idle spin) and runs each transaction's
            // task INLINE via RunSynchronously (finding P2). Running the body inline - rather than
            // Start()ing the task on the thread pool and Wait()ing on it, as the old design did -
            // keeps every transaction body on this one thread (single writer; the body can never be
            // inlined onto an enqueuer's Wait() because the task is never scheduled to a TaskScheduler)
            // and removes the second thread the old design consumed per transaction. A waited-on
            // enqueuer still blocks in TransactionInformation.WaitUntilFinished (Task.Wait), which
            // returns only after RunSynchronously has completed the task - preserving the happens-before
            // that publishes the master-store snapshot and the terminal TransactionState/Error to that
            // caller. The thread is a background thread so it never keeps the process alive; Dispose
            // stops it cleanly by completing the queue.
            _worker = new Thread(ConsumeLoop)
            {
                IsBackground = true,
                Name = "Fallen8-Transaction-Writer"
            };
            _worker.Start();
        }

        /// <summary>
        ///   The single-writer consume loop with GROUP COMMIT (feature write-path-throughput). It
        ///   blocks for the first ready transaction, then greedily drains the rest of the currently
        ///   queued work into one batch, executes each body in commit order on THIS thread (single
        ///   writer) buffering its WAL frame without fsyncing, issues ONE fsync for the whole batch,
        ///   and only THEN completes every transaction's completion source. Because completion moves
        ///   strictly after the single fsync, no waiter observes a commit before its WAL entry is
        ///   durable - the durable-before-ack contract is preserved, just amortised. A lone writer with
        ///   an empty queue commits as a group of one and fsyncs immediately, so its latency is
        ///   unchanged. A Save/Load is a hard batch boundary: the pending group is flushed and completed
        ///   before it, and it commits as its own group, because it rewrites/replaces the WAL file.
        /// </summary>
        private void ConsumeLoop()
        {
            foreach (var first in _transactions.GetConsumingEnumerable())
            {
                var group = new List<WorkItem>();
                var item = first;

                try
                {
                    while (true)
                    {
                        if (IsBatchBoundary(item.Tx))
                        {
                            // Finish the accumulated group (durable) BEFORE the boundary tx, so no
                            // buffered frame straddles the Save/Load that rewrites/replaces the log.
                            FlushAndCompleteGroup(group);
                            group = new List<WorkItem>();

                            ExecuteTransactionBody(item);
                            FlushAndCompleteGroup(new List<WorkItem> { item });
                        }
                        else
                        {
                            ExecuteTransactionBody(item);
                            group.Add(item);

                            if (group.Count >= MaxGroupSize)
                            {
                                FlushAndCompleteGroup(group);
                                group = new List<WorkItem>();
                            }
                        }

                        // Drain the rest of the currently-ready work into this batch (non-blocking).
                        if (!_transactions.TryTake(out item, 0))
                        {
                            break;
                        }
                    }

                    FlushAndCompleteGroup(group);
                }
                catch (Exception ex)
                {
                    // Belt-and-suspenders: ExecuteTransactionBody and FlushAndCompleteGroup are each
                    // fully contained, so this should be unreachable. If an unforeseen fault does
                    // escape, complete every still-pending group member so no waiter hangs, then keep
                    // the writer alive (the B6 "the worker survives a faulting transaction" contract).
                    _logger.LogError(ex, "The transaction writer thread caught an unexpected exception in the group-commit loop; completing pending transactions and continuing.");
                    foreach (var pending in group)
                    {
                        FinishSpanSafely(pending);
                        pending.Completion.TrySetResult();
                    }
                }
            }
        }

        /// <summary>Whether a transaction is a hard commit-group boundary (it rewrites/replaces the WAL
        /// file, so no buffered frame may share its group). Save resets the log to the new snapshot;
        /// Load may replay/re-anchor it.</summary>
        private static bool IsBatchBoundary(ATransaction tx)
        {
            return tx is SaveTransaction || tx is LoadTransaction;
        }

        /// <summary>
        ///   Flushes the current commit group's buffered WAL frames with ONE fsync, then completes
        ///   every member's completion source (durable-before-ack - see <see cref="ConsumeLoop"/>).
        ///   A committed member whose frame did not reach disk durably (the flush failed or the
        ///   fence had tripped) has its <see cref="TransactionInformation.Durable"/> set false; it
        ///   stays <c>Finished</c> (applied in memory).
        /// </summary>
        private void FlushAndCompleteGroup(List<WorkItem> group)
        {
            if (group == null || group.Count == 0)
            {
                return;
            }

            bool groupDurable;
            try
            {
                groupDurable = _f8.FlushWal();
            }
            catch (Exception ex)
            {
                // FlushWal is contained (FlushGroup returns false on failure), so this is defensive.
                _logger.LogError(ex, "The write-ahead-log group flush faulted unexpectedly; the group is treated as non-durable.");
                groupDurable = false;
            }

            // Publish the group's change descriptors (feature change-feed) strictly AFTER the group
            // fsync - the durable-before-ack boundary - and BEFORE the completions fire, in commit
            // order. Publish is a non-blocking channel TryWrite (a full inbox becomes a resync for
            // everyone), contained like every other writer-side step. Note the feed mirrors
            // COMMITTED state, not durability: a degraded flush (Durable=false) still publishes,
            // because readers see the commit either way.
            var feed = _f8.ChangeFeed;
            if (feed != null)
            {
                foreach (var item in group)
                {
                    if (item.Changes != null)
                    {
                        try
                        {
                            feed.Publish(item.Changes);
                        }
                        catch (Exception publishEx)
                        {
                            // Publish never throws by contract; defensive containment regardless -
                            // the feed must never fault the writer.
                            _logger.LogError(publishEx, "Publishing a change descriptor to the change feed failed; the event is lost for subscribers.");
                        }

                        item.Changes = null;
                    }
                }
            }

            var metrics = _f8.Metrics;
            metrics?.RecordGroupSize(group.Count);

            foreach (var item in group)
            {
                if (item.Info.TransactionState == TransactionState.Finished && !(item.BufferedDurable && groupDurable))
                {
                    // Committed in memory but not durable in the log (fence tripped / flush failed /
                    // logging suspended). The degraded state is signalled via Durable, not Error
                    // (feature crash-durability-hardening D1); a later Save re-establishes durability.
                    item.Info.Durable = false;
                    metrics?.RecordNonDurable();
                }

                // Commit latency = enqueue to durable acknowledgement, recorded HERE - strictly
                // after the group fsync, just before the completion fires (feature observability).
                // The stamp exists only when the histogram had a listener at enqueue time.
                if (item.EnqueueTimestamp != 0L && metrics != null &&
                    item.Info.TransactionState == TransactionState.Finished)
                {
                    metrics.RecordCommitDuration(item.Tx.GetType().Name,
                        Stopwatch.GetElapsedTime(item.EnqueueTimestamp).TotalSeconds);
                }

                // Finish the execute span with the now-known durability (Finished items only);
                // its duration covers execute through the durable-ack point.
                FinishSpanSafely(item);

                item.Completion.TrySetResult();
            }
        }

        private TransactionInformation SetTransactionState(ATransaction tx, TransactionState state)
        {
            // Guard so the id string + interpolation are not built when Debug is disabled (F14).
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Transaction {TransactionId} ({TransactionType}) state changed to {State}",
                    tx.TransactionId, tx.GetType().Name, state);
            }

            var info = transactionState.AddOrUpdate(tx.TransactionIdGuid,
                                        new TransactionInformation(null) { Transaction = tx, TransactionState = state },
                                        (id, existing) =>
                                        {
                                            existing.TransactionState = state;
                                            return existing;
                                        });

            // Bound terminal-entry retention (R1). SetTransactionState is called ONLY for terminal
            // transitions (Finished/RolledBack) and ONLY on the single writer thread, so the FIFO is
            // maintained here without a lock: record the id, then evict the oldest terminal entries past
            // the bound. A caller holding the returned TransactionInformation is unaffected (it reads the
            // instance directly); only a GetTransactionState(txId) lookup of a long-superseded id stops
            // resolving (-> NotExist), exactly as a trimmed id already does.
            _terminalFifo.Enqueue(tx.TransactionIdGuid);
            while (_terminalFifo.Count > MaxRetainedTerminalTransactions)
            {
                var oldest = _terminalFifo.Dequeue();
                if (transactionState.TryRemove(oldest, out var evicted))
                {
                    evicted.Transaction?.Cleanup();
                }
            }

            return info;
        }

        /// <summary>
        ///   Executes ONE transaction body on the single writer thread: TryExecute, publish the
        ///   terminal state, buffer its WAL frame (no fsync - the group flush does that), release the
        ///   heavy input, and run auto-trim. It does NOT fsync and does NOT complete the transaction's
        ///   completion source; the surrounding group-commit loop does both after the batch flush.
        ///   Fully contained (never throws), so the writer survives any faulting transaction (B6).
        /// </summary>
        private void ExecuteTransactionBody(WorkItem item)
        {
            var tx = item.Tx;
            var transactionType = tx.GetType().Name;

            // Feature observability. The execute span parents to the ENQUEUING request's context
            // (captured in AddTransaction), so a slow REST mutation shows its queue wait and
            // execution even though the body runs on this decoupled writer thread. The span is
            // finished by the group flush once durability is known. The execute-duration
            // timestamp is Enabled-gated: no clock read when nobody listens.
            var metrics = _f8.Metrics;
            item.Span = StartExecuteSpanSafely(item, transactionType);
            var executeStart = metrics != null && metrics.ExecuteDurationEnabled
                ? Stopwatch.GetTimestamp()
                : 0L;

            bool succeeded;
            try
            {
                succeeded = tx.TryExecute(_f8);
            }
            catch (Exception ex)
            {
                // A faulting transaction must not tear down the single worker thread. Contain the
                // failure, roll the transaction back and keep processing the queue.
                _logger.LogError(ex, "Transaction {TransactionId} ({TransactionType}) threw during execution and will be rolled back.",
                    tx.TransactionId, transactionType);

                RollbackSafely(tx, transactionType);

                // Keep the terminal state RolledBack (as for a clean TryExecute()==false), but ALSO
                // record the fault on the caller's TransactionInformation so a waited-on caller can
                // distinguish a genuine exception from a clean rollback, and classify it as
                // InternalError (mapped to 500). Set before the completion source is set in the group
                // flush, so it is visible under the same happens-before (B6).
                var faultedInfo = SetTransactionState(tx, TransactionState.RolledBack);
                faultedInfo.Error = ex;
                faultedInfo.FailureReason = TransactionFailureReason.InternalError;

                if (executeStart != 0L)
                {
                    metrics.RecordExecuteDuration(transactionType, Stopwatch.GetElapsedTime(executeStart).TotalSeconds);
                }
                metrics?.RecordRollback(transactionType, TransactionFailureReason.InternalError);
                TagSpanSafely(item.Span, "transaction.state", nameof(TransactionState.RolledBack), markError: true);

                ReleaseInputsSafely(tx, transactionType);
                return;
            }

            if (executeStart != 0L)
            {
                metrics.RecordExecuteDuration(transactionType, Stopwatch.GetElapsedTime(executeStart).TotalSeconds);
            }

            if (succeeded)
            {
                // The commit counter is recorded strictly AFTER the terminal state is published
                // and the WAL frame buffered would be even safer - but the record helper is
                // exception-contained (a hostile listener cannot fault the writer), so ordering
                // here is a non-issue; keep it adjacent to the state for readability.
                metrics?.RecordCommit(transactionType);
                TagSpanSafely(item.Span, "transaction.state", nameof(TransactionState.Finished), markError: false);

                SetTransactionState(tx, TransactionState.Finished);

                // Buffer this committed transaction's WAL frame (no fsync; the group flush fsyncs once
                // for the whole batch). Records whether it buffered durably-so-far; the group flush
                // result is ANDed with this to set Durable. A no-op when the WAL is disabled.
                item.BufferedDurable = BufferCommittedTransactionSafely(tx, transactionType);

                // Capture the committed transaction's change descriptor (feature change-feed) while
                // the transaction still holds its state - BEFORE ReleaseAfterCompletion drops the
                // input payload. A null check when the feed is off; primitives only when it is on.
                item.Changes = CaptureChangesSafely(tx, transactionType);

                // Drop the heavy input payload now that the transaction is committed (M3). The captured
                // created-models are intentionally kept for a waited-on caller to read.
                ReleaseInputsSafely(tx, transactionType);

                // A committed element removal may have pushed the tombstone count over the
                // auto-compaction threshold (M4). Any auto-trim marker it logs is buffered in commit
                // order with the surrounding group's frames.
                if (tx.TriggersAutoTrim)
                {
                    MaybeAutoTrimSafely(tx, transactionType);
                }
            }
            else
            {
                RollbackSafely(tx, transactionType);
                var rolledBackInfo = SetTransactionState(tx, TransactionState.RolledBack);
                rolledBackInfo.FailureReason = tx.FailureReason;

                metrics?.RecordRollback(transactionType, tx.FailureReason);
                TagSpanSafely(item.Span, "transaction.state", nameof(TransactionState.RolledBack), markError: false);

                ReleaseInputsSafely(tx, transactionType);
            }
        }

        /// <summary>
        ///   Starts the execute span with the enqueue-time parent context, CONTAINED (feature
        ///   observability): ActivitySource.StartActivity invokes listener callbacks inline, and
        ///   a hostile/buggy listener must never fault the single writer (B6). The span also
        ///   deliberately does NOT stay current on this thread: StartActivity sets
        ///   Activity.Current, and because the span outlives this method (the group flush
        ///   finishes it), leaving it current would (a) mis-parent later same-group transactions
        ///   that carry NO explicit parent (StartActivity falls back to Activity.Current when the
        ///   parent context is default) and (b) leave a stopped span current after the flush.
        ///   Restoring the previous Current immediately keeps the writer thread's ambient
        ///   context clean.
        /// </summary>
        private static Activity StartExecuteSpanSafely(WorkItem item, String transactionType)
        {
            if (!Diagnostics.Fallen8Diagnostics.Source.HasListeners())
            {
                return null;
            }

            try
            {
                var previous = Activity.Current;
                var span = Diagnostics.Fallen8Diagnostics.Source.StartActivity(
                    "fallen8.transaction.execute", ActivityKind.Internal, item.ParentContext);
                if (span != null)
                {
                    Activity.Current = previous;
                    span.SetTag("transaction.type", transactionType);
                }
                return span;
            }
            catch
            {
                // A throwing listener must never fault the writer thread.
                return null;
            }
        }

        /// <summary>Sets a span tag (and optionally the error status), contained like every
        /// other observability call on the writer thread.</summary>
        private static void TagSpanSafely(Activity span, String key, Object value, Boolean markError)
        {
            if (span == null)
            {
                return;
            }

            try
            {
                span.SetTag(key, value);
                if (markError)
                {
                    span.SetStatus(ActivityStatusCode.Error);
                }
            }
            catch
            {
                // contained
            }
        }

        /// <summary>Finishes the execute span at the durable-ack point, contained. The durable
        /// tag is stamped only for COMMITTED transactions (durability is meaningless for a
        /// rollback - nothing was applied, so nothing needed the log).</summary>
        private static void FinishSpanSafely(WorkItem item)
        {
            var span = item.Span;
            if (span == null)
            {
                return;
            }

            item.Span = null;
            try
            {
                if (item.Info.TransactionState == TransactionState.Finished)
                {
                    span.SetTag("transaction.durable", item.Info.Durable);
                }
                span.Dispose();
            }
            catch
            {
                // contained
            }
        }

        /// <summary>
        ///   Buffers a committed transaction's WAL frame (feature write-path-throughput) and returns
        ///   whether it is durable-so-far: <c>true</c> when buffered (or the WAL is disabled),
        ///   <c>false</c> when logging is suspended/degraded (the fence has tripped, D1, or the log is
        ///   anchored and awaiting its paired Load, D3). The final <see cref="TransactionInformation.Durable"/>
        ///   is this ANDed with the group flush result. Never faults the worker.
        /// </summary>
        private bool BufferCommittedTransactionSafely(ATransaction tx, String transactionType)
        {
            try
            {
                return _f8.LogCommittedTransaction(tx);
            }
            catch (Exception logEx)
            {
                // Buffering must never fault the single worker thread. The transaction is already
                // applied in memory and stays committed (Finished); its log durability is degraded,
                // signalled via Durable (set false by the group flush) rather than Error.
                _logger.LogError(logEx, "Buffering transaction {TransactionId} ({TransactionType}) into the write-ahead log failed; the transaction stays committed but is not durable in the log.",
                    tx.TransactionId, transactionType);
                return false;
            }
        }

        /// <summary>
        ///   Captures a committed transaction's change descriptor for the feed (feature
        ///   change-feed). A null check when the feed is off; primitives-only capture when on.
        ///   Never faults the worker: a throwing DescribeChanges loses that transaction's events
        ///   (logged), never the commit.
        /// </summary>
        private ChangeFeed.ChangeDescriptor CaptureChangesSafely(ATransaction tx, String transactionType)
        {
            if (_f8.ChangeFeed == null)
            {
                return null;
            }

            try
            {
                var builder = new ChangeFeed.ChangeDescriptor.Builder();
                tx.DescribeChanges(_f8, builder);
                return builder.BuildOrNull();
            }
            catch (Exception captureEx)
            {
                _logger.LogError(captureEx, "Capturing change-feed descriptors for transaction {TransactionId} ({TransactionType}) failed; its events are lost for subscribers.",
                    tx.TransactionId, transactionType);
                return null;
            }
        }

        private void ReleaseInputsSafely(ATransaction tx, String transactionType)
        {
            try
            {
                tx.ReleaseAfterCompletion();
            }
            catch (Exception releaseEx)
            {
                // Releasing transient input state must never fault the worker thread; the data is
                // freed at the latest by the next Trim regardless.
                _logger.LogError(releaseEx, "Releasing input state of transaction {TransactionId} ({TransactionType}) after completion failed",
                    tx.TransactionId, transactionType);
            }
        }

        private void MaybeAutoTrimSafely(ATransaction tx, String transactionType)
        {
            try
            {
                _f8.MaybeAutoTrim();
            }
            catch (Exception trimEx)
            {
                // An auto-compaction failure must never fault the worker thread. The store is left
                // correct (soft-deleted tombstones remain); the next removal or explicit Trim retries.
                _logger.LogError(trimEx, "Auto-trim after transaction {TransactionId} ({TransactionType}) failed",
                    tx.TransactionId, transactionType);
            }
        }

        private void RollbackSafely(ATransaction tx, String transactionType)
        {
            try
            {
                tx.Rollback(_f8);
            }
            catch (Exception rollbackEx)
            {
                // Never let a failing rollback escape onto the worker thread.
                _logger.LogError(rollbackEx, "Rollback of transaction {TransactionId} ({TransactionType}) failed",
                    tx.TransactionId, transactionType);
            }
        }

        public TransactionInformation AddTransaction(ATransaction tx)
        {
            // Demoted to Debug and guarded (F14): no id string / interpolation per enqueue at the
            // default level. The rare failure-path logs stay as LogError/LogWarning.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Adding transaction {TransactionId} ({TransactionType}) to queue",
                    tx.TransactionId, tx.GetType().Name);
            }

            // The writer completes this AFTER the group fsync (feature write-path-throughput).
            // RunContinuationsAsynchronously so completing it on the writer thread never inlines an
            // awaiter's continuation onto the writer (which would stall the single writer).
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var txInfo = new TransactionInformation(completion.Task)
            {
                Transaction = tx,
                TransactionState = TransactionState.Enqueued
            };

            // Register the TransactionInformation BEFORE handing the work to the consumer. The consumer
            // wakes immediately (it blocks on the queue rather than polling), so it can reach
            // SetTransactionState before this method returns. Publishing the entry first means the
            // worker's SetTransactionState takes its AddOrUpdate UPDATE path and mutates THIS exact
            // instance, so a waited-on caller observes the terminal TransactionState/Error/Durable on
            // the instance returned here (B6). The transaction id is a fresh GUID, so the indexer
            // assignment simply publishes it unconditionally.
            transactionState[tx.TransactionIdGuid] = txInfo;

            var item = new WorkItem(tx, completion, txInfo);

            // Feature observability, Enabled-gated: the enqueue timestamp (for the commit-latency
            // histogram) is taken ONLY when a listener is attached, and the enqueuing activity
            // context (for cross-thread span parenting) only when something samples.
            var metrics = _f8.Metrics;
            if (metrics != null && metrics.CommitDurationEnabled)
            {
                item.EnqueueTimestamp = Stopwatch.GetTimestamp();
            }
            if (Diagnostics.Fallen8Diagnostics.Source.HasListeners())
            {
                item.ParentContext = Activity.Current?.Context ?? default;
            }

            _transactions.Add(item);

            return txInfo;
        }

        /// <summary>Transactions waiting for the single writer, for the queue-depth gauge
        /// (feature observability). 0 during teardown races - the gauge callback must never throw.</summary>
        internal int QueueDepth
        {
            get
            {
                try
                {
                    return _transactions.Count;
                }
                catch (ObjectDisposedException)
                {
                    return 0;
                }
            }
        }

        public TransactionState GetState(String txId)
        {
            // Resolve the string id via a Try*-style parse (F14: the id is a Guid internally). An
            // unparseable or unknown id is simply NotExist - the same answer a trimmed/evicted id gives.
            if (Guid.TryParse(txId, out var id) && transactionState.TryGetValue(id, out var info))
            {
                return info.TransactionState;
            }

            return TransactionState.NotExist;
        }

        public void Trim()
        {
            // Reclaim BOTH terminal states, not just Finished (finding P9). A rolled-back transaction
            // is just as done as a finished one, yet the old filter only removed Finished entries, so
            // RolledBack ones accumulated in the dictionary without bound under a fault-heavy or
            // remove-heavy workload. M3 already drops each transaction's heavy INPUT at its terminal
            // state; this reclaims the residual TransactionInformation entry itself. A caller that
            // still holds its TransactionInformation reference keeps reading TransactionState/Error
            // (B6 observability); only the GetTransactionState dictionary lookup stops resolving a
            // trimmed id (returning NotExist), exactly as it already did for trimmed Finished ids.
            var toBeTrimmed = transactionState
                .Where(_ => _.Value.TransactionState.Equals(TransactionState.Finished)
                         || _.Value.TransactionState.Equals(TransactionState.RolledBack))
                .Select(_ => _.Key).ToList();

            if (toBeTrimmed.Count > 0)
            {
                _logger.LogInformation("Trimming {Count} terminal (finished or rolled-back) transactions", toBeTrimmed.Count);
            }

            foreach (var aTxId in toBeTrimmed)
            {
                TransactionInformation txInfo;
                transactionState.TryRemove(aTxId, out txInfo);
                txInfo.Transaction.Cleanup();
            }

            // A full Trim removed every terminal entry the FIFO tracked, so its ids are now stale.
            // Clear it so it does not carry dead ids (feature transaction-retention R1). Runs on the
            // writer (TrimTransaction/Load/MaybeAutoTrim) or single-threaded during WAL-replay bootstrap,
            // the same threads that enqueue into the FIFO, so no lock is needed.
            _terminalFifo.Clear();
        }

        /// <summary>
        ///   Stops the single writer thread cleanly (finding P2). Marks the queue complete so the
        ///   consumer drains any already-queued transactions and then exits its loop, and joins the
        ///   thread so a caller that disposes the engine gets a deterministic teardown. Idempotent.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            // Stop accepting new work; the consumer finishes what is queued and then leaves the loop.
            _transactions.CompleteAdding();

            if (Thread.CurrentThread == _worker)
            {
                // A transaction body disposed the engine (no such path exists today - this is a
                // guard). We are ON the worker thread, still inside GetConsumingEnumerable, so we must
                // neither join ourselves (deadlock) nor dispose the collection we are still
                // enumerating: the next MoveNext would throw ObjectDisposedException OUTSIDE the
                // ConsumeLoop try/catch and kill the worker. CompleteAdding above lets the loop exit
                // cleanly once the queue drains; leave the collection for the GC.
                return;
            }

            // Join so teardown is deterministic. The thread is a background thread, so a
            // pathologically slow transaction can never block process exit; the timeout just bounds
            // the synchronous Dispose call.
            if (_worker.Join(TimeSpan.FromSeconds(5)))
            {
                // Dispose the collection ONLY now that the consumer has certainly stopped using it
                // (worker is not this thread AND it has joined), so no MoveNext can race the Dispose.
                _transactions.Dispose();
            }
            else
            {
                _logger.LogWarning("The transaction writer thread did not stop within the shutdown timeout.");
            }
        }
    }
}
