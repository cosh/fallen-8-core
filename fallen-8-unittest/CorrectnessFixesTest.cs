// MIT License
//
// CorrectnessFixesTest.cs
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
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Index.Fulltext;
using NoSQL.GraphDB.Core.Index.Range;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Regression tests for the "correctness-fixes" feature (defects B1-B6).
    /// Each test reproduces a specific latent defect surfaced by the repository review.
    /// </summary>
    [TestClass]
    public class CorrectnessFixesTest
    {
        private ILoggerFactory _loggerFactory;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
        }

        private VertexModel[] CreateVertices(Fallen8 fallen8, int count)
        {
            var tx = new CreateVerticesTransaction();
            for (int i = 0; i < count; i++)
            {
                tx.AddVertex(1, "test", new Dictionary<string, object> { { "idx", i } });
            }
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.GetCreatedVertices().ToArray();
        }

        #region B1 - DictionaryIndex discards the ImmutableList return

        [TestMethod]
        public void DictionaryIndex_WhenAddingMultipleValuesUnderOneKey_ShouldReturnAllOfThem()
        {
            // Arrange
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = CreateVertices(fallen8, 3);
            var index = new DictionaryIndex();
            index.Initialize(fallen8, null);

            // Act
            index.AddOrUpdate("key", vertices[0]);
            index.AddOrUpdate("key", vertices[1]);
            index.AddOrUpdate("key", vertices[2]);

            // Assert
            ImmutableList<AGraphElementModel> result;
            bool found = index.TryGetValue(out result, "key");
            Assert.IsTrue(found, "The key should be present in the index.");
            Assert.AreEqual(3, result.Count, "All three values added under one key must be retained.");
            CollectionAssert.AreEquivalent(
                new AGraphElementModel[] { vertices[0], vertices[1], vertices[2] },
                result.ToList(),
                "The bucket must contain exactly the three added elements.");
        }

        [TestMethod]
        public void DictionaryIndex_WhenRemovingOneValueFromAKey_ShouldKeepTheRest()
        {
            // Arrange
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = CreateVertices(fallen8, 3);
            var index = new DictionaryIndex();
            index.Initialize(fallen8, null);
            index.AddOrUpdate("key", vertices[0]);
            index.AddOrUpdate("key", vertices[1]);
            index.AddOrUpdate("key", vertices[2]);

            // Act
            index.RemoveValue(vertices[1]);

            // Assert
            ImmutableList<AGraphElementModel> result;
            bool found = index.TryGetValue(out result, "key");
            Assert.IsTrue(found, "The key should still be present after removing one of its values.");
            Assert.AreEqual(2, result.Count, "Exactly the removed value should be gone.");
            Assert.IsTrue(result.Contains(vertices[0]), "The first value must remain.");
            Assert.IsTrue(result.Contains(vertices[2]), "The third value must remain.");
            Assert.IsFalse(result.Contains(vertices[1]), "The removed value must be gone.");
        }

        #endregion

        #region B2 - RegExIndex discards the ImmutableList return

        [TestMethod]
        public void RegExIndex_WhenAddingMultipleValuesUnderOneKey_ShouldReturnAllOfThem()
        {
            // Arrange
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = CreateVertices(fallen8, 3);
            var index = new RegExIndex();
            index.Initialize(fallen8, null);

            // Act
            index.AddOrUpdate("the quick brown fox", vertices[0]);
            index.AddOrUpdate("the quick brown fox", vertices[1]);
            index.AddOrUpdate("the quick brown fox", vertices[2]);

            // Assert
            ImmutableList<AGraphElementModel> result;
            bool found = index.TryGetValue(out result, "the quick brown fox");
            Assert.IsTrue(found, "The key should be present in the index.");
            Assert.AreEqual(3, result.Count, "All three values added under one key must be retained.");
            CollectionAssert.AreEquivalent(
                new AGraphElementModel[] { vertices[0], vertices[1], vertices[2] },
                result.ToList(),
                "The bucket must contain exactly the three added elements.");
        }

        [TestMethod]
        public void RegExIndex_WhenRemovingOneValueFromAKey_ShouldKeepTheRest()
        {
            // Arrange
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = CreateVertices(fallen8, 3);
            var index = new RegExIndex();
            index.Initialize(fallen8, null);
            index.AddOrUpdate("the quick brown fox", vertices[0]);
            index.AddOrUpdate("the quick brown fox", vertices[1]);
            index.AddOrUpdate("the quick brown fox", vertices[2]);

            // Act
            index.RemoveValue(vertices[1]);

            // Assert
            ImmutableList<AGraphElementModel> result;
            bool found = index.TryGetValue(out result, "the quick brown fox");
            Assert.IsTrue(found, "The key should still be present after removing one of its values.");
            Assert.AreEqual(2, result.Count, "Exactly the removed value should be gone.");
            Assert.IsTrue(result.Contains(vertices[0]), "The first value must remain.");
            Assert.IsTrue(result.Contains(vertices[2]), "The third value must remain.");
            Assert.IsFalse(result.Contains(vertices[1]), "The removed value must be gone.");
        }

        #endregion

        #region B3 - RangeIndex.Between predicate inverted

        [TestMethod]
        public void RangeIndex_Between_WithLowerBelowUpper_ShouldReturnInRangeElements()
        {
            // Arrange
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = CreateVertices(fallen8, 3);
            var index = new RangeIndex();
            index.Initialize(fallen8, null);
            index.AddOrUpdate(10, vertices[0]);
            index.AddOrUpdate(20, vertices[1]);
            index.AddOrUpdate(30, vertices[2]);

            // Act - inclusive range [15, 25] should catch only key 20
            ImmutableList<AGraphElementModel> result;
            bool found = index.Between(out result, 15, 25, true, true);

            // Assert
            Assert.IsTrue(found, "Between should report success.");
            Assert.AreEqual(1, result.Count, "Only the element at key 20 is inside [15, 25].");
            Assert.AreSame(vertices[1], result[0], "The in-range element must be the one at key 20.");
        }

        [TestMethod]
        public void RangeIndex_Between_InclusiveBounds_ShouldReturnAllElementsInRange()
        {
            // Arrange
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = CreateVertices(fallen8, 3);
            var index = new RangeIndex();
            index.Initialize(fallen8, null);
            index.AddOrUpdate(10, vertices[0]);
            index.AddOrUpdate(20, vertices[1]);
            index.AddOrUpdate(30, vertices[2]);

            // Act - inclusive range [10, 30] should catch all three keys
            ImmutableList<AGraphElementModel> result;
            bool found = index.Between(out result, 10, 30, true, true);

            // Assert
            Assert.IsTrue(found);
            Assert.AreEqual(3, result.Count, "All three elements are inside the inclusive range [10, 30].");
            CollectionAssert.AreEquivalent(
                new AGraphElementModel[] { vertices[0], vertices[1], vertices[2] },
                result.ToList());
        }

        [TestMethod]
        public void RangeIndex_Between_ExclusiveBounds_ShouldHonorBoundaryFlags()
        {
            // Arrange
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = CreateVertices(fallen8, 3);
            var index = new RangeIndex();
            index.Initialize(fallen8, null);
            index.AddOrUpdate(10, vertices[0]);
            index.AddOrUpdate(20, vertices[1]);
            index.AddOrUpdate(30, vertices[2]);

            // Act - exclusive range (10, 30) should catch only key 20
            ImmutableList<AGraphElementModel> result;
            bool found = index.Between(out result, 10, 30, false, false);

            // Assert
            Assert.IsTrue(found);
            Assert.AreEqual(1, result.Count, "With both boundaries excluded only key 20 remains.");
            Assert.AreSame(vertices[1], result[0]);
        }

        [TestMethod]
        public void RangeIndexScan_ViaFallen8_ShouldReturnInRangeElements()
        {
            // Arrange - reach the Between predicate through the public Fallen8 surface.
            var fallen8 = new Fallen8(_loggerFactory);
            var vertices = CreateVertices(fallen8, 3);

            IIndex index;
            Assert.IsTrue(fallen8.IndexFactory.TryCreateIndex(out index, "ageRange", "RangeIndex"),
                "The range index should be created.");
            index.AddOrUpdate(10, vertices[0]);
            index.AddOrUpdate(20, vertices[1]);
            index.AddOrUpdate(30, vertices[2]);

            // Act
            ImmutableList<AGraphElementModel> result;
            bool found = fallen8.RangeIndexScan(out result, "ageRange", 15, 25, true, true);

            // Assert
            Assert.IsTrue(found, "RangeIndexScan should report success.");
            Assert.AreEqual(1, result.Count, "Only the element at key 20 is inside [15, 25].");
            Assert.AreSame(vertices[1], result[0]);
        }

        #endregion
    }
}
