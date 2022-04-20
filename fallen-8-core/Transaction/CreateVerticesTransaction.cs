// MIT License
//
// CreateVerticesTransaction.cs
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

namespace NoSQL.GraphDB.Core.Transaction
{
    public class CreateVerticesTransaction : ATransaction
    {
        public List<CreateVertexTransaction> Vertices
            { get; set; } = new List<CreateVertexTransaction>();

        private List<Int32> _verticesAdded = new List<Int32>();

        public override void Rollback(Fallen8 f8)
        {
            foreach (var aVertexId in _verticesAdded)
            {
                f8.TryRemoveGraphElement(aVertexId);
            }
        }

        public override Boolean TryExecute(Fallen8 f8)
        {
            try
            {
                foreach (var aTx in Vertices)
                {
                    var vertex = f8.CreateVertex_interal(aTx.CreationDate, aTx.Properties);
                    
                    _verticesAdded.Add(vertex.Id);
                }
            }
            catch (Exception)
            {
                return false;
            }

            return  true;
        }

        public CreateVerticesTransaction AddVertex(UInt32 CreationDate, PropertyContainer[] Properties)
        {
            Vertices.Add(new CreateVertexTransaction() { CreationDate = CreationDate, Properties = Properties });

            return this;
        }
    }
}
