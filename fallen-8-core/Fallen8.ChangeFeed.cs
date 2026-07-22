// MIT License
//
// Fallen8.ChangeFeed.cs
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
using NoSQL.GraphDB.Core.ChangeFeed;
using NoSQL.GraphDB.Core.Model;

namespace NoSQL.GraphDB.Core
{
    public sealed partial class Fallen8
    {
        /// <summary>
        ///   Resolves an element's category and label for the change feed (feature change-feed),
        ///   INCLUDING tombstoned (soft-removed) elements - a removal descriptor is captured after
        ///   the element was marked removed, and removal is a soft-delete that keeps the model in
        ///   its slot. WRITER THREAD ONLY (descriptor capture).
        /// </summary>
        internal bool TryDescribeElement(Int32 graphElementId, out ChangeElementType elementType, out String label)
        {
            elementType = ChangeElementType.None;
            label = null;

            var snap = _snapshot;
            if (graphElementId < 0 || graphElementId >= snap.Count)
            {
                return false;
            }

            var element = snap.Segments[graphElementId >> SegmentShift][graphElementId & SegmentMask];
            if (element == null)
            {
                return false;
            }

            elementType = element is VertexModel ? ChangeElementType.Vertex : ChangeElementType.Edge;
            label = element.Label;
            return true;
        }

        /// <summary>
        ///   Describes one element THIS transaction removed - plus, for a vertex, its
        ///   cascade-removed edges - into a change descriptor (feature change-feed). WRITER THREAD
        ///   ONLY, called from a removal transaction's <c>DescribeChanges</c> after a successful
        ///   execute. Cascades enumerate the removed vertex's own raw adjacency: an edge removed
        ///   EARLIER (directly, or by the other endpoint's removal) was detached from this vertex's
        ///   containers at that time, so exactly the edges this removal cascaded remain - a
        ///   self-loop (present in both directions) is deduplicated by id.
        /// </summary>
        internal void DescribeRemovedElement(Int32 graphElementId, ChangeDescriptor.Builder builder)
        {
            var snap = _snapshot;
            if (graphElementId < 0 || graphElementId >= snap.Count)
            {
                return;
            }

            var element = snap.Segments[graphElementId >> SegmentShift][graphElementId & SegmentMask];

            if (element is VertexModel vertex)
            {
                builder.VertexRemoved(vertex.Id, vertex.Label);

                var seenEdges = new HashSet<Int32>();

                var outgoing = vertex.GetRawOutEdges();
                if (outgoing != null)
                {
                    foreach (var edgesPerProperty in outgoing)
                    {
                        foreach (var edge in edgesPerProperty.Value)
                        {
                            if (seenEdges.Add(edge.Id))
                            {
                                builder.EdgeRemoved(edge.Id, edge.Label);
                            }
                        }
                    }
                }

                var incoming = vertex.GetRawInEdges();
                if (incoming != null)
                {
                    foreach (var edgesPerProperty in incoming)
                    {
                        foreach (var edge in edgesPerProperty.Value)
                        {
                            if (seenEdges.Add(edge.Id))
                            {
                                builder.EdgeRemoved(edge.Id, edge.Label);
                            }
                        }
                    }
                }
            }
            else if (element is EdgeModel removedEdge)
            {
                builder.EdgeRemoved(removedEdge.Id, removedEdge.Label);
            }
        }
    }
}
