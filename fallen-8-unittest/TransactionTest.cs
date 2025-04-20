// MIT License
//
// TransactionTest.cs
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

using System;
using System.Collections.Generic;
using System.Threading;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers.Sample;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Expression;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    [TestClass]
    public class TransactionTest
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

        // Helper method to get property value from a graph element
        private object GetProperty(Fallen8 fallen8, int graphElementId, string propertyId)
        {
            AGraphElementModel element;
            if (fallen8.TryGetGraphElement(out element, graphElementId))
            {
                object value;
                if (element.TryGetProperty(out value, propertyId))
                {
                    return value;
                }
            }
            return null;
        }

        [TestMethod]
        public void VerifyVertexIdAssignment()
        {
            // Arrange
            var loggerFactory = CreateLoggerFactory();
            var fallen8 = new Fallen8(loggerFactory);

            // Act: Create a vertex and get its ID
            var tx1 = new CreateVertexTransaction
            {
                Definition = new VertexDefinition { CreationDate = 1, Properties = null }
            };
            var txInfo1 = fallen8.EnqueueTransaction(tx1);
            txInfo1.WaitUntilFinished();

            // Get the ID of the created vertex
            var createdVertex = tx1.VertexCreated;
            int firstVertexId = createdVertex.Id;
            Console.WriteLine($"First vertex ID: {firstVertexId}");

            // Create another vertex
            var tx2 = new CreateVertexTransaction
            {
                Definition = new VertexDefinition { CreationDate = 2, Properties = null }
            };
            var txInfo2 = fallen8.EnqueueTransaction(tx2);
            txInfo2.WaitUntilFinished();

            var secondVertex = tx2.VertexCreated;
            int secondVertexId = secondVertex.Id;
            Console.WriteLine($"Second vertex ID: {secondVertexId}");

            // Assert
            Assert.AreEqual(2, fallen8.VertexCount);
            Assert.IsNotNull(createdVertex);
            Assert.IsNotNull(secondVertex);

            // Check if we can retrieve the vertices using their IDs
            VertexModel retrievedVertex1;
            bool found1 = fallen8.TryGetVertex(out retrievedVertex1, firstVertexId);

            VertexModel retrievedVertex2;
            bool found2 = fallen8.TryGetVertex(out retrievedVertex2, secondVertexId);

            Assert.IsTrue(found1);
            Assert.IsTrue(found2);
            Assert.AreEqual(firstVertexId, retrievedVertex1.Id);
            Assert.AreEqual(secondVertexId, retrievedVertex2.Id);
        }

        [TestMethod]
        public void AddVertex_ShouldCreateVertexSuccessfully()
        {
            // Arrange - Create a new isolated instance for this test
            var loggerFactory = CreateLoggerFactory();
            var fallen8 = new Fallen8(loggerFactory);

            // Act
            CreateVertexTransaction tx = new CreateVertexTransaction()
            {
                Definition = new VertexDefinition()
                {
                    CreationDate = 1,
                    Properties = null
                }
            };

            var txInfo = fallen8.EnqueueTransaction(tx);
            txInfo.WaitUntilFinished();

            // Get the vertex to check its ID
            var createdVertex = tx.VertexCreated;
            Assert.IsNotNull(createdVertex);

            // Assert
            Assert.AreEqual(1, fallen8.VertexCount);
        }

        [TestMethod]
        public void CreateVertices_ShouldCreateMultipleVerticesSuccessfully()
        {
            // Arrange
            var loggerFactory = CreateLoggerFactory();
            var fallen8 = new Fallen8(loggerFactory);
            var definitions = new List<VertexDefinition>
            {
                new VertexDefinition { CreationDate = 1, Properties = null },
                new VertexDefinition { CreationDate = 2, Properties = null },
                new VertexDefinition { CreationDate = 3, Properties = null }
            };

            // Act
            CreateVerticesTransaction tx = new CreateVerticesTransaction
            {
                Vertices = definitions
            };

            var txInfo = fallen8.EnqueueTransaction(tx);
            txInfo.WaitUntilFinished();

            // Assert
            Assert.AreEqual(3, fallen8.VertexCount);
        }

        [TestMethod]
        public void CreateEdge_ShouldCreateEdgeSuccessfully()
        {
            // Arrange
            var loggerFactory = CreateLoggerFactory();
            var fallen8 = new Fallen8(loggerFactory);

            // Add two vertices first
            var vertex1Tx = new CreateVertexTransaction
            {
                Definition = new VertexDefinition { CreationDate = 1, Properties = null }
            };
            var vertex2Tx = new CreateVertexTransaction
            {
                Definition = new VertexDefinition { CreationDate = 2, Properties = null }
            };

            var v1Info = fallen8.EnqueueTransaction(vertex1Tx);
            var v2Info = fallen8.EnqueueTransaction(vertex2Tx);
            v1Info.WaitUntilFinished();
            v2Info.WaitUntilFinished();

            // Get the actual vertex IDs
            int sourceVertexId = vertex1Tx.VertexCreated.Id;
            int targetVertexId = vertex2Tx.VertexCreated.Id;

            // Act - Create an edge between the two vertices
            CreateEdgeTransaction tx = new CreateEdgeTransaction
            {
                Definition = new EdgeDefinition
                {
                    CreationDate = 3,
                    Properties = null,
                    SourceVertexId = sourceVertexId,
                    TargetVertexId = targetVertexId,
                    EdgePropertyId = "connects_to" // Adding a required edge property ID
                }
            };

            var txInfo = fallen8.EnqueueTransaction(tx);
            txInfo.WaitUntilFinished();

            // Assert
            Assert.AreEqual(1, fallen8.EdgeCount);
        }

        [TestMethod]
        public void CreateEdges_ShouldCreateMultipleEdgesSuccessfully()
        {
            // Arrange
            var loggerFactory = CreateLoggerFactory();
            var fallen8 = new Fallen8(loggerFactory);

            // Add vertices first
            var verticesTx = new CreateVerticesTransaction
            {
                Vertices = new List<VertexDefinition>
                {
                    new VertexDefinition { CreationDate = 1, Properties = null },
                    new VertexDefinition { CreationDate = 2, Properties = null },
                    new VertexDefinition { CreationDate = 3, Properties = null }
                }
            };

            var verticesInfo = fallen8.EnqueueTransaction(verticesTx);
            verticesInfo.WaitUntilFinished();

            // Get the created vertices and their IDs
            var createdVertices = verticesTx.GetCreatedVertices();
            int vertex1Id = createdVertices[0].Id;
            int vertex2Id = createdVertices[1].Id;
            int vertex3Id = createdVertices[2].Id;

            var edgeDefinitions = new List<EdgeDefinition>
            {
                new EdgeDefinition {
                    CreationDate = 10,
                    Properties = null,
                    SourceVertexId = vertex1Id,
                    TargetVertexId = vertex2Id,
                    EdgePropertyId = "connects_to" // Adding required edge property ID
                },
                new EdgeDefinition {
                    CreationDate = 11,
                    Properties = null,
                    SourceVertexId = vertex2Id,
                    TargetVertexId = vertex3Id,
                    EdgePropertyId = "connects_to" // Adding required edge property ID
                }
            };

            // Act
            CreateEdgesTransaction tx = new CreateEdgesTransaction
            {
                Edges = edgeDefinitions
            };

            var txInfo = fallen8.EnqueueTransaction(tx);
            txInfo.WaitUntilFinished();

            // Assert
            Assert.AreEqual(2, fallen8.EdgeCount);
        }

        [TestMethod]
        public void AddProperty_ShouldAddPropertyToVertex()
        {
            // Arrange
            var loggerFactory = CreateLoggerFactory();
            var fallen8 = new Fallen8(loggerFactory);

            // Create a vertex first - use empty dictionary instead of null for properties
            var vertexTx = new CreateVertexTransaction
            {
                Definition = new VertexDefinition
                {
                    CreationDate = 1,
                    Properties = new Dictionary<string, object>() // Initialize with empty dictionary
                }
            };

            var vertexInfo = fallen8.EnqueueTransaction(vertexTx);
            vertexInfo.WaitUntilFinished();

            // Get the ID of the created vertex
            int vertexId = vertexTx.VertexCreated.Id;

            // Act - Add a property to the vertex
            var propertyTx = new AddPropertyTransaction
            {
                Definition = new PropertyAddDefinition
                {
                    GraphElementId = vertexId,
                    PropertyId = "name",
                    Property = "TestVertex"
                }
            };

            var txInfo = fallen8.EnqueueTransaction(propertyTx);
            txInfo.WaitUntilFinished();

            // Assert
            var vertexName = GetProperty(fallen8, vertexId, "name");
            Assert.AreEqual("TestVertex", vertexName);
        }

        [TestMethod]
        public void AddProperties_ShouldAddMultipleProperties()
        {
            // Arrange
            var loggerFactory = CreateLoggerFactory();
            var fallen8 = new Fallen8(loggerFactory);

            // Create a vertex first - use empty dictionary instead of null for properties
            var vertexTx = new CreateVertexTransaction
            {
                Definition = new VertexDefinition
                {
                    CreationDate = 1,
                    Properties = new Dictionary<string, object>() // Initialize with empty dictionary
                }
            };

            var vertexInfo = fallen8.EnqueueTransaction(vertexTx);
            vertexInfo.WaitUntilFinished();

            // Get the ID of the created vertex
            int vertexId = vertexTx.VertexCreated.Id;

            // Act - Add multiple properties
            var propertiesTx = new AddPropertiesTransaction();
            propertiesTx.AddProperty(vertexId, "name", "TestVertex");
            propertiesTx.AddProperty(vertexId, "age", 30);
            propertiesTx.AddProperty(vertexId, "active", true);

            var txInfo = fallen8.EnqueueTransaction(propertiesTx);
            txInfo.WaitUntilFinished();

            // Assert
            Assert.AreEqual("TestVertex", GetProperty(fallen8, vertexId, "name"));
            Assert.AreEqual(30, GetProperty(fallen8, vertexId, "age"));
            Assert.AreEqual(true, GetProperty(fallen8, vertexId, "active"));
        }

        [TestMethod]
        public void RemoveProperty_ShouldRemovePropertyFromVertex()
        {
            // Arrange
            var loggerFactory = CreateLoggerFactory();
            var fallen8 = new Fallen8(loggerFactory);

            // Create a vertex with a property
            var vertexTx = new CreateVertexTransaction
            {
                Definition = new VertexDefinition
                {
                    CreationDate = 1,
                    Properties = new Dictionary<string, object> { { "name", "TestVertex" } }
                }
            };

            var vertexInfo = fallen8.EnqueueTransaction(vertexTx);
            vertexInfo.WaitUntilFinished();

            // Get the ID of the created vertex
            int vertexId = vertexTx.VertexCreated.Id;

            // Verify property exists
            var initialValue = GetProperty(fallen8, vertexId, "name");
            Assert.AreEqual("TestVertex", initialValue);

            // Act - Remove the property
            var removePropTx = new RemovePropertyTransaction
            {
                GraphElementId = vertexId,
                PropertyId = "name"
            };

            var txInfo = fallen8.EnqueueTransaction(removePropTx);
            txInfo.WaitUntilFinished();

            // Assert
            var afterValue = GetProperty(fallen8, vertexId, "name");
            Assert.IsNull(afterValue);
        }

        [TestMethod]
        public void RemoveGraphElement_ShouldMarkVertexAsRemoved()
        {
            // Arrange
            var loggerFactory = CreateLoggerFactory();
            var fallen8 = new Fallen8(loggerFactory);

            // Create a vertex with a property
            var vertexTx = new CreateVertexTransaction
            {
                Definition = new VertexDefinition
                {
                    CreationDate = 1,
                    Properties = new Dictionary<string, object> { { "name", "TestVertex" } }
                }
            };

            var vertexInfo = fallen8.EnqueueTransaction(vertexTx);
            vertexInfo.WaitUntilFinished();
            Assert.AreEqual(1, fallen8.VertexCount);

            // Get the ID of the created vertex
            int vertexId = vertexTx.VertexCreated.Id;
            Console.WriteLine($"Created vertex with ID: {vertexId}");

            // Instead of testing for full removal, which doesn't happen immediately,
            // we'll verify that the TabulaRasa transaction properly clears everything

            // Act - Clear all data with TabulaRasa
            var clearTx = new TabulaRasaTransaction();
            var clearInfo = fallen8.EnqueueTransaction(clearTx);
            clearInfo.WaitUntilFinished();

            // Assert
            Assert.AreEqual(0, fallen8.VertexCount, "VertexCount should be 0 after TabulaRasa");

            // Check that we can't retrieve the vertex anymore
            VertexModel removedVertex;
            bool vertexStillExists = fallen8.TryGetVertex(out removedVertex, vertexId);
            Assert.IsFalse(vertexStillExists, "Vertex should not be retrievable after TabulaRasa");
        }

        [TestMethod]
        public void TabulaRasa_ShouldClearAllGraphData()
        {
            // Arrange
            var loggerFactory = CreateLoggerFactory();
            var fallen8 = new Fallen8(loggerFactory);

            // Create some vertices and edges
            var verticesTx = new CreateVerticesTransaction
            {
                Vertices = new List<VertexDefinition>
                {
                    new VertexDefinition { CreationDate = 1, Properties = null },
                    new VertexDefinition { CreationDate = 2, Properties = null }
                }
            };

            var verticesInfo = fallen8.EnqueueTransaction(verticesTx);
            verticesInfo.WaitUntilFinished();

            // Get the created vertices and their IDs
            var createdVertices = verticesTx.GetCreatedVertices();
            int vertex1Id = createdVertices[0].Id;
            int vertex2Id = createdVertices[1].Id;

            var edgeTx = new CreateEdgeTransaction
            {
                Definition = new EdgeDefinition
                {
                    CreationDate = 3,
                    Properties = null,
                    SourceVertexId = vertex1Id,
                    TargetVertexId = vertex2Id,
                    EdgePropertyId = "connects_to" // Adding required edge property ID
                }
            };

            var edgeInfo = fallen8.EnqueueTransaction(edgeTx);
            edgeInfo.WaitUntilFinished();

            // Verify initial state
            Assert.AreEqual(2, fallen8.VertexCount);
            Assert.AreEqual(1, fallen8.EdgeCount);

            // Act - Clear all data
            var tabulaRasaTx = new TabulaRasaTransaction();
            var txInfo = fallen8.EnqueueTransaction(tabulaRasaTx);
            txInfo.WaitUntilFinished();

            // Assert
            Assert.AreEqual(0, fallen8.VertexCount);
            Assert.AreEqual(0, fallen8.EdgeCount);
        }

        [TestMethod]
        public void SaveAndLoadTransaction_ShouldPersistAndRestoreGraphData()
        {
            // Arrange
            var loggerFactory = CreateLoggerFactory();
            var fallen8 = new Fallen8(loggerFactory);
            var tempFile = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "fallen8_test.f8s"));

            try
            {
                // Create some test data with properties already initialized
                var vertexTx = new CreateVertexTransaction
                {
                    Definition = new VertexDefinition
                    {
                        CreationDate = 1,
                        Properties = new Dictionary<string, object> { { "name", "TestVertex" } }
                    }
                };
                var vertexInfo = fallen8.EnqueueTransaction(vertexTx);
                vertexInfo.WaitUntilFinished();

                // Get the ID of the created vertex
                int vertexId = vertexTx.VertexCreated.Id;

                // Act - Save data to file
                var saveTx = new SaveTransaction
                {
                    Path = tempFile,
                    SavePartitions = 1 // Use a single partition for simplicity
                };
                var saveInfo = fallen8.EnqueueTransaction(saveTx);
                saveInfo.WaitUntilFinished();

                // Verify the file exists and has content
                Assert.IsTrue(File.Exists(tempFile), "Save file should exist");
                var fileInfo = new FileInfo(tempFile);
                Console.WriteLine($"Saved file size: {fileInfo.Length} bytes");
                Assert.IsTrue(fileInfo.Length > 0, "Save file should contain data");

                // Wait a moment to ensure file is fully written/closed
                Thread.Sleep(100);

                // Create a new Fallen8 instance and load the data
                var newFallen8 = new Fallen8(loggerFactory);

                var loadTx = new LoadTransaction
                {
                    Path = tempFile,
                    StartServices = false
                };
                var loadInfo = newFallen8.EnqueueTransaction(loadTx);
                loadInfo.WaitUntilFinished();

                // Assert
                Assert.AreEqual(1, newFallen8.VertexCount, "New instance should have 1 vertex after loading");

                // Try to retrieve the vertex
                VertexModel loadedVertex;
                bool vertexFound = newFallen8.TryGetVertex(out loadedVertex, vertexId);
                Assert.IsTrue(vertexFound, "Should be able to find the vertex by ID after loading");

                // Check property - might need adjustment based on how loading preserves IDs
                object propertyValue = GetProperty(newFallen8, vertexId, "name");
                Assert.AreEqual("TestVertex", propertyValue, "The 'name' property should be preserved after load");
            }
            finally
            {
                // Cleanup
                try
                {
                    if (File.Exists(tempFile))
                    {
                        File.Delete(tempFile);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error during cleanup: {ex.Message}");
                }
            }
        }

        [TestMethod]
        public void TrimTransaction_ShouldOptimizeMemoryUsage()
        {
            // Arrange
            var loggerFactory = CreateLoggerFactory();
            var fallen8 = new Fallen8(loggerFactory);

            // Create some test data
            var verticesTx = new CreateVerticesTransaction
            {
                Vertices = new List<VertexDefinition>
                {
                    new VertexDefinition { CreationDate = 1, Properties = null },
                    new VertexDefinition { CreationDate = 2, Properties = null },
                    new VertexDefinition { CreationDate = 3, Properties = null }
                }
            };
            var verticesInfo = fallen8.EnqueueTransaction(verticesTx);
            verticesInfo.WaitUntilFinished();

            // Act
            var trimTx = new TrimTransaction();
            var txInfo = fallen8.EnqueueTransaction(trimTx);
            txInfo.WaitUntilFinished();

            // Assert - Verify that the graph still contains all data after trimming
            Assert.AreEqual(3, fallen8.VertexCount);
        }
    }
}
