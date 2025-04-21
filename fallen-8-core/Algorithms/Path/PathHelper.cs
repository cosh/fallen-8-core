// MIT License
//
// PathHelper.cs
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

#region Usings

using System;
using System.Collections.Generic;
using NoSQL.GraphDB.Core.Model;

#endregion

namespace NoSQL.GraphDB.Core.Algorithms.Path
{
    /// <summary>
    /// A static helper class for path algorithms
    /// </summary>
    public static class PathHelper
    {
        /// <summary>
        /// Get the valid edges of a vertex
        /// </summary>
        /// <param name="vertex">The vertex.</param>
        /// <param name="direction">The direction.</param>
        /// <param name="edgepropertyFilter">The edge property filter.</param>
        /// <param name="edgeFilter">The edge filter.</param>
        /// <param name="vertexFilter">The target vertex filter.</param>
        /// <returns>Valid edges</returns>
        public static List<Tuple<String, IEnumerable<EdgeModel>>> GetValidEdges(
            VertexModel vertex,
            Direction direction,
            Delegates.EdgePropertyFilter edgepropertyFilter,
            Delegates.EdgeFilter edgeFilter,
            Delegates.VertexFilter vertexFilter)
        {
            var edgeProperties = direction == Direction.IncomingEdge ? vertex.InEdges : vertex.OutEdges;
            var result = new List<Tuple<String, IEnumerable<EdgeModel>>>();

            if (edgeProperties != null)
            {
                foreach (var edgeContainer in edgeProperties)
                {
                    if (edgepropertyFilter != null && !edgepropertyFilter(edgeContainer.Key, direction))
                    {
                        continue;
                    }

                    if (edgeFilter != null)
                    {
                        var validEdges = new List<EdgeModel>();

                        for (var i = 0; i < edgeContainer.Value.Count; i++)
                        {
                            var aEdge = edgeContainer.Value[i];
                            if (edgeFilter(aEdge, direction))
                            {
                                if (vertexFilter != null)
                                {
                                    if (
                                        vertexFilter(direction == Direction.IncomingEdge
                                                         ? aEdge.SourceVertex
                                                         : aEdge.TargetVertex))
                                    {
                                        validEdges.Add(aEdge);
                                    }
                                }
                                else
                                {
                                    validEdges.Add(aEdge);
                                }
                            }
                        }
                        result.Add(new Tuple<String, IEnumerable<EdgeModel>>(edgeContainer.Key, validEdges));
                    }
                    else
                    {
                        if (vertexFilter != null)
                        {
                            var validEdges = new List<EdgeModel>();

                            for (var i = 0; i < edgeContainer.Value.Count; i++)
                            {
                                var aEdge = edgeContainer.Value[i];
                                if (
                                    vertexFilter(direction == Direction.IncomingEdge
                                                     ? aEdge.SourceVertex
                                                     : aEdge.TargetVertex))
                                {
                                    validEdges.Add(aEdge);
                                }
                            }
                            result.Add(new Tuple<String, IEnumerable<EdgeModel>>(edgeContainer.Key, validEdges));
                        }
                        else
                        {
                            result.Add(new Tuple<String, IEnumerable<EdgeModel>>(edgeContainer.Key, edgeContainer.Value));
                        }
                    }
                }
            }

            return result;
        }
    }
}
