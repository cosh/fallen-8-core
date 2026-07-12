// MIT License
//
// ATransaction.cs
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
using System.Text;

namespace NoSQL.GraphDB.Core.Transaction
{
    public abstract class ATransaction
    {
        public readonly string TransactionId = Guid.NewGuid().ToString();

        /// <summary>
        ///   Fully releases every reference the transaction still holds (both its input
        ///   definition and any captured created-model list). Called by the transaction manager
        ///   at <c>Trim</c>, once the transaction is no longer observable.
        /// </summary>
        internal abstract void Cleanup();

        /// <summary>
        ///   Releases the transaction's HEAVY INPUT payload (the definition and the property
        ///   dictionaries the caller supplied) as soon as the transaction reaches a terminal
        ///   state, so that transient data is not retained in duplicate until the next
        ///   <c>Trim</c> (finding M3). The default is a no-op; transactions that carry a bulk
        ///   definition override it.
        ///
        ///   Contract: this MUST NOT drop anything a waited-on caller still reads after
        ///   <c>WaitUntilFinished()</c> - in particular the captured created-models
        ///   (<c>GetCreatedVertices()</c> / <c>VertexCreated</c> / ...). Only the input side is
        ///   released here; the created-models are dropped later, in <see cref="Cleanup"/>.
        /// </summary>
        internal virtual void ReleaseAfterCompletion()
        {
            // no-op by default
        }

        abstract internal void Rollback(Fallen8 f8);
        abstract internal Boolean TryExecute(Fallen8 f8);

        /// <summary>
        ///   Whether a successful commit of this transaction should let the engine consider an
        ///   automatic compaction (finding M4). Scoped to element-removal transactions: only they
        ///   raise the tombstone count, and (unlike the create transactions) they hold no
        ///   created-models a caller reads afterwards, so compacting right after they commit is
        ///   safe. Default is <c>false</c>.
        /// </summary>
        internal virtual Boolean TriggersAutoTrim
        {
            get { return false; }
        }
    }
}
