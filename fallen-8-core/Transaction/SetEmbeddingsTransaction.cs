// MIT License
//
// SetEmbeddingsTransaction.cs
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
using NoSQL.GraphDB.Core.Model;

namespace NoSQL.GraphDB.Core.Transaction
{
    /// <summary>
    ///   Sets or removes named element embeddings as one atomic batch (feature
    ///   element-embeddings). Unlike <see cref="AddPropertiesTransaction" /> this has REPLACE
    ///   semantics - an embedding write always overwrites the prior vector (one current vector
    ///   per name, mirroring the vector index's one-vector-per-element rule) - so a re-embed is
    ///   a single transaction, never a remove+add pair.
    /// </summary>
    public class SetEmbeddingsTransaction : ATransaction
    {
        public List<EmbeddingSetDefinition> Embeddings
        {
            get; set;
        } = new List<EmbeddingSetDefinition>();

        // Inverse of each applied write, in apply order (the transaction-atomicity pattern of
        // AddPropertiesTransaction), replayed in reverse on rollback.
        private List<PropertyMutationUndo> _undo = new List<PropertyMutationUndo>();

        internal override Boolean TryExecute(Fallen8 f8)
        {
            if (!f8.SetEmbeddings_internal(Embeddings, _undo, out var reason))
            {
                FailureReason = reason;
                return false;
            }

            return true;
        }

        internal override void Rollback(Fallen8 f8)
        {
            f8.RestoreEmbeddings_internal(_undo);
        }

        internal override void DescribeChanges(Fallen8 f8, ChangeFeed.ChangeDescriptor.Builder builder)
        {
            // One undo entry per applied write; the feed reports the reserved property key -
            // primitives only, never the vector.
            if (_undo == null)
            {
                return;
            }

            foreach (var applied in _undo)
            {
                if (f8.TryDescribeElement(applied.GraphElementId, out var elementType, out var label))
                {
                    builder.PropertySet(elementType, applied.GraphElementId, label, applied.PropertyId);
                }
            }
        }

        public SetEmbeddingsTransaction SetEmbedding(Int32 graphElementId, String name, Single[] vector,
            String modelStamp = null)
        {
            Embeddings.Add(new EmbeddingSetDefinition
            {
                GraphElementId = graphElementId,
                Name = name,
                Vector = vector,
                ModelStamp = modelStamp
            });

            return this;
        }

        internal override void ReleaseAfterCompletion()
        {
            // Drop the input batch (and the vectors it carries) once the transaction completes (M3).
            Embeddings = null;
        }

        internal override void Cleanup()
        {
            Embeddings = null;
            _undo = null;
        }
    }
}
