// MIT License
//
// BreadthFirstSearchSubgraphAlgorithm.cs
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
using NoSQL.GraphDB.Core.Plugin;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;
using System.Collections.Immutable;
using System.Collections;
using System.Runtime.CompilerServices;

#endregion

namespace NoSQL.GraphDB.Core.Algorithms.SubGraph
{
    public sealed class BreadthFirstSearchSubgraphAlgorithm : ISubGraphAlgorithm
    {
        /// <summary>
        /// The plugin name this algorithm registers under. Plugins are discovered and
        /// cached by <see cref="PluginName"/>, so callers of the factory must use this
        /// value (not the CLR type name) to select the algorithm.
        /// </summary>
        public const string AlgorithmPluginName = "Breadth First Search Subgraph Algorithm";

        /// <inheritdoc />
        public string PluginName => AlgorithmPluginName;

        /// <inheritdoc />
        public Type PluginCategory => typeof(ISubGraphAlgorithm);

        /// <inheritdoc />
        public string Description => "Creates a subgraph using breadth-first search traversal with multi-phase filtering";

        /// <inheritdoc />
        public string Manufacturer => "Henning Rauch";

        private IFallen8 _fallen8;
        private ILogger<BreadthFirstSearchSubgraphAlgorithm> _logger;

        /// <inheritdoc />
        public void Initialize(IFallen8 fallen8, IDictionary<string, object> configuration)
        {
            _fallen8 = fallen8;
            _logger = fallen8.LoggerFactory.CreateLogger<BreadthFirstSearchSubgraphAlgorithm>();
        }

        /// <inheritdoc />
        public bool TryCreateSubgraph(out SubGraphResult result, SubGraphDefinition definition)
        {
            result = null;

            if (definition == null)
            {
                return false;
            }

            // Validate the pattern STRUCTURE up front, before touching the source graph, so a
            // structurally-invalid pattern is rejected (a clean false -> InvalidInput -> 400)
            // CONSISTENTLY, whether the source graph is empty or populated. A syntactically-valid
            // pattern that simply matches nothing is NOT a failure: it yields an empty subgraph
            // (below), identically for an empty source graph and a populated one.
            if (definition.Pattern != null && definition.Pattern.Count > 0 && !ValidatePattern(definition.Pattern))
            {
                _logger?.LogWarning("Invalid pattern definition");
                result = null;
                return false;
            }

            var subgraph = new Fallen8(_fallen8.LoggerFactory);

            result = new SubGraphResult
            {
                Definitions = definition,
                SubGraph = subgraph,
                SourceFallen8Id = _fallen8.Id,
                SourceFallen8 = _fallen8,
                AlgorithmPluginName = PluginName,
                AlgorithmParameters = null
            };

            // Step 2: Copy vertices matching VertexFilter (or all vertices if null)
            // <oldVertex, newVertex>
            var vertexIdMapping = CopyVerticesWithFilter(subgraph, definition.VertexFilter);

            if (vertexIdMapping.Count == 0)
            {
                // No vertices to match against - because the source graph is empty OR because the
                // top-level vertex filter matched nothing. Either way, a valid pattern that matches
                // nothing is a valid EMPTY result: return the (empty) subgraph rather than false, so
                // the empty-graph and populated-no-match cases behave identically (both -> 201).
                _logger?.LogInformation("No vertices matched the vertex filter; producing an empty subgraph.");
                return true;
            }

            // Step 3: Copy edges matching EdgeFilter (or all valid edges if null)
            var edgesAdded = CopyEdgesWithFilter(subgraph, definition.EdgeFilter, vertexIdMapping);

            _logger?.LogInformation($"Initial subgraph created with {vertexIdMapping.Count} vertices and {edgesAdded} edges");

            // Step 4: If patterns are defined, evaluate them to filter the subgraph further
            if (definition.Pattern != null && definition.Pattern.Count > 0)
            {
                if (!EvaluatePatternsAndFilterSubgraph(subgraph, definition.Pattern))
                {
                    // Honor the interface contract: no result on failure.
                    result = null;
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Phase 1: Copy all vertices matching the vertex filter to the new subgraph
        /// </summary>
        private Dictionary<int, int> CopyVerticesWithFilter(Fallen8 subgraph, Delegates.VertexFilter vertexFilter)
        {
            var vertexIdMapping = new Dictionary<int, int>();

            // Use LINQ to filter vertices directly
            var validVertices = _fallen8.GetAllVertices()
                .Where(v => vertexFilter == null || vertexFilter(v))
                .ToList();

            if (validVertices.Count == 0)
            {
                return vertexIdMapping;
            }

            // Create a batch transaction for all vertices
            var createVerticesTransaction = new CreateVerticesTransaction();

            foreach (var vertex in validVertices)
            {
                // Deep copy the vertex properties
                var properties = new Dictionary<string, object>();
                var allProperties = vertex.GetAllProperties();
                if (allProperties != null)
                {
                    foreach (var prop in allProperties)
                    {
                        properties[prop.Key] = prop.Value;
                    }
                }

                var vertexDef = new VertexDefinition
                {
                    CreationDate = vertex.CreationDate,
                    Label = vertex.Label,
                    Properties = properties.Count > 0 ? properties : null
                };

                createVerticesTransaction.AddVertex(vertexDef);
            }

            // Execute the batch transaction
            var txInfo = subgraph.EnqueueTransaction(createVerticesTransaction);
            txInfo.WaitUntilFinished();

            // Map old vertex IDs to new vertex IDs
            var createdVertices = createVerticesTransaction.GetCreatedVertices();
            for (int i = 0; i < validVertices.Count && i < createdVertices.Count; i++)
            {
                vertexIdMapping[validVertices[i].Id] = createdVertices[i].Id;
            }

            return vertexIdMapping;
        }

        /// <summary>
        /// Phase 2: Copy all edges matching the edge filter to the new subgraph
        /// </summary>
        private int CopyEdgesWithFilter(
            Fallen8 subgraph,
            Delegates.EdgeFilter edgeFilter,
            Dictionary<int, int> vertexIdMapping)
        {

            // Get all edges from the source graph which connect vertices in the subgraph
            // Filter by edge properties
            var allEdges = _fallen8.GetAllEdges().Where(_ =>
                vertexIdMapping.ContainsKey(_.SourceVertex.Id)
                && vertexIdMapping.ContainsKey(_.TargetVertex.Id)
                && (edgeFilter == null || edgeFilter(_)));

            // Build a list of edge definitions that pass all filters
            var createEdgesTransaction = new CreateEdgesTransaction();

            foreach (var edge in allEdges)
            {
                // Deep copy the edge properties
                var properties = new Dictionary<string, object>();
                var allProperties = edge.GetAllProperties();
                if (allProperties != null)
                {
                    foreach (var prop in allProperties)
                    {
                        properties[prop.Key] = prop.Value;
                    }
                }

                var edgeDef = new EdgeDefinition
                {
                    SourceVertexId = vertexIdMapping[edge.SourceVertex.Id],
                    EdgePropertyId = edge.EdgePropertyId,
                    TargetVertexId = vertexIdMapping[edge.TargetVertex.Id],
                    CreationDate = edge.CreationDate,
                    Label = edge.Label,
                    Properties = properties.Count > 0 ? properties : null
                };

                createEdgesTransaction.AddEdge(edgeDef);
            }

            // Capture the edge count BEFORE enqueueing: once the transaction completes, the engine
            // releases its input definition list (memory-footprint M3), so reading
            // createEdgesTransaction.Edges after WaitUntilFinished() is no longer valid.
            int edgeCount = createEdgesTransaction.Edges.Count;

            // Execute the batch transaction if there are edges to create
            if (edgeCount > 0)
            {
                var txInfo = subgraph.EnqueueTransaction(createEdgesTransaction);
                txInfo.WaitUntilFinished();
            }

            return edgeCount;
        }

        /// <summary>
        /// Phase 3: Evaluate patterns and filter the subgraph to only include vertices and edges
        /// that are part of valid paths matching the patterns
        /// </summary>
        private bool EvaluatePatternsAndFilterSubgraph(
            Fallen8 subgraph,
            List<APattern> patterns)
        {
            if (patterns == null || patterns.Count == 0)
                return true;

            if (!ValidatePattern(patterns))
            {
                _logger?.LogWarning("Invalid pattern definition");
                return false;
            }

            // Find all valid paths that match the patterns
            var validPaths = FindAllValidPaths(subgraph, patterns);

            _logger?.LogInformation($"Found {validPaths.Count} valid paths");

            // Remove vertices and edges that are not part of valid paths
            RemoveInvalidElementsFromSubgraph(subgraph, validPaths);

            return true;
        }

        private Boolean ValidatePattern(List<APattern> patterns)
        {
            //it is totally valid if there are no patterns
            if (patterns == null || patterns.Count == 0)
                return true;

            // A variable-length edge cannot lead: level-0 seeding treats a leading edge as a
            // single hop and cannot honor Min/MaxLength, so it would silently ignore the range.
            if (patterns[0].Type == PatternType.VariableLengthEdge)
                return false;

            int currentIndex = 0;

            while (currentIndex < patterns.Count - 1)
            {
                var currentPattern = patterns[currentIndex];
                var nextPattern = patterns[currentIndex + 1];

                // Use the Type property instead of pattern matching for better performance
                switch (currentPattern.Type)
                {
                    case PatternType.Vertex:
                        // VertexPattern must be followed by EdgePattern or VariableLengthEdgePattern
                        if (nextPattern.Type == PatternType.Edge || nextPattern.Type == PatternType.VariableLengthEdge)
                        {
                            currentIndex++;
                        }
                        else
                        {
                            return false;
                        }
                        break;

                    case PatternType.Edge:
                    case PatternType.VariableLengthEdge:
                        //must be edge or variable length edge pattern
                        if (nextPattern.Type == PatternType.Vertex)
                        {
                            currentIndex++;
                        }
                        else
                        {
                            return false;
                        }
                        break;
                }
            }

            // A well-formed pattern path must terminate at a vertex. A trailing edge
            // pattern has no closing vertex and would describe a dangling half-edge.
            if (patterns[patterns.Count - 1].Type != PatternType.Vertex)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Find all valid paths in the subgraph that match the pattern definition
        /// </summary>
        private List<PathInfo> FindAllValidPaths(Fallen8 subgraph, List<APattern> patterns)
        {
            var validPaths = new List<PathInfo>();

            for (int i = 0; i < patterns.Count; i++)
            {
                var currentLevel = i;
                var currentPattern = patterns[i];

                ProcessPattern(currentLevel, currentPattern, validPaths, subgraph);

                // After all patterns are processed, remove any paths that were marked as invalid
                validPaths.RemoveAll(p => !p.IsValid);
            }

            return validPaths;
        }

        private void ProcessPattern(Int32 currentLevel, APattern currentPattern, List<PathInfo> validPaths, Fallen8 subgraph)
        {
            if (currentLevel == 0)
            {
                ProcessLevel0(currentPattern, validPaths, subgraph);
            }
            else
            {
                // we can assume that the level 0 produced paths with at least one element.
                // the last element of the path from level 0 is a vertex.

                PatternType currentPatternType = currentPattern.Type;

                var ep = currentPattern as EdgePattern;
                var vp = currentPattern as VertexPattern;
                var vep = currentPattern as VariableLengthEdgePattern;

                var workingSet = validPaths.Where(p => p.IsValid).ToList();

                List<PathInfo> vepResult;

                foreach (var path in workingSet)
                {
                    var lastElement = (VertexModel)path.LastElement;

                    switch (currentPatternType)
                    {
                        case PatternType.Vertex:
                            {
                                //Check the last vertex of the path
                                if (!MatchesVertexPattern(lastElement, vp))
                                {
                                    path.IsValid = false;
                                }
                                //nothing needs to be added
                            }
                            break;

                        case PatternType.Edge:
                            {
                                vepResult = ProcessEdgePattern(ep, path, lastElement);
                                validPaths.AddRange(vepResult);
                            }
                            break;

                        case PatternType.VariableLengthEdge:
                            {
                                // Expand the mandatory minimum number of hops. ProcessEdgePattern
                                // invalidates the source path it extends, so 'frontier' holds only
                                // the freshly extended paths at the current length.
                                var frontier = new List<PathInfo>() { path };
                                for (int i = 0; i < vep.MinLength; i++)
                                {
                                    var next = new List<PathInfo>();

                                    foreach (var aStart in frontier)
                                    {
                                        if (aStart.IsValid)
                                        {
                                            next.AddRange(ProcessEdgePattern(ep, aStart, (VertexModel)aStart.LastElement));
                                        }
                                    }

                                    frontier = next;
                                }

                                // Every path from MinLength..MaxLength is a candidate. Keep an
                                // INDEPENDENT copy of each length, because the next expansion
                                // invalidates the frontier objects; adding copies keeps the shorter
                                // matches alive while still expanding toward MaxLength.
                                AddCandidateCopies(frontier, validPaths);

                                for (int i = vep.MinLength; i < vep.MaxLength; i++)
                                {
                                    var next = new List<PathInfo>();

                                    foreach (var aStart in frontier)
                                    {
                                        if (aStart.IsValid)
                                        {
                                            next.AddRange(ProcessEdgePattern(ep, aStart, (VertexModel)aStart.LastElement));
                                        }
                                    }

                                    frontier = next;
                                    AddCandidateCopies(frontier, validPaths);
                                }
                            }
                            break;

                        default:
                            throw new NotSupportedException(
                                String.Format("Unsupported pattern type '{0}' at pattern level {1}.", currentPatternType, currentLevel));
                    }
                }
            }
        }

        /// <summary>
        /// Adds an independent copy of each valid path in the frontier to the accumulated
        /// valid-path set. Copies are required because the frontier objects are invalidated
        /// when they are expanded to the next length; without copying, every path shorter
        /// than MaxLength would be removed by the per-level invalidity sweep.
        /// </summary>
        private static void AddCandidateCopies(List<PathInfo> frontier, List<PathInfo> validPaths)
        {
            foreach (var candidate in frontier)
            {
                if (candidate.IsValid)
                {
                    validPaths.Add(new PathInfo(candidate));
                }
            }
        }

        private List<PathInfo> ProcessEdgePattern(EdgePattern ep, PathInfo currentPath, VertexModel currentPathLastElement)
        {
            //setting the old path as invalid. we create new paths by traversing
            currentPath.IsValid = false;

            var result = new List<PathInfo>();


            //now we need to traverse from the last vertex of the path based on the pattern

            switch (ep.Direction)
            {
                case Direction.OutgoingEdge:
                    {
                        //Outgoing
                        ProcessOutgoingEdges(ep, currentPath, currentPathLastElement, result);
                    }
                    break;

                case Direction.IncomingEdge:
                    {
                        //Incoming
                        ProcessIncomingEdges(ep, currentPath, currentPathLastElement, result);
                    }
                    break;

                case Direction.UndirectedEdge:
                    {
                        //Undirected
                        ProcessUndirectedEdge(ep, currentPath, currentPathLastElement, result);
                    }

                    break;

                default:
                    throw new NotSupportedException(
                        String.Format("Unsupported edge direction '{0}'.", ep.Direction));
            }

            return result;
        }

        private void ProcessUndirectedEdge(EdgePattern ep, PathInfo currentPath, VertexModel currentPathLastElement, List<PathInfo> result)
        {
            IReadOnlyList<EdgeModel> edges;
            foreach (var aOutEdgePropertyId in currentPathLastElement.GetOutgoingEdgeIds())
            {
                if (ep.EdgeProperty != null && !ep.EdgeProperty(aOutEdgePropertyId))
                {
                    continue;
                }

                if (currentPathLastElement.TryGetOutEdge(out edges, aOutEdgePropertyId))
                {
                    foreach (var aEdge in edges)
                    {
                        if (MatchesEdgePattern(aEdge, ep, Direction.UndirectedEdge))
                        {
                            //we found a new valid path and add it
                            var newPath = new PathInfo(currentPath);
                            newPath.AddGraphElement(aEdge);
                            newPath.AddGraphElement(aEdge.TargetVertex);
                            result.Add(newPath);
                        }
                    }
                }
            }

            foreach (var aInEdgePropertyId in currentPathLastElement.GetIncomingEdgeIds())
            {
                if (ep.EdgeProperty != null && !ep.EdgeProperty(aInEdgePropertyId))
                {
                    continue;
                }

                if (currentPathLastElement.TryGetInEdge(out edges, aInEdgePropertyId))
                {
                    foreach (var aEdge in edges)
                    {
                        if (MatchesEdgePattern(aEdge, ep, Direction.UndirectedEdge))
                        {
                            //we found a new valid path and add it
                            var newPath = new PathInfo(currentPath);
                            newPath.AddGraphElement(aEdge);
                            newPath.AddGraphElement(aEdge.SourceVertex);
                            result.Add(newPath);
                        }
                    }
                }
            }
        }

        private void ProcessOutgoingEdges(EdgePattern ep, PathInfo currentPath, VertexModel currentPathLastElement, List<PathInfo> result)
        {
            IReadOnlyList<EdgeModel> edges;
            foreach (var aOutEdgePropertyId in currentPathLastElement.GetOutgoingEdgeIds())
            {
                if (ep.EdgeProperty != null && !ep.EdgeProperty(aOutEdgePropertyId))
                {
                    continue;
                }

                if (currentPathLastElement.TryGetOutEdge(out edges, aOutEdgePropertyId))
                {
                    foreach (var aEdge in edges)
                    {
                        if (MatchesEdgePattern(aEdge, ep, Direction.OutgoingEdge))
                        {
                            //we found a new valid path and add it
                            var newPath = new PathInfo(currentPath);
                            newPath.AddGraphElement(aEdge);
                            newPath.AddGraphElement(aEdge.TargetVertex);
                            result.Add(newPath);
                        }
                    }
                }
            }
        }

        private void ProcessIncomingEdges(EdgePattern ep, PathInfo currentPath, VertexModel currentPathLastElement, List<PathInfo> result)
        {
            IReadOnlyList<EdgeModel> edges;
            foreach (var aInEdgePropertyId in currentPathLastElement.GetIncomingEdgeIds())
            {
                if (ep.EdgeProperty != null && !ep.EdgeProperty(aInEdgePropertyId))
                {
                    continue;
                }

                if (currentPathLastElement.TryGetInEdge(out edges, aInEdgePropertyId))
                {
                    foreach (var aEdge in edges)
                    {
                        if (MatchesEdgePattern(aEdge, ep, Direction.IncomingEdge))
                        {
                            //we found a new valid path and add it
                            var newPath = new PathInfo(currentPath);
                            newPath.AddGraphElement(aEdge);
                            newPath.AddGraphElement(aEdge.SourceVertex);
                            result.Add(newPath);
                        }
                    }
                }
            }
        }

        private void ProcessLevel0(APattern currentPattern, List<PathInfo> validPaths, Fallen8 subgraph)
        {
            //create initial paths from all vertices or from all edges
            switch (currentPattern)
            {
                case VertexPattern vp:
                    {
                        foreach (var startVertex in subgraph.GetAllVertices().Where(v => MatchesVertexPattern(v, vp)))
                        {
                            var path = new PathInfo();
                            path.AddGraphElement(startVertex);
                            validPaths.Add(path);
                        }
                    }
                    break;
                case EdgePattern ep:
                    {
                        foreach (var e in subgraph.GetAllEdges())
                        {
                            if (MatchesEdgePattern(e, ep, Direction.OutgoingEdge))
                            {
                                var path = new PathInfo();
                                path.AddGraphElement(e.SourceVertex);
                                path.AddGraphElement(e);
                                path.AddGraphElement(e.TargetVertex);
                                validPaths.Add(path);
                            }
                            else if (MatchesEdgePattern(e, ep, Direction.IncomingEdge))
                            {
                                var path = new PathInfo();
                                path.AddGraphElement(e.TargetVertex);
                                path.AddGraphElement(e);
                                path.AddGraphElement(e.SourceVertex);
                                validPaths.Add(path);
                            }
                            else if (MatchesEdgePattern(e, ep, Direction.UndirectedEdge))
                            {
                                var pathOut = new PathInfo();
                                pathOut.AddGraphElement(e.SourceVertex);
                                pathOut.AddGraphElement(e);
                                pathOut.AddGraphElement(e.TargetVertex);
                                var pathIn = new PathInfo();
                                pathIn.AddGraphElement(e.TargetVertex);
                                pathIn.AddGraphElement(e);
                                pathIn.AddGraphElement(e.SourceVertex);

                                validPaths.Add(pathOut);
                                validPaths.Add(pathIn);
                            }
                        }
                    }
                    break;
                default:
                    throw new NotSupportedException("First pattern must be a VertexPattern or EdgePattern");
            }
        }


        /// <summary>
        /// Remove vertices and edges from the subgraph that are not in the valid sets
        /// </summary>
        private void RemoveInvalidElementsFromSubgraph(
            Fallen8 subgraph,
            List<PathInfo> validPaths)
        {
            var validGraphElementIDs = validPaths
                .SelectMany(p => p.GetGraphElementIds())
                .ToHashSet();

            var toBeRemovedGraphElementIds = subgraph.GetAllGraphElements()
                .Select(ge => ge.Id)
                .Where(id => !validGraphElementIDs.Contains(id))
                .ToList();

            if (toBeRemovedGraphElementIds.Count == 0)
                return;

            var txInfo = subgraph.EnqueueTransaction(new RemoveGraphElementsTransaction { GraphElementIds = toBeRemovedGraphElementIds });
            txInfo.WaitUntilFinished();

            txInfo = subgraph.EnqueueTransaction(new TrimTransaction());
            txInfo.WaitUntilFinished();
        }

        private bool MatchesVertexPattern(VertexModel vertex, VertexPattern pattern)
        {
            // If pattern is null, match everything
            if (pattern == null)
            {
                return true;
            }

            // Check Vertex-specific filter
            if (pattern.Vertex != null && !pattern.Vertex(vertex))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Check if an edge matches an edge pattern
        /// </summary>
        private bool MatchesEdgePattern(EdgeModel edge, EdgePattern pattern, Direction direction)
        {
            // If pattern is null, match everything
            if (pattern == null)
            {
                return true;
            }

            if (!pattern.Direction.Equals(direction))
            {
                return false;
            }

            // Check edge filter
            if (pattern.Edge != null && !pattern.Edge(edge))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Helper class to track edge information
        /// </summary>
        private class EdgeInfo
        {
            public int OriginalEdgeId
            {
                get; set;
            }
            public string EdgePropertyId
            {
                get; set;
            }
        }

        /// <summary>
        /// Helper class to track path information
        /// </summary>
        private class PathInfo
        {
            public PathInfo()
            {

            }

            public PathInfo(PathInfo anotherPath)
            {
                // Deep copy: each path must own its element set. Sharing the set by
                // reference lets elements visited on one branch leak into the keep-set
                // of a sibling branch and corrupts the per-path cycle check.
                _graphElements = new HashSet<int>(anotherPath._graphElements);
                LastElement = anotherPath.LastElement;
            }

            public Guid Id { get; } = Guid.NewGuid();
            public Boolean IsValid { get; set; } = true;
            private HashSet<int> _graphElements { get; set; } = new HashSet<int>();

            public AGraphElementModel LastElement
            {
                get; private set;
            }

            public HashSet<int> GetGraphElementIds()
            {
                return _graphElements;
            }

            public void AddGraphElement(AGraphElementModel ge)
            {
                if (!IsValid)
                {
                    return;
                }

                // Revisiting an element closes a cycle: invalidate the path and do NOT mutate
                // its element set or LastElement, so a freshly-invalidated path never exposes
                // cycle-polluted state to any later reader.
                if (_graphElements.Contains(ge.Id))
                {
                    IsValid = false;
                    return;
                }

                _graphElements.Add(ge.Id);
                LastElement = ge;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            // Clean up resources if needed
        }
    }
}
