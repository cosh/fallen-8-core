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
using System.Text;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;

namespace NoSQL.GraphDB.Core.Transaction
{
    internal class TransactionManager
    {
        private readonly ConcurrentQueue<Task> transactions = new ConcurrentQueue<Task>();

        private readonly ConcurrentDictionary<String, TransactionInformation> transactionState = new ConcurrentDictionary<String, TransactionInformation>();

        private readonly Fallen8 _f8;

        public TransactionManager(Fallen8 f8)
        {
            _f8 = f8;

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

            //do some work
            if (tx.TryExecute(_f8))
            {
                SetTransactionState(tx, TransactionState.Finished);
            }
            else
            {
                tx.Rollback(_f8);
                SetTransactionState(tx, TransactionState.RolledBack);
            }
        }

        public TransactionInformation AddTransaction(ATransaction tx)
        {
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
            foreach (var aTxId in toBeTrimmed)
            {
                TransactionInformation txInfo;
                transactionState.TryRemove(aTxId, out txInfo);
                txInfo.Transaction.Cleanup();
            }
        }
    }
}
