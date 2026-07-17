// MIT License
//
// SemanticTraversalHelper.cs
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
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core.Algorithms;
using NoSQL.GraphDB.Core.Index.Vector;
using NoSQL.GraphDB.Core.Model;

namespace NoSQL.GraphDB.Core.App.Helper
{
    /// <summary>
    ///   The materialized declarative half of a request's semantic block (feature
    ///   element-embeddings): the traversal context every delegate factory receives, plus the
    ///   code-free filter/cost closures derived from <c>minScore</c>/<c>costBySimilarity</c>.
    /// </summary>
    internal sealed class SemanticTraversal
    {
        internal TraversalContext Context = TraversalContext.Empty;

        /// <summary>The declarative vertex filter (path slot), or null.</summary>
        internal Delegates.VertexFilter VertexFilter;

        /// <summary>The declarative pre-filter (subgraph slot), or null.</summary>
        internal Delegates.GraphElementFilter GraphElementFilter;

        /// <summary>The declarative vertex cost (path slot), or null.</summary>
        internal Delegates.VertexCost VertexCost;
    }

    /// <summary>
    ///   Validates a request's <see cref="SemanticTraversalSpecification" /> and builds the
    ///   <see cref="SemanticTraversal" /> for it. Shared by the path and subgraph endpoints; the
    ///   block is pure data, so nothing here is gated by the dynamic-code capability.
    /// </summary>
    internal static class SemanticTraversalHelper
    {
        /// <summary>
        ///   Builds the semantic traversal. Returns <c>null</c> on success, or a human-readable
        ///   error (the controllers map it to 400).
        /// </summary>
        /// <param name="specification">The request's semantic block; null yields the empty context.</param>
        /// <param name="allowCost">Whether <c>costBySimilarity</c> is meaningful here (path only).</param>
        /// <param name="result">The materialized context + declarative delegates.</param>
        internal static String TryBuild(SemanticTraversalSpecification specification, Boolean allowCost,
            out SemanticTraversal result)
        {
            result = new SemanticTraversal();

            if (specification == null)
            {
                return null;
            }

            if (specification.QueryVector == null || specification.QueryVector.Length == 0)
            {
                return "semantic.queryVector is required.";
            }

            if (specification.QueryVector.Length > VectorIndex.MaxDimension)
            {
                return String.Format("semantic.queryVector exceeds the maximum dimension of {0}.", VectorIndex.MaxDimension);
            }

            if (VectorIndex.HasNonFiniteComponent(specification.QueryVector))
            {
                return "semantic.queryVector contains NaN or Infinity components.";
            }

            var embeddingName = specification.EmbeddingName ?? AGraphElementModel.DefaultEmbeddingName;
            if (!AGraphElementModel.IsValidEmbeddingName(embeddingName))
            {
                return String.Format("'{0}' is not a valid embedding name.", specification.EmbeddingName);
            }

            VectorDistanceMetric metric;
            switch (specification.Metric)
            {
                case null:
                case "Cosine": metric = VectorDistanceMetric.Cosine; break;
                case "DotProduct": metric = VectorDistanceMetric.DotProduct; break;
                case "L2": metric = VectorDistanceMetric.L2; break;
                default:
                    return String.Format("'{0}' is not a valid semantic metric. Expected Cosine, DotProduct or L2.",
                        specification.Metric);
            }

            if (metric == VectorDistanceMetric.Cosine && VectorIndex.IsZeroNorm(specification.QueryVector))
            {
                return "semantic.queryVector must not be zero-norm under Cosine.";
            }

            if (specification.CostBySimilarity && !allowCost)
            {
                return "semantic.costBySimilarity applies to path requests only.";
            }

            if (specification.CostBySimilarity && metric == VectorDistanceMetric.DotProduct)
            {
                return "semantic.costBySimilarity is not available under DotProduct: the dot product is unbounded " +
                       "and sign-indefinite, so it has no honest non-negative cost mapping.";
            }

            var context = new TraversalContext(specification.QueryVector, embeddingName, metric);
            result.Context = context;

            if (specification.MinScore.HasValue)
            {
                var minScore = specification.MinScore.Value;
                var higherIsBetter = metric != VectorDistanceMetric.L2;

                Boolean Passes(AGraphElementModel element)
                {
                    return context.TrySimilarity(element, out var score) &&
                           (higherIsBetter ? score >= minScore : score <= minScore);
                }

                result.VertexFilter = vertex => Passes(vertex);
                result.GraphElementFilter = element => Passes(element);
            }
            else if (specification.CostBySimilarity)
            {
                // A cost is only defined over embedded vertices - stated, not silent: the
                // implied filter drops vertices without the named embedding.
                result.VertexFilter = vertex => context.TrySimilarity(vertex, out _);
                result.GraphElementFilter = element => context.TrySimilarity(element, out _);
            }

            if (specification.CostBySimilarity)
            {
                result.VertexCost = metric == VectorDistanceMetric.Cosine
                    ? vertex => context.TrySimilarity(vertex, out var score) ? 1.0 - score : 1.0
                    : vertex => context.TrySimilarity(vertex, out var score) ? score : Double.MaxValue;
            }

            return null;
        }
    }
}
