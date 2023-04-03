﻿// MIT License
//
// BidirectionalLevelSynchronousSSSP.cs
//
// Copyright (c) 2022 Henning Rauch
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
        private Fallen8 _fallen8;

        #endregion

        #region IShortestPathAlgorithm Members

        public List<Path> Calculate(
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
                    //so there is something
                    return new List<Path>(depthOneFrontier
                        .Where(_ => ReferenceEquals(_.Key, targetVertex))
                        .Select(__ => CreateAPath(__.Value))
                        .SelectMany(___ => ___));
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
                    }
                }
                else
                {
                    //recursion
                    middleToSourcePaths = CreateToSourcePaths(middleVertices, sourceFrontiers, previousSourceLevel);
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
                        }

                        break;

                    default:
                        //recursion
                        result = CreatePathsRecusive(middleToSourcePaths, targetFrontiers, previousTargetLevel, Direction.OutgoingEdge, Direction.IncomingEdge, false);

                        break;

                }

                #endregion

                return result.Take(maxResults).ToList();
            }

            return null;
        }

        /// <summary>
        /// Creates the paths from the middle vertices to the source
        /// </summary>
        /// <param name="middleVertices">The middle vertices</param>
        /// <param name="sourceFrontiers">The source frontier</param>
        /// <param name="sourceLevel">The source level</param>
        /// <returns>The list of paths from the source to the middle vertices in reverse order</returns>
        private static List<Path> CreateToSourcePaths(List<VertexModel> middleVertices, List<Dictionary<VertexModel, VertexPredecessor>> sourceFrontiers, int sourceLevel)
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
            }

            if (sourceLevel == 0)
            {
                return firstPaths;
            }

            var newSourceLevel = sourceLevel - 1;

            return CreatePathsRecusive(firstPaths, sourceFrontiers, newSourceLevel, Direction.IncomingEdge, Direction.OutgoingEdge, true);
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
        private static List<Path> CreatePathsRecusive(List<Path> currentPaths, List<Dictionary<VertexModel, VertexPredecessor>> frontier, int level, Direction incomingPredDirection, Direction outgoingPredDirection, bool toSource)
        {
            var result = new List<Path>();

            switch (level)
            {
                case -1:
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
                    }
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
                    }

                    var newPredLevel = level - 1;

                    result = CreatePathsRecusive(newMiddlePaths, frontier, newPredLevel, incomingPredDirection, outgoingPredDirection, toSource);

                    break;
            }

            return result;
        }


        /// <summary>
        /// Gets the global frontier corresponding to a certain level
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

            return frontier;
        }

        /// <summary>
        /// Gets the frontier elements on an incoming edge
        /// </summary>
        /// <param name="vertex">The vertex behind the frontier</param>
        /// <param name="edgepropertyFilter">The edge property filter</param>
        /// <param name="edgeFilter">The edge filter</param>
        /// <param name="vertexFilter">The vertex filter</param>
        /// <param name="alreadyVisited">The vertices that have been visited already</param>
        /// <returns>A number of frontier elements</returns>
        private static IEnumerable<FrontierElement> GetValidIncomingEdges(
            VertexModel vertex,
            Delegates.EdgePropertyFilter edgepropertyFilter,
            Delegates.EdgeFilter edgeFilter,
            Delegates.VertexFilter vertexFilter,
            HashSet<VertexModel> alreadyVisited)
        {
            var edgeProperties = vertex.InEdges;
            var result = new List<FrontierElement>();

            if (edgeProperties != null)
            {
                foreach (var edgeContainer in edgeProperties)
                {
                    if (edgepropertyFilter != null && !edgepropertyFilter(edgeContainer.Key, Direction.IncomingEdge))
                    {
                        continue;
                    }

                    if (edgeFilter != null)
                    {
                        for (var i = 0; i < edgeContainer.Value.Count; i++)
                        {
                            var aEdge = edgeContainer.Value[i];
                            if (edgeFilter(aEdge, Direction.IncomingEdge))
                            {
                                if (alreadyVisited.Add(aEdge.SourceVertex))
                                {
                                    if (vertexFilter != null)
                                    {
                                        if (vertexFilter(aEdge.SourceVertex))
                                        {
                                            result.Add(new FrontierElement
                                            {
                                                EdgeDirection = Direction.IncomingEdge,
                                                EdgeLocation = new EdgeLocation
                                                {
                                                    Edge = aEdge,
                                                    EdgePropertyId =
                                                        edgeContainer.Key
                                                },
                                                FrontierVertex = aEdge.SourceVertex
                                            });
                                        }
                                    }
                                    else
                                    {
                                        result.Add(new FrontierElement
                                        {
                                            EdgeDirection = Direction.IncomingEdge,
                                            EdgeLocation = new EdgeLocation
                                            {
                                                Edge = aEdge,
                                                EdgePropertyId =
                                                    edgeContainer.Key
                                            },
                                            FrontierVertex = aEdge.SourceVertex
                                        });
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        if (vertexFilter != null)
                        {
                            for (var i = 0; i < edgeContainer.Value.Count; i++)
                            {
                                var aEdge = edgeContainer.Value[i];

                                if (alreadyVisited.Add(aEdge.SourceVertex))
                                {
                                    if (vertexFilter(aEdge.SourceVertex))
                                    {
                                        result.Add(new FrontierElement
                                        {
                                            EdgeDirection = Direction.IncomingEdge,
                                            EdgeLocation = new EdgeLocation
                                            {
                                                Edge = aEdge,
                                                EdgePropertyId =
                                                    edgeContainer.Key
                                            },
                                            FrontierVertex = aEdge.SourceVertex
                                        });
                                    }
                                }
                            }
                        }
                        else
                        {
                            for (var i = 0; i < edgeContainer.Value.Count; i++)
                            {
                                var aEdge = edgeContainer.Value[i];
                                if (alreadyVisited.Add(aEdge.SourceVertex))
                                {
                                    result.Add(new FrontierElement
                                    {
                                        EdgeDirection = Direction.IncomingEdge,
                                        EdgeLocation = new EdgeLocation
                                        {
                                            Edge = aEdge,
                                            EdgePropertyId =
                                                edgeContainer.Key
                                        },
                                        FrontierVertex = aEdge.SourceVertex
                                    });
                                }
                            }
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Gets the frontier elements on an outgoing edge
        /// </summary>
        /// <param name="vertex">The vertex behind the frontier</param>
        /// <param name="edgepropertyFilter">The edge property filter</param>
        /// <param name="edgeFilter">The edge filter</param>
        /// <param name="vertexFilter">The vertex filter</param>
        /// <param name="alreadyVisited">The vertices that have been visited already</param>
        /// <returns>A number of frontier elements</returns>
        private static IEnumerable<FrontierElement> GetValidOutgoingEdges(
            VertexModel vertex,
            Delegates.EdgePropertyFilter edgepropertyFilter,
            Delegates.EdgeFilter edgeFilter,
            Delegates.VertexFilter vertexFilter,
            HashSet<VertexModel> alreadyVisited)
        {
            var edgeProperties = vertex.OutEdges;
            var result = new List<FrontierElement>();

            if (edgeProperties != null)
            {
                foreach (var edgeContainer in edgeProperties)
                {
                    if (edgepropertyFilter != null && !edgepropertyFilter(edgeContainer.Key, Direction.OutgoingEdge))
                    {
                        continue;
                    }

                    if (edgeFilter != null)
                    {
                        for (var i = 0; i < edgeContainer.Value.Count; i++)
                        {
                            var aEdge = edgeContainer.Value[i];
                            if (edgeFilter(aEdge, Direction.OutgoingEdge))
                            {
                                if (alreadyVisited.Add(aEdge.TargetVertex))
                                {
                                    if (vertexFilter != null)
                                    {
                                        if (vertexFilter(aEdge.TargetVertex))
                                        {
                                            result.Add(new FrontierElement
                                            {
                                                EdgeDirection = Direction.OutgoingEdge,
                                                EdgeLocation = new EdgeLocation
                                                {
                                                    Edge = aEdge,
                                                    EdgePropertyId =
                                                        edgeContainer.Key
                                                },
                                                FrontierVertex = aEdge.TargetVertex
                                            });
                                        }
                                    }
                                    else
                                    {
                                        result.Add(new FrontierElement
                                        {
                                            EdgeDirection = Direction.OutgoingEdge,
                                            EdgeLocation = new EdgeLocation
                                            {
                                                Edge = aEdge,
                                                EdgePropertyId =
                                                    edgeContainer.Key
                                            },
                                            FrontierVertex = aEdge.TargetVertex
                                        });
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        if (vertexFilter != null)
                        {
                            for (var i = 0; i < edgeContainer.Value.Count; i++)
                            {
                                var aEdge = edgeContainer.Value[i];

                                if (alreadyVisited.Add(aEdge.TargetVertex))
                                {
                                    if (vertexFilter(aEdge.TargetVertex))
                                    {
                                        result.Add(new FrontierElement
                                        {
                                            EdgeDirection = Direction.OutgoingEdge,
                                            EdgeLocation = new EdgeLocation
                                            {
                                                Edge = aEdge,
                                                EdgePropertyId =
                                                    edgeContainer.Key
                                            },
                                            FrontierVertex = aEdge.TargetVertex
                                        });
                                    }
                                }
                            }
                        }
                        else
                        {
                            for (var i = 0; i < edgeContainer.Value.Count; i++)
                            {
                                var aEdge = edgeContainer.Value[i];
                                if (alreadyVisited.Add(aEdge.TargetVertex))
                                {
                                    result.Add(new FrontierElement
                                    {
                                        EdgeDirection = Direction.OutgoingEdge,
                                        EdgeLocation = new EdgeLocation
                                        {
                                            Edge = aEdge,
                                            EdgePropertyId =
                                                edgeContainer.Key
                                        },
                                        FrontierVertex = aEdge.TargetVertex
                                    });
                                }
                            }
                        }
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

            result.AddRange(GetValidIncomingEdges(vertex, edgepropertyFilter, edgeFilter, vertexFilter, alreadyVisitedVertices));
            result.AddRange(GetValidOutgoingEdges(vertex, edgepropertyFilter, edgeFilter, vertexFilter, alreadyVisitedVertices));

            return result;
        }

        #endregion

        #region IPlugin Members

        public string PluginName
        {
            get { return "BLS"; }
        }

        public Type PluginCategory
        {
            get { return typeof(IShortestPathAlgorithm); }
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
            get { return "Henning Rauch"; }
        }

        public void Initialize(Fallen8 fallen8, IDictionary<string, object> parameter)
        {
            _fallen8 = fallen8;
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
