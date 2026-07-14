// MIT License
//
//
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
using System.Threading.Tasks;

namespace NoSQL.GraphDB.Core.Transaction
{
    public class TransactionInformation
    {
        public ATransaction Transaction
        {
            get; set;
        }

        public TransactionState TransactionState
        {
            get; set;
        }

        /// <summary>
        /// The exception that faulted the transaction during execution, if any. It is
        /// <c>null</c> when the transaction finished successfully or rolled back cleanly (its
        /// <c>TryExecute</c> returned <c>false</c> without throwing). Because the worker maps
        /// both a thrown exception and a clean <c>false</c> to
        /// <see cref="TransactionState.RolledBack"/>, this property is what lets a caller that
        /// waited on the outcome tell a genuine internal fault apart from a clean rollback.
        /// Set in place by the transaction manager before the transaction task completes, so it
        /// is visible under the same happens-before edge as <see cref="TransactionState"/>.
        /// </summary>
        public Exception Error
        {
            get; set;
        }

        /// <summary>
        /// The structured reason a transaction rolled back, if any. It is
        /// <see cref="TransactionFailureReason.None"/> when the transaction finished successfully
        /// or rolled back without a recorded reason. Whereas <see cref="Error"/> distinguishes a
        /// genuine thrown fault from a clean rollback, this classifies WHY a clean rollback
        /// happened (a missing referenced element, a quota breach, a name conflict, an invalid
        /// request, ...), so a caller that waited on the outcome can map it to the correct
        /// response instead of collapsing everything to a single status. An escaped exception
        /// implies <see cref="TransactionFailureReason.InternalError"/>. Set in place by the
        /// transaction manager before the transaction task completes, so it is visible under the
        /// same happens-before edge as <see cref="TransactionState"/> and <see cref="Error"/>.
        /// </summary>
        public TransactionFailureReason FailureReason
        {
            get; set;
        } = TransactionFailureReason.None;

        /// <summary>
        /// Whether this committed transaction is durable in the write-ahead log (feature
        /// crash-durability-hardening D1/D3). <c>true</c> in the normal case and whenever the WAL is
        /// disabled (durability is then via the next explicit Save). Set to <c>false</c> - under the
        /// same happens-before as <see cref="TransactionState"/> - when the WAL append failed or the
        /// log is in a degraded/suspended state (a tripped failure fence, or an anchored log awaiting
        /// its paired snapshot Load): the transaction is still applied in memory and reported
        /// <see cref="TransactionState.Finished"/>, but a waited-on caller can see its log durability
        /// is degraded. A subsequent successful Save re-establishes a durable baseline and clears the
        /// degraded state for future transactions.
        /// </summary>
        public Boolean Durable
        {
            get; set;
        } = true;

        /// <summary>
        /// Whether this transaction committed (<see cref="TransactionState.Finished"/>) but its
        /// write-ahead-log append did not reach disk - the inverse of <see cref="Durable"/>, named for
        /// the durability-degraded contract (feature transaction-retention R3). It lets a caller
        /// distinguish a committed-but-degraded transaction (<c>Finished</c> +
        /// <c>DurabilityDegraded == true</c> + <c>Error == null</c>) from a genuine execution fault
        /// (<c>RolledBack</c> + <c>Error != null</c>): the WAL-append failure is signalled HERE, never on
        /// <see cref="Error"/>, so <see cref="Error"/> keeps its single meaning "execution faulted".
        /// </summary>
        public Boolean DurabilityDegraded => !Durable;

        private readonly Task _txTask;

        public TransactionInformation(Task txTask)
        {
            _txTask = txTask;
        }

        /// <summary>
        /// A task that completes when this transaction reaches its terminal state AND (with the WAL
        /// enabled) its commit group has been fsynced - i.e. the durable-before-ack point (feature
        /// write-path-throughput). <c>await</c>ing it registers a continuation instead of blocking, so
        /// a waiting ASP.NET request releases its thread-pool thread rather than pinning it for the
        /// whole queue latency. Never <c>null</c>: a transaction created without a backing task
        /// (defensive) returns an already-completed task.
        /// </summary>
        public Task Completion => _txTask ?? Task.CompletedTask;

        public void WaitUntilFinished()
        {
            if (_txTask != null)
            {
                _txTask.Wait();
            }
        }

        /// <summary>
        /// Waits up to <paramref name="timeout"/> for the transaction to finish. Returns <c>true</c>
        /// if it finished within the budget, <c>false</c> on a deadline miss (feature
        /// write-path-throughput). Does not throw on timeout.
        /// </summary>
        public Boolean WaitUntilFinished(TimeSpan timeout)
        {
            return _txTask == null || _txTask.Wait(timeout);
        }
    }
}
