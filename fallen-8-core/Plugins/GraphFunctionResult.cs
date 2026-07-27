// MIT License
//
// GraphFunctionResult.cs
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
using NoSQL.GraphDB.Core.Model;

namespace NoSQL.GraphDB.Core.Plugins
{
    /// <summary>
    ///   The result of an <see cref="IGraphFunction"/> invocation (feature plugin-registration): a
    ///   view of EXISTING vertices and edges in the graph, not a deep copy. The hosting layer projects
    ///   these with the same DTOs as <c>GET /vertex/{id}</c> and <c>GET /edge/{id}</c>. Because the
    ///   result references live elements captured at call time, a concurrently removed element simply
    ///   reflects the read snapshot the function observed - the documented read semantics; graph
    ///   functions are read-only in v1.
    /// </summary>
    public sealed class GraphFunctionResult
    {
        /// <summary>The vertices the function selected (never null; empty when none).</summary>
        public IReadOnlyList<VertexModel> Vertices
        {
            get;
        }

        /// <summary>The edges the function selected (never null; empty when none).</summary>
        public IReadOnlyList<EdgeModel> Edges
        {
            get;
        }

        /// <summary>
        ///   Creates a result. Null element lists are normalized to empty so callers never have to
        ///   null-check the projection.
        /// </summary>
        public GraphFunctionResult(IReadOnlyList<VertexModel> vertices, IReadOnlyList<EdgeModel> edges)
        {
            Vertices = vertices ?? Array.Empty<VertexModel>();
            Edges = edges ?? Array.Empty<EdgeModel>();
        }

        /// <summary>
        ///   Convenience factory over element sequences (materialized once into lists). Nulls are
        ///   treated as empty.
        /// </summary>
        public static GraphFunctionResult FromElements(IEnumerable<VertexModel> vertices, IEnumerable<EdgeModel> edges)
        {
            return new GraphFunctionResult(
                vertices == null ? Array.Empty<VertexModel>() : new List<VertexModel>(vertices),
                edges == null ? Array.Empty<EdgeModel>() : new List<EdgeModel>(edges));
        }
    }
}
