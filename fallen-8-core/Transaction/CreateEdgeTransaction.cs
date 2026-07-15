// MIT License
//
// CreateEdgeTransaction.cs
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

namespace NoSQL.GraphDB.Core.Transaction
{
    public class CreateEdgeTransaction : ATransaction
    {
        public EdgeDefinition Definition
        {
            get;
            set;
        }

        internal override void ReleaseAfterCompletion()
        {
            // Drop the input definition (and its property dictionary) once the transaction
            // completes (M3).
            Definition = null;
        }

        internal override void Cleanup()
        {
            Definition = null;
        }

        private EdgeModel _edgeCreated;

        internal override void Rollback(Fallen8 f8)
        {
            // Remove the edge if one was created (feature transaction-atomicity). A clean NotFound
            // rollback leaves _edgeCreated null (nothing to compensate); this fills the former //TODO
            // so the "RolledBack => no observable effect" invariant is uniform across transactions.
            if (_edgeCreated != null)
            {
                f8.TryRemoveGraphElement_private(_edgeCreated.Id);
            }
        }

        internal override Boolean TryExecute(Fallen8 f8)
        {
            _edgeCreated = f8.CreateEdge_internal(Definition.SourceVertexId, Definition.EdgePropertyId, Definition.TargetVertexId, Definition.CreationDate, Definition.Label, Definition.Properties);

            if (_edgeCreated == null)
            {
                // The only clean reason CreateEdge_internal returns null is a missing/removed
                // referenced vertex - a client-caused NotFound, not an internal fault.
                FailureReason = TransactionFailureReason.NotFound;
                return false;
            }

            return true;
        }

        internal override void DescribeChanges(Fallen8 f8, ChangeFeed.ChangeDescriptor.Builder builder)
        {
            if (_edgeCreated != null)
            {
                builder.EdgeCreated(_edgeCreated.Id, _edgeCreated.Label,
                    _edgeCreated.SourceVertex.Id, _edgeCreated.TargetVertex.Id);
            }
        }
    }
}
