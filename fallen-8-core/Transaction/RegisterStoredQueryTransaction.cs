// MIT License
//
// RegisterStoredQueryTransaction.cs
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
    ///   Transaction registering a stored query (feature stored-query-library). The caller (the
    ///   REST controller) compiles the definition BEFORE enqueueing - validation must fail fast
    ///   with diagnostics and Roslyn must never occupy the single writer thread - and hands the
    ///   ready-built <see cref="Entry"/> here to be published atomically. The library re-checks
    ///   the invariants the controller pre-checked (duplicate name, quota) so TOCTOU races
    ///   resolve on the writer thread, mirroring <see cref="CreateSubGraphTransaction"/>.
    /// </summary>
    public class RegisterStoredQueryTransaction : ATransaction
    {
        /// <summary>
        ///   The fully-built entry to register: definition + pre-compiled artifact (or a
        ///   rehydration state on the load/replay paths).
        /// </summary>
        public StoredQueryEntry Entry
        {
            get;
            set;
        }

        /// <summary>Whether the entry was registered (drives rollback).</summary>
        private Boolean _registered;

        /// <summary>
        /// Cleans up the transaction resources.
        /// </summary>
        internal override void Cleanup()
        {
            Entry = null;
        }

        /// <summary>
        /// Rolls back the transaction.
        /// </summary>
        /// <param name="f8">The Fallen8 instance.</param>
        internal override void Rollback(Fallen8 f8)
        {
            if (_registered && Entry?.Definition?.Name != null)
            {
                f8.StoredQueries.TryRemove(out _, Entry.Definition.Name, out _);
            }
        }

        /// <summary>
        /// Tries to execute the transaction.
        /// </summary>
        /// <param name="f8">The Fallen8 instance.</param>
        /// <returns>True if successful, false otherwise.</returns>
        internal override Boolean TryExecute(Fallen8 f8)
        {
            if (Entry == null || Entry.Definition == null)
            {
                FailureReason = TransactionFailureReason.InvalidInput;
                return false;
            }

            if (!f8.StoredQueries.TryRegister(Entry, out var reason))
            {
                FailureReason = reason;
                return false;
            }

            _registered = true;
            return true;
        }
    }
}
