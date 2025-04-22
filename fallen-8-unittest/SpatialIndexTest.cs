// MIT License
//
// SpatialIndexTest.cs
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
using System.Collections.Immutable;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Index.Spatial;
using NoSQL.GraphDB.Core.Index.Spatial.Implementation.RTree;
using NoSQL.GraphDB.Core.Index.Spatial.Implementation.SpatialContainer;

namespace NoSQL.GraphDB.Tests
{
    [TestClass]
    public class SpatialIndexTest
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
        public void RTree_WhenCreatingRTreeIndex_ShouldInitializeCorrectly()
        {
            // Arrange & Act
            var spatialIndex = CreateAndInitializeRTree();

            // Assert
            Assert.IsNotNull(spatialIndex);
            Assert.IsInstanceOfType(spatialIndex, typeof(ISpatialIndex));
        }

        [TestMethod]
        public void Distance_BetweenTwoPoints_ShouldCalculateCorrectly()
        {
            // Arrange
            var spatialIndex = CreateAndInitializeRTree();

            var point1 = CreatePointGeometry(0, 0);
            var point2 = CreatePointGeometry(3, 4);

            // Act
            float distance = spatialIndex.Distance(point1, point2);

            // Assert
            Assert.AreEqual(5.0f, distance, 0.001f, "Distance between (0,0) and (3,4) should be 5");
        }

        [TestMethod]
        public void Distance_BetweenTwoGraphElements_ShouldCalculateCorrectly()
        {
            // Arrange
            var spatialIndex = CreateAndInitializeRTree();
            var point1 = CreatePointGeometry(0, 0);
            var point2 = CreatePointGeometry(3, 4);
            var element1 = CreateVertexWithGeometry(1, point1);
            var element2 = CreateVertexWithGeometry(2, point2);

            spatialIndex.AddOrUpdate(point1, element1);
            spatialIndex.AddOrUpdate(point2, element2);

            // Act
            float distance = spatialIndex.Distance(element1, element2);

            // Assert
            Assert.AreEqual(5.0f, distance, 0.001f, "Distance between graph elements at (0,0) and (3,4) should be 5");
        }

        [TestMethod]
        public void SearchRegion_WithPointsInRegion_ShouldReturnCorrectResults()
        {
            // Arrange
            var spatialIndex = CreateAndInitializeRTree();
            InitializeSpatialIndex(spatialIndex);

            // Define search region (MBR)
            float[] lower = new float[] { 1.0f, 1.0f };
            float[] upper = new float[] { 4.0f, 4.0f };
            var mbr = CreateMBR(lower, upper);

            // Act
            ImmutableList<AGraphElementModel> result;
            var found = spatialIndex.SearchRegion(out result, mbr);

            // Assert
            Assert.IsTrue(found, "Search should find points in the region");
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Count > 0, "Should return at least one point in region");
        }

        [TestMethod]
        public void Overlap_WithOverlappingGeometries_ShouldReturnOverlappingElements()
        {
            // Arrange
            var spatialIndex = CreateAndInitializeRTree();
            InitializeSpatialIndex(spatialIndex);

            // Create a geometry that overlaps with existing geometries
            var overlapGeometry = CreateRectangleGeometry(2.0f, 2.0f, 4.0f, 4.0f);

            // Act
            ImmutableList<AGraphElementModel> result;
            var found = spatialIndex.Overlap(out result, overlapGeometry);

            // Assert
            Assert.IsTrue(found, "Should find overlapping geometries");
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Count > 0, "Should return at least one overlapping element");
        }

        [TestMethod]
        public void GetNextNeighbors_ForPoint_ShouldReturnRequestedNumberOfNeighbors()
        {
            // Arrange
            var spatialIndex = CreateAndInitializeRTree();
            InitializeSpatialIndex(spatialIndex);
            var point = CreatePointGeometry(2.5f, 2.5f);
            int neighborCount = 2;

            // Act
            ImmutableList<AGraphElementModel> result;
            var found = spatialIndex.GetNextNeighbors(out result, point, neighborCount);

            // Assert
            Assert.IsTrue(found, "Should find nearest neighbors");
            Assert.IsNotNull(result);
            Assert.AreEqual(neighborCount, result.Count, $"Should return exactly {neighborCount} neighbors");
        }

        [TestMethod]
        public void SearchDistance_ForPoint_ShouldReturnElementsWithinDistance()
        {
            // Arrange
            var spatialIndex = CreateAndInitializeRTree();
            InitializeSpatialIndex(spatialIndex);
            var point = CreatePointGeometry(2.5f, 2.5f);
            float searchDistance = 2.0f;

            // Act
            ImmutableList<AGraphElementModel> result;
            var found = spatialIndex.SearchDistance(out result, searchDistance, point);

            // Assert
            Assert.IsTrue(found, "Should find points within distance");
            Assert.IsNotNull(result);
            Assert.IsTrue(result.Count > 0, "Should return at least one point within distance");
        }

        [TestMethod]
        public void SearchPoint_ExactPointMatch_ShouldReturnElement()
        {
            // Arrange
            var spatialIndex = CreateAndInitializeRTree();
            InitializeSpatialIndex(spatialIndex);
            var point = CreatePoint(2.0f, 2.0f);

            // Act
            ImmutableList<AGraphElementModel> result;
            var found = spatialIndex.SearchPoint(out result, point);

            // Assert
            Assert.IsTrue(found, "Should find exact point match");
            Assert.IsNotNull(result);
        }

        #region Helper Methods

        private ISpatialIndex CreateAndInitializeRTree()
        {
            // Create a new RTree instance
            var rTree = new RTree();

            // Create required parameters for initialization
            var parameters = new Dictionary<string, object>();

            // Add metric for distance calculations
            parameters["IMetric"] = new EuclideanMetric();

            // Set minimum and maximum node counts
            parameters["MinCount"] = 2;
            parameters["MaxCount"] = 5;

            // Create space dimensions for 2D space
            var spaceDimensions = new List<IDimension>
            {
                new NoSQL.GraphDB.Core.Index.Spatial.Implementation.Geometry.RealDimension(),
                new NoSQL.GraphDB.Core.Index.Spatial.Implementation.Geometry.RealDimension(),
            };
            parameters["Space"] = spaceDimensions;

            // Initialize the RTree with the parameters
            rTree.Initialize(null, parameters);

            return rTree;
        }

        private void InitializeSpatialIndex(ISpatialIndex spatialIndex)
        {
            // Only initialize if it's an RTree (our test implementation)
            if (spatialIndex is RTree)
            {
                // Add sample vertices with spatial data to index
                var vertices = new List<AGraphElementModel>
                {
                    CreateVertexWithGeometry(1, CreatePointGeometry(1.0f, 1.0f)),
                    CreateVertexWithGeometry(2, CreatePointGeometry(2.0f, 2.0f)),
                    CreateVertexWithGeometry(3, CreatePointGeometry(3.0f, 3.0f)),
                    CreateVertexWithGeometry(4, CreatePointGeometry(4.0f, 4.0f)),
                    CreateVertexWithGeometry(5, CreateRectangleGeometry(1.5f, 1.5f, 3.5f, 3.5f))
                };

                // Add vertices to the spatial index
                foreach (var vertex in vertices)
                {
                    AddToSpatialIndex(spatialIndex, vertex);
                }
            }
        }

        private void AddToSpatialIndex(ISpatialIndex spatialIndex, AGraphElementModel element)
        {
            // Get the geometry property from the element
            IGeometry geometry;
            if (TryGetGeometry(element, out geometry))
            {
                // Add the element to the index with its geometry
                spatialIndex.AddOrUpdate(geometry, element);
            }
        }

        private bool TryGetGeometry(AGraphElementModel element, out IGeometry geometry)
        {
            // Try to get geometry property using reflection since SetProperty is internal
            object propertyValue;
            var properties = GetPropertiesViaReflection(element);

            if (properties != null && properties.TryGetValue("geometry", out propertyValue) && propertyValue is IGeometry)
            {
                geometry = (IGeometry)propertyValue;
                return true;
            }

            geometry = null;
            return false;
        }

        private ImmutableDictionary<string, object> GetPropertiesViaReflection(AGraphElementModel element)
        {
            // Use reflection to access the private _properties field of AGraphElementModel
            var field = typeof(AGraphElementModel).GetField("_properties",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (field != null)
            {
                return (ImmutableDictionary<string, object>)field.GetValue(element);
            }

            return null;
        }

        // Implementation of IMetric for Euclidean distance calculations
        private class EuclideanMetric : IMetric
        {
            public float Distance(IMBP point1, IMBP point2)
            {
                var sum = 0.0f;
                for (int i = 0; i < point1.Coordinates.Length; i++)
                {
                    var diff = point1.Coordinates[i] - point2.Coordinates[i];
                    sum += diff * diff;
                }
                return (float)Math.Sqrt(sum);
            }

            public float[] TransformationOfDistance(float distance, IMBR mbr)
            {
                // Return uniform transformation in all dimensions for Euclidean distance
                return new float[] { distance, distance };
            }
        }

        private IGeometry CreatePointGeometry(float x, float y)
        {
            // Create a point geometry using a custom implementation
            return CreatePoint(x, y);
        }

        private IPoint CreatePoint(float x, float y)
        {
            // Create a point implementation using a custom implementation
            return new NoSQL.GraphDB.Core.Index.Spatial.Point(x, y);
        }

        private IGeometry CreateRectangleGeometry(float x1, float y1, float x2, float y2)
        {
            // Create a rectangle geometry using a custom implementation
            var lower = new NoSQL.GraphDB.Core.Index.Spatial.Point(x1, y1);
            var upper = new NoSQL.GraphDB.Core.Index.Spatial.Point(x2, y2);
            return new Rectangle(lower, upper);
        }

        private IMBR CreateMBR(float[] lower, float[] upper)
        {
            // Create a MBR (Minimum Bounding Rectangle) implementation using our custom class
            return new MBR(lower, upper);
        }

        private AGraphElementModel CreateVertexWithGeometry(int id, IGeometry geometry)
        {
            // Create a vertex model with spatial geometry
            var vertex = new VertexModel(
                id,
                Convert.ToUInt32(DateTimeOffset.Now.ToUnixTimeSeconds()), // Creation date
                "SpatialVertex", // Label
                new Dictionary<string, object>() // Properties
            );

            // Attach geometry to the vertex
            // This depends on how geometries are attached to graph elements in your implementation
            AttachGeometryToVertex(vertex, geometry);

            return vertex;
        }

        private void AttachGeometryToVertex(VertexModel vertex, IGeometry geometry)
        {
            // Since we don't have direct access to add properties (SetProperty is internal)
            // For testing purposes, we'll use reflection to set the property
            // In a real implementation, you would use the appropriate API calls

            // Get the SetProperty method via reflection
            var method = typeof(AGraphElementModel).GetMethod("SetProperty",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (method != null)
            {
                method.Invoke(vertex, new object[] { "geometry", geometry });
            }
        }

        #endregion
    }
}
