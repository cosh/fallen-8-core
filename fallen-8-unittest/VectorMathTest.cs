// MIT License
//
// VectorMathTest.cs
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
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Index.Vector;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Feature element-embeddings, Phase 1: the shared similarity primitive - hand-computed
    ///   values per metric, guarded-variant rejection, and the bit-identity pin against the
    ///   index's kNN scores (both consumers are the same VectorMath.Score call).
    /// </summary>
    [TestClass]
    public class VectorMathTest
    {
        private const float Epsilon = 1e-6f;

        [TestMethod]
        public void Score_Cosine_MatchesHandComputedValues()
        {
            // (1,2,2)·(2,-1,2) = 4; |a| = 3, |b| = 3 -> 4/9. Negative components, non-unit norms.
            Assert.AreEqual(4f / 9f, VectorMath.Score(new[] { 1f, 2f, 2f }, new[] { 2f, -1f, 2f }, VectorDistanceMetric.Cosine), Epsilon);
            Assert.AreEqual(1f, VectorMath.Score(new[] { 0.5f, 0.5f }, new[] { 2f, 2f }, VectorDistanceMetric.Cosine), Epsilon);
            Assert.AreEqual(-1f, VectorMath.Score(new[] { 1f, 0f }, new[] { -3f, 0f }, VectorDistanceMetric.Cosine), Epsilon);
        }

        [TestMethod]
        public void Score_DotProduct_MatchesHandComputedValues()
        {
            Assert.AreEqual(4f, VectorMath.Score(new[] { 1f, 2f, 2f }, new[] { 2f, -1f, 2f }, VectorDistanceMetric.DotProduct), Epsilon);
            Assert.AreEqual(-6f, VectorMath.Score(new[] { 1f, -2f }, new[] { 2f, 4f }, VectorDistanceMetric.DotProduct), Epsilon);
        }

        [TestMethod]
        public void Score_L2_MatchesHandComputedValues()
        {
            // (1,2)-(4,6) -> sqrt(9+16) = 5.
            Assert.AreEqual(5f, VectorMath.Score(new[] { 1f, 2f }, new[] { 4f, 6f }, VectorDistanceMetric.L2), Epsilon);
            Assert.AreEqual(0f, VectorMath.Score(new[] { 3f, -3f }, new[] { 3f, -3f }, VectorDistanceMetric.L2), Epsilon);
        }

        [TestMethod]
        public void TryScore_RejectsLengthMismatch_EmptyPairs_AndNonFiniteScores()
        {
            Assert.IsFalse(VectorMath.TryScore(out _, new[] { 1f }, new[] { 1f, 2f }, VectorDistanceMetric.Cosine));
            Assert.IsFalse(VectorMath.TryScore(out _, ReadOnlySpan<float>.Empty, ReadOnlySpan<float>.Empty, VectorDistanceMetric.L2));

            // Cosine of the zero vector is 0/0 = NaN -> guarded false, never a NaN escape.
            Assert.IsFalse(VectorMath.TryScore(out var nan, new[] { 0f, 0f }, new[] { 1f, 2f }, VectorDistanceMetric.Cosine));
            Assert.AreEqual(default, nan);

            // Dot-product overflow to Infinity -> guarded false.
            Assert.IsFalse(VectorMath.TryScore(out _, new[] { float.MaxValue, float.MaxValue },
                new[] { float.MaxValue, float.MaxValue }, VectorDistanceMetric.DotProduct));

            Assert.IsTrue(VectorMath.TryScore(out var fine, new[] { 1f, 2f }, new[] { 4f, 6f }, VectorDistanceMetric.L2));
            Assert.AreEqual(5f, fine, Epsilon);
        }

        [TestMethod]
        public void Score_IsBitIdenticalToIndexKnnScores_PerMetric()
        {
            var candidates = new[]
            {
                new[] { 0.25f, -1.5f, 3f, 0.125f },
                new[] { 2f, 2f, -2f, 1f },
                new[] { -0.75f, 0.1f, 0.9f, -4f }
            };
            var query = new[] { 1.5f, -0.25f, 2f, 0.5f };

            foreach (var metric in new[] { VectorDistanceMetric.Cosine, VectorDistanceMetric.DotProduct, VectorDistanceMetric.L2 })
            {
                using var fallen8 = new Fallen8(TestLoggerFactory.Create());
                var create = new CreateVerticesTransaction();
                for (var i = 0; i < candidates.Length; i++)
                {
                    create.AddVertex(1u, "v");
                }
                fallen8.EnqueueTransaction(create).WaitUntilFinished();
                var vertices = create.GetCreatedVertices();

                Assert.IsTrue(fallen8.IndexFactory.TryCreateIndex(out var index, "knn", "VectorIndex",
                    new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["dimension"] = 4,
                        ["metric"] = metric.ToString()
                    }));
                for (var i = 0; i < candidates.Length; i++)
                {
                    index.AddOrUpdate(candidates[i], vertices[i]);
                }

                Assert.IsTrue(((IVectorIndex)index).TryNearestNeighbors(out var result, query, candidates.Length));

                foreach (var entry in result.Entries)
                {
                    var stored = candidates[vertices.ToList().FindIndex(v => v.Id == entry.Element.Id)];
                    var direct = VectorMath.Score(query, stored, metric);
                    Assert.AreEqual(direct, entry.Score,
                        $"kNN score and VectorMath.Score must be BIT-identical under {metric}");
                }
            }
        }
    }
}
