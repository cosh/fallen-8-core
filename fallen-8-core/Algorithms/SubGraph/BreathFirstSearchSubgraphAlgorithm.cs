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
        public string Description => "Creates a subgraph using breadth-first search traversal";

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

            if (definition == null || definition.Pattern == null || definition.Pattern.Count == 0)
            {
                return false;
            }

            // Get all vertices that match the first pattern (start vertices)
            var firstPattern = definition.Pattern[0] as VertexPattern;
            if (firstPattern == null)
            {
                return false;
            }

            // Find all vertices matching the first pattern
            var startVertices = FindMatchingVertices(firstPattern);
            if (startVertices.Count == 0)
            {
                return false;
            }

            // Track visited vertices and edges for the subgraph
            var subgraphVertices = new HashSet<VertexModel>();
            var subgraphEdges = new HashSet<EdgeModel>();

            // Process each starting vertex
            foreach (var startVertex in startVertices)
            {
                // BFS from this start vertex
                var queue = new Queue<Tuple<VertexModel, int>>();
                var visited = new HashSet<int>();

                queue.Enqueue(new Tuple<VertexModel, int>(startVertex, 0));
                visited.Add(startVertex.Id);
                subgraphVertices.Add(startVertex);

                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    var currentVertex = current.Item1;
                    var patternIndex = current.Item2;

                    // Check if there are more patterns to match
                    if (patternIndex + 1 < definition.Pattern.Count)
                    {
                        var nextPattern = definition.Pattern[patternIndex + 1];

                        // Handle single edge pattern
                        if (nextPattern is EdgePattern edgePattern)
                        {
                            // Get matching edges with their traversal direction
                            var edgesWithDirection = GetMatchingEdgesWithDirection(currentVertex, edgePattern);

                            foreach (var edgeInfo in edgesWithDirection)
                            {
                                subgraphEdges.Add(edgeInfo.Item1);

                                // Get the next vertex based on traversal direction
                                var nextVertex = edgeInfo.Item2 == Direction.OutgoingEdge
                                    ? edgeInfo.Item1.TargetVertex
                                    : edgeInfo.Item1.SourceVertex;

                                // Check if there's a vertex pattern after the edge
                                if (patternIndex + 2 < definition.Pattern.Count)
                                {
                                    var vertexPattern = definition.Pattern[patternIndex + 2] as VertexPattern;
                                    if (vertexPattern != null && MatchesVertexPattern(nextVertex, vertexPattern))
                                    {
                                        if (!visited.Contains(nextVertex.Id))
                                        {
                                            visited.Add(nextVertex.Id);
                                            subgraphVertices.Add(nextVertex);
                                            queue.Enqueue(new Tuple<VertexModel, int>(nextVertex, patternIndex + 2));
                                        }
                                        else if (!subgraphVertices.Contains(nextVertex))
                                        {
                                            subgraphVertices.Add(nextVertex);
                                        }
                                    }
                                }
                                else
                                {
                                    // No vertex pattern, just add the target
                                    if (!subgraphVertices.Contains(nextVertex))
                                    {
                                        subgraphVertices.Add(nextVertex);
                                    }
                                }
                            }
                        }
                        // Handle variable length edge pattern
                        else if (nextPattern is VariableLengthEdgePattern variableEdgePattern)
                        {
                            // Check if there's a vertex pattern after the edge pattern
                            VertexPattern targetVertexPattern = null;
                            if (patternIndex + 2 < definition.Pattern.Count)
                            {
                                targetVertexPattern = definition.Pattern[patternIndex + 2] as VertexPattern;
                            }

                            // Find all paths of variable length
                            var paths = FindVariableLengthPaths(currentVertex, variableEdgePattern, targetVertexPattern, visited);

                            foreach (var path in paths)
                            {
                                // Add all edges and vertices from the path
                                foreach (var edge in path.Edges)
                                {
                                    subgraphEdges.Add(edge);
                                }

                                foreach (var vertex in path.Vertices)
                                {
                                    subgraphVertices.Add(vertex);
                                }

                                // Add the target vertex to the queue for further exploration
                                var targetVertex = path.Vertices[path.Vertices.Count - 1];
                                if (!visited.Contains(targetVertex.Id))
                                {
                                    visited.Add(targetVertex.Id);
                                    if (targetVertexPattern != null)
                                    {
                                        queue.Enqueue(new Tuple<VertexModel, int>(targetVertex, patternIndex + 2));
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // If we found any vertices and edges, create the subgraph
            if (subgraphVertices.Count > 0)
            {
                var subgraph = CreateSubgraphInstance(subgraphVertices, subgraphEdges);
                result = new SubGraphResult
                {
                    Definitions = definition,
                    SubGraph = subgraph,
                    SourceFallen8Id = _fallen8.Id,
                    SourceFallen8 = _fallen8,
                    AlgorithmPluginName = PluginName,
                    AlgorithmParameters = null
                };
                return true;
            }

            return false;
        }

        private List<VertexModel> FindMatchingVertices(VertexPattern pattern)
        {
            var allVertices = _fallen8.GetAllVertices();
            var matchingVertices = new List<VertexModel>();

            foreach (var vertex in allVertices)
            {
                if (MatchesVertexPattern(vertex, pattern))
                {
                    matchingVertices.Add(vertex);
                }
            }

            return matchingVertices;
        }

        private bool MatchesVertexPattern(VertexModel vertex, VertexPattern pattern)
        {
            // Check label filter
            if (pattern.Label != null && !pattern.Label(vertex.Label))
            {
                return false;
            }

            // Check vertex filter
            if (pattern.Vertex != null && !pattern.Vertex(vertex))
            {
                return false;
            }

            return true;
        }

        private List<Tuple<EdgeModel, Direction>> GetMatchingEdgesWithDirection(VertexModel vertex, EdgePattern pattern)
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

        private bool MatchesEdgePattern(EdgeModel edge, EdgePattern pattern, Direction direction)
        {
            // Check label filter
            if (pattern.Label != null && !pattern.Label(edge.Label))
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

        private List<VariableLengthPath> FindVariableLengthPaths(
            VertexModel startVertex,
            VariableLengthEdgePattern pattern,
            VertexPattern targetVertexPattern,
            HashSet<int> globalVisited)
        {
            var resultPaths = new List<VariableLengthPath>();
            var queue = new Queue<VariableLengthPath>();

            // Start with the initial vertex
            var initialPath = new VariableLengthPath();
            initialPath.Vertices.Add(startVertex);
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
                        if (targetVertexPattern == null || MatchesVertexPattern(currentVertex, targetVertexPattern))
                        {
                            resultPaths.Add(currentPath);
                        }
                    }
                    continue;
                }

                // Get matching edges from the current vertex with their directions
                var edgesWithDirection = GetMatchingEdgesForVariableLengthWithDirection(currentVertex, pattern);

                foreach (var edgeInfo in edgesWithDirection)
                {
                    var edge = edgeInfo.Item1;
                    var direction = edgeInfo.Item2;

                    // Get the next vertex based on traversal direction
                    var nextVertex = direction == Direction.OutgoingEdge
                        ? edge.TargetVertex
                        : edge.SourceVertex;

                    // Avoid cycles in the same path
                    if (currentPath.Vertices.Contains(nextVertex))
                    {
                        continue;
                    }

                    // Create a new path with this edge
                    var newPath = new VariableLengthPath();
                    newPath.Vertices.AddRange(currentPath.Vertices);
                    newPath.Vertices.Add(nextVertex);
                    newPath.Edges.AddRange(currentPath.Edges);
                    newPath.Edges.Add(edge);
                    newPath.EdgeDirections.AddRange(currentPath.EdgeDirections);
                    newPath.EdgeDirections.Add(direction);

                    var newLength = newPath.Edges.Count;

                    // Check if this path is complete
                    if (newLength >= pattern.MinLength && newLength <= pattern.MaxLength)
                    {
                        if (targetVertexPattern == null || MatchesVertexPattern(nextVertex, targetVertexPattern))
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

        private List<EdgeModel> GetMatchingEdgesForVariableLength(VertexModel vertex, VariableLengthEdgePattern pattern)
        {
            var matchingEdges = new List<EdgeModel>();

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
                        if (MatchesVariableLengthEdgePattern(edge, pattern, Direction.OutgoingEdge))
                        {
                            matchingEdges.Add(edge);
                        }
                    }
                }
            }

            // Check incoming edges - traverse in reverse
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
                        if (MatchesVariableLengthEdgePattern(edge, pattern, Direction.IncomingEdge))
                        {
                            matchingEdges.Add(edge);
                        }
                    }
                }
            }

            return matchingEdges;
        }

        private List<Tuple<EdgeModel, Direction>> GetMatchingEdgesForVariableLengthWithDirection(VertexModel vertex, VariableLengthEdgePattern pattern)
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
                        if (MatchesVariableLengthEdgePattern(edge, pattern, Direction.OutgoingEdge))
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
                        if (MatchesVariableLengthEdgePattern(edge, pattern, Direction.IncomingEdge))
                        {
                            matchingEdges.Add(new Tuple<EdgeModel, Direction>(edge, Direction.IncomingEdge));
                        }
                    }
                }
            }

            return matchingEdges;
        }

        private bool MatchesVariableLengthEdgePattern(EdgeModel edge, VariableLengthEdgePattern pattern, Direction direction)
        {
            // Check label filter
            if (pattern.Label != null && !pattern.Label(edge.Label))
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

        private class VariableLengthPath
        {
            public List<VertexModel> Vertices { get; set; } = new List<VertexModel>();
            public List<EdgeModel> Edges { get; set; } = new List<EdgeModel>();
            public List<Direction> EdgeDirections { get; set; } = new List<Direction>();
        }

        private Fallen8 CreateSubgraphInstance(HashSet<VertexModel> vertices, HashSet<EdgeModel> edges)
        {
            // Cast to Fallen8 to access internal GetLoggerFactory method
            var fallen8Concrete = _fallen8 as Fallen8;
            if (fallen8Concrete == null)
            {
                throw new InvalidOperationException("Cannot create subgraph: Fallen8 instance required");
            }

            var subgraph = new Fallen8(fallen8Concrete.LoggerFactory);

            // Create a mapping from old vertex IDs to new vertex IDs
            var vertexIdMapping = new Dictionary<int, int>();

            // Create vertices in the subgraph
            foreach (var vertex in vertices)
            {
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

                var txInfo = subgraph.EnqueueTransaction(new CreateVertexTransaction { Definition = vertexDef });
                txInfo.WaitUntilFinished();

                if (txInfo.Transaction is CreateVertexTransaction cvt && cvt.VertexCreated != null)
                {
                    vertexIdMapping[vertex.Id] = cvt.VertexCreated.Id;
                }
            }

            // Create edges in the subgraph
            foreach (var edge in edges)
            {
                // Only create edges where both vertices are in the subgraph
                if (vertexIdMapping.ContainsKey(edge.SourceVertex.Id) &&
                    vertexIdMapping.ContainsKey(edge.TargetVertex.Id))
                {
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
                        EdgePropertyId = "edge", // default edge property ID
                        TargetVertexId = vertexIdMapping[edge.TargetVertex.Id],
                        CreationDate = edge.CreationDate,
                        Label = edge.Label,
                        Properties = properties.Count > 0 ? properties : null
                    };

                    var txInfo = subgraph.EnqueueTransaction(new CreateEdgeTransaction { Definition = edgeDef });
                    txInfo.WaitUntilFinished();
                }
            }

            return subgraph;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            // Clean up resources if needed
        }
    }
}
