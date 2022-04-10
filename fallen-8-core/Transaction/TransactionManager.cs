// MIT License
//
// TransactionManager.cs
//
// Copyright (c) 2021 Henning Rauch
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

namespace NoSQL.GraphDB.Core.Transaction
{
    internal class TransactionManager
    {
        private readonly ConcurrentQueue<ATransaction> transactions = new ConcurrentQueue<ATransaction>();

        private readonly ConcurrentDictionary<String, TransactionInformation> transactionState = new ConcurrentDictionary<String, TransactionInformation>();

        public TransactionManager(Fallen8 f8)
        {
            Action action = () =>
            {
                ATransaction transaction;
                while (transactions.TryDequeue(out transaction))
                {
                    //do some work
                    if (!transaction.TryExecute(f8))
                    {
                        transaction.Rollback(f8);
                    }
                }
            };

            Task.Run(action);
        }

        public TransactionInformation AddTransaction(ATransaction tx)
        {
            transactions.Enqueue(tx);

            var txInfo = new TransactionInformation()
            {
                TransactionID = Guid.NewGuid().ToString(),
                TransactionState = TransactionState.Enqueued
            };

            transactionState.TryAdd(txInfo.TransactionID, txInfo);

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
            }
        }
    }
}
