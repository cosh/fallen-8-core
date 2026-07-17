// MIT License
//
// BidirectionalLevelSynchronousSSSP.cs
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
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Core.Model;


#endregion

namespace NoSQL.GraphDB.Core.Algorithms.Path
{
    /// <summary>
    ///   Bidirctional level synchronous SSSP algorithm
    /// </summary>
    public sealed class BidirectionalLevelSynchronousSSSP : IShortestPathAlgorithm
    {
        #region Data

        /// <summary>
        ///   The Fallen-8
        /// </summary>
        private IFallen8 _fallen8;

        /// <summary>
        /// The logger
        /// </summary>
        private ILogger<BidirectionalLevelSynchronousSSSP> _logger;

        #endregion

        #region IShortestPathAlgorithm Members

        public bool TryCalculateShortestPath(
            out List<Path> result,
            ShortestPathDefinition definition)
        {
            var calculatedResult = Calculate(
                definition.SourceVertexId,
                definition.DestinationVertexId,
                definition.MaxDepth,
                definition.MaxPathWeight,
                definition.MaxResults,
                definition.EdgePropertyFilter,
                definition.VertexFilter,
                definition.EdgeFilter,
                definition.EdgeCost,
                definition.VertexCost);

            if (calculatedResult != null && calculatedResult.Count > 0)
            {
                result = calculatedResult;
                return true;
            }

            result = new List<Path>();
            return false;
        }

        private List<Path> Calculate(
            Int32 sourceVertexId,
            Int32 destinationVertexId,
            Int32 maxDepth = 1,
            Double maxPathWeight = Double.MaxValue,
            Int32 maxResults = 1,
            Delegates.EdgePropertyFilter edgePropertyFilter = null,
            Delegates.VertexFilter vertexFilter = null,
            Delegates.EdgeFilter edgeFilter = null,
            Delegates.EdgeCost edgeCost = null,
            Delegates.VertexCost vertexCost = null)
        {
            #region initial checks

            VertexModel sourceVertex;
            VertexModel targetVertex;
            if (!(_fallen8.TryGetVertex(out sourceVertex, sourceVertexId)
                  && _fallen8.TryGetVertex(out targetVertex, destinationVertexId)))
            {
                return null;
            }

            if (sourceVertex._removed || targetVertex._removed)
            {
                return null;
            }

            if (maxDepth == 0 || maxResults == 0 || maxResults <= 0)
            {
                return null;
            }

            if (ReferenceEquals(sourceVertex, targetVertex))
            {
                return null;
            }

            #endregion

            #region data

            var sourceVisitedVertices = new HashSet<VertexModel>();
            sourceVisitedVertices.Add(sourceVertex);

            var targetVisitedVertices = new HashSet<VertexModel>();
            targetVisitedVertices.Add(targetVertex);

            #endregion

            if (maxDepth == 1)
            {
                #region maxdepth == 1

                var depthOneFrontier = GetGlobalFrontier(new List<VertexModel> { sourceVertex }, sourceVisitedVertices, edgePropertyFilter, edgeFilter, vertexFilter);

                if (depthOneFrontier != null && depthOneFrontier.Count > 0 && depthOneFrontier.ContainsKey(targetVertex))
                {
                    //so there is something; parallel edges each yield a path, so cap at maxResults
                    return new List<Path>(depthOneFrontier
                        .Where(_ => ReferenceEquals(_.Key, targetVertex))
                        .Select(__ => CreateAPath(__.Value))
                        .SelectMany(___ => ___)
                        .Take(maxResults));
                }

                return null;

                #endregion
            }
            else
            {
                #region maxdepth > 1

                //find the middle element  s-->m-->t
                Int32 sourceLevel = 0;
                Int32 targetLevel = 0;

                var sourceFrontiers = new List<Dictionary<VertexModel, VertexPredecessor>>();
                var targetFrontiers = new List<Dictionary<VertexModel, VertexPredecessor>>();
                Dictionary<VertexModel, VertexPredecessor> currentSourceFrontier = null;
                Dictionary<VertexModel, VertexPredecessor> currentTargetFrontier = null;
                IEnumerable<VertexModel> currentSourceVertices = new List<VertexModel> { sourceVertex };
                IEnumerable<VertexModel> currentTargetVertices = new List<VertexModel> { targetVertex };

                List<VertexModel> middleVertices = null;

                do
                {
                    #region calculate frontier

                    #region source --> target

                    currentSourceFrontier = GetGlobalFrontier(currentSourceVertices, sourceVisitedVertices, edgePropertyFilter, edgeFilter, vertexFilter);
                    sourceFrontiers.Add(currentSourceFrontier);
                    currentSourceVertices = sourceFrontiers[sourceLevel].Keys;
                    sourceLevel++;

                    if (currentSourceFrontier.ContainsKey(targetVertex))
                    {
                        if (middleVertices == null)
                        {
                            middleVertices = new List<VertexModel> { targetVertex };
                        }
                        else
                        {
                            middleVertices.Add(targetVertex);
                        }
                        break;
                    }
                    if (FindMiddleVertices(out middleVertices, currentSourceFrontier, currentTargetFrontier)) break;
                    if ((sourceLevel + targetLevel) == maxDepth) break;

                    #endregion

                    #region target --> source

                    currentTargetFrontier = GetGlobalFrontier(currentTargetVertices, targetVisitedVertices, edgePropertyFilter, edgeFilter, vertexFilter);
                    targetFrontiers.Add(currentTargetFrontier);
                    currentTargetVertices = targetFrontiers[targetLevel].Keys;
                    targetLevel++;


                    if (currentTargetFrontier.ContainsKey(sourceVertex))
                    {
                        if (middleVertices == null)
                        {
                            middleVertices = new List<VertexModel> { sourceVertex };
                        }
                        else
                        {
                            middleVertices.Add(sourceVertex);
                        }
                        break;
                    }
                    if (FindMiddleVertices(out middleVertices, currentSourceFrontier, currentTargetFrontier)) break;
                    if ((sourceLevel + targetLevel) == maxDepth) break;

                    #endregion

                    #endregion

                } while (true);

                return middleVertices != null
                    ? CreatePaths(middleVertices, sourceFrontiers, targetFrontiers, maxResults, sourceLevel, targetLevel)
                    : null;

                #endregion
            }
        }

        #endregion

        #region private helper

        /// <summary>
        /// Creates paths
        /// </summary>
        /// <param name="vertexPredecessor">The vertex predecessors</param>
        /// <returns>Enumeration of paths</returns>
        private static IEnumerable<Path> CreateAPath(VertexPredecessor vertexPredecessor)
        {
            foreach (var aInPred in vertexPredecessor.Incoming)
            {
                yield return new Path(new PathElement(aInPred.Edge, aInPred.EdgePropertyId, Direction.IncomingEdge));
            }

            foreach (var aOutPred in vertexPredecessor.Outgoing)
            {
                yield return new Path(new PathElement(aOutPred.Edge, aOutPred.EdgePropertyId, Direction.OutgoingEdge));
            }
        }

        /// <summary>
        /// Finds the middle vertices of two given frontiers
        /// </summary>
        /// <param name="middleVertices">The result</param>
        /// <param name="currentSourceFrontier">The source frontier</param>
        /// <param name="currentTargetFrontier">The target frontier</param>
        /// <returns>True if there are middle vertices, otherwise false</returns>
        private static bool FindMiddleVertices(
            out List<VertexModel> middleVertices,
            Dictionary<VertexModel, VertexPredecessor> currentSourceFrontier,
            Dictionary<VertexModel, VertexPredecessor> currentTargetFrontier)
        {
            if (currentSourceFrontier == null || currentTargetFrontier == null)
            {
                middleVertices = null;
                return false;
            }

            middleVertices = currentSourceFrontier.Keys.Intersect(currentTargetFrontier.Keys).ToList();
            middleVertices = middleVertices.Count > 0 ? middleVertices : null;

            return middleVertices != null;
        }

        /// <summary>
        /// Creates the paths
        /// </summary>
        /// <param name="middleVertices">The middle vertices of the path</param>
        /// <param name="sourceFrontiers">The source frontier</param>
        /// <param name="targetFrontiers">The target frontier</param>
        /// <param name="maxResults">The maximum number of paths in result</param>
        /// <param name="sourceLevel">The source level</param>
        /// <param name="targetLevel">The target level</param>
        /// <returns>A list of paths</returns>
        private static List<Path> CreatePaths(
            List<VertexModel> middleVertices,
            List<Dictionary<VertexModel, VertexPredecessor>> sourceFrontiers,
            List<Dictionary<VertexModel, VertexPredecessor>> targetFrontiers,
            int maxResults,
            Int32 sourceLevel,
            Int32 targetLevel)
        {
            if (middleVertices != null && middleVertices.Count > 0)
            {
                var result = new List<Path>();

                #region middle --> source

                var middleToSourcePaths = new List<Path>();

                var previousSourceLevel = sourceLevel - 1;
                if (previousSourceLevel == 0)
                {
                    //source must be pred now
                    for (var i = 0; i < middleVertices.Count; i++)
                    {
                        var middleVertex = middleVertices[i];
                        var pred = sourceFrontiers[previousSourceLevel][middleVertex];

                        middleToSourcePaths.AddRange(
                            pred.Incoming.Select(
                                edgeLocation =>
                                new Path(new PathElement(edgeLocation.Edge, edgeLocation.EdgePropertyId,
                                                         Direction.IncomingEdge))));
                        middleToSourcePaths.AddRange(
                            pred.Outgoing.Select(
                                edgeLocation =>
                                new Path(new PathElement(edgeLocation.Edge, edgeLocation.EdgePropertyId,
                                                         Direction.OutgoingEdge))));

                        // Early termination (finding P6): each middle->source path yields at least one
                        // full path once extended toward the target, so once we hold maxResults of them
                        // we already have enough to satisfy the final cap in order; stop building more.
                        if (middleToSourcePaths.Count >= maxResults)
                        {
                            break;
                        }
                    }
                    CapPaths(middleToSourcePaths, maxResults);
                }
                else
                {
                    //recursion
                    middleToSourcePaths = CreateToSourcePaths(middleVertices, sourceFrontiers, previousSourceLevel, maxResults);
                }


                //they have to be in reverse order because we went backward
                middleToSourcePaths.ForEach(_ => _.ReversePath());

                #endregion

                #region middle --> target

                var previousTargetLevel = targetLevel - 1;
                switch (previousTargetLevel)
                {
                    case -1:
                        //the target vertex located in the middle vertices
                        //nothing to do
                        result.AddRange(middleToSourcePaths);

                        break;

                    case 0:
                        //target is direct pred
                        for (var i = 0; i < middleToSourcePaths.Count; i++)
                        {
                            var middlePath = middleToSourcePaths[i];
                            var pred = targetFrontiers[previousTargetLevel][middlePath.LastPathElement.TargetVertex];
                            result.AddRange(
                            pred.Incoming.Select(
                                edgeLocation =>
                                new Path(middlePath, new PathElement(edgeLocation.Edge, edgeLocation.EdgePropertyId,
                                                         Direction.OutgoingEdge))));
                            result.AddRange(
                                pred.Outgoing.Select(
                                    edgeLocation =>
                                    new Path(middlePath, new PathElement(edgeLocation.Edge, edgeLocation.EdgePropertyId,
                                                             Direction.IncomingEdge))));

                            //early termination once we have enough (finding P6)
                            if (result.Count >= maxResults)
                            {
                                break;
                            }
                        }

                        break;

                    default:
                        //recursion
                        result = CreatePathsRecusive(middleToSourcePaths, targetFrontiers, previousTargetLevel, Direction.OutgoingEdge, Direction.IncomingEdge, false, maxResults);

                        break;

                }

                #endregion

                return result.Take(maxResults).ToList();
            }

            return null;
        }

        /// <summary>
        ///   Caps a path list to the first <paramref name="maxResults" /> entries in place (finding
        ///   P6). Truncating an intermediate reconstruction list to maxResults is result-preserving:
        ///   the final result is <c>Take(maxResults)</c> of paths built in a fixed order, each
        ///   intermediate path yields at least one final path when extended, so the first maxResults
        ///   intermediates already cover the first maxResults finals; dropping the tail never changes
        ///   the first maxResults paths or their order.
        /// </summary>
        private static void CapPaths(List<Path> paths, int maxResults)
        {
            if (maxResults >= 0 && paths.Count > maxResults)
            {
                paths.RemoveRange(maxResults, paths.Count - maxResults);
            }
        }

        /// <summary>
        /// Creates the paths from the middle vertices to the source
        /// </summary>
        /// <param name="middleVertices">The middle vertices</param>
        /// <param name="sourceFrontiers">The source frontier</param>
        /// <param name="sourceLevel">The source level</param>
        /// <returns>The list of paths from the source to the middle vertices in reverse order</returns>
        private static List<Path> CreateToSourcePaths(List<VertexModel> middleVertices, List<Dictionary<VertexModel, VertexPredecessor>> sourceFrontiers, int sourceLevel, int maxResults)
        {
            var firstPaths = new List<Path>();
            //source must be pred now
            for (var i = 0; i < middleVertices.Count; i++)
            {
                var middleVertex = middleVertices[i];
                var pred = sourceFrontiers[sourceLevel][middleVertex];

                firstPaths.AddRange(
                    pred.Incoming.Select(
                        edgeLocation =>
                        new Path(new PathElement(edgeLocation.Edge, edgeLocation.EdgePropertyId,
                                                 Direction.IncomingEdge))));
                firstPaths.AddRange(
                    pred.Outgoing.Select(
                        edgeLocation =>
                        new Path(new PathElement(edgeLocation.Edge, edgeLocation.EdgePropertyId,
                                                 Direction.OutgoingEdge))));

                //early termination once we have enough (finding P6)
                if (firstPaths.Count >= maxResults)
                {
                    break;
                }
            }
            CapPaths(firstPaths, maxResults);

            if (sourceLevel == 0)
            {
                return firstPaths;
            }

            var newSourceLevel = sourceLevel - 1;

            return CreatePathsRecusive(firstPaths, sourceFrontiers, newSourceLevel, Direction.IncomingEdge, Direction.OutgoingEdge, true, maxResults);
        }

        /// <summary>
        /// Creates paths in a recursive way
        /// </summary>
        /// <param name="currentPaths">The paths to start from</param>
        /// <param name="frontier">The frontier to walk on</param>
        /// <param name="level">The current level</param>
        /// <param name="incomingPredDirection">The direction of the incoming predecessors (depends on the frontier)</param>
        /// <param name="outgoingPredDirection">The direction of the outgoing predecessors (depends on the frontier)</param>
        /// <returns>List of paths</returns>
        private static List<Path> CreatePathsRecusive(List<Path> currentPaths, List<Dictionary<VertexModel, VertexPredecessor>> frontier, int level, Direction incomingPredDirection, Direction outgoingPredDirection, bool toSource, int maxResults)
        {
            var result = new List<Path>();

            switch (level)
            {
                case -1:
                    // currentPaths is already capped to maxResults by the caller.
                    return currentPaths;

                case 0:
                    //target is direct pred
                    for (var i = 0; i < currentPaths.Count; i++)
                    {
                        var middlePath = currentPaths[i];
                        var keyVertex = toSource ? middlePath.LastPathElement.SourceVertex : middlePath.LastPathElement.TargetVertex;
                        var pred = frontier[level][keyVertex];
                        result.AddRange(
                        pred.Incoming.Select(
                            edgeLocation =>
                            new Path(middlePath, new PathElement(edgeLocation.Edge, edgeLocation.EdgePropertyId,
                                                     incomingPredDirection))));
                        result.AddRange(
                            pred.Outgoing.Select(
                                edgeLocation =>
                                new Path(middlePath, new PathElement(edgeLocation.Edge, edgeLocation.EdgePropertyId,
                                                         outgoingPredDirection))));

                        //early termination once we have enough (finding P6)
                        if (result.Count >= maxResults)
                        {
                            break;
                        }
                    }
                    CapPaths(result, maxResults);
                    break;

                default:

                    var newMiddlePaths = new List<Path>();

                    for (var i = 0; i < currentPaths.Count; i++)
                    {
                        var middlePath = currentPaths[i];
                        var keyVertex = toSource ? middlePath.LastPathElement.SourceVertex : middlePath.LastPathElement.TargetVertex;
                        var pred = frontier[level][keyVertex];
                        newMiddlePaths.AddRange(
                        pred.Incoming.Select(
                            edgeLocation =>
                            new Path(middlePath, new PathElement(edgeLocation.Edge, edgeLocation.EdgePropertyId,
                                                     incomingPredDirection))));
                        newMiddlePaths.AddRange(
                            pred.Outgoing.Select(
                                edgeLocation =>
                                new Path(middlePath, new PathElement(edgeLocation.Edge, edgeLocation.EdgePropertyId,
                                                         outgoingPredDirection))));

                        // Each of these intermediate paths yields at least one final path deeper in
                        // the recursion, so once we hold maxResults of them we can stop expanding this
                        // level (finding P6); the deeper levels and the final Take(maxResults) trim.
                        if (newMiddlePaths.Count >= maxResults)
                        {
                            break;
                        }
                    }
                    CapPaths(newMiddlePaths, maxResults);

                    var newPredLevel = level - 1;

                    result = CreatePathsRecusive(newMiddlePaths, frontier, newPredLevel, incomingPredDirection, outgoingPredDirection, toSource, maxResults);

                    break;
            }

            return result;
        }


        /// <summary>
        /// Gets the global frontier corresponding to a certain level.
        ///
        /// Visited bookkeeping (fix for the converging-shortest-paths bug pinned by
        /// PathAlgorithmParityTest): a frontier vertex becomes "visited" only AFTER the whole
        /// level is built. Marking it during enumeration dropped every further edge that reaches
        /// the same vertex within the SAME level - parallel edges and equal-length paths
        /// converging on a shared vertex - so only one predecessor per vertex survived and BLS
        /// under-reported the fewest-hop paths that a unit-cost DIJKSTRA finds. The visited set
        /// therefore only excludes EARLIER levels here, and all same-level discoveries merge
        /// their predecessor edges below.
        /// </summary>
        /// <param name="startingVertices">The starting vertices behind the frontier</param>
        /// <param name="visitedVertices">The visited vertices corresponding to the frontier</param>
        /// <param name="edgepropertyFilter">The edge property filter</param>
        /// <param name="edgeFilter">The edge filter</param>
        /// <param name="vertexFilter">The vertex filter</param>
        /// <returns>The frontier vertices and their predecessors</returns>
        private static Dictionary<VertexModel, VertexPredecessor> GetGlobalFrontier(IEnumerable<VertexModel> startingVertices, HashSet<VertexModel> visitedVertices,
            Delegates.EdgePropertyFilter edgepropertyFilter,
            Delegates.EdgeFilter edgeFilter,
            Delegates.VertexFilter vertexFilter)
        {
            var frontier = new Dictionary<VertexModel, VertexPredecessor>();

            foreach (var aKv in startingVertices)
            {
                foreach (var aFrontierElement in GetLocalFrontier(aKv, visitedVertices, edgepropertyFilter, edgeFilter, vertexFilter))
                {
                    VertexPredecessor pred;
                    if (frontier.TryGetValue(aFrontierElement.FrontierVertex, out pred))
                    {
                        switch (aFrontierElement.EdgeDirection)
                        {
                            case Direction.IncomingEdge:
                                pred.Incoming.Add(aFrontierElement.EdgeLocation);
                                break;

                            case Direction.OutgoingEdge:
                                pred.Outgoing.Add(aFrontierElement.EdgeLocation);
                                break;

                            default:
                                throw new ArgumentOutOfRangeException();
                        }
                    }
                    else
                    {
                        pred = new VertexPredecessor();
                        switch (aFrontierElement.EdgeDirection)
                        {
                            case Direction.IncomingEdge:
                                pred.Incoming.Add(aFrontierElement.EdgeLocation);
                                break;

                            case Direction.OutgoingEdge:
                                pred.Outgoing.Add(aFrontierElement.EdgeLocation);
                                break;

                            default:
                                throw new ArgumentOutOfRangeException();
                        }
                        frontier.Add(aFrontierElement.FrontierVertex, pred);
                    }
                }
            }

            //the level is complete - only now do its vertices become visited (see summary)
            foreach (var aFrontierVertex in frontier.Keys)
            {
                visitedVertices.Add(aFrontierVertex);
            }

            return frontier;
        }

        /// <summary>
        /// Gets the frontier elements over one adjacency direction (feature code-quality: one
        /// implementation instead of the former in/out near-twins whose edgeFilter x
        /// vertexFilter branch matrix repeated the same construction six times). The visited set
        /// is only READ for accepted vertices - the level marks them visited when it completes
        /// (see <see cref="GetGlobalFrontier"/>) - while a filter-rejected vertex is marked
        /// visited immediately and stays visited.
        /// </summary>
        /// <param name="vertex">The vertex behind the frontier</param>
        /// <param name="direction">Incoming or outgoing</param>
        /// <param name="edgepropertyFilter">The edge property filter</param>
        /// <param name="edgeFilter">The edge filter</param>
        /// <param name="vertexFilter">The vertex filter</param>
        /// <param name="alreadyVisited">The vertices that have been visited already</param>
        /// <returns>A number of frontier elements</returns>
        private static IEnumerable<FrontierElement> GetValidEdges(
            VertexModel vertex,
            Direction direction,
            Delegates.EdgePropertyFilter edgepropertyFilter,
            Delegates.EdgeFilter edgeFilter,
            Delegates.VertexFilter vertexFilter,
            HashSet<VertexModel> alreadyVisited)
        {
            var incoming = direction == Direction.IncomingEdge;
            var edgeProperties = incoming ? vertex.GetRawInEdges() : vertex.GetRawOutEdges();
            var result = new List<FrontierElement>();

            if (edgeProperties != null)
            {
                foreach (var edgeContainer in edgeProperties)
                {
                    if (edgepropertyFilter != null && !edgepropertyFilter(edgeContainer.Key))
                    {
                        continue;
                    }

                    for (var i = 0; i < edgeContainer.Value.Count; i++)
                    {
                        var aEdge = edgeContainer.Value[i];
                        if (edgeFilter != null && !edgeFilter(aEdge))
                        {
                            continue;
                        }

                        var frontierVertex = incoming ? aEdge.SourceVertex : aEdge.TargetVertex;
                        if (alreadyVisited.Contains(frontierVertex))
                        {
                            continue;
                        }

                        if (vertexFilter != null && !vertexFilter(frontierVertex))
                        {
                            alreadyVisited.Add(frontierVertex);
                            continue;
                        }

                        result.Add(new FrontierElement
                        {
                            EdgeDirection = direction,
                            EdgeLocation = new EdgeLocation
                            {
                                Edge = aEdge,
                                EdgePropertyId = edgeContainer.Key
                            },
                            FrontierVertex = frontierVertex
                        });
                    }
                }
            }

            return result;
        }
        /// <summary>
        /// Gets the local frontier corresponding to a vertex
        /// </summary>
        /// <param name="vertex">The vertex behind the local frontier</param>
        /// <param name="alreadyVisitedVertices">The vertices that have been visited already</param>
        /// <param name="edgepropertyFilter">The edge property filter</param>
        /// <param name="edgeFilter">The edge filter</param>
        /// <param name="vertexFilter">The vertex filter</param>
        /// <returns>The local frontier</returns>
        private static IEnumerable<FrontierElement> GetLocalFrontier(VertexModel vertex, HashSet<VertexModel> alreadyVisitedVertices,
            Delegates.EdgePropertyFilter edgepropertyFilter,
            Delegates.EdgeFilter edgeFilter,
            Delegates.VertexFilter vertexFilter)
        {
            var result = new List<FrontierElement>();

            result.AddRange(GetValidEdges(vertex, Direction.IncomingEdge, edgepropertyFilter, edgeFilter, vertexFilter, alreadyVisitedVertices));
            result.AddRange(GetValidEdges(vertex, Direction.OutgoingEdge, edgepropertyFilter, edgeFilter, vertexFilter, alreadyVisitedVertices));

            return result;
        }

        #endregion

        #region IPlugin Members

        public string PluginName
        {
            get
            {
                return "BLS";
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
                return "Bidirectional level synchronous single source shortest path algorithm.";
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
            _logger = fallen8.LoggerFactory.CreateLogger<BidirectionalLevelSynchronousSSSP>();
        }

        #endregion

        #region IDisposable Members

        public void Dispose()
        {
            //nothing to do atm
        }

        #endregion
    }

    internal class VertexPredecessor
    {
        public readonly List<EdgeLocation> Incoming = new List<EdgeLocation>();
        public readonly List<EdgeLocation> Outgoing = new List<EdgeLocation>();
    }

    internal class EdgeLocation
    {
        public EdgeModel Edge;
        public String EdgePropertyId;
    }

    internal class FrontierElement
    {
        public VertexModel FrontierVertex;
        public EdgeLocation EdgeLocation;
        public Direction EdgeDirection;
    }
}
