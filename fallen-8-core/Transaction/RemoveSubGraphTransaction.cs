// MIT License
//
// RemoveSubGraphTransaction.cs
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

using NoSQL.GraphDB.Core.Algorithms.SubGraph;
using System;

namespace NoSQL.GraphDB.Core.Transaction
{
    /// <summary>
    /// Transaction for removing a subgraph.
    /// </summary>
    public class RemoveSubGraphTransaction : ATransaction
    {
        /// <summary>
        /// Gets or sets the name of the subgraph to remove.
        /// </summary>
        public String SubGraphName
        {
            get;
            set;
        }

        /// <summary>
        /// Stores the removed subgraph for potential rollback.
        /// </summary>
        private SubGraphResult _removedSubGraph;

        /// <summary>
        /// Cleans up the transaction resources.
        /// </summary>
        internal override void Cleanup()
        {
            SubGraphName = null;
            _removedSubGraph = null;
        }

        /// <summary>
        /// Rolls back the transaction by restoring the removed subgraph.
        /// </summary>
        /// <param name="f8">The Fallen8 instance.</param>
        internal override void Rollback(Fallen8 f8)
        {
            // If a subgraph was removed, restore it
            if (_removedSubGraph != null && !String.IsNullOrWhiteSpace(SubGraphName))
            {
                f8.SubGraphFactory.TryRegisterSubGraph(_removedSubGraph);
            }
        }

        /// <summary>
        /// Tries to execute the transaction.
        /// </summary>
        /// <param name="f8">The Fallen8 instance.</param>
        /// <returns>True if successful, false otherwise.</returns>
        internal override Boolean TryExecute(Fallen8 f8)
        {
            if (String.IsNullOrWhiteSpace(SubGraphName))
            {
                FailureReason = TransactionFailureReason.InvalidInput;
                return false;
            }

            // Try to get the subgraph before removing it for potential rollback
            if (!f8.SubGraphFactory.TryGetSubGraph(out _removedSubGraph, SubGraphName))
            {
                // Subgraph doesn't exist - a client-caused NotFound, not an internal fault.
                FailureReason = TransactionFailureReason.NotFound;
                return false;
            }

            // Remove the subgraph. A false here (deregistration lost a race after the existence
            // check) is an unexpected internal condition, not a client error.
            if (!f8.SubGraphFactory.TryDeregisterSubGraph(SubGraphName))
            {
                FailureReason = TransactionFailureReason.InternalError;
                return false;
            }

            return true;
        }
    }
}
