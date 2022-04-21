// MIT License
//
// CreateEdgesTransaction.cs
//
// Copyright (c) 2021 Henning Rauch
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

        public override void Rollback(Fallen8 f8)
        {
            foreach (var aEdge in _edgesAdded)
            {
                f8.TryRemoveGraphElement(aEdge.Id);
            }
        }

        public override Boolean TryExecute(Fallen8 f8)
        {
            try
            {
                _edgesAdded = f8.CreateEdges_internal(Edges);
            }
            catch (Exception)
            {
                return false;
            }

            return true;
        }

        public CreateEdgesTransaction AddEdge(Int32 sourceVertexId, UInt16 edgePropertyId, Int32 targetVertexId, UInt32 creationDate, PropertyContainer[] properties = null)
        {
            Edges.Add(new EdgeDefinition() { SourceVertexId = sourceVertexId, EdgePropertyId = edgePropertyId, TargetVertexId = targetVertexId, CreationDate = creationDate, Properties = properties });

            return this;
        }

        public CreateEdgesTransaction AddEdge(EdgeDefinition definition)
        {
            Edges.Add(definition);

            return this;
        }
        
        public ImmutableList<EdgeModel> GetCreatedEdges()
        {
            return ImmutableList.CreateRange(_edgesAdded);
        }
    }
}
