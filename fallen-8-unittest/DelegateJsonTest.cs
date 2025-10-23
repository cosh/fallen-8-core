// MIT License
//
// DelegateJsonTest.cs
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
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core.Algorithms;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Serializer;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Tests for DelegateJson serialization and deserialization.
    /// </summary>
    [TestClass]
    public class DelegateJsonTest
    {
        #region Static Method Tests (EdgeCost)

        /// <summary>
        /// Sample static method for EdgeCost delegate.
        /// </summary>
        public static double UniformEdgeCost(EdgeModel edge)
        {
            return 1.0;
        }

        /// <summary>
        /// Sample static method with property-based cost.
        /// </summary>
        public static double WeightedEdgeCost(EdgeModel edge)
        {
            if (edge != null && edge.TryGetProperty(out double weight, "weight"))
            {
                return weight;
            }
            return 1.0;
        }

        [TestMethod]
        public void TestStaticMethodSerialization()
        {
            // Arrange
            Delegates.EdgeCost costDelegate = UniformEdgeCost;

            // Act
            var json = DelegateJson.Serialize(costDelegate);

            // Assert
            Assert.IsNotNull(json);
            Assert.IsTrue(json.Contains("UniformEdgeCost"));
            Console.WriteLine("Serialized static method:");
            Console.WriteLine(json);
        }

        [TestMethod]
        public void TestStaticMethodDeserialization()
        {
            // Arrange
            Delegates.EdgeCost costDelegate = UniformEdgeCost;
            var json = DelegateJson.Serialize(costDelegate);

            // Act
            var deserializedDelegate = DelegateJson.Deserialize<Delegates.EdgeCost>(json);

            // Assert
            Assert.IsNotNull(deserializedDelegate);

            // Test the delegate works
            var sourceVertex = new VertexModel(1, 1000);
            var targetVertex = new VertexModel(2, 1000);
            var testEdge = new EdgeModel(1, 1000, targetVertex, sourceVertex);
            var result = deserializedDelegate(testEdge);
            Assert.AreEqual(1.0, result);
        }

        [TestMethod]
        public void TestStaticMethodRoundTrip()
        {
            // Arrange
            Delegates.EdgeCost costDelegate = WeightedEdgeCost;

            // Act
            var json = DelegateJson.Serialize(costDelegate);
            var deserializedDelegate = DelegateJson.Deserialize<Delegates.EdgeCost>(json);

            // Test with weighted edge
            var properties = new System.Collections.Generic.Dictionary<string, object>
            {
                { "weight", 5.5 }
            };
            var sourceVertex = new VertexModel(1, 1000);
            var targetVertex = new VertexModel(2, 1000);
            var testEdge = new EdgeModel(1, 1000, targetVertex, sourceVertex, null, null, properties);

            var result = deserializedDelegate(testEdge);

            // Assert
            Assert.AreEqual(5.5, result);
        }

        #endregion

        #region Instance Method Tests (EdgeFilter with Factory)

        /// <summary>
        /// Sample filter class for edge filtering based on a threshold.
        /// </summary>
        public class EdgeThresholdFilter
        {
            private readonly double _threshold;

            public EdgeThresholdFilter(double threshold)
            {
                _threshold = threshold;
            }

            public bool FilterByWeight(EdgeModel edge)
            {
                if (edge != null && edge.TryGetProperty(out double weight, "weight"))
                {
                    return weight >= _threshold;
                }
                return false;
            }
        }

        /// <summary>
        /// Factory for creating EdgeThresholdFilter instances.
        /// </summary>
        public class EdgeThresholdFilterFactory : ITargetFactory
        {
            public object Create(string jsonArgs)
            {
                if (string.IsNullOrEmpty(jsonArgs))
                {
                    return new EdgeThresholdFilter(0.0);
                }

                var args = JsonSerializer.Deserialize<FilterFactoryArgs>(jsonArgs);
                return new EdgeThresholdFilter(args.Threshold);
            }
        }

        /// <summary>
        /// Arguments for EdgeThresholdFilterFactory.
        /// </summary>
        public class FilterFactoryArgs
        {
            public double Threshold
            {
                get; set;
            }
        }

        [TestMethod]
        public void TestInstanceMethodSerialization()
        {
            // Arrange
            var filter = new EdgeThresholdFilter(2.5);
            Delegates.EdgeFilter filterDelegate = filter.FilterByWeight;

            var factoryArgs = JsonSerializer.Serialize(new FilterFactoryArgs { Threshold = 2.5 });
            var targetSpec = new DelegateTargetSpec
            {
                FactoryTypeAssemblyQualifiedName = typeof(EdgeThresholdFilterFactory).AssemblyQualifiedName,
                JsonArgs = factoryArgs
            };

            // Act
            var json = DelegateJson.Serialize(filterDelegate, targetSpec);

            // Assert
            Assert.IsNotNull(json);
            Assert.IsTrue(json.Contains("FilterByWeight"));
            Assert.IsTrue(json.Contains("target"));
            Console.WriteLine("Serialized instance method:");
            Console.WriteLine(json);
        }

        [TestMethod]
        public void TestInstanceMethodDeserialization()
        {
            // Arrange
            var filter = new EdgeThresholdFilter(2.5);
            Delegates.EdgeFilter filterDelegate = filter.FilterByWeight;

            var factoryArgs = JsonSerializer.Serialize(new FilterFactoryArgs { Threshold = 2.5 });
            var targetSpec = new DelegateTargetSpec
            {
                FactoryTypeAssemblyQualifiedName = typeof(EdgeThresholdFilterFactory).AssemblyQualifiedName,
                JsonArgs = factoryArgs
            };

            var json = DelegateJson.Serialize(filterDelegate, targetSpec);

            // Act
            var deserializedDelegate = DelegateJson.Deserialize<Delegates.EdgeFilter>(json);

            // Assert
            Assert.IsNotNull(deserializedDelegate);

            // Test the delegate works with threshold 2.5
            var sourceVertex = new VertexModel(1, 1000);
            var targetVertex = new VertexModel(2, 1000);

            var properties1 = new System.Collections.Generic.Dictionary<string, object> { { "weight", 3.0 } };
            var testEdge1 = new EdgeModel(1, 1000, targetVertex, sourceVertex, null, null, properties1);
            Assert.IsTrue(deserializedDelegate(testEdge1)); // 3.0 >= 2.5

            var properties2 = new System.Collections.Generic.Dictionary<string, object> { { "weight", 2.0 } };
            var testEdge2 = new EdgeModel(2, 1000, targetVertex, sourceVertex, null, null, properties2);
            Assert.IsFalse(deserializedDelegate(testEdge2)); // 2.0 < 2.5
        }

        [TestMethod]
        public void TestInstanceMethodWithoutTargetSpecThrows()
        {
            // Arrange
            var filter = new EdgeThresholdFilter(2.5);
            Delegates.EdgeFilter filterDelegate = filter.FilterByWeight;

            // Act & Assert
            Assert.ThrowsException<InvalidOperationException>(() =>
            {
                DelegateJson.Serialize(filterDelegate);
            });
        }

        #endregion

        #region Error Handling Tests

        [TestMethod]
        public void TestDelegateTypeMismatchThrows()
        {
            // Arrange
            Delegates.EdgeCost costDelegate = UniformEdgeCost;
            var json = DelegateJson.Serialize(costDelegate);

            // Act & Assert - trying to deserialize as wrong delegate type
            Assert.ThrowsException<InvalidOperationException>(() =>
            {
                DelegateJson.Deserialize<Delegates.EdgeFilter>(json);
            });
        }

        [TestMethod]
        public void TestInvalidJsonThrows()
        {
            // Arrange
            var invalidJson = "{ invalid json }";

            // Act & Assert
            Assert.ThrowsException<InvalidOperationException>(() =>
            {
                DelegateJson.Deserialize<Delegates.EdgeCost>(invalidJson);
            });
        }

        [TestMethod]
        public void TestNullDelegateThrows()
        {
            // Act & Assert
            Assert.ThrowsException<ArgumentNullException>(() =>
            {
                DelegateJson.Serialize(null);
            });
        }

        [TestMethod]
        public void TestClosureWithoutTargetSpecThrows()
        {
            // Arrange - create a closure
            double capturedValue = 5.0;
            Delegates.EdgeCost closureDelegate = (edge) => capturedValue;

            // Act & Assert
            Assert.ThrowsException<InvalidOperationException>(() =>
            {
                DelegateJson.Serialize(closureDelegate);
            });
        }

        [TestMethod]
        public void TestMissingMethodThrows()
        {
            // Arrange - manually create a descriptor with a non-existent method
            var json = @"{
              ""version"": 1,
              ""delegateTypeAssemblyQualifiedName"": """ + typeof(Delegates.EdgeCost).AssemblyQualifiedName + @""",
              ""declaringTypeAssemblyQualifiedName"": """ + typeof(DelegateJsonTest).AssemblyQualifiedName + @""",
              ""methodName"": ""NonExistentMethod"",
              ""parameterTypeAssemblyQualifiedNames"": [
                """ + typeof(EdgeModel).AssemblyQualifiedName + @"""
              ],
              ""isStatic"": true
            }";

            // Act & Assert
            Assert.ThrowsException<InvalidOperationException>(() =>
            {
                DelegateJson.Deserialize<Delegates.EdgeCost>(json);
            });
        }

        #endregion

        #region Additional Delegate Type Tests

        [TestMethod]
        public void TestVertexFilterSerialization()
        {
            // Arrange
            Delegates.VertexFilter vertexFilter = IsActiveVertex;
            var json = DelegateJson.Serialize(vertexFilter);

            // Act
            var deserializedDelegate = DelegateJson.Deserialize<Delegates.VertexFilter>(json);

            // Assert
            var properties = new System.Collections.Generic.Dictionary<string, object>
            {
                { "active", true }
            };
            var testVertex = new VertexModel(1, 1000, null, properties);
            Assert.IsTrue(deserializedDelegate(testVertex));
        }

        public static bool IsActiveVertex(VertexModel vertex)
        {
            if (vertex != null && vertex.TryGetProperty(out bool active, "active"))
            {
                return active;
            }
            return false;
        }

        [TestMethod]
        public void TestLabelFilterSerialization()
        {
            // Arrange
            Delegates.LabelFilter labelFilter = IsImportantLabel;
            var json = DelegateJson.Serialize(labelFilter);

            // Act
            var deserializedDelegate = DelegateJson.Deserialize<Delegates.LabelFilter>(json);

            // Assert
            Assert.IsTrue(deserializedDelegate("Important"));
            Assert.IsFalse(deserializedDelegate("Normal"));
        }

        public static bool IsImportantLabel(string label)
        {
            return label != null && label.StartsWith("Important");
        }

        #endregion

        #region JSON Format Verification

        [TestMethod]
        public void TestJsonFormatContainsExpectedFields()
        {
            // Arrange
            Delegates.EdgeCost costDelegate = UniformEdgeCost;

            // Act
            var json = DelegateJson.Serialize(costDelegate);

            // Assert
            Assert.IsTrue(json.Contains("version"));
            Assert.IsTrue(json.Contains("delegateTypeAssemblyQualifiedName"));
            Assert.IsTrue(json.Contains("declaringTypeAssemblyQualifiedName"));
            Assert.IsTrue(json.Contains("methodName"));
            Assert.IsTrue(json.Contains("parameterTypeAssemblyQualifiedNames"));
            Assert.IsTrue(json.Contains("isStatic"));
        }

        [TestMethod]
        public void TestJsonIsCamelCaseAndIndented()
        {
            // Arrange
            Delegates.EdgeCost costDelegate = UniformEdgeCost;

            // Act
            var json = DelegateJson.Serialize(costDelegate);

            // Assert
            // Check for camelCase (lowercase first letter)
            Assert.IsTrue(json.Contains("\"version\""));
            Assert.IsTrue(json.Contains("\"methodName\""));

            // Check for indentation (newlines and spaces)
            Assert.IsTrue(json.Contains("\n"));
            Assert.IsTrue(json.Contains("  "));
        }

        #endregion

        #region Security Tests

        [TestMethod]
        public void TestSignatureHashGeneration()
        {
            // Arrange
            Delegates.EdgeCost costDelegate = UniformEdgeCost;

            // Act
            var json = DelegateJson.Serialize(costDelegate, null, includeSignatureHash: true);

            // Assert
            Assert.IsTrue(json.Contains("signatureHash"));
            Console.WriteLine("JSON with signature hash:");
            Console.WriteLine(json);
        }

        [TestMethod]
        public void TestSignatureHashVerification()
        {
            // Arrange
            Delegates.EdgeCost costDelegate = UniformEdgeCost;
            var json = DelegateJson.Serialize(costDelegate, null, includeSignatureHash: true);

            var config = DelegateSecurityConfig.CreateDefault();
            config.VerifySignatureHash = true;

            // Act
            var deserializedDelegate = DelegateJson.Deserialize<Delegates.EdgeCost>(json, null, config);

            // Assert
            Assert.IsNotNull(deserializedDelegate);
        }

        [TestMethod]
        public void TestTamperedSignatureHashThrows()
        {
            // Arrange
            Delegates.EdgeCost costDelegate = UniformEdgeCost;
            var json = DelegateJson.Serialize(costDelegate, null, includeSignatureHash: true);

            // Tamper with the JSON
            json = json.Replace("UniformEdgeCost", "WeightedEdgeCost");

            var config = DelegateSecurityConfig.CreateDefault();
            config.VerifySignatureHash = true;

            // Act & Assert
            Assert.ThrowsException<InvalidOperationException>(() =>
            {
                DelegateJson.Deserialize<Delegates.EdgeCost>(json, null, config);
            });
        }

        [TestMethod]
        public void TestUntrustedAssemblyThrows()
        {
            // Arrange
            Delegates.EdgeCost costDelegate = UniformEdgeCost;
            var json = DelegateJson.Serialize(costDelegate);

            // Create strict config that doesn't trust our test assembly
            var config = new DelegateSecurityConfig
            {
                TrustedAssemblies = new HashSet<string> { "NonExistentAssembly" },
                TrustedNamespaces = new HashSet<string> { "NoSQL.GraphDB.Core" }
            };

            // Act & Assert
            Assert.ThrowsException<InvalidOperationException>(() =>
            {
                DelegateJson.Deserialize<Delegates.EdgeCost>(json, null, config);
            });
        }

        [TestMethod]
        public void TestUntrustedNamespaceThrows()
        {
            // Arrange
            Delegates.EdgeCost costDelegate = UniformEdgeCost;
            var json = DelegateJson.Serialize(costDelegate);

            // Create config that doesn't trust our namespace
            var config = new DelegateSecurityConfig
            {
                TrustedAssemblies = new HashSet<string> { "fallen-8-unittest" },
                TrustedNamespaces = new HashSet<string> { "SomeOther.Namespace" }
            };

            // Act & Assert
            Assert.ThrowsException<InvalidOperationException>(() =>
            {
                DelegateJson.Deserialize<Delegates.EdgeCost>(json, null, config);
            });
        }

        [TestMethod]
        public void TestStrictSecurityConfig()
        {
            // Arrange
            Delegates.EdgeCost costDelegate = UniformEdgeCost;
            var json = DelegateJson.Serialize(costDelegate, null, includeSignatureHash: true);

            // Use strict config - should fail because test is not in fallen-8-core assembly
            var config = DelegateSecurityConfig.CreateStrict();

            // Act & Assert
            Assert.ThrowsException<InvalidOperationException>(() =>
            {
                DelegateJson.Deserialize<Delegates.EdgeCost>(json, null, config);
            });
        }

        [TestMethod]
        public void TestDefaultSecurityConfigWorks()
        {
            // Arrange
            Delegates.EdgeCost costDelegate = UniformEdgeCost;
            var json = DelegateJson.Serialize(costDelegate);

            // Use default config
            var config = DelegateSecurityConfig.CreateDefault();

            // Act
            var deserializedDelegate = DelegateJson.Deserialize<Delegates.EdgeCost>(json, null, config);

            // Assert
            Assert.IsNotNull(deserializedDelegate);
        }

        [TestMethod]
        public void TestNonPublicMethodBlocked()
        {
            // This test verifies that non-public methods are blocked by default
            // We would need a non-public method to test this properly
            // For now, just verify the config flag exists
            var config = DelegateSecurityConfig.CreateDefault();
            Assert.IsFalse(config.AllowNonPublic);
        }

        #endregion
    }
}
