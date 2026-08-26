// MIT License
//
// TestVertices.cs
//
// Copyright (c) 2011-2026 Henning Rauch
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
using System.Linq;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   THE shared arrange step for "give me N plain vertices": one
    ///   <see cref="CreateVerticesTransaction"/> enqueued and waited on, returning the created
    ///   vertices in creation order. Use this instead of growing another private
    ///   <c>CreateVertices</c> per test class.
    ///
    ///   <para>The two knobs are the ones the suite actually needs. <c>label</c> is what a test
    ///   later filters or asserts on, so it is explicit at the call site whenever it is not the
    ///   plain <c>"v"</c>. <c>sequencePropertyName</c> covers the other real variant: vertices
    ///   that must be tellable apart by a property (the classic <c>{ "idx", i }</c>), which is
    ///   NOT the same graph as a vertex with no properties at all - hence a knob rather than a
    ///   default.</para>
    /// </summary>
    internal static class TestVertices
    {
        /// <summary>
        ///   Creates <paramref name="count"/> vertices labelled <paramref name="label"/> in one
        ///   committed transaction. When <paramref name="sequencePropertyName"/> is given, each
        ///   vertex additionally carries that property set to its own 0-based position; when it is
        ///   <c>null</c> the vertices carry no properties at all.
        /// </summary>
        internal static VertexModel[] Create(Fallen8 fallen8, Int32 count, String label = "v",
            String sequencePropertyName = null)
        {
            var tx = new CreateVerticesTransaction();
            for (var i = 0; i < count; i++)
            {
                tx.AddVertex(1u, label, sequencePropertyName == null
                    ? null
                    : new Dictionary<String, Object> { { sequencePropertyName, i } });
            }
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.GetCreatedVertices().ToArray();
        }
    }
}
