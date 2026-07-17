// MIT License
//
// VectorMath.cs
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
using System.Numerics.Tensors;

namespace NoSQL.GraphDB.Core.Index.Vector
{
    /// <summary>
    ///   The ONE similarity/distance implementation shared by index kNN
    ///   (<see cref="VectorIndex.TryNearestNeighbors" />) and in-traversal scoring (feature
    ///   element-embeddings) - both call <see cref="Score" />, so a kNN score and a traversal
    ///   score for the same vector pair are bit-identical by construction, and the metric tests
    ///   pin both consumers at once.
    /// </summary>
    public static class VectorMath
    {
        /// <summary>
        ///   Scores <paramref name="a" /> against <paramref name="b" /> under
        ///   <paramref name="metric" /> via SIMD <c>TensorPrimitives</c>. The spans must be
        ///   equal-length; the result may be non-finite for finite inputs (cosine squared-norm
        ///   underflow, dot-product overflow) - rankings use <see cref="TryScore" /> or check
        ///   <see cref="Single.IsFinite" /> themselves, exactly as the index scan does.
        /// </summary>
        public static Single Score(ReadOnlySpan<Single> a, ReadOnlySpan<Single> b, VectorDistanceMetric metric)
        {
            return metric switch
            {
                VectorDistanceMetric.Cosine => TensorPrimitives.CosineSimilarity(a, b),
                VectorDistanceMetric.DotProduct => TensorPrimitives.Dot(a, b),
                _ => TensorPrimitives.Distance(a, b)
            };
        }

        /// <summary>
        ///   Guarded variant for traversal callers: <c>false</c> on a length mismatch, an empty
        ///   pair, or a non-finite score - NaN never escapes into a traversal decision, mirroring
        ///   the index scan's non-finite-score skip.
        /// </summary>
        public static Boolean TryScore(out Single score, ReadOnlySpan<Single> a, ReadOnlySpan<Single> b,
            VectorDistanceMetric metric)
        {
            if (a.Length == 0 || a.Length != b.Length)
            {
                score = default;
                return false;
            }

            var raw = Score(a, b, metric);
            if (!Single.IsFinite(raw))
            {
                score = default;
                return false;
            }

            score = raw;
            return true;
        }
    }
}
