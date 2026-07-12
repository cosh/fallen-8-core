// MIT License
//
// WeightedDijkstraShortestPath.cs
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
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Core.Model;

#endregion

namespace NoSQL.GraphDB.Core.Algorithms.Path
{
    /// <summary>
    ///   Weighted single-source shortest path (Dijkstra) that honours the edge/vertex
    ///   <see cref="Delegates.EdgeCost"/> / <see cref="Delegates.VertexCost"/> functions and the
    ///   <see cref="ShortestPathDefinition.MaxPathWeight"/> bound.
    /// </summary>
    /// <remarks>
    ///   <para>
    ///   Cost model: reaching a neighbour <c>v</c> across edge <c>e</c> costs
    ///   <c>(edgeCost(e) ?? 1.0) + (vertexCost(v) ?? 0.0)</c>. With no cost delegates every edge
    ///   costs 1, so the result is a fewest-hop shortest path that agrees with the hop-count
    ///   <c>BLS</c> algorithm on path length.
    ///   </para>
    ///   <para>
    ///   Both incoming and outgoing edges are traversable (undirected reachability over directed
    ///   edges, matching <c>BLS</c>); each <see cref="PathElement"/> records the traversal
    ///   direction. The <c>EdgePropertyFilter</c>, <c>EdgeFilter</c> and <c>VertexFilter</c> are
    ///   applied exactly as in <c>BLS</c> (a filtered edge/vertex is not traversable).
    ///   </para>
    ///   <para>
    ///   <see cref="ShortestPathDefinition.MaxDepth"/> caps the number of edges on a returned path
    ///   and is honoured during the search (via a hop dimension in the search state), so a cheaper
    ///   but longer route is correctly rejected in favour of a costlier route that fits the hop
    ///   budget. <see cref="ShortestPathDefinition.MaxPathWeight"/> prunes any path whose cumulative
    ///   weight would exceed it.
    ///   </para>
    ///   <para>
    ///   With <c>MaxResults == 1</c> the single least-weight path is returned; with
    ///   <c>MaxResults &gt; 1</c> the K least-weight loop-free paths are returned in non-decreasing
    ///   weight order (Yen's algorithm over the Dijkstra subroutine).
    ///   </para>
    ///   <para>
    ///   Dijkstra requires non-negative step costs. Negative costs are undefined; defensively, a
    ///   negative combined step cost is clamped to <c>0</c> (for both ordering and the reported
    ///   weight) and a warning is logged once per calculation.
    ///   </para>
    /// </remarks>
    public sealed class WeightedDijkstraShortestPath : IShortestPathAlgorithm
    {
        #region Data

        /// <summary>
        ///   The Fallen-8
        /// </summary>
        private IFallen8 _fallen8;

        /// <summary>
        ///   The logger
        /// </summary>
        private ILogger<WeightedDijkstraShortestPath> _logger;

        #endregion

        #region IShortestPathAlgorithm Members

        public bool TryCalculateShortestPath(
            out List<Path> result,
            ShortestPathDefinition definition)
        {
            result = new List<Path>();

            #region initial checks (mirror BLS guard clauses)

            if (definition == null)
            {
                return false;
            }

            VertexModel sourceVertex;
            VertexModel destinationVertex;
            if (!(_fallen8.TryGetVertex(out sourceVertex, definition.SourceVertexId)
                  && _fallen8.TryGetVertex(out destinationVertex, definition.DestinationVertexId)))
            {
                return false;
            }

            if (sourceVertex._removed || destinationVertex._removed)
            {
                return false;
            }

            if (definition.MaxDepth <= 0 || definition.MaxResults <= 0)
            {
                return false;
            }

            if (ReferenceEquals(sourceVertex, destinationVertex))
            {
                return false;
            }

            #endregion

            // Bound the effective hop budget to min(MaxDepth, VertexCount - 1).
            //
            // Correctness (see features/weighted-shortest-paths/spec.md §2): with non-negative
            // (clamped) step costs the minimum-weight walk is achieved by a SIMPLE path, and this
            // algorithm only ever returns loop-free paths. A loop-free/simple path visits at most
            // VertexCount vertices, i.e. at most VertexCount - 1 edges. So capping the hop bound at
            // VertexCount - 1 cannot exclude any returned path or change any result (neither the
            // single least-weight path nor Yen's K loop-free paths) -- it only stops the
            // (vertexId, hops) search from exploring redundant deeper states, bounding the
            // otherwise O(V * MaxDepth) state space (a resource guard for a huge opt-in MaxDepth
            // against an unreachable destination in a cyclic component). The cap never INCREASES
            // the caller's MaxDepth. Applied once here so it flows into both the single Dijkstra
            // and Yen's spur searches.
            var effectiveMaxDepth = definition.MaxDepth;
            var hopCap = _fallen8.VertexCount - 1;
            if (hopCap >= 1 && hopCap < effectiveMaxDepth)
            {
                effectiveMaxDepth = hopCap;
            }

            var search = new Search(definition, _fallen8, _logger);

            List<Path> paths;
            if (definition.MaxResults == 1)
            {
                var single = search.TryShortestPath(
                    sourceVertex, destinationVertex, effectiveMaxDepth, definition.MaxPathWeight, null, null);

                paths = single != null ? new List<Path> { single } : new List<Path>();
            }
            else
            {
                paths = search.KShortestPaths(
                    sourceVertex, destinationVertex, definition.MaxResults, effectiveMaxDepth, definition.MaxPathWeight);
            }

            if (paths.Count > 0)
            {
                result = paths;
                return true;
            }

            return false;
        }

        #endregion

        #region search implementation

        /// <summary>
        ///   Per-call search state. Kept off the (cached, potentially shared) plugin instance so a
        ///   calculation carries no state between invocations and is safe to run concurrently.
        /// </summary>
        private sealed class Search
        {
            private readonly IFallen8 _fallen8;
            private readonly ILogger _logger;
            private readonly Delegates.EdgePropertyFilter _edgePropertyFilter;
            private readonly Delegates.EdgeFilter _edgeFilter;
            private readonly Delegates.VertexFilter _vertexFilter;
            private readonly Delegates.EdgeCost _edgeCost;
            private readonly Delegates.VertexCost _vertexCost;

            private bool _negativeCostWarned;
            private long _sequence;

            public Search(ShortestPathDefinition definition, IFallen8 fallen8, ILogger logger)
            {
                _fallen8 = fallen8;
                _logger = logger;
                _edgePropertyFilter = definition.EdgePropertyFilter;
                _edgeFilter = definition.EdgeFilter;
                _vertexFilter = definition.VertexFilter;
                _edgeCost = definition.EdgeCost;
                _vertexCost = definition.VertexCost;
            }

            #region single shortest path (hop-constrained Dijkstra)

            /// <summary>
            ///   Computes the least-weight path from <paramref name="source"/> to
            ///   <paramref name="destination"/> using at most <paramref name="maxHops"/> edges and a
            ///   cumulative weight of at most <paramref name="maxWeight"/>, optionally forbidding a
            ///   set of vertices and directed edge-steps (used by the K-shortest routine).
            /// </summary>
            /// <returns>The path, or <c>null</c> when none exists within the bounds.</returns>
            public Path TryShortestPath(
                VertexModel source,
                VertexModel destination,
                int maxHops,
                double maxWeight,
                HashSet<int> bannedVertexIds,
                HashSet<(int EdgeId, Direction Direction)> bannedSteps)
            {
                if (maxHops <= 0 || ReferenceEquals(source, destination))
                {
                    return null;
                }

                // Label-setting Dijkstra over states (vertexId, hops). The hop dimension makes the
                // MaxDepth bound exact: the least-weight route that would exceed the hop budget does
                // not shadow a costlier route that fits, because they are distinct states.
                var dist = new Dictionary<(int VertexId, int Hops), double>();
                var pred = new Dictionary<(int VertexId, int Hops), PredecessorRecord>();

                // Priority ordered by (weight, hops, sequence): least weight first, then fewest hops
                // on a weight tie, then discovery order (deterministic under ascending edge-id
                // neighbour enumeration, which favours lower edge ids).
                var queue = new PriorityQueue<(int VertexId, int Hops), (double Weight, int Hops, long Sequence)>();

                var startState = (source.Id, 0);
                dist[startState] = 0.0;
                queue.Enqueue(startState, (0.0, 0, _sequence++));

                while (queue.TryDequeue(out var state, out var priority))
                {
                    var currentWeight = priority.Weight;

                    // Skip stale queue entries (no decrease-key; superseded labels are left behind).
                    double best;
                    if (!dist.TryGetValue(state, out best) || currentWeight > best)
                    {
                        continue;
                    }

                    if (state.Item1 == destination.Id)
                    {
                        return Reconstruct(pred, state, source.Id);
                    }

                    if (state.Item2 >= maxHops)
                    {
                        continue;
                    }

                    VertexModel current;
                    if (!_fallen8.TryGetVertex(out current, state.Item1))
                    {
                        continue;
                    }

                    foreach (var step in GetNeighbours(current))
                    {
                        var neighbourId = step.Neighbour.Id;

                        if (bannedVertexIds != null && bannedVertexIds.Contains(neighbourId))
                        {
                            continue;
                        }

                        if (bannedSteps != null && bannedSteps.Contains((step.Edge.Id, step.Direction)))
                        {
                            continue;
                        }

                        var neighbourWeight = currentWeight + step.Cost;
                        if (neighbourWeight > maxWeight)
                        {
                            continue;
                        }

                        var neighbourState = (neighbourId, state.Item2 + 1);

                        double known;
                        if (!dist.TryGetValue(neighbourState, out known) || neighbourWeight < known)
                        {
                            dist[neighbourState] = neighbourWeight;
                            pred[neighbourState] = new PredecessorRecord(
                                step.Edge, step.EdgePropertyId, step.Direction, state.Item1, state.Item2, step.Cost);
                            queue.Enqueue(neighbourState, (neighbourWeight, neighbourState.Item2, _sequence++));
                        }
                    }
                }

                return null;
            }

            /// <summary>
            ///   Rebuilds the path (source -&gt; destination order) from the predecessor map, then
            ///   removes any loops. With non-negative costs an optimal route only ever repeats a
            ///   vertex across a zero-weight cycle; excising it preserves both weight and validity
            ///   while guaranteeing a loop-free result.
            /// </summary>
            private Path Reconstruct(
                Dictionary<(int VertexId, int Hops), PredecessorRecord> pred,
                (int VertexId, int Hops) endState,
                int sourceId)
            {
                var steps = new List<PredecessorRecord>();

                var cursor = endState;
                while (cursor.Hops > 0)
                {
                    var record = pred[cursor];
                    steps.Add(record);
                    cursor = (record.FromVertexId, record.FromHops);
                }

                steps.Reverse();
                RemoveLoops(steps, sourceId);

                var path = new Path(steps.Count);
                foreach (var record in steps)
                {
                    path.AddPathElement(new PathElement(record.Edge, record.EdgePropertyId, record.Direction, record.Cost));
                }

                return path;
            }

            /// <summary>
            ///   Removes cycles from a source -&gt; destination step list in place, keeping the path
            ///   loop-free.
            /// </summary>
            private static void RemoveLoops(List<PredecessorRecord> steps, int sourceId)
            {
                var changed = true;
                while (changed)
                {
                    changed = false;

                    var seen = new Dictionary<int, int>();
                    var vertexAtPosition = sourceId;
                    seen[vertexAtPosition] = 0;

                    for (var i = 0; i < steps.Count; i++)
                    {
                        var reached = steps[i].ToVertexId;

                        int firstPosition;
                        if (seen.TryGetValue(reached, out firstPosition))
                        {
                            // Positions firstPosition..i describe a cycle ending back at 'reached';
                            // drop the steps that form it (indices firstPosition..i inclusive).
                            steps.RemoveRange(firstPosition, i - firstPosition + 1);
                            changed = true;
                            break;
                        }

                        seen[reached] = i + 1;
                    }
                }
            }

            #endregion

            #region K-shortest loop-free paths (Yen's algorithm)

            /// <summary>
            ///   Computes up to <paramref name="k"/> least-weight loop-free paths in non-decreasing
            ///   weight order using Yen's algorithm on top of <see cref="TryShortestPath"/>.
            /// </summary>
            public List<Path> KShortestPaths(
                VertexModel source,
                VertexModel destination,
                int k,
                int maxHops,
                double maxWeight)
            {
                var accepted = new List<Path>();

                var shortest = TryShortestPath(source, destination, maxHops, maxWeight, null, null);
                if (shortest == null)
                {
                    return accepted;
                }

                var acceptedSignatures = new HashSet<string>();
                accepted.Add(shortest);
                acceptedSignatures.Add(Signature(shortest));

                var candidates = new PriorityQueue<Path, (double Weight, int Hops, string Signature)>(CandidatePriorityComparer.Instance);
                var candidateSignatures = new HashSet<string>();

                for (var index = 1; index < k; index++)
                {
                    var previous = accepted[index - 1];
                    var previousElements = previous.GetPathElements();

                    for (var spurIndex = 0; spurIndex < previousElements.Count; spurIndex++)
                    {
                        var spurNodeId = previousElements[spurIndex].SourceVertex.Id;

                        VertexModel spurNode;
                        if (!_fallen8.TryGetVertex(out spurNode, spurNodeId))
                        {
                            continue;
                        }

                        var rootElements = previousElements.Take(spurIndex).ToList();
                        var rootWeight = rootElements.Sum(element => element.Weight);
                        var rootHops = rootElements.Count;

                        // Forbid the step leaving the spur node that would reproduce an already
                        // known path sharing this root prefix.
                        var bannedSteps = new HashSet<(int EdgeId, Direction Direction)>();
                        foreach (var candidate in accepted)
                        {
                            var candidateElements = candidate.GetPathElements();
                            if (candidateElements.Count > spurIndex && ShareRoot(candidateElements, rootElements))
                            {
                                bannedSteps.Add((candidateElements[spurIndex].Edge.Id, candidateElements[spurIndex].Direction));
                            }
                        }

                        // Forbid the root's vertices (all but the spur node) so the spur path cannot
                        // route back through the already-fixed prefix.
                        var bannedVertices = new HashSet<int>();
                        foreach (var element in rootElements)
                        {
                            bannedVertices.Add(element.SourceVertex.Id);
                        }

                        var spurMaxHops = maxHops - rootHops;
                        if (spurMaxHops <= 0)
                        {
                            continue;
                        }

                        var spurMaxWeight = maxWeight == Double.MaxValue ? Double.MaxValue : maxWeight - rootWeight;

                        var spurPath = TryShortestPath(spurNode, destination, spurMaxHops, spurMaxWeight, bannedVertices, bannedSteps);
                        if (spurPath == null)
                        {
                            continue;
                        }

                        var total = Combine(rootElements, spurPath);
                        var signature = Signature(total);

                        if (!acceptedSignatures.Contains(signature) && candidateSignatures.Add(signature))
                        {
                            candidates.Enqueue(total, (total.Weight, total.GetLength(), signature));
                        }
                    }

                    Path next = null;
                    while (candidates.TryDequeue(out var candidatePath, out _))
                    {
                        var signature = Signature(candidatePath);
                        candidateSignatures.Remove(signature);

                        if (acceptedSignatures.Contains(signature))
                        {
                            continue;
                        }

                        next = candidatePath;
                        break;
                    }

                    if (next == null)
                    {
                        break;
                    }

                    accepted.Add(next);
                    acceptedSignatures.Add(Signature(next));
                }

                return accepted;
            }

            /// <summary>
            ///   Whether the first <c>rootElements.Count</c> steps of <paramref name="candidateElements"/>
            ///   are identical (edge + direction) to <paramref name="rootElements"/>.
            /// </summary>
            private static bool ShareRoot(List<PathElement> candidateElements, List<PathElement> rootElements)
            {
                for (var i = 0; i < rootElements.Count; i++)
                {
                    if (!ReferenceEquals(candidateElements[i].Edge, rootElements[i].Edge)
                        || candidateElements[i].Direction != rootElements[i].Direction)
                    {
                        return false;
                    }
                }

                return true;
            }

            /// <summary>
            ///   Concatenates a root prefix with a spur path into a single path.
            /// </summary>
            private static Path Combine(List<PathElement> rootElements, Path spurPath)
            {
                var spurElements = spurPath.GetPathElements();

                var path = new Path(rootElements.Count + spurElements.Count);
                foreach (var element in rootElements)
                {
                    path.AddPathElement(element);
                }

                foreach (var element in spurElements)
                {
                    path.AddPathElement(element);
                }

                return path;
            }

            /// <summary>
            ///   A stable identity for a path (its ordered edge-id/direction sequence), used to
            ///   de-duplicate candidates and results.
            /// </summary>
            private static string Signature(Path path)
            {
                var builder = new StringBuilder();
                foreach (var element in path.GetPathElements())
                {
                    builder.Append(element.Edge.Id);
                    builder.Append((byte)element.Direction == (byte)Direction.IncomingEdge ? 'I' : 'O');
                    builder.Append('-');
                }

                return builder.ToString();
            }

            #endregion

            #region neighbour enumeration

            /// <summary>
            ///   Enumerates the traversable neighbour steps of <paramref name="vertex"/> across both
            ///   outgoing and incoming edges, applying the edge-property, edge and vertex filters in
            ///   the same order as <c>BLS</c>. The result is ordered by (edge id, direction) so the
            ///   search is deterministic regardless of the underlying dictionary ordering.
            /// </summary>
            private List<NeighbourStep> GetNeighbours(VertexModel vertex)
            {
                var steps = new List<NeighbourStep>();

                var outEdges = vertex.OutEdges;
                if (outEdges != null)
                {
                    foreach (var container in outEdges)
                    {
                        if (_edgePropertyFilter != null && !_edgePropertyFilter(container.Key))
                        {
                            continue;
                        }

                        foreach (var edge in container.Value)
                        {
                            if (_edgeFilter != null && !_edgeFilter(edge))
                            {
                                continue;
                            }

                            var neighbour = edge.TargetVertex;
                            if (_vertexFilter != null && !_vertexFilter(neighbour))
                            {
                                continue;
                            }

                            steps.Add(new NeighbourStep(edge, container.Key, Direction.OutgoingEdge, neighbour, StepCost(edge, neighbour)));
                        }
                    }
                }

                var inEdges = vertex.InEdges;
                if (inEdges != null)
                {
                    foreach (var container in inEdges)
                    {
                        if (_edgePropertyFilter != null && !_edgePropertyFilter(container.Key))
                        {
                            continue;
                        }

                        foreach (var edge in container.Value)
                        {
                            if (_edgeFilter != null && !_edgeFilter(edge))
                            {
                                continue;
                            }

                            var neighbour = edge.SourceVertex;
                            if (_vertexFilter != null && !_vertexFilter(neighbour))
                            {
                                continue;
                            }

                            steps.Add(new NeighbourStep(edge, container.Key, Direction.IncomingEdge, neighbour, StepCost(edge, neighbour)));
                        }
                    }
                }

                steps.Sort(CompareSteps);

                return steps;
            }

            private static int CompareSteps(NeighbourStep a, NeighbourStep b)
            {
                var byEdge = a.Edge.Id.CompareTo(b.Edge.Id);
                if (byEdge != 0)
                {
                    return byEdge;
                }

                return ((byte)a.Direction).CompareTo((byte)b.Direction);
            }

            /// <summary>
            ///   The cost of extending a path to <paramref name="neighbour"/> across
            ///   <paramref name="edge"/>: <c>(edgeCost(e) ?? 1.0) + (vertexCost(v) ?? 0.0)</c>. A
            ///   negative combined cost is clamped to 0 (Dijkstra requires non-negative costs) and a
            ///   warning is logged once per calculation.
            /// </summary>
            private double StepCost(EdgeModel edge, VertexModel neighbour)
            {
                var cost = (_edgeCost != null ? _edgeCost(edge) : 1.0)
                           + (_vertexCost != null ? _vertexCost(neighbour) : 0.0);

                if (cost < 0.0)
                {
                    if (!_negativeCostWarned)
                    {
                        _logger?.LogWarning(
                            "DIJKSTRA encountered a negative step cost ({Cost}) at edge {EdgeId}; clamping it to 0. Dijkstra requires non-negative costs, so results are undefined for negative costs.",
                            cost, edge.Id);
                        _negativeCostWarned = true;
                    }

                    cost = 0.0;
                }

                return cost;
            }

            #endregion

            #region helper types

            /// <summary>
            ///   Orders K-shortest candidates by (weight, hops, signature). The signature tie-break
            ///   uses an ORDINAL string comparison so the choice between equal-weight, equal-length
            ///   candidates is culture-invariant and the returned K paths are byte-identical across
            ///   locales. (The default <c>ValueTuple</c> comparer would compare the signature with the
            ///   culture-sensitive default string comparer, so which tie is emitted when
            ///   <c>MaxResults</c> is smaller than a tie group could otherwise differ per locale.)
            /// </summary>
            private sealed class CandidatePriorityComparer : IComparer<(double Weight, int Hops, string Signature)>
            {
                public static readonly CandidatePriorityComparer Instance = new CandidatePriorityComparer();

                public int Compare(
                    (double Weight, int Hops, string Signature) x,
                    (double Weight, int Hops, string Signature) y)
                {
                    var byWeight = x.Weight.CompareTo(y.Weight);
                    if (byWeight != 0)
                    {
                        return byWeight;
                    }

                    var byHops = x.Hops.CompareTo(y.Hops);
                    if (byHops != 0)
                    {
                        return byHops;
                    }

                    return String.CompareOrdinal(x.Signature, y.Signature);
                }
            }

            /// <summary>
            ///   A candidate traversal from the currently expanded vertex to a neighbour.
            /// </summary>
            private readonly struct NeighbourStep
            {
                public readonly EdgeModel Edge;
                public readonly String EdgePropertyId;
                public readonly Direction Direction;
                public readonly VertexModel Neighbour;
                public readonly double Cost;

                public NeighbourStep(EdgeModel edge, String edgePropertyId, Direction direction, VertexModel neighbour, double cost)
                {
                    Edge = edge;
                    EdgePropertyId = edgePropertyId;
                    Direction = direction;
                    Neighbour = neighbour;
                    Cost = cost;
                }
            }

            /// <summary>
            ///   The step taken to reach a search state, plus the predecessor state, for path
            ///   reconstruction.
            /// </summary>
            private readonly struct PredecessorRecord
            {
                public readonly EdgeModel Edge;
                public readonly String EdgePropertyId;
                public readonly Direction Direction;
                public readonly int FromVertexId;
                public readonly int FromHops;
                public readonly double Cost;

                public PredecessorRecord(EdgeModel edge, String edgePropertyId, Direction direction, int fromVertexId, int fromHops, double cost)
                {
                    Edge = edge;
                    EdgePropertyId = edgePropertyId;
                    Direction = direction;
                    FromVertexId = fromVertexId;
                    FromHops = fromHops;
                    Cost = cost;
                }

                /// <summary>
                ///   The vertex reached by this step (the neighbour side of the edge given the
                ///   traversal direction).
                /// </summary>
                public int ToVertexId
                {
                    get
                    {
                        return Direction == Direction.IncomingEdge ? Edge.SourceVertex.Id : Edge.TargetVertex.Id;
                    }
                }
            }

            #endregion
        }

        #endregion

        #region IPlugin Members

        public string PluginName
        {
            get
            {
                return "DIJKSTRA";
            }
        }

        public Type PluginCategory
        {
            get
            {
                return typeof(IShortestPathAlgorithm);
            }
        }

        public string Description
        {
            get
            {
                return "Weighted single source shortest path (Dijkstra) honouring edge/vertex costs and the maximum path weight; returns the K least-weight loop-free paths via Yen's algorithm.";
            }
        }

        public string Manufacturer
        {
            get
            {
                return "Henning Rauch";
            }
        }

        public void Initialize(IFallen8 fallen8, IDictionary<string, object> parameter)
        {
            _fallen8 = fallen8;
            _logger = fallen8.LoggerFactory.CreateLogger<WeightedDijkstraShortestPath>();
        }

        #endregion

        #region IDisposable Members

        public void Dispose()
        {
            //nothing to do atm
        }

        #endregion
    }
}
