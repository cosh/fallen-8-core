// MIT License
//
// CreateSubGraphTransaction.cs
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
    /// Transaction for creating a subgraph based on a subgraph definition.
    /// </summary>
    public class CreateSubGraphTransaction : ATransaction
    {
        /// <summary>
        /// Gets the created subgraph result.
        /// </summary>
        public SubGraphResult SubGraphCreated;

        /// <summary>
        /// Gets or sets the subgraph definition.
        /// </summary>
        public SubGraphDefinition Definition
        {
            get;
            set;
        }

        /// <summary>
        /// Optional name of an already-registered subgraph to use as the source (creating a
        /// nested subgraph). When null/empty, the subgraph is sourced from the graph itself.
        /// </summary>
        public String SourceSubGraphName
        {
            get;
            set;
        }

        /// <summary>
        /// Cleans up the transaction resources.
        /// </summary>
        internal override void Cleanup()
        {
            Definition = null;
            SubGraphCreated = null;
        }

        /// <summary>
        /// Rolls back the transaction.
        /// </summary>
        /// <param name="f8">The Fallen8 instance.</param>
        internal override void Rollback(Fallen8 f8)
        {
            // If a subgraph was created, deregister it
            if (SubGraphCreated != null && Definition != null && !String.IsNullOrWhiteSpace(Definition.Name))
            {
                f8.SubGraphFactory.TryDeregisterSubGraph(Definition.Name);
            }
        }

        /// <summary>
        /// Tries to execute the transaction.
        /// </summary>
        /// <param name="f8">The Fallen8 instance.</param>
        /// <returns>True if successful, false otherwise.</returns>
        internal override Boolean TryExecute(Fallen8 f8)
        {
            if (Definition == null)
            {
                FailureReason = TransactionFailureReason.InvalidInput;
                return false;
            }

            if (String.IsNullOrWhiteSpace(Definition.Name))
            {
                FailureReason = TransactionFailureReason.InvalidInput;
                return false;
            }

            // Nested subgraph: source is another registered subgraph.
            if (!String.IsNullOrWhiteSpace(SourceSubGraphName))
            {
                if (!f8.SubGraphFactory.TryGetSubGraph(out var sourceResult, SourceSubGraphName))
                {
                    FailureReason = TransactionFailureReason.NotFound;
                    return false;
                }

                if (!f8.SubGraphFactory.TryCreateSubGraphFromSource(
                        out SubGraphCreated,
                        Definition.Name,
                        Definition,
                        sourceResult.SubGraph,
                        out var nestedReason))
                {
                    FailureReason = nestedReason;
                    return false;
                }

                return true;
            }

            // Root subgraph: source is the graph itself.
            if (!f8.SubGraphFactory.TryCreateSubGraph(
                    out SubGraphCreated,
                    Definition.Name,
                    Definition,
                    out var rootReason))
            {
                FailureReason = rootReason;
                return false;
            }

            return true;
        }
    }
}
