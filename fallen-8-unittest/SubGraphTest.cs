// MIT License
//
// SubGraphTest.cs
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
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms;
using NoSQL.GraphDB.Core.Algorithms.SubGraph;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    [TestClass]
    public class SubGraphTest
    {
        // Helper method to create logger factory consistently
        private ILoggerFactory CreateLoggerFactory()
        {
            return LoggerFactory.Create(builder =>
            {
                builder
                    .AddFilter("Microsoft", LogLevel.Warning)
                    .AddFilter("System", LogLevel.Warning)
                    .AddFilter("NoSQL.GraphDB", LogLevel.Debug)
                    .AddConsole();
            });
        }

        private Fallen8 CreateSimpleGraph()
        {
            var loggerFactory = CreateLoggerFactory();
            var fallen8 = new Fallen8(loggerFactory);
            var creationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds());

            // Create vertices: A -> B -> C -> D
            var verticesTx = new CreateVerticesTransaction();
            verticesTx.AddVertex(creationDate, "node", new Dictionary<string, object>() { { "name", "A" } });
            verticesTx.AddVertex(creationDate, "node", new Dictionary<string, object>() { { "name", "B" } });
            verticesTx.AddVertex(creationDate, "node", new Dictionary<string, object>() { { "name", "C" } });
            verticesTx.AddVertex(creationDate, "node", new Dictionary<string, object>() { { "name", "D" } });

            var verticesInfo = fallen8.EnqueueTransaction(verticesTx);
            verticesInfo.WaitUntilFinished();

            var vertices = verticesTx.GetCreatedVertices();

            // Create edges
            var edgesTx = new CreateEdgesTransaction();
            edgesTx.AddEdge(vertices[0].Id, "connects", vertices[1].Id, creationDate, "link");
            edgesTx.AddEdge(vertices[1].Id, "connects", vertices[2].Id, creationDate, "link");
            edgesTx.AddEdge(vertices[2].Id, "connects", vertices[3].Id, creationDate, "link");

            var edgesInfo = fallen8.EnqueueTransaction(edgesTx);
            edgesInfo.WaitUntilFinished();

            return fallen8;
        }

        private Fallen8 CreateComplexGraph()
        {
            var loggerFactory = CreateLoggerFactory();
            var fallen8 = new Fallen8(loggerFactory);
            var creationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds());

            // Create a more complex graph with multiple paths and labels
            var verticesTx = new CreateVerticesTransaction();
            verticesTx.AddVertex(creationDate, "person", new Dictionary<string, object>() { { "name", "Alice" }, { "age", 30 } });
            verticesTx.AddVertex(creationDate, "person", new Dictionary<string, object>() { { "name", "Bob" }, { "age", 25 } });
            verticesTx.AddVertex(creationDate, "person", new Dictionary<string, object>() { { "name", "Charlie" }, { "age", 35 } });
            verticesTx.AddVertex(creationDate, "company", new Dictionary<string, object>() { { "name", "TechCorp" } });
            verticesTx.AddVertex(creationDate, "company", new Dictionary<string, object>() { { "name", "DataInc" } });

            var verticesInfo = fallen8.EnqueueTransaction(verticesTx);
            verticesInfo.WaitUntilFinished();

            var vertices = verticesTx.GetCreatedVertices();

            // Create edges with different labels
            var edgesTx = new CreateEdgesTransaction();
            edgesTx.AddEdge(vertices[0].Id, "knows", vertices[1].Id, creationDate, "knows");
            edgesTx.AddEdge(vertices[1].Id, "knows", vertices[2].Id, creationDate, "knows");
            edgesTx.AddEdge(vertices[0].Id, "works_at", vertices[3].Id, creationDate, "works_at");
            edgesTx.AddEdge(vertices[1].Id, "works_at", vertices[3].Id, creationDate, "works_at");
            edgesTx.AddEdge(vertices[2].Id, "works_at", vertices[4].Id, creationDate, "works_at");

            var edgesInfo = fallen8.EnqueueTransaction(edgesTx);
            edgesInfo.WaitUntilFinished();

            return fallen8;
        }

        [TestMethod]
        public void TryCreateSubgraph_NullDefinition_ShouldReturnFalse()
        {
            // Arrange
            var fallen8 = CreateSimpleGraph();
            var algorithm = new BreathFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null, CreateLoggerFactory());

            // Act
            var result = algorithm.TryCreateSubgraph(out SubGraphResult subgraphResult, null);

            // Assert
            Assert.IsFalse(result, "Should return false for null definition");
            Assert.IsNull(subgraphResult, "Result should be null");
        }

        [TestMethod]
        public void TryCreateSubgraph_EmptyPattern_ShouldReturnFalse()
        {
            // Arrange
            var fallen8 = CreateSimpleGraph();
            var algorithm = new BreathFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null, CreateLoggerFactory());

            var definition = new SubGraphDefinition
            {
                Id = "empty",
                Pattern = new List<APattern>()
            };

            // Act
            var result = algorithm.TryCreateSubgraph(out SubGraphResult subgraphResult, definition);

            // Assert
            Assert.IsFalse(result, "Should return false for empty pattern");
            Assert.IsNull(subgraphResult, "Result should be null");
        }

        [TestMethod]
        public void TryCreateSubgraph_NoMatchingStartVertex_ShouldReturnFalse()
        {
            // Arrange
            var fallen8 = CreateSimpleGraph();
            var algorithm = new BreathFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null, CreateLoggerFactory());

            var definition = new SubGraphDefinition
            {
                Id = "no-match",
                Pattern = new List<APattern>
                {
                    new VertexPattern
                    {
                        Reference = "start",
                        Label = label => label == "nonexistent"
                    }
                }
            };

            // Act
            var result = algorithm.TryCreateSubgraph(out SubGraphResult subgraphResult, definition);

            // Assert
            Assert.IsFalse(result, "Should return false when no start vertex matches");
            Assert.IsNull(subgraphResult, "Result should be null");
        }

        [TestMethod]
        public void TryCreateSubgraph_SingleVertexPattern_ShouldReturnMatchingVertices()
        {
            // Arrange
            var fallen8 = CreateSimpleGraph();
            var algorithm = new BreathFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null, CreateLoggerFactory());

            var definition = new SubGraphDefinition
            {
                Id = "single-vertex",
                Pattern = new List<APattern>
                {
                    new VertexPattern
                    {
                        Reference = "nodes",
                        Label = label => label == "node"
                    }
                }
            };

            // Act
            var result = algorithm.TryCreateSubgraph(out SubGraphResult subgraphResult, definition);

            // Assert
            Assert.IsTrue(result, "Should find matching vertices");
            Assert.IsNotNull(subgraphResult, "Result should not be null");
            Assert.IsNotNull(subgraphResult.SubGraph, "SubGraph should not be null");
            Assert.AreEqual(4, subgraphResult.SubGraph.VertexCount, "Should have 4 vertices");
            Assert.AreEqual(0, subgraphResult.SubGraph.EdgeCount, "Should have no edges with single vertex pattern");
        }

        [TestMethod]
        public void TryCreateSubgraph_VertexEdgeVertexPattern_ShouldReturnMatchingSubgraph()
        {
            // Arrange
            var fallen8 = CreateSimpleGraph();
            var algorithm = new BreathFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null, CreateLoggerFactory());

            var definition = new SubGraphDefinition
            {
                Id = "simple-path",
                Pattern = new List<APattern>
                {
                    new VertexPattern { Reference = "start", Label = label => label == "node" },
                    new EdgePattern { Reference = "edge", Direction = Direction.OutgoingEdge },
                    new VertexPattern { Reference = "end", Label = label => label == "node" }
                }
            };

            // Act
            var result = algorithm.TryCreateSubgraph(out SubGraphResult subgraphResult, definition);

            // Assert
            Assert.IsTrue(result, "Should find matching pattern");
            Assert.IsNotNull(subgraphResult?.SubGraph, "SubGraph should not be null");
            Assert.IsTrue(subgraphResult.SubGraph.VertexCount >= 2, "Should have at least 2 vertices");
            Assert.IsTrue(subgraphResult.SubGraph.EdgeCount >= 1, "Should have at least 1 edge");
        }

        [TestMethod]
        public void TryCreateSubgraph_WithLabelFilter_ShouldFilterByLabel()
        {
            // Arrange
            var fallen8 = CreateComplexGraph();
            var algorithm = new BreathFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null, CreateLoggerFactory());

            var definition = new SubGraphDefinition
            {
                Id = "person-only",
                Pattern = new List<APattern>
                {
                    new VertexPattern
                    {
                        Reference = "person",
                        Label = label => label == "person"
                    },
                    new EdgePattern
                    {
                        Reference = "knows",
                        Direction = Direction.OutgoingEdge,
                        Label = label => label == "knows"
                    },
                    new VertexPattern
                    {
                        Reference = "friend",
                        Label = label => label == "person"
                    }
                }
            };

            // Act
            var result = algorithm.TryCreateSubgraph(out SubGraphResult subgraphResult, definition);

            // Assert
            Assert.IsTrue(result, "Should find person relationships");
            Assert.IsNotNull(subgraphResult?.SubGraph, "SubGraph should not be null");

            // Verify all vertices are persons
            var vertices = subgraphResult.SubGraph.GetAllVertices();
            foreach (var vertex in vertices)
            {
                Assert.AreEqual("person", vertex.Label, "All vertices should have 'person' label");
            }
        }

        [TestMethod]
        public void TryCreateSubgraph_WithVertexFilter_ShouldFilterByProperty()
        {
            // Arrange
            var fallen8 = CreateComplexGraph();
            var algorithm = new BreathFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null, CreateLoggerFactory());

            var definition = new SubGraphDefinition
            {
                Id = "age-filter",
                Pattern = new List<APattern>
                {
                    new VertexPattern
                    {
                        Reference = "person",
                        Label = label => label == "person",
                        Vertex = vertex =>
                        {
                            object age;
                            if (vertex.TryGetProperty(out age, "age"))
                            {
                                return (int)age >= 30;
                            }
                            return false;
                        }
                    }
                }
            };

            // Act
            var result = algorithm.TryCreateSubgraph(out SubGraphResult subgraphResult, definition);

            // Assert
            Assert.IsTrue(result, "Should find vertices matching age filter");
            Assert.IsNotNull(subgraphResult?.SubGraph, "SubGraph should not be null");
            Assert.IsTrue(subgraphResult.SubGraph.VertexCount >= 1, "Should have at least 1 vertex aged 30+");

            // Verify all vertices match the age filter
            var vertices = subgraphResult.SubGraph.GetAllVertices();
            foreach (var vertex in vertices)
            {
                object age;
                if (vertex.TryGetProperty(out age, "age"))
                {
                    Assert.IsTrue((int)age >= 30, "All vertices should have age >= 30");
                }
            }
        }

        [TestMethod]
        public void TryCreateSubgraph_WithEdgePropertyFilter_ShouldFilterByEdgeProperty()
        {
            // Arrange
            var fallen8 = CreateComplexGraph();
            var algorithm = new BreathFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null, CreateLoggerFactory());

            var definition = new SubGraphDefinition
            {
                Id = "knows-only",
                Pattern = new List<APattern>
                {
                    new VertexPattern { Reference = "p1", Label = label => label == "person" },
                    new EdgePattern
                    {
                        Reference = "relationship",
                        Direction = Direction.OutgoingEdge,
                        EdgeProperty = (propertyId, direction) => propertyId == "knows"
                    },
                    new VertexPattern { Reference = "p2", Label = label => label == "person" }
                }
            };

            // Act
            var result = algorithm.TryCreateSubgraph(out SubGraphResult subgraphResult, definition);

            // Assert
            Assert.IsTrue(result, "Should find 'knows' relationships");
            Assert.IsNotNull(subgraphResult?.SubGraph, "SubGraph should not be null");
            Assert.IsTrue(subgraphResult.SubGraph.EdgeCount >= 1, "Should have at least 1 'knows' edge");
        }

        [TestMethod]
        public void TryCreateSubgraph_WithEdgeFilter_ShouldFilterByEdgeLabel()
        {
            // Arrange
            var fallen8 = CreateComplexGraph();
            var algorithm = new BreathFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null, CreateLoggerFactory());

            var definition = new SubGraphDefinition
            {
                Id = "works-at",
                Pattern = new List<APattern>
                {
                    new VertexPattern { Reference = "person", Label = label => label == "person" },
                    new EdgePattern
                    {
                        Reference = "employment",
                        Direction = Direction.OutgoingEdge,
                        Edge = (edge, direction) => edge.Label == "works_at"
                    },
                    new VertexPattern { Reference = "company", Label = label => label == "company" }
                }
            };

            // Act
            var result = algorithm.TryCreateSubgraph(out SubGraphResult subgraphResult, definition);

            // Assert
            Assert.IsTrue(result, "Should find employment relationships");
            Assert.IsNotNull(subgraphResult?.SubGraph, "SubGraph should not be null");

            // Verify edges are 'works_at'
            var edges = subgraphResult.SubGraph.GetAllEdges();
            foreach (var edge in edges)
            {
                Assert.AreEqual("works_at", edge.Label, "All edges should be 'works_at'");
            }
        }

        [TestMethod]
        public void TryCreateSubgraph_WithIncomingEdges_ShouldTraverseReverse()
        {
            // Arrange
            var fallen8 = CreateSimpleGraph();
            var algorithm = new BreathFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null, CreateLoggerFactory());

            // Find vertex D and traverse backwards to C
            var definition = new SubGraphDefinition
            {
                Id = "reverse-traversal",
                Pattern = new List<APattern>
                {
                    new VertexPattern
                    {
                        Reference = "d",
                        Vertex = vertex =>
                        {
                            object name;
                            return vertex.TryGetProperty(out name, "name") && name.ToString() == "D";
                        }
                    },
                    new EdgePattern
                    {
                        Reference = "back",
                        Direction = Direction.IncomingEdge
                    },
                    new VertexPattern { Reference = "previous" }
                }
            };

            // Act
            var result = algorithm.TryCreateSubgraph(out SubGraphResult subgraphResult, definition);

            // Assert
            Assert.IsTrue(result, "Should traverse incoming edges");
            Assert.IsNotNull(subgraphResult?.SubGraph, "SubGraph should not be null");
            Assert.AreEqual(2, subgraphResult.SubGraph.VertexCount, "Should have 2 vertices (D and C)");
            Assert.AreEqual(1, subgraphResult.SubGraph.EdgeCount, "Should have 1 edge");
        }

        [TestMethod]
        public void TryCreateSubgraph_WithUndirectedEdges_ShouldTraverseBothDirections()
        {
            // Arrange
            var fallen8 = CreateSimpleGraph();
            var algorithm = new BreathFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null, CreateLoggerFactory());

            var definition = new SubGraphDefinition
            {
                Id = "undirected",
                Pattern = new List<APattern>
                {
                    new VertexPattern
                    {
                        Reference = "b",
                        Vertex = vertex =>
                        {
                            object name;
                            return vertex.TryGetProperty(out name, "name") && name.ToString() == "B";
                        }
                    },
                    new EdgePattern
                    {
                        Reference = "any",
                        Direction = Direction.UndirectedEdge
                    },
                    new VertexPattern { Reference = "neighbor" }
                }
            };

            // Act
            var result = algorithm.TryCreateSubgraph(out SubGraphResult subgraphResult, definition);

            // Assert
            Assert.IsTrue(result, "Should traverse in both directions");
            Assert.IsNotNull(subgraphResult?.SubGraph, "SubGraph should not be null");
            Assert.AreEqual(3, subgraphResult.SubGraph.VertexCount, "Should have 3 vertices (A, B, C)");
            Assert.AreEqual(2, subgraphResult.SubGraph.EdgeCount, "Should have 2 edges");
        }

        [TestMethod]
        public void TryCreateSubgraph_VariableLengthPattern_MinLength1_ShouldFindDirectConnections()
        {
            // Arrange
            var fallen8 = CreateSimpleGraph();
            var algorithm = new BreathFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null, CreateLoggerFactory());

            var definition = new SubGraphDefinition
            {
                Id = "variable-1hop",
                Pattern = new List<APattern>
                {
                    new VertexPattern
                    {
                        Reference = "a",
                        Vertex = vertex =>
                        {
                            object name;
                            return vertex.TryGetProperty(out name, "name") && name.ToString() == "A";
                        }
                    },
                    new VariableLengthEdgePattern
                    {
                        Reference = "path",
                        Direction = Direction.OutgoingEdge,
                        MinLength = 1,
                        MaxLength = 1
                    },
                    new VertexPattern { Reference = "target" }
                }
            };

            // Act
            var result = algorithm.TryCreateSubgraph(out SubGraphResult subgraphResult, definition);

            // Assert
            Assert.IsTrue(result, "Should find 1-hop paths");
            Assert.IsNotNull(subgraphResult?.SubGraph, "SubGraph should not be null");
            Assert.AreEqual(2, subgraphResult.SubGraph.VertexCount, "Should have 2 vertices (A and B)");
            Assert.AreEqual(1, subgraphResult.SubGraph.EdgeCount, "Should have 1 edge");
        }

        [TestMethod]
        public void TryCreateSubgraph_VariableLengthPattern_Range1To3_ShouldFindMultiplePathLengths()
        {
            // Arrange
            var fallen8 = CreateSimpleGraph();
            var algorithm = new BreathFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null, CreateLoggerFactory());

            var definition = new SubGraphDefinition
            {
                Id = "variable-1to3",
                Pattern = new List<APattern>
                {
                    new VertexPattern
                    {
                        Reference = "a",
                        Vertex = vertex =>
                        {
                            object name;
                            return vertex.TryGetProperty(out name, "name") && name.ToString() == "A";
                        }
                    },
                    new VariableLengthEdgePattern
                    {
                        Reference = "path",
                        Direction = Direction.OutgoingEdge,
                        MinLength = 1,
                        MaxLength = 3
                    },
                    new VertexPattern { Reference = "target" }
                }
            };

            // Act
            var result = algorithm.TryCreateSubgraph(out SubGraphResult subgraphResult, definition);

            // Assert
            Assert.IsTrue(result, "Should find paths of length 1-3");
            Assert.IsNotNull(subgraphResult?.SubGraph, "SubGraph should not be null");
            Assert.AreEqual(4, subgraphResult.SubGraph.VertexCount, "Should have all 4 vertices");
            Assert.AreEqual(3, subgraphResult.SubGraph.EdgeCount, "Should have all 3 edges");
        }

        [TestMethod]
        public void TryCreateSubgraph_VariableLengthPattern_WithTargetFilter_ShouldOnlyIncludeMatchingTargets()
        {
            // Arrange
            var fallen8 = CreateSimpleGraph();
            var algorithm = new BreathFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null, CreateLoggerFactory());

            var definition = new SubGraphDefinition
            {
                Id = "variable-with-target",
                Pattern = new List<APattern>
                {
                    new VertexPattern
                    {
                        Reference = "a",
                        Vertex = vertex =>
                        {
                            object name;
                            return vertex.TryGetProperty(out name, "name") && name.ToString() == "A";
                        }
                    },
                    new VariableLengthEdgePattern
                    {
                        Reference = "path",
                        Direction = Direction.OutgoingEdge,
                        MinLength = 1,
                        MaxLength = 3
                    },
                    new VertexPattern
                    {
                        Reference = "target-c-or-d",
                        Vertex = vertex =>
                        {
                            object name;
                            if (vertex.TryGetProperty(out name, "name"))
                            {
                                var nameStr = name.ToString();
                                return nameStr == "C" || nameStr == "D";
                            }
                            return false;
                        }
                    }
                }
            };

            // Act
            var result = algorithm.TryCreateSubgraph(out SubGraphResult subgraphResult, definition);

            // Assert
            Assert.IsTrue(result, "Should find paths to C and D");
            Assert.IsNotNull(subgraphResult?.SubGraph, "SubGraph should not be null");
            Assert.IsTrue(subgraphResult.SubGraph.VertexCount >= 3, "Should have at least vertices A, C, D");
        }

        [TestMethod]
        public void TryCreateSubgraph_ComplexPattern_ShouldHandleMultipleSteps()
        {
            // Arrange
            var fallen8 = CreateComplexGraph();
            var algorithm = new BreathFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null, CreateLoggerFactory());

            // Find: person -> knows -> person -> works_at -> company
            var definition = new SubGraphDefinition
            {
                Id = "complex",
                Pattern = new List<APattern>
                {
                    new VertexPattern { Reference = "p1", Label = label => label == "person" },
                    new EdgePattern { Reference = "knows", Direction = Direction.OutgoingEdge, Label = label => label == "knows" },
                    new VertexPattern { Reference = "p2", Label = label => label == "person" },
                    new EdgePattern { Reference = "works", Direction = Direction.OutgoingEdge, Label = label => label == "works_at" },
                    new VertexPattern { Reference = "company", Label = label => label == "company" }
                }
            };

            // Act
            var result = algorithm.TryCreateSubgraph(out SubGraphResult subgraphResult, definition);

            // Assert
            Assert.IsTrue(result, "Should find complex pattern");
            Assert.IsNotNull(subgraphResult?.SubGraph, "SubGraph should not be null");
            Assert.IsTrue(subgraphResult.SubGraph.VertexCount >= 3, "Should have at least 3 vertices");
            Assert.IsTrue(subgraphResult.SubGraph.EdgeCount >= 2, "Should have at least 2 edges");
        }

        [TestMethod]
        public void TryCreateSubgraph_SubgraphShouldBeIndependent()
        {
            // Arrange
            var fallen8 = CreateSimpleGraph();
            var algorithm = new BreathFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null, CreateLoggerFactory());

            var definition = new SubGraphDefinition
            {
                Id = "independence",
                Pattern = new List<APattern>
                {
                    new VertexPattern { Reference = "node", Label = label => label == "node" },
                    new EdgePattern { Reference = "edge", Direction = Direction.OutgoingEdge },
                    new VertexPattern { Reference = "next", Label = label => label == "node" }
                }
            };

            // Act
            var result = algorithm.TryCreateSubgraph(out SubGraphResult subgraphResult, definition);

            // Assert
            Assert.IsTrue(result, "Should create subgraph");

            var originalVertexCount = fallen8.VertexCount;
            var subgraphVertexCount = subgraphResult.SubGraph.VertexCount;

            // Add a new vertex to the subgraph
            var newVertexTx = new CreateVertexTransaction
            {
                Definition = new VertexDefinition
                {
                    CreationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds()),
                    Label = "new",
                    Properties = new Dictionary<string, object> { { "name", "NewVertex" } }
                }
            };

            var txInfo = subgraphResult.SubGraph.EnqueueTransaction(newVertexTx);
            txInfo.WaitUntilFinished();

            // Verify original graph is unchanged
            Assert.AreEqual(originalVertexCount, fallen8.VertexCount, "Original graph should be unchanged");
            Assert.AreEqual(subgraphVertexCount + 1, subgraphResult.SubGraph.VertexCount, "Subgraph should have one more vertex");
        }

        [TestMethod]
        public void TryCreateSubgraph_SubgraphShouldPreserveProperties()
        {
            // Arrange
            var fallen8 = CreateComplexGraph();
            var algorithm = new BreathFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null, CreateLoggerFactory());

            var definition = new SubGraphDefinition
            {
                Id = "properties",
                Pattern = new List<APattern>
                {
                    new VertexPattern
                    {
                        Reference = "alice",
                        Vertex = vertex =>
                        {
                            object name;
                            return vertex.TryGetProperty(out name, "name") && name.ToString() == "Alice";
                        }
                    }
                }
            };

            // Act
            var result = algorithm.TryCreateSubgraph(out SubGraphResult subgraphResult, definition);

            // Assert
            Assert.IsTrue(result, "Should find Alice");
            var vertices = subgraphResult.SubGraph.GetAllVertices();
            var alice = vertices.FirstOrDefault();

            Assert.IsNotNull(alice, "Should have Alice vertex");

            object name, age;
            Assert.IsTrue(alice.TryGetProperty(out name, "name"), "Should have name property");
            Assert.AreEqual("Alice", name.ToString(), "Name should be preserved");
            Assert.IsTrue(alice.TryGetProperty(out age, "age"), "Should have age property");
            Assert.AreEqual(30, age, "Age should be preserved");
        }

        [TestMethod]
        public void TryCreateSubgraph_SubgraphShouldPreserveEdgeProperties()
        {
            // Arrange
            var loggerFactory = CreateLoggerFactory();
            var fallen8 = new Fallen8(loggerFactory);
            var creationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds());

            // Create graph with edge properties
            var verticesTx = new CreateVerticesTransaction();
            verticesTx.AddVertex(creationDate, "node", new Dictionary<string, object>() { { "name", "A" } });
            verticesTx.AddVertex(creationDate, "node", new Dictionary<string, object>() { { "name", "B" } });

            var verticesInfo = fallen8.EnqueueTransaction(verticesTx);
            verticesInfo.WaitUntilFinished();

            var vertices = verticesTx.GetCreatedVertices();

            var edgesTx = new CreateEdgesTransaction();
            edgesTx.AddEdge(vertices[0].Id, "related", vertices[1].Id, creationDate, "connection",
                new Dictionary<string, object> { { "weight", 5 }, { "type", "strong" } });

            var edgesInfo = fallen8.EnqueueTransaction(edgesTx);
            edgesInfo.WaitUntilFinished();

            var algorithm = new BreathFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null, CreateLoggerFactory());

            var definition = new SubGraphDefinition
            {
                Id = "edge-props",
                Pattern = new List<APattern>
                {
                    new VertexPattern { Reference = "a" },
                    new EdgePattern { Reference = "edge", Direction = Direction.OutgoingEdge },
                    new VertexPattern { Reference = "b" }
                }
            };

            // Act
            var result = algorithm.TryCreateSubgraph(out SubGraphResult subgraphResult, definition);

            // Assert
            Assert.IsTrue(result, "Should find edge");
            var edges = subgraphResult.SubGraph.GetAllEdges();
            var edge = edges.FirstOrDefault();

            Assert.IsNotNull(edge, "Should have edge");

            object weight, type;
            Assert.IsTrue(edge.TryGetProperty(out weight, "weight"), "Should have weight property");
            Assert.AreEqual(5, weight, "Weight should be preserved");
            Assert.IsTrue(edge.TryGetProperty(out type, "type"), "Should have type property");
            Assert.AreEqual("strong", type.ToString(), "Type should be preserved");
        }

        [TestMethod]
        public void PluginProperties_ShouldHaveCorrectMetadata()
        {
            // Arrange
            var algorithm = new BreathFirstSearchSubgraphAlgorithm();

            // Assert
            Assert.AreEqual("Breadth First Search Subgraph Algorithm", algorithm.PluginName);
            Assert.AreEqual(typeof(ISubGraphAlgorithm), algorithm.PluginCategory);
            Assert.AreEqual("Creates a subgraph using breadth-first search traversal", algorithm.Description);
            Assert.AreEqual("Henning Rauch", algorithm.Manufacturer);
        }

        [TestMethod]
        public void Dispose_ShouldNotThrowException()
        {
            // Arrange
            var fallen8 = CreateSimpleGraph();
            var algorithm = new BreathFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null, CreateLoggerFactory());

            // Act & Assert - should not throw
            algorithm.Dispose();
        }
    }
}
