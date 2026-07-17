// MIT License
//
// IVectorIndex.cs
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

namespace NoSQL.GraphDB.Core.Index.Vector
{
    /// <summary>
    ///   A k-nearest-neighbour index over fixed-dimension <c>float[]</c> embedding vectors
    ///   (feature vector-index) - the fourth index family, a sibling of dictionary/range,
    ///   fulltext and spatial.
    ///
    ///   <para>Contract notes: ONE vector per element - <c>AddOrUpdate</c> with an already-indexed
    ///   element REPLACES its vector (kNN over stale duplicates is a wrong answer, so the
    ///   dictionary family's multi-bucket semantics deliberately do not apply). Via the generic
    ///   <c>IIndex.AddOrUpdate</c> an invalid key (not a <c>float[]</c>, wrong dimension,
    ///   zero-norm under Cosine) is logged and skipped - the family's silent-skip contract; the
    ///   typed REST endpoint validates first and answers 400, so over REST the error is never
    ///   silent.</para>
    /// </summary>
    public interface IVectorIndex : IIndex
    {
        /// <summary>The fixed vector dimension, set at creation.</summary>
        Int32 Dimension { get; }

        /// <summary>The metric, set at creation.</summary>
        VectorDistanceMetric Metric { get; }

        /// <summary>
        ///   The element-embedding name this index is BOUND to, or null for an unbound (raw)
        ///   index (feature element-embeddings). A bound index is a pure derived projection of
        ///   the named embedding: the engine's writer thread projects committed embedding writes
        ///   into it, membership is "every live element carrying the named embedding with the
        ///   index's dimension", explicit adds are rejected at the REST boundary, the checkpoint
        ///   persists only the header, and load rebuilds the slab from element embeddings.
        /// </summary>
        String EmbeddingName { get; }

        /// <summary>
        ///   The opaque model-identity string this index's vectors are declared to come from, or
        ///   null. Enforced by the embedding provider (feature embedding-provider); the index
        ///   itself only stores and persists it.
        /// </summary>
        String Model { get; }

        /// <summary>
        ///   The k best-scoring LIVE elements for the query vector, best first, ties by ascending
        ///   element id. Returns false on invalid input: wrong query dimension, k outside
        ///   [1, <c>VectorIndex.MaxK</c>], or a zero-norm query under Cosine.
        /// </summary>
        Boolean TryNearestNeighbors(out VectorSearchResult result, ReadOnlySpan<Single> query,
            Int32 k, VectorSearchConstraint constraint = null);
    }
}
