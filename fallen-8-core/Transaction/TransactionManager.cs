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
        private readonly BlockingCollection<Task> _transactions = new BlockingCollection<Task>();

        private readonly ConcurrentDictionary<String, TransactionInformation> transactionState = new ConcurrentDictionary<String, TransactionInformation>();

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
        ///   The single-writer consume loop. Blocks on the queue and runs each transaction inline.
        /// </summary>
        private void ConsumeLoop()
        {
            // GetConsumingEnumerable blocks until an item is available and ends once the queue is
            // both marked complete AND drained, so the loop exits cleanly on shutdown with no spin.
            foreach (var transactionTask in _transactions.GetConsumingEnumerable())
            {
                try
                {
                    // Run the transaction body on THIS thread (single writer). ProcessTransaction
                    // already contains every failure internally (it never rethrows), so the task
                    // completes successfully and a waiting Task.Wait() returns cleanly. This guard is
                    // a belt-and-suspenders so that no unforeseen fault can ever tear the writer down
                    // (the same "the worker must survive a faulting transaction" contract as B6).
                    transactionTask.RunSynchronously();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "The transaction writer thread caught an unexpected exception while running a transaction; it keeps processing the queue.");
                }
            }
        }

        private TransactionInformation SetTransactionState(ATransaction tx, TransactionState state)
        {
            _logger.LogDebug("Transaction {TransactionId} ({TransactionType}) state changed to {State}",
                tx.TransactionId, tx.GetType().Name, state);

            return transactionState.AddOrUpdate(tx.TransactionId,
                                        new TransactionInformation(null) { Transaction = tx, TransactionState = state },
                                        (id, info) =>
                                        {
                                            info.TransactionState = state;
                                            return info;
                                        });
        }

        private void ProcessTransaction(Object transactionObj)
        {
            ATransaction tx = (ATransaction)transactionObj;

            var stopwatch = Stopwatch.StartNew();
            var transactionType = tx.GetType().Name;

            _logger.LogInformation("Starting execution of transaction {TransactionId} ({TransactionType})",
                tx.TransactionId, transactionType);

            //do some work
            bool succeeded;
            try
            {
                succeeded = tx.TryExecute(_f8);
            }
            catch (Exception ex)
            {
                // A faulting transaction must not tear down the single worker thread. Contain the
                // failure, roll the transaction back and keep processing the queue.
                stopwatch.Stop();
                _logger.LogError(ex, "Transaction {TransactionId} ({TransactionType}) threw during execution and will be rolled back (execution time: {ElapsedMilliseconds}ms)",
                    tx.TransactionId, transactionType, stopwatch.ElapsedMilliseconds);

                RollbackSafely(tx, transactionType);

                // Keep the terminal state RolledBack (as for a clean TryExecute()==false), but ALSO
                // record the fault on the same TransactionInformation instance the caller holds, so a
                // waited-on caller can distinguish a genuine exception from a clean rollback. Set in
                // place before the task completes so Task.Wait() publishes it (B6 follow-up).
                var faultedInfo = SetTransactionState(tx, TransactionState.RolledBack);
                faultedInfo.Error = ex;

                // The rollback above has already run and the terminal state (with Error) is set, so
                // the heavy input payload can be dropped now instead of lingering until Trim (M3).
                // This releases ONLY the input; the TransactionInformation entry, its state and its
                // Error stay in place and readable.
                ReleaseInputsSafely(tx, transactionType);
                return;
            }

            if (succeeded)
            {
                stopwatch.Stop();
                _logger.LogInformation("Transaction {TransactionId} ({TransactionType}) finished successfully in {ElapsedMilliseconds}ms",
                    tx.TransactionId, transactionType, stopwatch.ElapsedMilliseconds);
                SetTransactionState(tx, TransactionState.Finished);

                // Drop the heavy input payload now that the transaction is committed (M3). The
                // captured created-models are intentionally kept for a waited-on caller to read.
                ReleaseInputsSafely(tx, transactionType);

                // A committed element removal may have pushed the tombstone count over the
                // auto-compaction threshold (M4). Checking here - after commit, on the single
                // writer thread, and only for removal transactions (which hold no created-models) -
                // keeps reclamation off the rollback-sensitive removal path.
                if (tx.TriggersAutoTrim)
                {
                    MaybeAutoTrimSafely(tx, transactionType);
                }
            }
            else
            {
                stopwatch.Stop();
                _logger.LogWarning("Transaction {TransactionId} ({TransactionType}) failed and will be rolled back (execution time: {ElapsedMilliseconds}ms)",
                    tx.TransactionId, transactionType, stopwatch.ElapsedMilliseconds);
                RollbackSafely(tx, transactionType);
                SetTransactionState(tx, TransactionState.RolledBack);
                ReleaseInputsSafely(tx, transactionType);
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
            _logger.LogInformation("Adding transaction {TransactionId} ({TransactionType}) to queue",
                tx.TransactionId, tx.GetType().Name);

            var task = new Task(ProcessTransaction, tx);

            var txInfo = new TransactionInformation(task)
            {
                Transaction = tx,
                TransactionState = TransactionState.Enqueued
            };

            // Register the TransactionInformation BEFORE handing the task to the consumer. The P2
            // consumer wakes immediately (it blocks on the queue rather than polling lazily), so it
            // can reach SetTransactionState before this method returns. Publishing the entry first
            // means the worker's SetTransactionState takes its AddOrUpdate UPDATE path and mutates
            // THIS exact instance, so a waited-on caller observes the terminal TransactionState and
            // Error on the instance returned here (B6). The transaction id is a fresh GUID, so there
            // is never a real collision; the indexer assignment simply publishes it unconditionally.
            transactionState[tx.TransactionId] = txInfo;

            _transactions.Add(task);

            return txInfo;
        }

        public TransactionState GetState(String txId)
        {
            TransactionInformation info;
            if (transactionState.TryGetValue(txId, out info))
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

            // Join so teardown is deterministic - but never join the writer to itself (no transaction
            // disposes the engine, so this is only a guard). The thread is a background thread, so a
            // pathologically slow transaction can never block process exit; the timeout just bounds
            // the synchronous Dispose call.
            if (Thread.CurrentThread != _worker && !_worker.Join(TimeSpan.FromSeconds(5)))
            {
                _logger.LogWarning("The transaction writer thread did not stop within the shutdown timeout.");
            }
            else
            {
                // Only safe to dispose the collection once the consumer has certainly stopped using it.
                _transactions.Dispose();
            }
        }
    }
}
