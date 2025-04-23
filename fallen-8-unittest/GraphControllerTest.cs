// MIT License
//
// GraphControllerTest.cs
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
using System.Net;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.App.Controllers.Model;
using NoSQL.GraphDB.Core.Expression;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    [TestClass]
    public class GraphControllerTest
    {
        private ILoggerFactory _loggerFactory;
        private Fallen8 _fallen8;
        private GraphController _controller;

        [TestInitialize]
        public void TestInitialize()
        {
            // Set up a fresh test environment before each test
            _loggerFactory = LoggerFactory.Create(builder =>
            {
                builder
                    .AddFilter("Microsoft", LogLevel.Warning)
                    .AddFilter("System", LogLevel.Warning)
                    .AddFilter("NoSQL.GraphDB", LogLevel.Debug)
                    .AddConsole();
            });

            // Create a new Fallen8 instance for each test
            _fallen8 = new Fallen8(_loggerFactory);
            _controller = new GraphController(_loggerFactory.CreateLogger<GraphController>(), _fallen8);

            // Clear any previous data
            var tx = new TabulaRasaTransaction();
            _fallen8.EnqueueTransaction(tx).WaitUntilFinished();
        }

        [TestMethod]
        public void AddVertex_WhenValidVertexSpecificationProvided_ShouldCreateVertex()
        {
            // Arrange
            var vertexSpec = new VertexSpecification
            {
                Label = "person",
                CreationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds()),
                Properties = new List<PropertySpecification>
                {
                    new PropertySpecification {
                        PropertyId = "name",
                        PropertyValue = "John Doe",
                        FullQualifiedTypeName = "System.String"
                    }
                }
            };

            // Act
            _controller.AddVertex(vertexSpec);

            // Assert - Check if vertex is added by running a graph scan
            var scanDef = new ScanSpecification();
            scanDef.Literal = new LiteralSpecification()
            {
                Value = "John Doe",
                FullQualifiedTypeName = "System.String"
            };

            //we need to wait a bit
            System.Threading.Thread.Sleep(100);

            scanDef.Operator = BinaryOperator.Equals;
            var result = _controller.GraphScan("name", scanDef);

            Assert.IsNotNull(result, "Graph should not be null");
            Assert.AreEqual(1, result.Count(), "There should be one vertex in the graph");

            var vertex = _controller.GetVertex(result.First());

            Assert.AreEqual("person", vertex.Label, "The vertex should have the correct label");

            // Check if the vertex has the right property
            var vertexProperties = vertex.Properties;
            Assert.IsTrue(vertexProperties != null, "Vertex properties should not be null");

            // Find property with PropertyId "name"
            var nameProp = vertexProperties.FirstOrDefault(p => p.PropertyId == "name");
            Assert.IsNotNull(nameProp, "Vertex should have a name property");
            Assert.AreEqual("John Doe", nameProp.PropertyValue, "Vertex name property should have correct value");
        }

        [TestMethod]
        public void AddEdge_WhenValidEdgeSpecificationProvided_ShouldCreateEdge()
        {
            // Arrange - Create two vertices first
            var sourceVertexSpec = new VertexSpecification
            {
                Label = "person",
                CreationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds()),
                Properties = new List<PropertySpecification>
                {
                    new PropertySpecification {
                        PropertyId = "name",
                        PropertyValue = "Source",
                        FullQualifiedTypeName = "System.String"
                    }
                }
            };

            var targetVertexSpec = new VertexSpecification
            {
                Label = "person",
                CreationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds()),
                Properties = new List<PropertySpecification>
                {
                    new PropertySpecification {
                        PropertyId = "name",
                        PropertyValue = "Target",
                        FullQualifiedTypeName = "System.String"
                    }
                }
            };

            // Add the vertices
            _controller.AddVertex(sourceVertexSpec);
            _controller.AddVertex(targetVertexSpec);

            //we need to wait a bit
            System.Threading.Thread.Sleep(10);


            // Get the created vertices to get their IDs
            var graph = _controller.GetGraph(100);
            Assert.AreEqual(2, graph.Vertices.Count, "Two vertices should be created");

            var sourceVertex = graph.Vertices.FirstOrDefault(v =>
                v.Properties.Any(_ => _.PropertyId == "name" && _.PropertyValue == "Source"));
            var targetVertex = graph.Vertices.FirstOrDefault(v =>
                v.Properties.Any(_ => _.PropertyId == "name" && _.PropertyValue == "Target"));

            Assert.IsNotNull(sourceVertex, "Source vertex should be found");
            Assert.IsNotNull(targetVertex, "Target vertex should be found");

            // Create an edge specification
            var edgeSpec = new EdgeSpecification
            {
                Label = "knows",
                CreationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds()),
                SourceVertex = sourceVertex.Id,
                TargetVertex = targetVertex.Id,
                EdgePropertyId = "friendship",
                Properties = new List<PropertySpecification>
                {
                    new PropertySpecification {
                        PropertyId = "since",
                        PropertyValue = "2024-01-01",
                        FullQualifiedTypeName = "System.DateTime"
                    }
                }
            };

            // Act
            _controller.AddEdge(edgeSpec);

            //we need to wait a bit
            Thread.Sleep(100);

            // Assert
            // Refresh the graph
            var scanDef = new ScanSpecification();
            scanDef.ResultType = ResultTypeSpecification.Edges;
            scanDef.Literal = new LiteralSpecification()
            {
                Value = "2024-01-01",
                FullQualifiedTypeName = "System.DateTime"
            };

            scanDef.Operator = BinaryOperator.Equals;
            var result = _controller.GraphScan("since", scanDef);

            // Check if edge is created
            Assert.AreEqual(1, result.Count(), "There should be one edge in the graph");

            var edge = _controller.GetEdge(result.First());
            Assert.AreEqual("knows", edge.Label, "Edge should have the correct label");

            // Check outgoing edges on source vertex
            var outgoingEdges = _controller.GetOutgoingEdges(sourceVertex.Id, "friendship");
            Assert.IsNotNull(outgoingEdges, "Outgoing edges list should not be null");
            Assert.AreEqual(1, outgoingEdges.Count, "There should be one outgoing edge");

            // Check source and target vertex IDs for the edge
            var edgeId = outgoingEdges[0];
            Assert.AreEqual(sourceVertex.Id, _controller.GetSourceVertexForEdge(edgeId), "Edge source should match source vertex");
            Assert.AreEqual(targetVertex.Id, _controller.GetTargetVertexForEdge(edgeId), "Edge target should match target vertex");
        }

        [TestMethod]
        public void CreateIndex_WhenValidDefinitionProvided_ShouldCreateAndReturnTrue()
        {
            // Arrange
            var definition = new PluginSpecification
            {
                UniqueId = "nameIndex",
                PluginType = "DictionaryIndex",
                PluginOptions = null
            };

            // Act
            bool result = _controller.CreateIndex(definition);

            // Assert
            Assert.IsTrue(result, "Index should be created successfully");

            // Verify the index exists by deleting it - DeleteIndex will return true only if the index existed
            bool deletionSuccessful = _controller.DeleteIndex("nameIndex");
            Assert.IsTrue(deletionSuccessful, "Index should exist and be successfully deleted");
        }

        [TestMethod]
        public void DeleteIndex_WhenIndexExists_ShouldDeleteAndReturnTrue()
        {
            // Arrange - First create an index
            var definition = new PluginSpecification
            {
                UniqueId = "testIndex",
                PluginType = "DictionaryIndex",
                PluginOptions = null
            };

            bool created = _controller.CreateIndex(definition);
            Assert.IsTrue(created, "Index should be created successfully for the test");

            // Act
            bool result = _controller.DeleteIndex("testIndex");

            // Assert
            Assert.IsTrue(result, "Index should be deleted successfully");

            // Verify the index no longer exists by trying to delete it again
            bool secondDeletionAttempt = _controller.DeleteIndex("testIndex");
            Assert.IsFalse(secondDeletionAttempt, "Index should no longer exist after deletion");
        }

        [TestMethod]
        public void GetGraph_ShouldReturnGraphWithVerticesAndEdges()
        {
            // Arrange
            // Create two vertices
            var sourceVertexSpec = new VertexSpecification
            {
                Label = "person",
                CreationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds()),
                Properties = new List<PropertySpecification>
                {
                    new PropertySpecification {
                        PropertyId = "name",
                        PropertyValue = "Alice",
                        FullQualifiedTypeName = "System.String"
                    }
                }
            };

            var targetVertexSpec = new VertexSpecification
            {
                Label = "person",
                CreationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds()),
                Properties = new List<PropertySpecification>
                {
                    new PropertySpecification {
                        PropertyId = "name",
                        PropertyValue = "Bob",
                        FullQualifiedTypeName = "System.String"
                    }
                }
            };

            _controller.AddVertex(sourceVertexSpec);
            _controller.AddVertex(targetVertexSpec);

            //we need to wait a bit
            System.Threading.Thread.Sleep(10);

            // Get the created vertices to get their IDs
            var initialGraph = _controller.GetGraph(100);
            var sourceId = initialGraph.Vertices.FirstOrDefault(v =>
                v.Properties.Any(_ => _.PropertyId == "name" && _.PropertyValue == "Alice"));
            var targetId = initialGraph.Vertices.FirstOrDefault(v =>
                v.Properties.Any(_ => _.PropertyId == "name" && _.PropertyValue == "Bob"));

            // Create an edge
            var edgeSpec = new EdgeSpecification
            {
                Label = "knows",
                CreationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds()),
                SourceVertex = sourceId.Id,
                TargetVertex = targetId.Id,
                EdgePropertyId = "friendship",
                Properties = new List<PropertySpecification>
                {
                    new PropertySpecification {
                        PropertyId = "since",
                        PropertyValue = "2024-01-01",
                        FullQualifiedTypeName = "System.DateTime"
                    }
                }
            };
            _controller.AddEdge(edgeSpec);

            //we need to wait a bit
            Thread.Sleep(100);

            // Act
            Graph result = _controller.GetGraph(10);

            // Assert
            Assert.IsNotNull(result, "Graph result should not be null");
            Assert.AreEqual(2, result.Vertices.Count, "Should return correct number of vertices");
            Assert.AreEqual(1, result.Edges.Count, "Should return correct number of edges");
            Assert.AreEqual("knows", result.Edges[0].Label, "Edge should have the correct label");
        }

        [TestMethod]
        [ExpectedException(typeof(WebException))]
        public void GetSourceVertexForEdge_WhenEdgeNotExists_ShouldThrowException()
        {
            // Act - should throw exception for non-existent edge ID
            _controller.GetSourceVertexForEdge(999);
        }

        [TestMethod]
        [ExpectedException(typeof(WebException))]
        public void GetTargetVertexForEdge_WhenEdgeNotExists_ShouldThrowException()
        {
            // Act - should throw exception for non-existent edge ID
            _controller.GetTargetVertexForEdge(999);
        }

        [TestMethod]
        public void GetAllAvailableOutEdgesOnVertex_WhenVertexExists_ShouldReturnEdgePropertyIds()
        {
            // Arrange - Create vertex with outgoing edges
            var sourceVertexSpec = new VertexSpecification
            {
                Label = "person",
                CreationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds()),
                Properties = new List<PropertySpecification>
                {
                    new PropertySpecification {
                        PropertyId = "name",
                        PropertyValue = "Source",
                        FullQualifiedTypeName = "System.String"
                    }
                }
            };

            var targetVertexSpec = new VertexSpecification
            {
                Label = "person",
                CreationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds()),
                Properties = new List<PropertySpecification>
                {
                    new PropertySpecification {
                        PropertyId = "name",
                        PropertyValue = "Target",
                        FullQualifiedTypeName = "System.String"
                    }
                }
            };

            _controller.AddVertex(sourceVertexSpec);
            _controller.AddVertex(targetVertexSpec);

            //we need to wait a bit
            System.Threading.Thread.Sleep(10);

            // Get the created vertices to get their IDs
            var initialGraph = _controller.GetGraph(100);
            var sourceId = initialGraph.Vertices.FirstOrDefault(v =>
                v.Properties.Any(_ => _.PropertyId == "name" && _.PropertyValue == "Source"));
            var targetId = initialGraph.Vertices.FirstOrDefault(v =>
                v.Properties.Any(_ => _.PropertyId == "name" && _.PropertyValue == "Target"));

            // Create edges with different edge property IDs
            var edgeSpec1 = new EdgeSpecification
            {
                Label = "knows",
                CreationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds()),
                SourceVertex = sourceId.Id,
                TargetVertex = targetId.Id,
                EdgePropertyId = "friendship",
                Properties = new List<PropertySpecification>()
            };

            var edgeSpec2 = new EdgeSpecification
            {
                Label = "works_with",
                CreationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds()),
                SourceVertex = sourceId.Id,
                TargetVertex = targetId.Id,
                EdgePropertyId = "professional",
                Properties = new List<PropertySpecification>()
            };

            _controller.AddEdge(edgeSpec1);
            _controller.AddEdge(edgeSpec2);

            //we need to wait a bit
            Thread.Sleep(100);

            // Act
            List<string> result = _controller.GetAllAvailableOutEdgesOnVertex(sourceId.Id);

            // Assert
            Assert.IsNotNull(result, "Result should not be null");
            Assert.AreEqual(2, result.Count, "Should return two edge property IDs");
            CollectionAssert.Contains(result, "friendship", "Should contain friendship edge property");
            CollectionAssert.Contains(result, "professional", "Should contain professional edge property");
        }

        [TestMethod]
        public void GetAllAvailableIncEdgesOnVertex_WhenVertexExists_ShouldReturnEdgePropertyIds()
        {
            // Arrange - Create vertices with incoming edges to target
            var sourceVertexSpec = new VertexSpecification
            {
                Label = "person",
                CreationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds()),
                Properties = new List<PropertySpecification>
                {
                    new PropertySpecification {
                        PropertyId = "name",
                        PropertyValue = "Source",
                        FullQualifiedTypeName = "System.String"
                    }
                }
            };

            var targetVertexSpec = new VertexSpecification
            {
                Label = "person",
                CreationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds()),
                Properties = new List<PropertySpecification>
                {
                    new PropertySpecification {
                        PropertyId = "name",
                        PropertyValue = "Target",
                        FullQualifiedTypeName = "System.String"
                    }
                }
            };

            _controller.AddVertex(sourceVertexSpec);
            _controller.AddVertex(targetVertexSpec);

            //we need to wait a bit
            System.Threading.Thread.Sleep(10);

            // Get the created vertices to get their IDs
            var initialGraph = _controller.GetGraph(100);
            var sourceId = initialGraph.Vertices.FirstOrDefault(v =>
                v.Properties.Any(_ => _.PropertyId == "name" && _.PropertyValue == "Source"));
            var targetId = initialGraph.Vertices.FirstOrDefault(v =>
                v.Properties.Any(_ => _.PropertyId == "name" && _.PropertyValue == "Target"));

            // Create edges with different edge property IDs
            var edgeSpec1 = new EdgeSpecification
            {
                Label = "knows",
                CreationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds()),
                SourceVertex = sourceId.Id,
                TargetVertex = targetId.Id,
                EdgePropertyId = "friendship",
                Properties = new List<PropertySpecification>()
            };

            var edgeSpec2 = new EdgeSpecification
            {
                Label = "works_with",
                CreationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds()),
                SourceVertex = sourceId.Id,
                TargetVertex = targetId.Id,
                EdgePropertyId = "professional",
                Properties = new List<PropertySpecification>()
            };

            _controller.AddEdge(edgeSpec1);
            _controller.AddEdge(edgeSpec2);

            //we need to wait a bit
            Thread.Sleep(100);

            // Act
            List<string> result = _controller.GetAllAvailableIncEdgesOnVertex(targetId.Id);

            // Assert
            Assert.IsNotNull(result, "Result should not be null");
            Assert.AreEqual(2, result.Count, "Should return two edge property IDs");
            CollectionAssert.Contains(result, "friendship", "Should contain friendship edge property");
            CollectionAssert.Contains(result, "professional", "Should contain professional edge property");
        }

        [TestMethod]
        public void GetIncomingEdges_WhenEdgeExists_ShouldReturnEdgeIds()
        {
            // Arrange - Create vertices and edge
            var sourceVertexSpec = new VertexSpecification
            {
                Label = "person",
                CreationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds()),
                Properties = new List<PropertySpecification>
                {
                    new PropertySpecification {
                        PropertyId = "name",
                        PropertyValue = "Source",
                        FullQualifiedTypeName = "System.String"
                    }
                }
            };

            var targetVertexSpec = new VertexSpecification
            {
                Label = "person",
                CreationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds()),
                Properties = new List<PropertySpecification>
                {
                    new PropertySpecification {
                        PropertyId = "name",
                        PropertyValue = "Target",
                        FullQualifiedTypeName = "System.String"
                    }
                }
            };

            _controller.AddVertex(sourceVertexSpec);
            _controller.AddVertex(targetVertexSpec);

            //we need to wait a bit
            System.Threading.Thread.Sleep(10);

            // Get the created vertices to get their IDs
            var initialGraph = _controller.GetGraph(100);
            var sourceId = initialGraph.Vertices.FirstOrDefault(v =>
                v.Properties.Any(_ => _.PropertyId == "name" && _.PropertyValue == "Source"));
            var targetId = initialGraph.Vertices.FirstOrDefault(v =>
                v.Properties.Any(_ => _.PropertyId == "name" && _.PropertyValue == "Target"));

            // Create an edge
            var edgeSpec = new EdgeSpecification
            {
                Label = "knows",
                CreationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds()),
                SourceVertex = sourceId.Id,
                TargetVertex = targetId.Id,
                EdgePropertyId = "friendship",
                Properties = new List<PropertySpecification>()
            };
            _controller.AddEdge(edgeSpec);

            //we need to wait a bit
            Thread.Sleep(100);

            // Act
            var result = _controller.GetIncomingEdges(targetId.Id, "friendship");

            // Assert
            Assert.IsNotNull(result, "Incoming edges list should not be null");
            Assert.AreEqual(1, result.Count, "There should be one incoming edge");

            // Get the edge ID and verify it connects the expected vertices
            var edgeId = result[0];
            Assert.AreEqual(sourceId.Id, _controller.GetSourceVertexForEdge(edgeId), "Edge source should match source vertex");
            Assert.AreEqual(targetId.Id, _controller.GetTargetVertexForEdge(edgeId), "Edge target should match target vertex");
        }
    }
}
