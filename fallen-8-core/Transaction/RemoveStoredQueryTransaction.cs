// MIT License
//
// RemoveStoredQueryTransaction.cs
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
using NoSQL.GraphDB.Core.StoredQueries;

namespace NoSQL.GraphDB.Core.Transaction
{
    /// <summary>
    ///   Transaction removing a stored query by name (feature stored-query-library). Removal
    ///   drops the library's strong reference to the compiled artifact so its collectible
    ///   AssemblyLoadContext can unload once in-flight invocations finish.
    /// </summary>
    public class RemoveStoredQueryTransaction : ATransaction
    {
        /// <summary>
        /// Gets or sets the name of the stored query to remove.
        /// </summary>
        public String Name
        {
            get;
            set;
        }

        /// <summary>
        /// Stores the removed entry for potential rollback.
        /// </summary>
        private StoredQueryEntry _removedEntry;

        /// <summary>
        /// Cleans up the transaction resources.
        /// </summary>
        internal override void Cleanup()
        {
            Name = null;
            _removedEntry = null;
        }

        /// <summary>
        ///   Drops the removed entry once the transaction is terminal (nothing reads it after
        ///   completion; rollback - which restores it - runs before the terminal state), so a
        ///   deleted stored query's compiled artifact is not kept alive by transaction-manager
        ///   bookkeeping until the next Trim: the delete itself unpins, and the collectible load
        ///   context can unload as soon as in-flight invocations finish.
        /// </summary>
        internal override void ReleaseAfterCompletion()
        {
            _removedEntry = null;
        }

        /// <summary>
        /// Rolls back the transaction by restoring the removed entry.
        /// </summary>
        /// <param name="f8">The Fallen8 instance.</param>
        internal override void Rollback(Fallen8 f8)
        {
            if (_removedEntry != null)
            {
                f8.StoredQueries.TryRegister(_removedEntry, out _);
            }
        }

        /// <summary>
        /// Tries to execute the transaction.
        /// </summary>
        /// <param name="f8">The Fallen8 instance.</param>
        /// <returns>True if successful, false otherwise.</returns>
        internal override Boolean TryExecute(Fallen8 f8)
        {
            if (String.IsNullOrWhiteSpace(Name))
            {
                FailureReason = TransactionFailureReason.InvalidInput;
                return false;
            }

            if (!f8.StoredQueries.TryRemove(out _removedEntry, Name, out var reason))
            {
                FailureReason = reason;
                return false;
            }

            return true;
        }
    }
}
