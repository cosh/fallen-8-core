// MIT License
//
// VectorRankabilityTest.cs
//
// Copyright (c) 2011-2026 Henning Rauch
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

using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core.Index.Vector;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Pins the single-homed rankability predicate (consolidation-audit CA-5):
    ///   <see cref="VectorIndex.Classify"/> is the one definition the live writer projection, the
    ///   projection rebuild, <c>AddOrUpdate</c> and the REST validate path all share. These assert
    ///   each verdict and, crucially, the check-ORDER priority (dimension, then finite, then
    ///   zero-norm) that every call site relies on to report the correct reason when more than one
    ///   condition fails.
    /// </summary>
    [TestClass]
    public class VectorRankabilityTest
    {
        [TestMethod]
        public void Classify_ReturnsOk_ForAFiniteCorrectDimensionNonZeroVector()
        {
            Assert.AreEqual(VectorRankability.Ok,
                VectorIndex.Classify(new[] { 1f, 0f, 2f }, 3, VectorDistanceMetric.Cosine));
        }

        [TestMethod]
        public void Classify_ReturnsWrongDimension_WhenLengthDiffers()
        {
            Assert.AreEqual(VectorRankability.WrongDimension,
                VectorIndex.Classify(new[] { 1f, 2f }, 3, VectorDistanceMetric.Cosine));
            Assert.AreEqual(VectorRankability.WrongDimension,
                VectorIndex.Classify(new[] { 1f, 2f, 3f, 4f }, 3, VectorDistanceMetric.L2));
        }

        [TestMethod]
        public void Classify_ReturnsNonFinite_ForNaNOrInfinity()
        {
            Assert.AreEqual(VectorRankability.NonFinite,
                VectorIndex.Classify(new[] { 1f, float.NaN, 2f }, 3, VectorDistanceMetric.L2));
            Assert.AreEqual(VectorRankability.NonFinite,
                VectorIndex.Classify(new[] { 1f, float.PositiveInfinity, 2f }, 3, VectorDistanceMetric.DotProduct));
        }

        [TestMethod]
        public void Classify_ReturnsZeroNormUnderCosine_OnlyForCosine()
        {
            // A zero-norm vector cannot rank under Cosine (its score is NaN)...
            Assert.AreEqual(VectorRankability.ZeroNormUnderCosine,
                VectorIndex.Classify(new[] { 0f, 0f, 0f }, 3, VectorDistanceMetric.Cosine));

            // ...but is fine under L2 and DotProduct, where a zero vector ranks normally.
            Assert.AreEqual(VectorRankability.Ok,
                VectorIndex.Classify(new[] { 0f, 0f, 0f }, 3, VectorDistanceMetric.L2));
            Assert.AreEqual(VectorRankability.Ok,
                VectorIndex.Classify(new[] { 0f, 0f, 0f }, 3, VectorDistanceMetric.DotProduct));
        }

        [TestMethod]
        public void Classify_PrioritizesDimension_ThenFinite_ThenZeroNorm()
        {
            // Wrong dimension wins over a non-finite component (dimension is checked first, so the
            // finite scan never runs on a mis-sized vector - the order every call site's message
            // depends on).
            Assert.AreEqual(VectorRankability.WrongDimension,
                VectorIndex.Classify(new[] { float.NaN, 1f }, 3, VectorDistanceMetric.Cosine));

            // A non-finite component wins over the (correct-length) zero-norm case: a NaN makes the
            // vector non-finite, and finiteness is checked before the Cosine zero-norm branch.
            Assert.AreEqual(VectorRankability.NonFinite,
                VectorIndex.Classify(new[] { 0f, 0f, float.NaN }, 3, VectorDistanceMetric.Cosine));
        }
    }
}
