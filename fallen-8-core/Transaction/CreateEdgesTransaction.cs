// MIT License
//
// CreateEdgesTransaction.cs
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

using NoSQL.GraphDB.Core.Model;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace NoSQL.GraphDB.Core.Transaction
{
    public class CreateEdgesTransaction : ATransaction
    {
        public List<EdgeDefinition> Edges
        {
            get; set;
        } = new List<EdgeDefinition>();

        private List<EdgeModel> _edgesAdded = new List<EdgeModel>();

        internal override void Rollback(Fallen8 f8)
        {
            // The internal method records edges into _edgesAdded as soon as they reach the store
            // (before adjacency wiring), so this removes exactly the edges that were committed if a
            // wiring step threw - detaching any partial adjacency and restoring EdgeCount. A clean
            // pre-validation false (invalid input / unresolved endpoint) leaves _edgesAdded empty, so
            // this is a no-op then (feature transaction-atomicity).
            if (_edgesAdded == null)
            {
                return;
            }

            foreach (var aEdge in _edgesAdded)
            {
                f8.TryRemoveGraphElement_private(aEdge.Id);
            }
        }

        internal override Boolean TryExecute(Fallen8 f8)
        {
            f8.CreateEdges_internal(Edges, _edgesAdded, out var inputValid, out var allEndpointsResolved);

            if (!inputValid)
            {
                // A structurally invalid batch (a null edge definition): a clean InvalidInput, no
                // throw, nothing wired.
                FailureReason = TransactionFailureReason.InvalidInput;
                return false;
            }

            if (!allEndpointsResolved)
            {
                // A referenced vertex was missing/removed: the whole batch rolled back cleanly and
                // atomically (nothing was wired) - a client-caused NotFound, not an internal fault.
                // A genuine unexpected exception is no longer swallowed here; it escapes to the
                // worker, which records it as Error/InternalError (B6).
                FailureReason = TransactionFailureReason.NotFound;
                return false;
            }

            return true;
        }

        public CreateEdgesTransaction AddEdge(Int32 sourceVertexId, String edgePropertyId, Int32 targetVertexId,
            UInt32 creationDate, String label = null, Dictionary<String, Object> properties = null)
        {
            Edges.Add(new EdgeDefinition()
            {
                SourceVertexId = sourceVertexId,
                EdgePropertyId = edgePropertyId,
                TargetVertexId = targetVertexId,
                CreationDate = creationDate,
                Label = label,
                Properties = properties
            });

            return this;
        }

        public CreateEdgesTransaction AddEdge(EdgeDefinition definition)
        {
            Edges.Add(definition);

            return this;
        }

        public ImmutableList<EdgeModel> GetCreatedEdges()
        {
            // Null-safe (feature transaction-retention R2): return empty rather than throwing when the
            // captured list has been dropped, so an unrelated trim can never turn this into an
            // ArgumentNullException. A waited-on caller reading promptly gets the actual created models.
            return _edgesAdded == null
                ? ImmutableList<EdgeModel>.Empty
                : ImmutableList.CreateRange(_edgesAdded);
        }

        internal override void DescribeChanges(Fallen8 f8, ChangeFeed.ChangeDescriptor.Builder builder)
        {
            if (_edgesAdded == null)
            {
                return;
            }

            foreach (var edge in _edgesAdded)
            {
                builder.EdgeCreated(edge.Id, edge.Label, edge.SourceVertex.Id, edge.TargetVertex.Id);
            }
        }

        internal override void ReleaseAfterCompletion()
        {
            // Drop the input definitions (and their property dictionaries) once the transaction
            // completes (M3). The created-model list is kept for GetCreatedEdges() after waiting.
            Edges = null;
        }

        internal override void Cleanup()
        {
            // Drop only the INPUT (feature transaction-retention R2). The created-model list is NOT
            // nulled here anymore: the edges are already referenced by the master store, and bounded
            // retention (R1) collects the whole ATransaction once its entry is evicted. Nulling it used
            // to race an unrelated trim against a caller's GetCreatedEdges().
            Edges = null;
        }
    }
}
