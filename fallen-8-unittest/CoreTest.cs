// MIT License
//
// CoreTest.cs
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
using System.Collections.Immutable;
using System.IO;
using System.Linq;
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
    public class CoreTest
    {
        private ILoggerFactory _loggerFactory;

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
        }

        [TestMethod]
        public void GraphScan_WhenSearchingForAlice_ShouldReturnSingleResult()
        {
            // Arrange
            var fallen8 = new Fallen8(_loggerFactory);
            TestGraphGenerator.GenerateSampleGraph(fallen8);

            // Act
            List<AGraphElementModel> result;
            fallen8.GraphScan(out result, "name", "Alice", BinaryOperator.Equals);

            // Assert
            Assert.IsNotNull(result);
            Assert.AreEqual(1, result.Count);

            VertexModel alice = (VertexModel)result[0];
            String name;
            alice.TryGetProperty(out name, "name");
            Assert.AreEqual("Alice", name);
        }

        [TestMethod]
        public void SaveAndLoad_WhenSavingAndLoadingGraph_ShouldMaintainData()
        {
            // Arrange
            var fallen8 = new Fallen8(_loggerFactory);
            TestGraphGenerator.GenerateSampleGraph(fallen8);
            var saveGameName = @"SaveAndLoadTest.f8";
            var saveGameDirectory = @".";
            var saveGameLocation = Path.Combine(saveGameDirectory, saveGameName);
            string actualPath = null;

            try
            {
                // Clean up any existing files before starting
                CleanupSavegames(saveGameDirectory, saveGameName.Split('.')[0]);

                // Act - Save the database
                SaveTransaction saveTx = new SaveTransaction() { Path = saveGameLocation, SavePartitions = 1 };
                fallen8.EnqueueTransaction(saveTx).WaitUntilFinished();
                actualPath = saveTx.ActualPath;

                // Clear the database
                TabulaRasaTransaction tx = new TabulaRasaTransaction();
                fallen8.EnqueueTransaction(tx).WaitUntilFinished();

                // Verify database is empty
                Assert.AreEqual(0, fallen8.VertexCount, "Database should be empty after TabulaRasa");

                // Load the database back
                LoadTransaction loadTransaction = new LoadTransaction() { Path = saveGameLocation };
                fallen8.EnqueueTransaction(loadTransaction).WaitUntilFinished();

                // Assert - Verify data was properly loaded
                List<AGraphElementModel> result;
                fallen8.GraphScan(out result, "name", "Alice", BinaryOperator.Equals);

                Assert.IsNotNull(result);
                Assert.AreEqual(1, result.Count, "Alice should be found after loading");

                VertexModel alice = (VertexModel)result[0];
                String name;
                alice.TryGetProperty(out name, "name");
                Assert.AreEqual("Alice", name);
            }
            finally
            {
                // Cleanup in a finally block to ensure files are deleted even if test fails
                if (actualPath != null)
                {
                    CleanupSavegames(saveGameDirectory, actualPath);
                }
            }
        }

        [TestMethod]
        public void CreateVertex_WhenAddingNewVertex_ShouldStoreAndRetrieveCorrectly()
        {
            // Arrange
            var fallen8 = new Fallen8(_loggerFactory);
            var vertexCreationDate = DateTimeOffset.Now.ToUnixTimeSeconds();
            var vertexLabel = "testPerson";
            var vertexProps = new Dictionary<string, object> { { "name", "John" }, { "age", 30 } };

            // Act - Create vertex
            CreateVertexTransaction vertexTx = new CreateVertexTransaction()
            {
                Definition = new VertexDefinition()
                {
                    CreationDate = Convert.ToUInt32(vertexCreationDate),
                    Label = vertexLabel,
                    Properties = vertexProps
                }
            };

            fallen8.EnqueueTransaction(vertexTx).WaitUntilFinished();

            // Retrieve the created vertex
            List<AGraphElementModel> result;
            fallen8.GraphScan(out result, "name", "John", BinaryOperator.Equals);

            // Assert
            Assert.IsNotNull(result);
            Assert.AreEqual(1, result.Count);
            Assert.IsInstanceOfType(result[0], typeof(VertexModel));

            VertexModel johnVertex = (VertexModel)result[0];

            // Check properties
            string name;
            johnVertex.TryGetProperty(out name, "name");
            Assert.AreEqual("John", name);

            int age;
            johnVertex.TryGetProperty(out age, "age");
            Assert.AreEqual(30, age);

            // Check label
            Assert.AreEqual(vertexLabel, johnVertex.Label);
        }

        [TestMethod]
        public void CreateEdge_WhenConnectingVertices_ShouldCreateRetrievableRelationship()
        {
            // Arrange
            var fallen8 = new Fallen8(_loggerFactory);
            var creationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds());

            // Source vertex
            CreateVertexTransaction sourceTx = new CreateVertexTransaction()
            {
                Definition = new VertexDefinition()
                {
                    CreationDate = creationDate,
                    Label = "person",
                    Properties = new Dictionary<string, object> { { "name", "Source" } }
                }
            };
            var sourceInfo = fallen8.EnqueueTransaction(sourceTx);
            sourceInfo.WaitUntilFinished();
            int sourceId = sourceTx.VertexCreated.Id;

            // Target vertex
            CreateVertexTransaction targetTx = new CreateVertexTransaction()
            {
                Definition = new VertexDefinition()
                {
                    CreationDate = creationDate,
                    Label = "person",
                    Properties = new Dictionary<string, object> { { "name", "Target" } }
                }
            };
            var targetInfo = fallen8.EnqueueTransaction(targetTx);
            targetInfo.WaitUntilFinished();
            int targetId = targetTx.VertexCreated.Id;

            // Act - Create edge between them
            CreateEdgeTransaction edgeTx = new CreateEdgeTransaction()
            {
                Definition = new EdgeDefinition()
                {
                    CreationDate = creationDate,
                    Label = "knows",
                    Properties = new Dictionary<string, object> { { "since", 2025 } },
                    EdgePropertyId = "friend",
                    SourceVertexId = sourceId,
                    TargetVertexId = targetId
                }
            };
            var edgeInfo = fallen8.EnqueueTransaction(edgeTx);
            edgeInfo.WaitUntilFinished();

            // Assert - Verify edge exists
            VertexModel sourceVertex;
            bool found = fallen8.TryGetVertex(out sourceVertex, sourceId);

            Assert.IsTrue(found);
            Assert.IsNotNull(sourceVertex.OutEdges);
            Assert.AreEqual(1, sourceVertex.OutEdges.Count);  // Should have one edge property type
            Assert.IsTrue(sourceVertex.OutEdges.ContainsKey("friend"));  // Should have the "friend" property

            // Check the edge properties
            var edges = sourceVertex.OutEdges["friend"];
            Assert.AreEqual(1, edges.Count);

            var edge = edges[0];
            Assert.AreEqual("knows", edge.Label);
            Assert.AreEqual(sourceId, edge.SourceVertex.Id);
            Assert.AreEqual(targetId, edge.TargetVertex.Id);

            // Check that the property exists
            int since;
            bool hasProperty = edge.TryGetProperty(out since, "since");
            Assert.IsTrue(hasProperty);
            Assert.AreEqual(2025, since);
        }

        [TestMethod]
        public void VertexProperties_WhenAddingUpdatingRemoving_ShouldPerformCorrectOperations()
        {
            // Arrange
            var fallen8 = new Fallen8(_loggerFactory);
            CreateVertexTransaction vertexTx = new CreateVertexTransaction()
            {
                Definition = new VertexDefinition()
                {
                    CreationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds()),
                    Label = "propertyTest",
                    Properties = new Dictionary<string, object> { { "initial", "value" } }
                }
            };
            var txInfo = fallen8.EnqueueTransaction(vertexTx);
            txInfo.WaitUntilFinished();
            int vertexId = vertexTx.VertexCreated.Id;

            // Act - Add new property
            AddPropertyTransaction addPropertyTx = new AddPropertyTransaction()
            {
                Definition = new PropertyAddDefinition()
                {
                    GraphElementId = vertexId,
                    PropertyId = "added",
                    Property = "newValue"
                }
            };
            fallen8.EnqueueTransaction(addPropertyTx).WaitUntilFinished();

            // Remove existing property
            RemovePropertyTransaction removeBeforeUpdateTx = new RemovePropertyTransaction()
            {
                GraphElementId = vertexId,
                PropertyId = "initial"
            };
            fallen8.EnqueueTransaction(removeBeforeUpdateTx).WaitUntilFinished();

            // Update property (add it back with new value)
            AddPropertyTransaction updatePropertyTx = new AddPropertyTransaction()
            {
                Definition = new PropertyAddDefinition()
                {
                    GraphElementId = vertexId,
                    PropertyId = "initial",
                    Property = "updatedValue"
                }
            };
            fallen8.EnqueueTransaction(updatePropertyTx).WaitUntilFinished();

            // Assert - Verify properties were added and updated correctly
            VertexModel vertex;
            fallen8.TryGetVertex(out vertex, vertexId);

            string addedValue;
            bool hasAdded = vertex.TryGetProperty(out addedValue, "added");
            Assert.IsTrue(hasAdded, "Added property should exist");
            Assert.AreEqual("newValue", addedValue, "Added property should have correct value");

            string updatedValue;
            bool hasUpdated = vertex.TryGetProperty(out updatedValue, "initial");
            Assert.IsTrue(hasUpdated, "Updated property should exist");
            Assert.AreEqual("updatedValue", updatedValue, "Updated property should have new value");

            // Act - Remove a property
            RemovePropertyTransaction removePropertyTx = new RemovePropertyTransaction()
            {
                GraphElementId = vertexId,
                PropertyId = "added"
            };
            fallen8.EnqueueTransaction(removePropertyTx).WaitUntilFinished();

            // Assert - Verify property was removed
            fallen8.TryGetVertex(out vertex, vertexId);
            string shouldNotExist;
            bool propertyExists = vertex.TryGetProperty(out shouldNotExist, "added");
            Assert.IsFalse(propertyExists, "Removed property should no longer exist");
        }

        [TestMethod]
        public void GraphScan_WithDifferentOperators_ShouldReturnCorrectResults()
        {
            // Arrange
            var fallen8 = new Fallen8(_loggerFactory);
            var creationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds());

            var ageVertices = new List<int>();
            for (int i = 20; i <= 50; i += 10)
            {
                var vertexTx = new CreateVertexTransaction()
                {
                    Definition = new VertexDefinition()
                    {
                        CreationDate = creationDate,
                        Label = "person",
                        Properties = new Dictionary<string, object>
                        {
                            { "name", $"Person{i}" },
                            { "age", i }
                        }
                    }
                };
                var txInfo = fallen8.EnqueueTransaction(vertexTx);
                txInfo.WaitUntilFinished();
                ageVertices.Add(vertexTx.VertexCreated.Id);
            }

            // Act & Assert - Test Equals operation
            List<AGraphElementModel> equalsResult;
            fallen8.GraphScan(out equalsResult, "age", 30, BinaryOperator.Equals);
            Assert.AreEqual(1, equalsResult.Count, "Should find exactly one vertex with age=30");
            int foundAge;
            ((VertexModel)equalsResult[0]).TryGetProperty(out foundAge, "age");
            Assert.AreEqual(30, foundAge, "Found vertex should have age=30");

            // Test Greater operation
            List<AGraphElementModel> greaterResult;
            fallen8.GraphScan(out greaterResult, "age", 30, BinaryOperator.Greater);
            Assert.AreEqual(2, greaterResult.Count, "Should find exactly two vertices with age>30");

            // Test LowerOrEquals operation
            List<AGraphElementModel> lowerOrEqualsResult;
            fallen8.GraphScan(out lowerOrEqualsResult, "age", 30, BinaryOperator.LowerOrEquals);
            Assert.AreEqual(2, lowerOrEqualsResult.Count, "Should find exactly two vertices with age<=30");
        }

        [TestMethod]
        public void RemoveGraphElement_WhenDeletingVertex_ShouldNoLongerBeRetrievable()
        {
            // Arrange
            var fallen8 = new Fallen8(_loggerFactory);
            CreateVertexTransaction vertexTx = new CreateVertexTransaction()
            {
                Definition = new VertexDefinition()
                {
                    CreationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds()),
                    Label = "toBeRemoved",
                    Properties = new Dictionary<string, object> { { "name", "RemoveMe" } }
                }
            };
            var txInfo = fallen8.EnqueueTransaction(vertexTx);
            txInfo.WaitUntilFinished();
            int vertexId = vertexTx.VertexCreated.Id;

            VertexModel beforeRemoval;
            bool existsBefore = fallen8.TryGetVertex(out beforeRemoval, vertexId);
            Assert.IsTrue(existsBefore, "Vertex should exist before removal");

            // Act
            RemoveGraphElementTransaction removeTx = new RemoveGraphElementTransaction()
            {
                GraphElementId = vertexId
            };
            fallen8.EnqueueTransaction(removeTx).WaitUntilFinished();

            // Assert
            List<AGraphElementModel> searchResult;
            fallen8.GraphScan(out searchResult, "name", "RemoveMe", BinaryOperator.Equals);
            Assert.AreEqual(0, searchResult.Count, "Vertex should not be found after removal");
        }

        [TestMethod]
        public void TabulaRasa_WhenClearingDatabase_ShouldRemoveAllGraphElements()
        {
            // Arrange
            var fallen8 = new Fallen8(_loggerFactory);
            TestGraphGenerator.GenerateSampleGraph(fallen8);
            var verticesBefore = fallen8.GetAllVertices().Count;
            var edgesBefore = fallen8.GetAllEdges().Count;

            Assert.IsTrue(verticesBefore > 0, "Should have vertices before TabulaRasa");

            // Act
            TabulaRasaTransaction tx = new TabulaRasaTransaction();
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();

            // Assert
            var verticesAfter = fallen8.GetAllVertices().Count;
            var edgesAfter = fallen8.GetAllEdges().Count;

            Assert.AreEqual(0, verticesAfter, "Should have no vertices after TabulaRasa");
            Assert.AreEqual(0, edgesAfter, "Should have no edges after TabulaRasa");

            // Restore test data is not needed since we're using TestInitialize/TestCleanup
        }

        [TestMethod]
        public void BulkOperations_WhenAddingMultipleVerticesAndEdges_ShouldCreateAllCorrectly()
        {
            // Arrange
            var fallen8 = new Fallen8(_loggerFactory);
            var creationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds());
            var verticesTransaction = new CreateVerticesTransaction();

            for (int i = 1; i <= 5; i++)
            {
                verticesTransaction.AddVertex(creationDate, "bulkPerson",
                    new Dictionary<string, object> { { "name", $"BulkPerson{i}" } });
            }

            // Act - Create vertices in bulk
            var txInfo = fallen8.EnqueueTransaction(verticesTransaction);
            txInfo.WaitUntilFinished();

            var createdVertices = verticesTransaction.GetCreatedVertices();

            // Create edges in bulk, connecting first vertex to all others
            var edgesTransaction = new CreateEdgesTransaction();

            for (int i = 1; i < createdVertices.Count; i++)
            {
                edgesTransaction.AddEdge(
                    createdVertices[0].Id,
                    "knows",
                    createdVertices[i].Id,
                    creationDate,
                    "knows",
                    new Dictionary<string, object> { { "weight", i } }
                );
            }

            var edgesTxInfo = fallen8.EnqueueTransaction(edgesTransaction);
            edgesTxInfo.WaitUntilFinished();

            // Assert
            Assert.AreEqual(5, createdVertices.Count, "Should have created 5 vertices");

            var firstPerson = createdVertices[0];
            Assert.IsTrue(firstPerson.OutEdges.ContainsKey("knows"), "First person should have outgoing 'knows' edges");

            var outEdges = firstPerson.OutEdges["knows"];
            Assert.AreEqual(4, outEdges.Count, "First person should have 4 outgoing edges");

            // Verify edge weights sum correctly (1+2+3+4=10)
            int weightSum = 0;
            foreach (var edge in outEdges)
            {
                int weight;
                edge.TryGetProperty(out weight, "weight");
                weightSum += weight;
            }
            Assert.AreEqual(10, weightSum, "Sum of edge weights should be 10");
        }

        [TestMethod]
        public void AddProperties_WhenUsingBulkPropertyAddition_ShouldAddAllPropertiesCorrectly()
        {
            // Arrange
            var fallen8 = new Fallen8(_loggerFactory);
            CreateVertexTransaction vertexTx = new CreateVertexTransaction()
            {
                Definition = new VertexDefinition()
                {
                    CreationDate = Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds()),
                    Label = "multiProp",
                    Properties = new Dictionary<string, object>()
                }
            };
            var txInfo = fallen8.EnqueueTransaction(vertexTx);
            txInfo.WaitUntilFinished();
            int vertexId = vertexTx.VertexCreated.Id;

            // Act - Add multiple properties in single transaction
            AddPropertiesTransaction propsTx = new AddPropertiesTransaction();
            propsTx.AddProperty(vertexId, "prop1", "value1");
            propsTx.AddProperty(vertexId, "prop2", 42);
            propsTx.AddProperty(vertexId, "prop3", true);

            fallen8.EnqueueTransaction(propsTx).WaitUntilFinished();

            // Assert - Verify all properties were added correctly
            VertexModel vertex;
            fallen8.TryGetVertex(out vertex, vertexId);

            string prop1;
            int prop2;
            bool prop3;

            bool hasProp1 = vertex.TryGetProperty(out prop1, "prop1");
            bool hasProp2 = vertex.TryGetProperty(out prop2, "prop2");
            bool hasProp3 = vertex.TryGetProperty(out prop3, "prop3");

            Assert.IsTrue(hasProp1, "Property prop1 should exist");
            Assert.IsTrue(hasProp2, "Property prop2 should exist");
            Assert.IsTrue(hasProp3, "Property prop3 should exist");

            Assert.AreEqual("value1", prop1, "String property value should match");
            Assert.AreEqual(42, prop2, "Integer property value should match");
            Assert.IsTrue(prop3, "Boolean property value should match");
        }

        private static void CleanupSavegames(String saveGameDirectory, String actualSaveGameLocation)
        {
            var toBeDeletedSaveGame = Path.GetFileName(actualSaveGameLocation) + "*";
            var files = Directory.GetFiles(saveGameDirectory, toBeDeletedSaveGame);

            foreach (var aToBeDeletedFile in files)
            {
                File.Delete(aToBeDeletedFile);
            }
        }
    }
}
