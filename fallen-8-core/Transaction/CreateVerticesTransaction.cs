// MIT License
//
// CreateVerticesTransaction.cs
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
    public class CreateVerticesTransaction : ATransaction
    {
        public List<VertexDefinition> Vertices
        {
            get; set;
        } = new List<VertexDefinition>();

        private List<VertexModel> _verticesCreated = new List<VertexModel>();

        internal override void Rollback(Fallen8 f8)
        {
            // Safety net for the construct-then-commit path (feature transaction-atomicity): the
            // internal method mutates nothing until an atomic append, so a rolled-back batch normally
            // has nothing to compensate. This still removes any vertices that did reach the store on
            // the residual case where a step after the append throws.
            if (_verticesCreated == null)
            {
                return;
            }

            foreach (var aVertex in _verticesCreated)
            {
                f8.TryRemoveGraphElement_private(aVertex.Id);
            }
        }

        internal override Boolean TryExecute(Fallen8 f8)
        {
            // No swallowing try/catch: a structurally invalid batch (a null definition) returns a
            // clean InvalidInput below, while a genuine exception (e.g. OOM) escapes to the worker,
            // which records it as Error/InternalError (B6) - instead of the old behaviour that hid
            // an id-space corruption behind a benign-looking clean rollback.
            _verticesCreated = f8.CreateVertices_internal(Vertices, out var inputValid);

            if (!inputValid)
            {
                FailureReason = TransactionFailureReason.InvalidInput;
                return false;
            }

            return true;
        }

        public CreateVerticesTransaction AddVertex(UInt32 CreationDate, String Label = null, Dictionary<String, Object> Properties = null)
        {
            Vertices.Add(new VertexDefinition() { CreationDate = CreationDate, Properties = Properties, Label = Label });

            return this;
        }

        public CreateVerticesTransaction AddVertex(VertexDefinition definition)
        {
            Vertices.Add(definition);

            return this;
        }

        public ImmutableList<VertexModel> GetCreatedVertices()
        {
            return ImmutableList.CreateRange(_verticesCreated);
        }

        internal override void ReleaseAfterCompletion()
        {
            // Drop the input definitions (and the property dictionaries they carry) as soon as the
            // transaction completes (M3). The created-model list is kept so a waited-on caller can
            // still read GetCreatedVertices() after WaitUntilFinished().
            Vertices = null;
        }

        internal override void Cleanup()
        {
            // Null-safe: ReleaseAfterCompletion() may already have dropped Vertices.
            _verticesCreated = null;
            Vertices = null;
        }
    }
}
