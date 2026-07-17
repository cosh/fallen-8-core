// MIT License
//
// TraversalContext.cs
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
using NoSQL.GraphDB.Core.Index.Vector;
using NoSQL.GraphDB.Core.Model;

namespace NoSQL.GraphDB.Core.Algorithms
{
    /// <summary>
    ///   Per-request context handed to the delegate FACTORIES of a traversal (feature
    ///   element-embeddings): the query vector - embedded ONCE, before the traversal starts -
    ///   plus the embedding name and metric to score it with. Passed as a factory parameter
    ///   (never instance or ambient state) because compiled traversers are cached process-wide
    ///   keyed on their fragments and serve concurrent requests; the materialized delegates
    ///   close over that request's context. Immutable, so it is safe under the algorithms'
    ///   internal parallelism.
    /// </summary>
    public sealed class TraversalContext
    {
        /// <summary>The context of a traversal without a query vector (every similarity is false).</summary>
        public static readonly TraversalContext Empty = new TraversalContext();

        private readonly Single[] _queryVector;

        /// <summary>The precomputed reserved property id of <see cref="EmbeddingName" /> (hot path).</summary>
        private readonly String _embeddingPropertyId;

        /// <summary>The embedding name scored against.</summary>
        public String EmbeddingName
        {
            get;
        }

        /// <summary>The metric scores are computed under.</summary>
        public VectorDistanceMetric Metric
        {
            get;
        }

        /// <summary>Whether this traversal carries a query vector.</summary>
        public Boolean HasQueryVector => _queryVector != null;

        /// <summary>The query vector (empty without one).</summary>
        public ReadOnlySpan<Single> QueryVector => _queryVector;

        private TraversalContext()
        {
            EmbeddingName = AGraphElementModel.DefaultEmbeddingName;
            Metric = VectorDistanceMetric.Cosine;
            _embeddingPropertyId = null;
        }

        /// <summary>
        ///   Builds a context. The caller (the REST layer) validates the inputs - name grammar,
        ///   finite components - before constructing; the vector reference is captured, not
        ///   copied (single-request lifetime).
        /// </summary>
        public TraversalContext(Single[] queryVector, String embeddingName = AGraphElementModel.DefaultEmbeddingName,
            VectorDistanceMetric metric = VectorDistanceMetric.Cosine)
        {
            _queryVector = queryVector;
            EmbeddingName = embeddingName ?? AGraphElementModel.DefaultEmbeddingName;
            Metric = metric;
            _embeddingPropertyId = AGraphElementModel.GetEmbeddingPropertyId(EmbeddingName);
        }

        /// <summary>
        ///   Scores the element's named embedding against the query vector - the accessor plus
        ///   <see cref="VectorMath.TryScore" /> in one call. <c>false</c> when the traversal has
        ///   no query vector, the element lacks the named embedding, the dimensions differ, or
        ///   the score is non-finite; NaN never reaches a traversal decision.
        /// </summary>
        public Boolean TrySimilarity(AGraphElementModel element, out Single score)
        {
            score = default;

            if (_queryVector == null || element == null)
            {
                return false;
            }

            if (!element.TryGetEmbeddingByPropertyId(out var embedding, _embeddingPropertyId))
            {
                return false;
            }

            return VectorMath.TryScore(out score, _queryVector, embedding, Metric);
        }
    }
}
