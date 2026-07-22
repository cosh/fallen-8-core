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
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Embedding;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.App.Security;
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

        /// <summary>The declarative vertex filter (path filter slot / subgraph pre-filter slot), or null.</summary>
        internal Delegates.VertexFilter VertexFilter;

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
        ///   Resolves a semantic block's <c>queryText</c> into its <c>queryVector</c> (feature
        ///   embedding-provider): capability-gated like the embedding endpoints (403 via the
        ///   authorization service; a null service - direct unit construction - bypasses the
        ///   gate), embedded ONCE with the provider's query prefix, before the traversal
        ///   starts. Returns <c>null</c> on success (including "nothing to resolve"), or the
        ///   ActionResult to short-circuit with.
        /// </summary>
        internal static async Task<ActionResult> TryResolveQueryTextAsync(SemanticTraversalSpecification specification,
            Fallen8EmbeddingProvider provider, IAuthorizationService authorizationService, ClaimsPrincipal user,
            CancellationToken cancellationToken)
        {
            if (specification == null || String.IsNullOrWhiteSpace(specification.QueryText))
            {
                return null;
            }

            if (specification.QueryVector != null)
            {
                return new BadRequestObjectResult("semantic.queryText and semantic.queryVector are mutually exclusive.");
            }

            if (authorizationService != null)
            {
                var authorization = await authorizationService.AuthorizeAsync(user, null,
                    new DynamicCapabilityRequirement(DynamicCapabilityRequirement.Capability.EmbeddingProvider));
                if (!authorization.Succeeded)
                {
                    return new ForbidResult();
                }
            }

            if (provider == null || !provider.IsEnabled)
            {
                return ProblemResults.Create(StatusCodes.Status503ServiceUnavailable,
                    "Embedding provider unavailable", "semantic.queryText requires the embedding provider (Fallen8:Embedding).");
            }

            try
            {
                var vectors = await provider.EmbedAsync(
                    new[] { provider.ApplyQueryPrefix(specification.QueryText) }, cancellationToken);
                specification.QueryVector = vectors[0];
                return null;
            }
            catch (EmbeddingProviderUnavailableException ex)
            {
                return ProblemResults.Create(StatusCodes.Status503ServiceUnavailable, "Embedding provider unavailable", ex.Message);
            }
            catch (EmbeddingProviderOutputException ex)
            {
                return ProblemResults.Create(StatusCodes.Status502BadGateway, "Embedding backend produced invalid output", ex.Message);
            }
        }

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
            }
            else if (specification.CostBySimilarity)
            {
                // A cost is only defined over embedded vertices - stated, not silent: the
                // implied filter drops vertices without the named embedding.
                Boolean HasEmbedding(AGraphElementModel element) => context.TrySimilarity(element, out _);

                result.VertexFilter = vertex => HasEmbedding(vertex);
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
