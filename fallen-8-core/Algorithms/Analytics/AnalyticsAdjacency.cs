// MIT License
//
// AnalyticsAdjacency.cs
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

namespace NoSQL.GraphDB.Core.Algorithms.Analytics
{
    /// <summary>
    ///   A visitor receiving each in-scope neighbour's dense index during an adjacency walk.
    ///   Implemented by STRUCTS and passed by <c>ref</c> so the JIT specializes and inlines
    ///   the callback per call site - no delegate, no interface dispatch on the hot path.
    /// </summary>
    internal interface IInScopeNeighborVisitor
    {
        void OnNeighbor(Int32 denseIndex);
    }

    /// <summary>
    ///   THE induced-subgraph adjacency walk of the analytics family (feature code-quality:
    ///   consolidates the six per-algorithm copies the sprint left behind). One direction of
    ///   one vertex's adjacency: filter groups by edge-property-id, skip tombstoned edges,
    ///   resolve the OTHER endpoint against the scope map (membership = in scope and live at
    ///   snapshot time), and hand the dense index to the visitor. Exactly the semantics every
    ///   algorithm pinned in its tests: scoped runs compute over the induced subgraph.
    /// </summary>
    internal static class AnalyticsAdjacency
    {
        /// <summary>Walks one adjacency (<paramref name="neighborIsTarget"/>: true for
        /// out-edges - the neighbour is the edge's target - false for in-edges).</summary>
        internal static void Visit<TVisitor>(EdgeAdjacency adjacency, Boolean neighborIsTarget,
            String edgePropertyId, Dictionary<Int32, Int32> scope, ref TVisitor visitor)
            where TVisitor : struct, IInScopeNeighborVisitor
        {
            if (adjacency == null)
            {
                return;
            }

            foreach (var group in adjacency)
            {
                if (edgePropertyId != null && !String.Equals(group.Key, edgePropertyId, StringComparison.Ordinal))
                {
                    continue;
                }

                var segment = group.Value;
                for (var e = 0; e < segment.Count; e++)
                {
                    var edge = segment.Array[segment.Offset + e];
                    if (edge == null || edge._removed)
                    {
                        continue;
                    }

                    var neighbor = neighborIsTarget ? edge.TargetVertex : edge.SourceVertex;
                    if (neighbor != null && scope.TryGetValue(neighbor.Id, out var denseIndex))
                    {
                        visitor.OnNeighbor(denseIndex);
                    }
                }
            }
        }

        /// <summary>Counts in-scope edges in one adjacency direction - the shared degree
        /// primitive (DEGREE scores, PageRank divisors). Parallel edges count multiply and a
        /// self-loop counts once per direction, exactly as the inline walkers did.</summary>
        internal static Int64 CountInScope(EdgeAdjacency adjacency, Boolean neighborIsTarget,
            String edgePropertyId, Dictionary<Int32, Int32> scope)
        {
            var visitor = new CountingVisitor();
            Visit(adjacency, neighborIsTarget, edgePropertyId, scope, ref visitor);
            return visitor.Count;
        }

        private struct CountingVisitor : IInScopeNeighborVisitor
        {
            public Int64 Count;

            public void OnNeighbor(Int32 denseIndex)
            {
                Count++;
            }
        }
    }
}
