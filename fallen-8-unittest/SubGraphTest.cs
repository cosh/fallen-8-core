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
        private Fallen8 CreateSimpleGraph()
        {
            var loggerFactory = TestLoggerFactory.Create();
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
            var loggerFactory = TestLoggerFactory.Create();
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
            var algorithm = new BreadthFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null);

            // Act
            var result = algorithm.TryCreateSubgraph(out SubGraphResult subgraphResult, null);

            // Assert
            Assert.IsFalse(result, "Should return false for null definition");
            Assert.IsNull(subgraphResult, "Result should be null");
        }

        [TestMethod]
        public void TryCreateSubgraph_EmptyPattern_ShouldReturnTrue()
        {
            // Arrange
            var fallen8 = CreateSimpleGraph();
            var algorithm = new BreadthFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null);

            var definition = new SubGraphDefinition
            {
                Name = "empty",
                Pattern = new List<APattern>()
            };

            // Act
            var result = algorithm.TryCreateSubgraph(out SubGraphResult subgraphResult, definition);

            // Assert - empty pattern with no filters should copy all vertices/edges
            Assert.IsTrue(result, "Should return true - empty pattern copies all vertices and edges");
            Assert.IsNotNull(subgraphResult, "Result should not be null");
            Assert.IsNotNull(subgraphResult.SubGraph, "Subgraph should not be null");
        }

        [TestMethod]
        public void TryCreateSubgraph_NoMatchingStartVertex_ShouldReturnFalse()
        {
            // Arrange
            var fallen8 = CreateSimpleGraph();
            var algorithm = new BreadthFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null);

            var definition = new SubGraphDefinition
            {
                Name = "no-match",
                Pattern = new List<APattern>
                {
                    new VertexPattern
                    {
                        PatternName = "start",
                        Vertex = v => v.Label == "nonexistent"
                    }
                }
            };

            // Act
            var result = algorithm.TryCreateSubgraph(out SubGraphResult subgraphResult, definition);

            // Assert
            Assert.IsTrue(result, "Should return true");
            Assert.IsNotNull(subgraphResult, "Result should not be null");
            Assert.AreEqual(0, subgraphResult.SubGraph.VertexCount, "Should have no vertices");
        }

        [TestMethod]
        public void TryCreateSubgraph_SingleVertexPattern_ShouldReturnMatchingVertices()
        {
            // Arrange
            var fallen8 = CreateSimpleGraph();
            var algorithm = new BreadthFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null);

            var definition = new SubGraphDefinition
            {
                Name = "single-vertex",
                Pattern = new List<APattern>
                {
                    new VertexPattern
                    {
                        PatternName = "nodes",
                        Vertex = v => v.Label == "node"
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
            var algorithm = new BreadthFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null);

            var definition = new SubGraphDefinition
            {
                Name = "simple-path",
                Pattern = new List<APattern>
                {
                    new VertexPattern { PatternName = "start", Vertex = v => v.Label == "node" },
                    new EdgePattern { PatternName = "edge", Direction = Direction.OutgoingEdge },
                    new VertexPattern { PatternName = "end", Vertex = v => v.Label == "node" }
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
            var algorithm = new BreadthFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null);

            var definition = new SubGraphDefinition
            {
                Name = "person-only",
                Pattern = new List<APattern>
                {
                    new VertexPattern
                    {
                        PatternName = "person",
                        Vertex = v => v.Label == "person"
                    },
                    new EdgePattern
                    {
                        PatternName = "knows",
                        Direction = Direction.OutgoingEdge,
                        Edge = e => e.Label == "knows"
                    },
                    new VertexPattern
                    {
                        PatternName = "friend",
                        Vertex = v => v.Label == "person"
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
            var algorithm = new BreadthFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null);

            var definition = new SubGraphDefinition
            {
                Name = "age-filter",
                Pattern = new List<APattern>
                {
                    new VertexPattern
                    {
                        PatternName = "person",
                        Vertex = vertex =>
                        {
                            if (vertex.Label != "person")
                            {
                                return false;
                            }

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
            var algorithm = new BreadthFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null);

            var definition = new SubGraphDefinition
            {
                Name = "knows-only",
                Pattern = new List<APattern>
                {
                    new VertexPattern { PatternName = "p1", Vertex = v => v.Label == "person" },
                    new EdgePattern
                    {
                        PatternName = "relationship",
                        Direction = Direction.OutgoingEdge,
                        EdgeProperty = (propertyId) => propertyId == "knows"
                    },
                    new VertexPattern { PatternName = "p2", Vertex = v => v.Label == "person" }
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
            var algorithm = new BreadthFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null);

            var definition = new SubGraphDefinition
            {
                Name = "works-at",
                Pattern = new List<APattern>
                {
                    new VertexPattern { PatternName = "person", Vertex = v => v.Label == "person" },
                    new EdgePattern
                    {
                        PatternName = "employment",
                        Direction = Direction.OutgoingEdge,
                        Edge = (edge) => edge.Label == "works_at"
                    },
                    new VertexPattern { PatternName = "company", Vertex = v => v.Label == "company" }
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
            var algorithm = new BreadthFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null);

            // Find vertex D and traverse backwards to C
            var definition = new SubGraphDefinition
            {
                Name = "reverse-traversal",
                Pattern = new List<APattern>
                {
                    new VertexPattern
                    {
                        PatternName = "d",
                        Vertex = vertex =>
                        {
                            object name;
                            return vertex.TryGetProperty(out name, "name") && name.ToString() == "D";
                        }
                    },
                    new EdgePattern
                    {
                        PatternName = "back",
                        Direction = Direction.IncomingEdge
                    },
                    new VertexPattern { PatternName = "previous" }
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
            var algorithm = new BreadthFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null);

            var definition = new SubGraphDefinition
            {
                Name = "undirected",
                Pattern = new List<APattern>
                {
                    new VertexPattern
                    {
                        PatternName = "b",
                        Vertex = vertex =>
                        {
                            object name;
                            return vertex.TryGetProperty(out name, "name") && name.ToString() == "B";
                        }
                    },
                    new EdgePattern
                    {
                        PatternName = "any",
                        Direction = Direction.UndirectedEdge
                    },
                    new VertexPattern { PatternName = "neighbor" }
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
            var algorithm = new BreadthFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null);

            var definition = new SubGraphDefinition
            {
                Name = "variable-1hop",
                Pattern = new List<APattern>
                {
                    new VertexPattern
                    {
                        PatternName = "a",
                        Vertex = vertex =>
                        {
                            object name;
                            return vertex.TryGetProperty(out name, "name") && name.ToString() == "A";
                        }
                    },
                    new VariableLengthEdgePattern
                    {
                        PatternName = "path",
                        Direction = Direction.OutgoingEdge,
                        MinLength = 1,
                        MaxLength = 1
                    },
                    new VertexPattern { PatternName = "target" }
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
            var algorithm = new BreadthFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null);

            var definition = new SubGraphDefinition
            {
                Name = "variable-1to3",
                Pattern = new List<APattern>
                {
                    new VertexPattern
                    {
                        PatternName = "a",
                        Vertex = vertex =>
                        {
                            object name;
                            return vertex.TryGetProperty(out name, "name") && name.ToString() == "A";
                        }
                    },
                    new VariableLengthEdgePattern
                    {
                        PatternName = "path",
                        Direction = Direction.OutgoingEdge,
                        MinLength = 1,
                        MaxLength = 3
                    },
                    new VertexPattern { PatternName = "target" }
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
            var algorithm = new BreadthFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null);

            var definition = new SubGraphDefinition
            {
                Name = "variable-with-target",
                Pattern = new List<APattern>
                {
                    new VertexPattern
                    {
                        PatternName = "a",
                        Vertex = vertex =>
                        {
                            object name;
                            return vertex.TryGetProperty(out name, "name") && name.ToString() == "A";
                        }
                    },
                    new VariableLengthEdgePattern
                    {
                        PatternName = "path",
                        Direction = Direction.OutgoingEdge,
                        MinLength = 1,
                        MaxLength = 3
                    },
                    new VertexPattern
                    {
                        PatternName = "target-c-or-d",
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
            var algorithm = new BreadthFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null);

            // Find: person -> knows -> person -> works_at -> company
            var definition = new SubGraphDefinition
            {
                Name = "complex",
                Pattern = new List<APattern>
                {
                    new VertexPattern { PatternName = "p1", Vertex = v => v.Label == "person" },
                    new EdgePattern { PatternName = "knows", Direction = Direction.OutgoingEdge, Edge = e => e.Label == "knows" },
                    new VertexPattern { PatternName = "p2", Vertex = v => v.Label == "person" },
                    new EdgePattern { PatternName = "works", Direction = Direction.OutgoingEdge, Edge = e => e.Label == "works_at" },
                    new VertexPattern { PatternName = "company", Vertex = v => v.Label == "company" }
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
        public void TryCreateSubgraph_SubgraphShouldBeReadOnly()
        {
            // Arrange
            var fallen8 = CreateSimpleGraph();
            var algorithm = new BreadthFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null);

            var definition = new SubGraphDefinition
            {
                Name = "readonly",
                Pattern = new List<APattern>
                {
                    new VertexPattern { PatternName = "node", Vertex = v => v.Label == "node" },
                    new EdgePattern { PatternName = "edge", Direction = Direction.OutgoingEdge },
                    new VertexPattern { PatternName = "next", Vertex = v => v.Label == "node" }
                }
            };

            // Act
            var result = algorithm.TryCreateSubgraph(out SubGraphResult subgraphResult, definition);

            // Assert
            Assert.IsTrue(result, "Should create subgraph");
            Assert.IsNotNull(subgraphResult?.SubGraph, "SubGraph should not be null");

            var originalVertexCount = fallen8.VertexCount;
            var subgraphVertexCount = subgraphResult.SubGraph.VertexCount;

            // Verify the subgraph is exposed as IFallen8Read (read-only)
            Assert.IsInstanceOfType(subgraphResult.SubGraph, typeof(IFallen8Read), "SubGraph should be IFallen8Read");

            // Verify we can read from the subgraph
            Assert.IsTrue(subgraphVertexCount > 0, "Subgraph should have vertices");
            var vertices = subgraphResult.SubGraph.GetAllVertices();
            Assert.IsNotNull(vertices, "Should be able to read vertices from subgraph");

            // Verify original graph is unchanged after subgraph creation
            Assert.AreEqual(originalVertexCount, fallen8.VertexCount, "Original graph should be unchanged");
        }

        [TestMethod]
        public void TryCreateSubgraph_SubgraphShouldPreserveProperties()
        {
            // Arrange
            var fallen8 = CreateComplexGraph();
            var algorithm = new BreadthFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null);

            var definition = new SubGraphDefinition
            {
                Name = "properties",
                Pattern = new List<APattern>
                {
                    new VertexPattern
                    {
                        PatternName = "alice",
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
            var loggerFactory = TestLoggerFactory.Create();
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

            var algorithm = new BreadthFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null);

            var definition = new SubGraphDefinition
            {
                Name = "edge-props",
                Pattern = new List<APattern>
                {
                    new VertexPattern { PatternName = "a" },
                    new EdgePattern { PatternName = "edge", Direction = Direction.OutgoingEdge },
                    new VertexPattern { PatternName = "b" }
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
            var algorithm = new BreadthFirstSearchSubgraphAlgorithm();

            // Assert
            Assert.AreEqual("Breadth First Search Subgraph Algorithm", algorithm.PluginName);
            Assert.AreEqual(typeof(ISubGraphAlgorithm), algorithm.PluginCategory);
            Assert.AreEqual("Creates a subgraph using breadth-first search traversal with multi-phase filtering", algorithm.Description);
            Assert.AreEqual("Henning Rauch", algorithm.Manufacturer);
        }

        [TestMethod]
        public void Dispose_ShouldNotThrowException()
        {
            // Arrange
            var fallen8 = CreateSimpleGraph();
            var algorithm = new BreadthFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null);

            // Act & Assert - should not throw
            algorithm.Dispose();
        }

        [TestMethod]
        public void TryRecalculateSubGraph_ExistingSubGraph_ShouldRecalculate()
        {
            // Arrange
            var fallen8 = CreateSimpleGraph();
            var subGraphName = "test-subgraph";

            var definition = new SubGraphDefinition
            {
                Name = subGraphName,
                Pattern = new List<APattern>
                {
                    new VertexPattern { PatternName = "node", Vertex = v => v.Label == "node" }
                }
            };

            // Create a subgraph using the typed version
            SubGraphResult originalSubGraph;
            Assert.IsTrue(fallen8.SubGraphFactory.TryCreateSubGraph<BreadthFirstSearchSubgraphAlgorithm>(
                out originalSubGraph, subGraphName, definition));
            var originalVertexCount = originalSubGraph.SubGraph.VertexCount;

            // Modify the graph by adding a new vertex
            var creationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds());
            var verticesTx = new CreateVerticesTransaction();
            verticesTx.AddVertex(creationDate, "node", new Dictionary<string, object>() { { "name", "E" } });
            var verticesInfo = fallen8.EnqueueTransaction(verticesTx);
            verticesInfo.WaitUntilFinished();

            // Act - Recalculate the subgraph
            var recalculateResult = fallen8.SubGraphFactory.TryRecalculateSubGraph(subGraphName);

            // Assert
            Assert.IsTrue(recalculateResult, "Recalculation should succeed");

            SubGraphResult recalculatedSubGraph;
            Assert.IsTrue(fallen8.SubGraphFactory.TryGetSubGraph(out recalculatedSubGraph, subGraphName));
            Assert.IsNotNull(recalculatedSubGraph, "Recalculated subgraph should not be null");
            Assert.AreEqual(originalVertexCount + 1, recalculatedSubGraph.SubGraph.VertexCount,
                "Recalculated subgraph should include the new vertex");
        }

        [TestMethod]
        public void TryRecalculateSubGraph_NonExistentSubGraph_ShouldReturnFalse()
        {
            // Arrange
            var fallen8 = CreateSimpleGraph();

            // Act
            var result = fallen8.SubGraphFactory.TryRecalculateSubGraph("nonexistent");

            // Assert
            Assert.IsFalse(result, "Should return false for non-existent subgraph");
        }

        [TestMethod]
        public void TryRecalculateSubGraph_ManuallyRegisteredSubGraph_ShouldReturnFalse()
        {
            // Arrange
            var fallen8 = CreateSimpleGraph();
            var subGraphName = "manual-subgraph";

            // Create a subgraph result manually without algorithm info
            var manualSubGraph = new SubGraphResult
            {
                SourceFallen8Id = fallen8.Id,
                Definitions = new SubGraphDefinition { Name = subGraphName },
                AlgorithmPluginName = null, // No algorithm plugin name to prevent recalculation
                SubGraph = new Fallen8(TestLoggerFactory.Create())
            };

            // Register it manually
            Assert.IsTrue(fallen8.SubGraphFactory.TryRegisterSubGraph(manualSubGraph));

            // Act
            var result = fallen8.SubGraphFactory.TryRecalculateSubGraph(subGraphName);

            // Assert
            Assert.IsFalse(result, "Should return false for manually registered subgraph without algorithm");
        }

        [TestMethod]
        public void RecalculateAllSubGraphs_MultipleSubGraphs_ShouldRecalculateAll()
        {
            // Arrange
            var fallen8 = CreateComplexGraph();
            var originalVertexCount = fallen8.VertexCount;

            var definition1 = new SubGraphDefinition
            {
                Name = "persons",
                Pattern = new List<APattern>
                {
                    new VertexPattern { PatternName = "person", Vertex = v => v.Label == "person" }
                }
            };

            var definition2 = new SubGraphDefinition
            {
                Name = "companies",
                Pattern = new List<APattern>
                {
                    new VertexPattern { PatternName = "company", Vertex = v => v.Label == "company" }
                }
            };

            // Create subgraphs using typed version
            SubGraphResult subGraph1, subGraph2;
            Assert.IsTrue(fallen8.SubGraphFactory.TryCreateSubGraph<BreadthFirstSearchSubgraphAlgorithm>(
                out subGraph1, "persons", definition1));
            Assert.IsTrue(fallen8.SubGraphFactory.TryCreateSubGraph<BreadthFirstSearchSubgraphAlgorithm>(
                out subGraph2, "companies", definition2));

            var originalPersonCount = subGraph1.SubGraph.VertexCount;
            var originalCompanyCount = subGraph2.SubGraph.VertexCount;

            // Modify the graph
            var creationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds());
            var verticesTx = new CreateVerticesTransaction();
            verticesTx.AddVertex(creationDate, "person", new Dictionary<string, object>() { { "name", "David" } });
            verticesTx.AddVertex(creationDate, "company", new Dictionary<string, object>() { { "name", "NewCorp" } });
            var verticesInfo = fallen8.EnqueueTransaction(verticesTx);
            verticesInfo.WaitUntilFinished();

            // Act - Recalculate all subgraphs
            var recalculatedCount = fallen8.SubGraphFactory.RecalculateAllSubGraphs();

            // Assert
            Assert.AreEqual(2, recalculatedCount, "Should recalculate 2 subgraphs");

            SubGraphResult recalcPersons, recalcCompanies;
            Assert.IsTrue(fallen8.SubGraphFactory.TryGetSubGraph(out recalcPersons, "persons"));
            Assert.IsTrue(fallen8.SubGraphFactory.TryGetSubGraph(out recalcCompanies, "companies"));

            Assert.AreEqual(originalPersonCount + 1, recalcPersons.SubGraph.VertexCount,
                "Persons subgraph should include the new person");
            Assert.AreEqual(originalCompanyCount + 1, recalcCompanies.SubGraph.VertexCount,
                "Companies subgraph should include the new company");
        }

        [TestMethod]
        public void RecalculateAllSubGraphs_MixedSubGraphs_ShouldOnlyRecalculateValid()
        {
            // Arrange
            var fallen8 = CreateSimpleGraph();

            var definition = new SubGraphDefinition
            {
                Name = "valid",
                Pattern = new List<APattern>
                {
                    new VertexPattern { PatternName = "node", Vertex = v => v.Label == "node" }
                }
            };

            // Create one valid subgraph using typed version
            SubGraphResult validSubGraph;
            Assert.IsTrue(fallen8.SubGraphFactory.TryCreateSubGraph<BreadthFirstSearchSubgraphAlgorithm>(
                out validSubGraph, "valid", definition));

            // Register one manual subgraph
            var manualSubGraph = new SubGraphResult
            {
                SourceFallen8Id = fallen8.Id,
                Definitions = new SubGraphDefinition { Name = "manual" },
                AlgorithmPluginName = null, // No algorithm to prevent recalculation
                SubGraph = new Fallen8(TestLoggerFactory.Create())
            };
            Assert.IsTrue(fallen8.SubGraphFactory.TryRegisterSubGraph(manualSubGraph));

            // Act
            var recalculatedCount = fallen8.SubGraphFactory.RecalculateAllSubGraphs();

            // Assert
            Assert.AreEqual(1, recalculatedCount, "Should only recalculate 1 subgraph (skip the manual one)");
        }

        [TestMethod]
        public void CanRecalculateSubGraph_ValidSubGraph_ShouldReturnTrue()
        {
            // Arrange
            var fallen8 = CreateSimpleGraph();

            var definition = new SubGraphDefinition
            {
                Name = "test",
                Pattern = new List<APattern>
                {
                    new VertexPattern { PatternName = "node", Vertex = v => v.Label == "node" }
                }
            };

            SubGraphResult subGraph;
            Assert.IsTrue(fallen8.SubGraphFactory.TryCreateSubGraph<BreadthFirstSearchSubgraphAlgorithm>(
                out subGraph, "test", definition));

            // Act
            var canRecalculate = fallen8.SubGraphFactory.CanRecalculateSubGraph("test");

            // Assert
            Assert.IsTrue(canRecalculate, "Should be able to recalculate valid subgraph");
        }

        [TestMethod]
        public void CanRecalculateSubGraph_ManualSubGraph_ShouldReturnFalse()
        {
            // Arrange
            var fallen8 = CreateSimpleGraph();

            var manualSubGraph = new SubGraphResult
            {
                SourceFallen8Id = fallen8.Id,
                Definitions = new SubGraphDefinition { Name = "manual" },
                AlgorithmPluginName = null, // No algorithm to prevent recalculation
                SubGraph = new Fallen8(TestLoggerFactory.Create())
            };
            Assert.IsTrue(fallen8.SubGraphFactory.TryRegisterSubGraph(manualSubGraph));

            // Act
            var canRecalculate = fallen8.SubGraphFactory.CanRecalculateSubGraph("manual");

            // Assert
            Assert.IsFalse(canRecalculate, "Should not be able to recalculate manually registered subgraph");
        }

        [TestMethod]
        public void CanRecalculateSubGraph_NonExistent_ShouldReturnFalse()
        {
            // Arrange
            var fallen8 = CreateSimpleGraph();

            // Act
            var canRecalculate = fallen8.SubGraphFactory.CanRecalculateSubGraph("nonexistent");

            // Assert
            Assert.IsFalse(canRecalculate, "Should return false for non-existent subgraph");
        }

        [TestMethod]
        public void GetAllSubGraphNames_WithSubGraphs_ShouldReturnAllNames()
        {
            // Arrange
            var fallen8 = CreateSimpleGraph();

            var definition1 = new SubGraphDefinition
            {
                Name = "test1",
                Pattern = new List<APattern>
                {
                    new VertexPattern { PatternName = "node", Vertex = v => v.Label == "node" }
                }
            };

            var definition2 = new SubGraphDefinition
            {
                Name = "test2",
                Pattern = new List<APattern>
                {
                    new VertexPattern { PatternName = "node", Vertex = v => v.Label == "node" }
                }
            };

            SubGraphResult subGraph1, subGraph2;
            Assert.IsTrue(fallen8.SubGraphFactory.TryCreateSubGraph<BreadthFirstSearchSubgraphAlgorithm>(
                out subGraph1, "test1", definition1));
            Assert.IsTrue(fallen8.SubGraphFactory.TryCreateSubGraph<BreadthFirstSearchSubgraphAlgorithm>(
                out subGraph2, "test2", definition2));

            // Act
            var names = fallen8.SubGraphFactory.GetAllSubGraphNames();

            // Assert
            var namesList = names.ToList();
            Assert.AreEqual(2, namesList.Count, "Should have 2 subgraph names");
            Assert.IsTrue(namesList.Contains("test1"), "Should contain test1");
            Assert.IsTrue(namesList.Contains("test2"), "Should contain test2");
        }

        [TestMethod]
        public void GetAllSubGraphNames_NoSubGraphs_ShouldReturnEmpty()
        {
            // Arrange
            var fallen8 = CreateSimpleGraph();

            // Act
            var names = fallen8.SubGraphFactory.GetAllSubGraphNames();

            // Assert
            Assert.IsFalse(names.Any(), "Should return empty collection");
        }

        [TestMethod]
        public void TryRecalculateSubGraph_SubGraphOfSubGraph_ShouldUseCorrectSource()
        {
            // Arrange
            var fallen8 = CreateComplexGraph();

            // Create first level subgraph - all persons
            var personDefinition = new SubGraphDefinition
            {
                Name = "persons",
                Pattern = new List<APattern>
                {
                    new VertexPattern { PatternName = "person", Vertex = v => v.Label == "person" }
                }
            };

            SubGraphResult personSubGraph;
            Assert.IsTrue(fallen8.SubGraphFactory.TryCreateSubGraph<BreadthFirstSearchSubgraphAlgorithm>(
                out personSubGraph, "persons", personDefinition));

            var personCount = personSubGraph.SubGraph.VertexCount;
            Assert.IsTrue(personCount > 0, "Should have person vertices");

            // Verify SourceFallen8 is correctly set
            Assert.IsNotNull(personSubGraph.SourceFallen8, "SourceFallen8 should be set");
            Assert.AreEqual(fallen8.Id, personSubGraph.SourceFallen8.Id,
                "First level subgraph should have main graph as source");

            // Now add a new person to the main graph
            var creationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds());
            var verticesTx = new CreateVerticesTransaction();
            verticesTx.AddVertex(creationDate, "person", new Dictionary<string, object>()
            {
                { "name", "Eve" },
                { "age", 35 }
            });
            var verticesInfo = fallen8.EnqueueTransaction(verticesTx);
            verticesInfo.WaitUntilFinished();

            // Recalculate the first level subgraph (persons)
            Assert.IsTrue(fallen8.SubGraphFactory.TryRecalculateSubGraph("persons"));

            SubGraphResult recalcPersons;
            Assert.IsTrue(fallen8.SubGraphFactory.TryGetSubGraph(out recalcPersons, "persons"));
            Assert.AreEqual(personCount + 1, recalcPersons.SubGraph.VertexCount,
                "First level subgraph should include new person");

            // Verify SourceFallen8 is still correctly set after recalculation
            Assert.IsNotNull(recalcPersons.SourceFallen8, "SourceFallen8 should still be set");
            Assert.AreEqual(fallen8.Id, recalcPersons.SourceFallen8.Id,
                "SourceFallen8 should still point to the main graph");
        }

        [TestMethod]
        public void SubGraphResult_ShouldStoreSourceFallen8()
        {
            // Arrange
            var fallen8 = CreateSimpleGraph();

            var definition = new SubGraphDefinition
            {
                Name = "test",
                Pattern = new List<APattern>
                {
                    new VertexPattern { PatternName = "node", Vertex = v => v.Label == "node" }
                }
            };

            // Act
            SubGraphResult subGraph;
            Assert.IsTrue(fallen8.SubGraphFactory.TryCreateSubGraph<BreadthFirstSearchSubgraphAlgorithm>(
                out subGraph, "test", definition));

            // Assert
            Assert.IsNotNull(subGraph.SourceFallen8, "SourceFallen8 should be set");
            Assert.AreEqual(fallen8.Id, subGraph.SourceFallen8.Id,
                "SourceFallen8 should be the original Fallen8 instance");
            Assert.IsFalse(string.IsNullOrEmpty(subGraph.AlgorithmPluginName), "AlgorithmPluginName should be stored");
            Assert.AreEqual("Breadth First Search Subgraph Algorithm", subGraph.AlgorithmPluginName, "AlgorithmPluginName should be correct");
            Assert.IsNotNull(subGraph.Definitions, "Definitions should be stored");
        }

        // ---------------------------------------------------------------------
        // Branching-graph regression tests (KD-1).
        //
        // These pin the behaviour that the pattern matcher must give each path
        // its OWN element set. The earlier bug shared a single HashSet across
        // every path branched from a common prefix, so elements visited on one
        // branch leaked into the keep-set of another. The leak was invisible on
        // linear graphs (A->B->C->D) because the union of polluted paths still
        // equalled the correct answer; it only shows up when the correct result
        // is a PROPER subset of a branching graph.
        // ---------------------------------------------------------------------

        /// <summary>
        /// Builds a fan-out graph: A has two outgoing edges, to B and to C.
        /// </summary>
        private Fallen8 CreateFanOutGraph()
        {
            var fallen8 = new Fallen8(TestLoggerFactory.Create());
            var creationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds());

            var verticesTx = new CreateVerticesTransaction();
            verticesTx.AddVertex(creationDate, "node", new Dictionary<string, object>() { { "name", "A" } });
            verticesTx.AddVertex(creationDate, "node", new Dictionary<string, object>() { { "name", "B" } });
            verticesTx.AddVertex(creationDate, "node", new Dictionary<string, object>() { { "name", "C" } });
            fallen8.EnqueueTransaction(verticesTx).WaitUntilFinished();
            var v = verticesTx.GetCreatedVertices();

            var edgesTx = new CreateEdgesTransaction();
            edgesTx.AddEdge(v[0].Id, "connects", v[1].Id, creationDate, "link"); // A -> B
            edgesTx.AddEdge(v[0].Id, "connects", v[2].Id, creationDate, "link"); // A -> C
            fallen8.EnqueueTransaction(edgesTx).WaitUntilFinished();

            return fallen8;
        }

        [TestMethod]
        public void TryCreateSubgraph_FanOut_TerminalFilterMatchesOneBranch_ShouldPruneOtherBranch()
        {
            // Arrange: A -> B and A -> C, keep only the path that ends at B.
            var fallen8 = CreateFanOutGraph();
            var algorithm = new BreadthFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null);

            var definition = new SubGraphDefinition
            {
                Name = "fan-out-terminal",
                Pattern = new List<APattern>
                {
                    new VertexPattern
                    {
                        PatternName = "start",
                        Vertex = vertex => vertex.TryGetProperty(out object n, "name") && n.ToString() == "A"
                    },
                    new EdgePattern { PatternName = "out", Direction = Direction.OutgoingEdge },
                    new VertexPattern
                    {
                        PatternName = "end",
                        Vertex = vertex => vertex.TryGetProperty(out object n, "name") && n.ToString() == "B"
                    }
                }
            };

            // Act
            var result = algorithm.TryCreateSubgraph(out SubGraphResult subgraphResult, definition);

            // Assert: only A and B (plus the A->B edge) are on a matching path.
            // With the shared-set bug, C and the A->C edge leak into the keep-set,
            // yielding 3 vertices / 2 edges.
            Assert.IsTrue(result, "Should match the A->B path");
            Assert.IsNotNull(subgraphResult?.SubGraph, "SubGraph should not be null");
            Assert.AreEqual(2, subgraphResult.SubGraph.VertexCount, "Only A and B should remain");
            Assert.AreEqual(1, subgraphResult.SubGraph.EdgeCount, "Only the A->B edge should remain");

            var names = subgraphResult.SubGraph.GetAllVertices()
                .Select(v => v.TryGetProperty(out object n, "name") ? n.ToString() : null)
                .OrderBy(x => x)
                .ToList();
            CollectionAssert.AreEqual(new[] { "A", "B" }, names, "C must be pruned from the subgraph");
        }

        /// <summary>
        /// Builds a Y-shaped graph with two arms of length two:
        /// A -> B -> D and A -> C -> E.
        /// </summary>
        private Fallen8 CreateYGraph()
        {
            var fallen8 = new Fallen8(TestLoggerFactory.Create());
            var creationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds());

            var verticesTx = new CreateVerticesTransaction();
            foreach (var name in new[] { "A", "B", "C", "D", "E" })
            {
                verticesTx.AddVertex(creationDate, "node", new Dictionary<string, object>() { { "name", name } });
            }
            fallen8.EnqueueTransaction(verticesTx).WaitUntilFinished();
            var v = verticesTx.GetCreatedVertices(); // 0:A 1:B 2:C 3:D 4:E

            var edgesTx = new CreateEdgesTransaction();
            edgesTx.AddEdge(v[0].Id, "connects", v[1].Id, creationDate, "link"); // A -> B
            edgesTx.AddEdge(v[0].Id, "connects", v[2].Id, creationDate, "link"); // A -> C
            edgesTx.AddEdge(v[1].Id, "connects", v[3].Id, creationDate, "link"); // B -> D
            edgesTx.AddEdge(v[2].Id, "connects", v[4].Id, creationDate, "link"); // C -> E
            fallen8.EnqueueTransaction(edgesTx).WaitUntilFinished();

            return fallen8;
        }

        [TestMethod]
        public void TryCreateSubgraph_VariableLengthTwoHopBranching_ShouldKeepOnlyMatchingArm()
        {
            // Arrange: A->B->D and A->C->E. A 2-hop variable-length path from A
            // whose terminal must be D. Only the A->B->D arm qualifies.
            var fallen8 = CreateYGraph();
            var algorithm = new BreadthFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null);

            var definition = new SubGraphDefinition
            {
                Name = "y-two-hop",
                Pattern = new List<APattern>
                {
                    new VertexPattern
                    {
                        PatternName = "a",
                        Vertex = vertex => vertex.TryGetProperty(out object n, "name") && n.ToString() == "A"
                    },
                    new VariableLengthEdgePattern
                    {
                        PatternName = "hops",
                        Direction = Direction.OutgoingEdge,
                        MinLength = 2,
                        MaxLength = 2
                    },
                    new VertexPattern
                    {
                        PatternName = "d",
                        Vertex = vertex => vertex.TryGetProperty(out object n, "name") && n.ToString() == "D"
                    }
                }
            };

            // Act
            var result = algorithm.TryCreateSubgraph(out SubGraphResult subgraphResult, definition);

            // Assert: keep-set is exactly {A, B, D} + {A->B, B->D}. With the
            // shared-set bug, the C/E arm leaks in, yielding 5 vertices / 4 edges.
            Assert.IsTrue(result, "Should match the A->B->D arm");
            Assert.IsNotNull(subgraphResult?.SubGraph, "SubGraph should not be null");
            Assert.AreEqual(3, subgraphResult.SubGraph.VertexCount, "Only A, B and D should remain");
            Assert.AreEqual(2, subgraphResult.SubGraph.EdgeCount, "Only A->B and B->D should remain");

            var names = subgraphResult.SubGraph.GetAllVertices()
                .Select(v => v.TryGetProperty(out object n, "name") ? n.ToString() : null)
                .OrderBy(x => x)
                .ToList();
            CollectionAssert.AreEqual(new[] { "A", "B", "D" }, names, "The C/E arm must be pruned");
        }

        [TestMethod]
        public void TryCreateSubgraph_FanOut_ShouldNotMutateSourceGraph()
        {
            // Arrange
            var fallen8 = CreateFanOutGraph();
            var sourceVertexCount = fallen8.VertexCount;
            var sourceEdgeCount = fallen8.EdgeCount;

            var algorithm = new BreadthFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null);

            var definition = new SubGraphDefinition
            {
                Name = "fan-out-readonly",
                Pattern = new List<APattern>
                {
                    new VertexPattern
                    {
                        PatternName = "start",
                        Vertex = vertex => vertex.TryGetProperty(out object n, "name") && n.ToString() == "A"
                    },
                    new EdgePattern { PatternName = "out", Direction = Direction.OutgoingEdge },
                    new VertexPattern
                    {
                        PatternName = "end",
                        Vertex = vertex => vertex.TryGetProperty(out object n, "name") && n.ToString() == "B"
                    }
                }
            };

            // Act
            algorithm.TryCreateSubgraph(out _, definition);

            // Assert: pruning happens on the copy, never on the source.
            Assert.AreEqual(sourceVertexCount, fallen8.VertexCount, "Source vertex count must be unchanged");
            Assert.AreEqual(sourceEdgeCount, fallen8.EdgeCount, "Source edge count must be unchanged");
        }

        // ---------------------------------------------------------------------
        // Pattern validation and variable-length semantics (KD-5 / KD-6).
        // ---------------------------------------------------------------------

        [TestMethod]
        public void TryCreateSubgraph_PatternEndingInEdge_ShouldReturnFalseAndNullResult()
        {
            // A well-formed pattern path must end at a vertex; a trailing edge is invalid.
            var fallen8 = CreateSimpleGraph();
            var algorithm = new BreadthFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null);

            var definition = new SubGraphDefinition
            {
                Name = "trailing-edge",
                Pattern = new List<APattern>
                {
                    new VertexPattern { PatternName = "v", Vertex = v => v.Label == "node" },
                    new EdgePattern { PatternName = "dangling", Direction = Direction.OutgoingEdge }
                }
            };

            var result = algorithm.TryCreateSubgraph(out SubGraphResult subgraphResult, definition);

            Assert.IsFalse(result, "A pattern ending in an edge must be rejected");
            Assert.IsNull(subgraphResult, "No result should be produced for an invalid pattern");
        }

        [TestMethod]
        public void TryCreateSubgraph_VariableLength_ConstrainsTerminalVertexOnly()
        {
            // On the linear graph A->B->C->D, a 2-hop variable-length path from A whose
            // terminal must be "C" yields A->B->C. The intermediate vertex B is included
            // even though it does not match the terminal ("C") filter - this pins the
            // documented terminal-only constraint semantics (KD-6).
            var fallen8 = CreateSimpleGraph();
            var algorithm = new BreadthFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null);

            var definition = new SubGraphDefinition
            {
                Name = "var-terminal-only",
                Pattern = new List<APattern>
                {
                    new VertexPattern
                    {
                        PatternName = "a",
                        Vertex = vertex => vertex.TryGetProperty(out object n, "name") && n.ToString() == "A"
                    },
                    new VariableLengthEdgePattern
                    {
                        PatternName = "hops",
                        Direction = Direction.OutgoingEdge,
                        MinLength = 2,
                        MaxLength = 2
                    },
                    new VertexPattern
                    {
                        PatternName = "c",
                        Vertex = vertex => vertex.TryGetProperty(out object n, "name") && n.ToString() == "C"
                    }
                }
            };

            var result = algorithm.TryCreateSubgraph(out SubGraphResult subgraphResult, definition);

            Assert.IsTrue(result, "Should match A->B->C");
            Assert.AreEqual(3, subgraphResult.SubGraph.VertexCount, "A, B (intermediate) and C are kept");
            Assert.AreEqual(2, subgraphResult.SubGraph.EdgeCount, "A->B and B->C are kept");

            var names = subgraphResult.SubGraph.GetAllVertices()
                .Select(v => v.TryGetProperty(out object n, "name") ? n.ToString() : null)
                .OrderBy(x => x)
                .ToList();
            CollectionAssert.AreEqual(new[] { "A", "B", "C" }, names,
                "Intermediate vertex B is present despite not matching the terminal filter");
        }

        /// <summary>
        /// Builds a graph where one arm is shorter than the other:
        /// A -> B (B is a leaf) and A -> C -> D.
        /// </summary>
        private Fallen8 CreateUnevenArmsGraph()
        {
            var fallen8 = new Fallen8(TestLoggerFactory.Create());
            var creationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds());

            var verticesTx = new CreateVerticesTransaction();
            foreach (var name in new[] { "A", "B", "C", "D" })
            {
                verticesTx.AddVertex(creationDate, "node", new Dictionary<string, object>() { { "name", name } });
            }
            fallen8.EnqueueTransaction(verticesTx).WaitUntilFinished();
            var v = verticesTx.GetCreatedVertices(); // 0:A 1:B 2:C 3:D

            var edgesTx = new CreateEdgesTransaction();
            edgesTx.AddEdge(v[0].Id, "connects", v[1].Id, creationDate, "link"); // A -> B (leaf)
            edgesTx.AddEdge(v[0].Id, "connects", v[2].Id, creationDate, "link"); // A -> C
            edgesTx.AddEdge(v[2].Id, "connects", v[3].Id, creationDate, "link"); // C -> D
            fallen8.EnqueueTransaction(edgesTx).WaitUntilFinished();

            return fallen8;
        }

        [TestMethod]
        public void TryCreateSubgraph_VariableLengthRange_KeepsBothShorterAndLongerMatches()
        {
            // A->B (length 1) and A->C->D (length 2). A variable-length 1..2 path from A
            // with an unconstrained terminal must keep BOTH arms. The earlier bug dropped
            // every path shorter than MaxLength, losing B and A->B entirely.
            var fallen8 = CreateUnevenArmsGraph();
            var algorithm = new BreadthFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null);

            var definition = new SubGraphDefinition
            {
                Name = "range-both",
                Pattern = new List<APattern>
                {
                    new VertexPattern
                    {
                        PatternName = "a",
                        Vertex = vertex => vertex.TryGetProperty(out object n, "name") && n.ToString() == "A"
                    },
                    new VariableLengthEdgePattern
                    {
                        PatternName = "hops",
                        Direction = Direction.OutgoingEdge,
                        MinLength = 1,
                        MaxLength = 2
                    },
                    new VertexPattern { PatternName = "any" }
                }
            };

            var result = algorithm.TryCreateSubgraph(out SubGraphResult subgraphResult, definition);

            Assert.IsTrue(result);
            Assert.AreEqual(4, subgraphResult.SubGraph.VertexCount, "All of A, B, C, D are on a 1..2-hop path");
            Assert.AreEqual(3, subgraphResult.SubGraph.EdgeCount, "A->B (len 1), A->C and C->D (len 2)");

            var names = subgraphResult.SubGraph.GetAllVertices()
                .Select(v => v.TryGetProperty(out object n, "name") ? n.ToString() : null)
                .OrderBy(x => x)
                .ToList();
            CollectionAssert.AreEqual(new[] { "A", "B", "C", "D" }, names,
                "The length-1 arm (B) must not be dropped by the range expansion");
        }

        [TestMethod]
        public void TryCreateSubgraph_VariableLengthRange_TerminalMatchesShortPath_ShouldNotBeEmpty()
        {
            // Linear A->B->C->D. A variable-length 1..2 path from A whose terminal must be "B"
            // matches only the length-1 path A->B. The earlier bug invalidated the length-1
            // path while expanding it, wrongly returning an empty subgraph.
            var fallen8 = CreateSimpleGraph();
            var algorithm = new BreadthFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null);

            var definition = new SubGraphDefinition
            {
                Name = "range-short-terminal",
                Pattern = new List<APattern>
                {
                    new VertexPattern
                    {
                        PatternName = "a",
                        Vertex = vertex => vertex.TryGetProperty(out object n, "name") && n.ToString() == "A"
                    },
                    new VariableLengthEdgePattern
                    {
                        PatternName = "hops",
                        Direction = Direction.OutgoingEdge,
                        MinLength = 1,
                        MaxLength = 2
                    },
                    new VertexPattern
                    {
                        PatternName = "b",
                        Vertex = vertex => vertex.TryGetProperty(out object n, "name") && n.ToString() == "B"
                    }
                }
            };

            var result = algorithm.TryCreateSubgraph(out SubGraphResult subgraphResult, definition);

            Assert.IsTrue(result);
            Assert.AreEqual(2, subgraphResult.SubGraph.VertexCount, "A and B on the length-1 match");
            Assert.AreEqual(1, subgraphResult.SubGraph.EdgeCount, "The A->B edge");
        }

        [TestMethod]
        public void TryCreateSubGraph_DuplicateName_ReturnsFalseWithNullResult()
        {
            // Registration failure (duplicate name) must yield a null result, not a non-null
            // one - otherwise a losing racer reports success and its rollback deletes the
            // winner's subgraph.
            var fallen8 = CreateSimpleGraph();
            var definition = new SubGraphDefinition
            {
                Name = "dup",
                Pattern = new List<APattern>
                {
                    new VertexPattern { PatternName = "node", Vertex = v => v.Label == "node" }
                }
            };

            Assert.IsTrue(fallen8.SubGraphFactory.TryCreateSubGraph<BreadthFirstSearchSubgraphAlgorithm>(
                out var first, "dup", definition), "First create should succeed");
            Assert.IsNotNull(first);

            var second = fallen8.SubGraphFactory.TryCreateSubGraph<BreadthFirstSearchSubgraphAlgorithm>(
                out var duplicate, "dup", definition);

            Assert.IsFalse(second, "Creating a second subgraph with the same name must fail");
            Assert.IsNull(duplicate, "A failed registration must not return a subgraph result");
        }

        [TestMethod]
        public void TryCreateSubgraph_LeadingVariableLengthEdge_ShouldReturnFalse()
        {
            // A pattern starting with a variable-length edge cannot honor Min/MaxLength and
            // must be rejected rather than silently treated as a single hop.
            var fallen8 = CreateSimpleGraph();
            var algorithm = new BreadthFirstSearchSubgraphAlgorithm();
            algorithm.Initialize(fallen8, null);

            var definition = new SubGraphDefinition
            {
                Name = "leading-varlen",
                Pattern = new List<APattern>
                {
                    new VariableLengthEdgePattern { PatternName = "hops", Direction = Direction.OutgoingEdge, MinLength = 2, MaxLength = 2 },
                    new VertexPattern { PatternName = "v" }
                }
            };

            var result = algorithm.TryCreateSubgraph(out SubGraphResult subgraphResult, definition);

            Assert.IsFalse(result, "A leading variable-length edge pattern must be rejected");
            Assert.IsNull(subgraphResult);
        }
    }
}


