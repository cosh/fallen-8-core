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
    internal class TransactionManager
    {
        private readonly ConcurrentQueue<Task> transactions = new ConcurrentQueue<Task>();

        private readonly ConcurrentDictionary<String, TransactionInformation> transactionState = new ConcurrentDictionary<String, TransactionInformation>();

        private readonly Fallen8 _f8;

        private readonly ILogger<TransactionManager> _logger;

        public TransactionManager(Fallen8 f8)
        {
            _f8 = f8;
            _logger = f8.CreateLogger<TransactionManager>();

            _logger.LogInformation("TransactionManager initialized");

            Action action = () =>
            {
                while (true)
                {
                    Task transactionTask;
                    while (transactions.TryDequeue(out transactionTask))
                    {
                        transactionTask.Start();
                        transactionTask.Wait();
                    }

                    Thread.Sleep(1);
                }
            };

            Task.Run(action);
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

            transactions.Enqueue(task);

            var txInfo = new TransactionInformation(task)
            {
                Transaction = tx,
                TransactionState = TransactionState.Enqueued
            };

            transactionState.TryAdd(tx.TransactionId, txInfo);

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
            var toBeTrimmed = transactionState.Where(_ => _.Value.TransactionState.Equals(TransactionState.Finished)).Select(_ => _.Key).ToList();

            if (toBeTrimmed.Count > 0)
            {
                _logger.LogInformation("Trimming {Count} finished transactions", toBeTrimmed.Count);
            }

            foreach (var aTxId in toBeTrimmed)
            {
                TransactionInformation txInfo;
                transactionState.TryRemove(aTxId, out txInfo);
                txInfo.Transaction.Cleanup();
            }
        }
    }
}
