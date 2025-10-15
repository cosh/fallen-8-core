// MIT License
//
// BreathFirstSearchSubgraphAlgorithm.cs
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

#endregion

namespace NoSQL.GraphDB.Core.Algorithms.SubGraph
{
    public sealed class BreathFirstSearchSubgraphAlgorithm : ISubGraphAlgorithm
    {
        /// <inheritdoc />
        public string PluginName => "Breadth First Search Subgraph Algorithm";

        /// <inheritdoc />
        public Type PluginCategory => typeof(ISubGraphAlgorithm);

        /// <inheritdoc />
        public string Description => "Creates a subgraph using breadth-first search traversal with multi-phase filtering";

        /// <inheritdoc />
        public string Manufacturer => "Henning Rauch";

        private IFallen8 _fallen8;
        private ILogger<BreathFirstSearchSubgraphAlgorithm> _logger;

        /// <inheritdoc />
        public void Initialize(IFallen8 fallen8, IDictionary<string, object> configuration)
        {
            _fallen8 = fallen8;
            _logger = fallen8.LoggerFactory.CreateLogger<BreathFirstSearchSubgraphAlgorithm>();
        }

        /// <inheritdoc />
        public bool TryCreateSubgraph(out SubGraphResult result, SubGraphDefinition definition)
        {
            result = null;

            if (definition == null)
            {
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
                _logger?.LogInformation("No vertices matched the vertex filter");

                // If patterns are defined, return false (no match)
                // Otherwise return an empty subgraph
                if (definition.Pattern != null && definition.Pattern.Count > 0)
                {
                    result = null;
                    return false;
                }
                return true;
            }

            // Step 3: Copy edges matching EdgeFilter (or all valid edges if null)
            var edgesAdded = CopyEdgesWithFilter(subgraph, definition.EdgeFilter, vertexIdMapping);

            _logger?.LogInformation($"Initial subgraph created with {vertexIdMapping.Count} vertices and {edgesAdded} edges");

            // Step 4: If patterns are defined, evaluate them to filter the subgraph further
            if (definition.Pattern != null && definition.Pattern.Count > 0)
            {
                return EvaluatePatternsAndFilterSubgraph(subgraph, definition.Pattern);
            }

            return true;
        }

        /// <summary>
        /// Phase 1: Copy all vertices matching the vertex filter to the new subgraph
        /// </summary>
        private Dictionary<int, int> CopyVerticesWithFilter(Fallen8 subgraph, GraphElementPattern vertexFilter)
        {
            var vertexIdMapping = new Dictionary<int, int>();

            // Use LINQ to filter vertices directly
            var validVertices = _fallen8.GetAllVertices()
                .Where(v => vertexFilter == null || MatchesAGraphElementPattern(v, vertexFilter))
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
            GraphElementPattern edgeFilter,
            Dictionary<int, int> vertexIdMapping)
        {

            // Get all edges from the source graph which connect vertices in the subgraph
            // Filter by edge properties
            var allEdges = _fallen8.GetAllEdges().Where(_ =>
                vertexIdMapping.ContainsKey(_.SourceVertex.Id)
                && vertexIdMapping.ContainsKey(_.TargetVertex.Id)
                && (edgeFilter == null || (edgeFilter.GraphElement != null && edgeFilter.GraphElement(_))));

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

            // Execute the batch transaction if there are edges to create
            if (createEdgesTransaction.Edges.Count > 0)
            {
                var txInfo = subgraph.EnqueueTransaction(createEdgesTransaction);
                txInfo.WaitUntilFinished();
            }

            return createEdgesTransaction.Edges.Count;
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

            // Get the first pattern - must be a vertex pattern
            if (!(patterns[0] is VertexPattern firstPattern))
            {
                _logger?.LogError("First pattern must be a VertexPattern");
                return false;
            }

            // Find all valid paths that match the patterns
            var validPaths = FindAllValidPaths(subgraph, patterns);

            /**

            if (validPaths.Count == 0)
            {
                return false;
            }

            **/

            _logger?.LogInformation($"Found {validPaths.Count} valid paths");

            // Remove vertices and edges that are not part of valid paths
            RemoveInvalidElementsFromSubgraph(subgraph, validPaths);

            return true;
        }

        /// <summary>
        /// Find all valid paths in the subgraph that match the pattern definition
        /// </summary>
        private List<PathInfo> FindAllValidPaths(Fallen8 subgraph, List<APattern> patterns)
        {
            var validPaths = new List<PathInfo>();

            // Get start vertices matching the first pattern
            var firstPattern = patterns[0] as VertexPattern;
            var allVertices = subgraph.GetAllVertices();
            var startVertices = allVertices.Where(v => MatchesVertexPattern(v, firstPattern)).ToList();

            // Use BFS from each start vertex to find matching paths
            foreach (var startVertex in startVertices)
            {
                var paths = FindPathsFromVertex(subgraph, startVertex, patterns, 0);
                validPaths.AddRange(paths);
            }

            return validPaths;
        }

        /// <summary>
        /// Find all paths from a given vertex that match the remaining patterns
        /// </summary>
        private List<PathInfo> FindPathsFromVertex(
            Fallen8 subgraph,
            VertexModel currentVertex,
            List<APattern> patterns,
            int patternIndex)
        {
            var results = new List<PathInfo>();

            // Base case: if we've matched all patterns, return a path with just this vertex
            if (patternIndex >= patterns.Count)
            {
                results.Add(new PathInfo
                {
                    Vertices = new List<VertexModel> { currentVertex },
                    Edges = new List<EdgeModel>()
                });
                return results;
            }

            // Current pattern should be a vertex pattern (already matched)
            if (!(patterns[patternIndex] is VertexPattern))
            {
                return results;
            }

            // Check if there's an edge pattern next
            if (patternIndex + 1 >= patterns.Count)
            {
                // No more patterns, just return this vertex
                results.Add(new PathInfo
                {
                    Vertices = new List<VertexModel> { currentVertex },
                    Edges = new List<EdgeModel>()
                });
                return results;
            }

            var nextPattern = patterns[patternIndex + 1];

            if (nextPattern is EdgePattern edgePattern)
            {
                // Find matching edges from current vertex
                var matchingEdges = GetMatchingEdgesFromVertex(currentVertex, edgePattern);

                foreach (var edgeInfo in matchingEdges)
                {
                    var edge = edgeInfo.Item1;
                    var direction = edgeInfo.Item2;
                    var nextVertex = direction == Direction.OutgoingEdge ? edge.TargetVertex : edge.SourceVertex;

                    // Check if there's a vertex pattern after the edge
                    if (patternIndex + 2 < patterns.Count && patterns[patternIndex + 2] is VertexPattern nextVertexPattern)
                    {
                        if (MatchesAGraphElementPattern(nextVertex, nextVertexPattern))
                        {
                            // Recursively find paths from the next vertex
                            var subPaths = FindPathsFromVertex(subgraph, nextVertex, patterns, patternIndex + 2);
                            foreach (var subPath in subPaths)
                            {
                                var newPath = new PathInfo
                                {
                                    Vertices = new List<VertexModel> { currentVertex },
                                    Edges = new List<EdgeModel> { edge }
                                };
                                newPath.Vertices.AddRange(subPath.Vertices);
                                newPath.Edges.AddRange(subPath.Edges);
                                results.Add(newPath);
                            }
                        }
                    }
                    else
                    {
                        // No vertex pattern after edge, just add this edge and vertex
                        results.Add(new PathInfo
                        {
                            Vertices = new List<VertexModel> { currentVertex, nextVertex },
                            Edges = new List<EdgeModel> { edge }
                        });
                    }
                }
            }
            else if (nextPattern is VariableLengthEdgePattern variableEdgePattern)
            {
                // Handle variable length paths
                VertexPattern targetVertexPattern = null;
                if (patternIndex + 2 < patterns.Count && patterns[patternIndex + 2] is VertexPattern vp)
                {
                    targetVertexPattern = vp;
                }

                var variablePaths = FindVariableLengthPathsFromVertex(
                    subgraph, currentVertex, variableEdgePattern, targetVertexPattern);

                foreach (var varPath in variablePaths)
                {
                    if (targetVertexPattern != null && patternIndex + 2 < patterns.Count)
                    {
                        var lastVertex = varPath.Vertices[varPath.Vertices.Count - 1];
                        var subPaths = FindPathsFromVertex(subgraph, lastVertex, patterns, patternIndex + 2);

                        foreach (var subPath in subPaths)
                        {
                            var newPath = new PathInfo
                            {
                                Vertices = new List<VertexModel>(varPath.Vertices),
                                Edges = new List<EdgeModel>(varPath.Edges)
                            };
                            // Don't duplicate the last vertex
                            if (subPath.Vertices.Count > 1)
                            {
                                newPath.Vertices.AddRange(subPath.Vertices.Skip(1));
                            }
                            newPath.Edges.AddRange(subPath.Edges);
                            results.Add(newPath);
                        }
                    }
                    else
                    {
                        results.Add(varPath);
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Find variable-length paths from a vertex
        /// </summary>
        private List<PathInfo> FindVariableLengthPathsFromVertex(
            Fallen8 subgraph,
            VertexModel startVertex,
            VariableLengthEdgePattern pattern,
            VertexPattern targetVertexPattern)
        {
            var resultPaths = new List<PathInfo>();
            var queue = new Queue<PathInfo>();

            // Start with the initial vertex
            var initialPath = new PathInfo
            {
                Vertices = new List<VertexModel> { startVertex },
                Edges = new List<EdgeModel>()
            };
            queue.Enqueue(initialPath);

            while (queue.Count > 0)
            {
                var currentPath = queue.Dequeue();
                var currentVertex = currentPath.Vertices[currentPath.Vertices.Count - 1];
                var currentLength = currentPath.Edges.Count;

                // Check if we've reached the maximum length
                if (currentLength >= pattern.MaxLength)
                {
                    // Check if this path meets the criteria
                    if (currentLength >= pattern.MinLength)
                    {
                        if (targetVertexPattern == null || MatchesAGraphElementPattern(currentVertex, targetVertexPattern))
                        {
                            resultPaths.Add(currentPath);
                        }
                    }
                    continue;
                }

                // Get matching edges from the current vertex
                var edgesWithDirection = GetMatchingEdgesForVariableLength(currentVertex, pattern);

                foreach (var edgeInfo in edgesWithDirection)
                {
                    var edge = edgeInfo.Item1;
                    var direction = edgeInfo.Item2;
                    var nextVertex = direction == Direction.OutgoingEdge ? edge.TargetVertex : edge.SourceVertex;

                    // Avoid cycles - don't revisit vertices in this path
                    if (currentPath.Vertices.Contains(nextVertex))
                    {
                        continue;
                    }

                    // Create new path with this edge
                    var newPath = new PathInfo
                    {
                        Vertices = new List<VertexModel>(currentPath.Vertices) { nextVertex },
                        Edges = new List<EdgeModel>(currentPath.Edges) { edge }
                    };

                    var newLength = newPath.Edges.Count;

                    // Check if this path is valid
                    if (newLength >= pattern.MinLength && newLength <= pattern.MaxLength)
                    {
                        if (targetVertexPattern == null || MatchesAGraphElementPattern(nextVertex, targetVertexPattern))
                        {
                            resultPaths.Add(newPath);
                        }
                    }

                    // Continue exploring if we haven't reached max length
                    if (newLength < pattern.MaxLength)
                    {
                        queue.Enqueue(newPath);
                    }
                }
            }

            return resultPaths;
        }

        /// <summary>
        /// Remove vertices and edges from the subgraph that are not in the valid sets
        /// </summary>
        private void RemoveInvalidElementsFromSubgraph(
            Fallen8 subgraph,
            List<PathInfo> validPaths)
        {
            // Get all vertices and edges in the subgraph
            var allVertices = subgraph.GetAllVertices();
            var allEdges = subgraph.GetAllEdges();

            var validGraphElementIDs = validPaths
                .SelectMany(p => p.Vertices)
                .Select(v => v.Id)
                .Union(validPaths
                    .SelectMany(p => p.Edges)
                    .Select(e => e.Id))
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

        /// <summary>
        /// Get matching edges from a vertex for a single edge pattern
        /// </summary>
        private List<Tuple<EdgeModel, Direction>> GetMatchingEdgesFromVertex(VertexModel vertex, EdgePattern pattern)
        {
            var matchingEdges = new List<Tuple<EdgeModel, Direction>>();

            // Handle direction
            bool checkOutgoing = pattern.Direction == Direction.OutgoingEdge || pattern.Direction == Direction.UndirectedEdge;
            bool checkIncoming = pattern.Direction == Direction.IncomingEdge || pattern.Direction == Direction.UndirectedEdge;

            // Check outgoing edges
            if (checkOutgoing && vertex.OutEdges != null)
            {
                foreach (var edgeProperty in vertex.OutEdges)
                {
                    // Check edge property filter
                    if (pattern.EdgeProperty != null && !pattern.EdgeProperty(edgeProperty.Key, Direction.OutgoingEdge))
                    {
                        continue;
                    }

                    foreach (var edge in edgeProperty.Value)
                    {
                        if (MatchesEdgePattern(edge, pattern, Direction.OutgoingEdge))
                        {
                            matchingEdges.Add(new Tuple<EdgeModel, Direction>(edge, Direction.OutgoingEdge));
                        }
                    }
                }
            }

            // Check incoming edges
            if (checkIncoming && vertex.InEdges != null)
            {
                foreach (var edgeProperty in vertex.InEdges)
                {
                    // Check edge property filter
                    if (pattern.EdgeProperty != null && !pattern.EdgeProperty(edgeProperty.Key, Direction.IncomingEdge))
                    {
                        continue;
                    }

                    foreach (var edge in edgeProperty.Value)
                    {
                        if (MatchesEdgePattern(edge, pattern, Direction.IncomingEdge))
                        {
                            matchingEdges.Add(new Tuple<EdgeModel, Direction>(edge, Direction.IncomingEdge));
                        }
                    }
                }
            }

            return matchingEdges;
        }

        /// <summary>
        /// Get matching edges for variable-length pattern
        /// </summary>
        private List<Tuple<EdgeModel, Direction>> GetMatchingEdgesForVariableLength(
            VertexModel vertex,
            VariableLengthEdgePattern pattern)
        {
            var matchingEdges = new List<Tuple<EdgeModel, Direction>>();

            bool checkOutgoing = pattern.Direction == Direction.OutgoingEdge || pattern.Direction == Direction.UndirectedEdge;
            bool checkIncoming = pattern.Direction == Direction.IncomingEdge || pattern.Direction == Direction.UndirectedEdge;

            if (checkOutgoing && vertex.OutEdges != null)
            {
                foreach (var edgeProperty in vertex.OutEdges)
                {
                    if (pattern.EdgeProperty != null && !pattern.EdgeProperty(edgeProperty.Key, Direction.OutgoingEdge))
                    {
                        continue;
                    }

                    foreach (var edge in edgeProperty.Value)
                    {
                        if (MatchesVariableLengthEdgePattern(edge, pattern, Direction.OutgoingEdge))
                        {
                            matchingEdges.Add(new Tuple<EdgeModel, Direction>(edge, Direction.OutgoingEdge));
                        }
                    }
                }
            }

            if (checkIncoming && vertex.InEdges != null)
            {
                foreach (var edgeProperty in vertex.InEdges)
                {
                    if (pattern.EdgeProperty != null && !pattern.EdgeProperty(edgeProperty.Key, Direction.IncomingEdge))
                    {
                        continue;
                    }

                    foreach (var edge in edgeProperty.Value)
                    {
                        if (MatchesVariableLengthEdgePattern(edge, pattern, Direction.IncomingEdge))
                        {
                            matchingEdges.Add(new Tuple<EdgeModel, Direction>(edge, Direction.IncomingEdge));
                        }
                    }
                }
            }

            return matchingEdges;
        }

        /// <summary>
        /// Check if a vertex matches a vertex pattern
        /// </summary>
        private bool MatchesAGraphElementPattern(AGraphElementModel vertex, GraphElementPattern pattern)
        {
            // If pattern is null, match everything
            if (pattern == null)
            {
                return true;
            }

            // Check GraphElement filter (general graph element filter)
            if (pattern.GraphElement != null)
            {
                return pattern.GraphElement(vertex);
            }

            // Both filters must pass (if they exist)
            return true;
        }

        private bool MatchesVertexPattern(VertexModel vertex, VertexPattern pattern)
        {
            // If pattern is null, match everything
            if (pattern == null)
            {
                return true;
            }

            // Check GraphElement filter (general graph element filter)
            if (pattern.GraphElement != null && !pattern.GraphElement(vertex))
            {
                return false;
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
            // Check label filter
            if (pattern.GraphElement != null && !pattern.GraphElement(edge))
            {
                return false;
            }

            // Check edge filter
            if (pattern.Edge != null && !pattern.Edge(edge, direction))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Check if an edge matches a variable-length edge pattern
        /// </summary>
        private bool MatchesVariableLengthEdgePattern(EdgeModel edge, VariableLengthEdgePattern pattern, Direction direction)
        {
            // Check label filter
            if (pattern.GetHashCode != null && !pattern.GraphElement(edge))
            {
                return false;
            }

            // Check edge filter
            if (pattern.Edge != null && !pattern.Edge(edge, direction))
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
            public List<VertexModel> Vertices { get; set; } = new List<VertexModel>();
            public List<EdgeModel> Edges { get; set; } = new List<EdgeModel>();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            // Clean up resources if needed
        }
    }
}
