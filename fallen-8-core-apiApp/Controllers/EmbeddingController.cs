// MIT License
//
// EmbeddingController.cs
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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Embedding;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Index.Vector;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.App.Controllers
{
    /// <summary>
    ///   Text-in workflows over the embedding provider (feature embedding-provider):
    ///   embed-to-element (single + batch), semantic search, raw text-to-vector. Every action
    ///   sits behind the EmbeddingProvider capability (403 when
    ///   <c>Fallen8:Embedding:Enabled</c> is off); generation runs on the request thread and
    ///   the result is written through the element-embeddings transaction surface - never on
    ///   the writer thread, never blocking a commit (FR-6/FR-7).
    /// </summary>
    [ApiController]
    [Route("api/v{version:apiVersion}/[controller]")]
    [ApiVersion("0.1")]
    [Authorize(Policy = Fallen8EmbeddingOptions.EmbeddingPolicy)]
    public class EmbeddingController : ControllerBase
    {
        private readonly IFallen8 _fallen8;
        private readonly Fallen8EmbeddingProvider _provider;
        private readonly Fallen8EmbeddingOptions _options;

        public EmbeddingController(IFallen8 fallen8, Fallen8EmbeddingProvider provider,
            Microsoft.Extensions.Options.IOptions<Fallen8EmbeddingOptions> options)
        {
            _fallen8 = fallen8;
            _provider = provider;
            _options = options.Value;
        }

        #region helpers

        /// <summary>Maps provider faults: unavailable backend → 503, contract-violating
        /// output → 502 - both problem+json, matching the repo's explicit-problem style.</summary>
        private static ObjectResult ProviderProblem(Exception ex)
        {
            return ex is EmbeddingProviderUnavailableException
                ? ProblemResults.Create(StatusCodes.Status503ServiceUnavailable, "Embedding provider unavailable", ex.Message)
                : ProblemResults.Create(StatusCodes.Status502BadGateway, "Embedding backend produced invalid output", ex.Message);
        }

        /// <summary>Runs the provider embed call, mapping its two fault types to a problem result via
        /// <see cref="ProviderProblem"/> (the single home for that mapping). Returns the vectors with a
        /// null error on success, or null vectors and the problem result on a provider fault.</summary>
        private async Task<(Single[][] vectors, ActionResult error)> TryEmbedAsync(
            IReadOnlyList<String> texts, CancellationToken cancellationToken)
        {
            try
            {
                return (await _provider.EmbedAsync(texts, cancellationToken), null);
            }
            catch (Exception ex) when (ex is EmbeddingProviderUnavailableException || ex is EmbeddingProviderOutputException)
            {
                return (null, ProviderProblem(ex));
            }
        }

        private String ValidateText(String text, out ActionResult error)
        {
            error = null;
            if (String.IsNullOrWhiteSpace(text))
            {
                error = BadRequest("A non-empty text is required.");
                return null;
            }

            if (text.Length > _options.MaxTextLength)
            {
                error = BadRequest(String.Format("The text ({0} chars) exceeds Fallen8:Embedding:MaxTextLength ({1}).",
                    text.Length, _options.MaxTextLength));
                return null;
            }

            return text;
        }

        /// <summary>The FR-8 consistency checks against every vector index bound to
        /// <paramref name="embeddingName" />: provider dimension must equal the index's, and a
        /// declared index model identity must match the provider's stamp. Hard errors (409).</summary>
        private ActionResult CheckBoundIndexContract(String embeddingName)
        {
            foreach (var namedIndex in _fallen8.IndexFactory.GetNamedIndicesSnapshot())
            {
                if (!(namedIndex.Value is IVectorIndex vectorIndex) ||
                    !String.Equals(vectorIndex.EmbeddingName, embeddingName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (vectorIndex.Dimension != _provider.Identity.Dimension)
                {
                    return Conflict(String.Format(
                        "The provider produces dimension {0}, but index '{1}' bound to embedding '{2}' requires {3}.",
                        _provider.Identity.Dimension, namedIndex.Key, embeddingName, vectorIndex.Dimension));
                }

                if (vectorIndex.Model != null &&
                    !String.Equals(vectorIndex.Model, _provider.Identity.Stamp, StringComparison.Ordinal))
                {
                    return Conflict(String.Format(
                        "Index '{0}' declares model identity '{1}', but the active provider is '{2}'.",
                        namedIndex.Key, vectorIndex.Model, _provider.Identity.Stamp));
                }
            }

            return null;
        }

        #endregion

        /// <summary>
        /// Embeds a text and stores it as the element's named embedding
        /// </summary>
        /// <param name="definition">The element, text and embedding name</param>
        /// <param name="cancellationToken">Aborts the provider call when the request is cancelled</param>
        /// <remarks>
        /// The generated vector is written through the element-embeddings surface together
        /// with the provider's model-identity stamp (one atomic transaction); a vector index
        /// bound to the name updates its projection on commit.
        /// </remarks>
        /// <response code="200">The embedding was generated and committed</response>
        /// <response code="400">Missing/oversized text or an invalid embedding name</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="403">The embedding provider is disabled (Fallen8:Embedding:Enabled)</response>
        /// <response code="404">The graph element does not exist</response>
        /// <response code="409">The provider's dimension or model identity conflicts with a vector index bound to this embedding name</response>
        /// <response code="429">The sensitive-endpoint rate limit was exceeded</response>
        /// <response code="502">The embedding backend produced invalid output</response>
        /// <response code="503">The embedding backend is unavailable (failed to load, or the Ollama sidecar is down)</response>
        [HttpPost("/embedding/element")]
        [EnableRateLimiting(Fallen8SecurityOptions.SensitiveRateLimitPolicy)]
        [RequestSizeLimit(1_048_576)]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<IActionResult> EmbedElement([FromBody] EmbedElementSpecification definition,
            CancellationToken cancellationToken)
        {
            if (definition == null)
            {
                return BadRequest("An embed specification is required.");
            }

            var name = definition.Name ?? AGraphElementModel.DefaultEmbeddingName;
            if (!AGraphElementModel.IsValidEmbeddingName(name))
            {
                return BadRequest(String.Format("'{0}' is not a valid embedding name.", definition.Name));
            }

            ValidateText(definition.Text, out var textError);
            if (textError != null)
            {
                return textError;
            }

            if (!_fallen8.TryGetGraphElement(out _, definition.GraphElementId))
            {
                return NotFound(String.Format("Could not find graph element with id {0}.", definition.GraphElementId));
            }

            var contractError = CheckBoundIndexContract(name);
            if (contractError != null)
            {
                return contractError;
            }

            var (vectors, embedError) = await TryEmbedAsync(new[] { definition.Text }, cancellationToken);
            if (embedError != null)
            {
                return embedError;
            }

            var tx = new SetEmbeddingsTransaction()
                .SetEmbedding(definition.GraphElementId, name, vectors[0], _provider.Identity.Stamp);
            var info = _fallen8.EnqueueTransaction(tx);
            await info.Completion;
            if (info.TransactionState == TransactionState.RolledBack)
            {
                return ProblemResults.Create(StatusCodes.Status500InternalServerError,
                    "Embedding write rolled back", info.FailureReason.ToString());
            }

            return Ok(true);
        }

        /// <summary>
        /// Embeds a batch of texts onto elements - one provider batch, one transaction
        /// </summary>
        /// <param name="definition">The batch (bounded by Fallen8:Embedding:MaxBatchSize)</param>
        /// <param name="cancellationToken">Aborts the provider call when the request is cancelled</param>
        /// <remarks>The bulk-ingestion path: every vector plus its model stamp commits atomically.</remarks>
        /// <response code="200">The batch was generated and committed</response>
        /// <response code="400">Empty/oversized batch, missing text, or an invalid embedding name</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="403">The embedding provider is disabled (Fallen8:Embedding:Enabled)</response>
        /// <response code="404">A referenced graph element does not exist</response>
        /// <response code="409">The provider's dimension or model identity conflicts with a bound vector index</response>
        /// <response code="429">The sensitive-endpoint rate limit was exceeded</response>
        /// <response code="502">The embedding backend produced invalid output</response>
        /// <response code="503">The embedding backend is unavailable</response>
        [HttpPost("/embedding/elements")]
        [EnableRateLimiting(Fallen8SecurityOptions.SensitiveRateLimitPolicy)]
        [RequestSizeLimit(1_048_576)]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<IActionResult> EmbedElements([FromBody] EmbedElementsSpecification definition,
            CancellationToken cancellationToken)
        {
            if (definition?.Items == null || definition.Items.Count == 0)
            {
                return BadRequest("A non-empty batch is required.");
            }

            if (definition.Items.Count > _options.MaxBatchSize)
            {
                return BadRequest(String.Format("The batch ({0} items) exceeds Fallen8:Embedding:MaxBatchSize ({1}).",
                    definition.Items.Count, _options.MaxBatchSize));
            }

            var name = definition.Name ?? AGraphElementModel.DefaultEmbeddingName;
            if (!AGraphElementModel.IsValidEmbeddingName(name))
            {
                return BadRequest(String.Format("'{0}' is not a valid embedding name.", definition.Name));
            }

            foreach (var item in definition.Items)
            {
                if (item == null)
                {
                    return BadRequest("A batch item is null.");
                }

                ValidateText(item.Text, out var textError);
                if (textError != null)
                {
                    return textError;
                }

                if (!_fallen8.TryGetGraphElement(out _, item.GraphElementId))
                {
                    return NotFound(String.Format("Could not find graph element with id {0}.", item.GraphElementId));
                }
            }

            var contractError = CheckBoundIndexContract(name);
            if (contractError != null)
            {
                return contractError;
            }

            var (vectors, embedError) = await TryEmbedAsync(definition.Items.Select(i => i.Text).ToList(), cancellationToken);
            if (embedError != null)
            {
                return embedError;
            }

            var tx = new SetEmbeddingsTransaction();
            for (var i = 0; i < definition.Items.Count; i++)
            {
                tx.SetEmbedding(definition.Items[i].GraphElementId, name, vectors[i], _provider.Identity.Stamp);
            }

            var info = _fallen8.EnqueueTransaction(tx);
            await info.Completion;
            if (info.TransactionState == TransactionState.RolledBack)
            {
                return ProblemResults.Create(StatusCodes.Status500InternalServerError,
                    "Embedding batch rolled back", info.FailureReason.ToString());
            }

            return Ok(true);
        }

        /// <summary>
        /// Semantic search: embeds a query text and runs kNN against a vector index
        /// </summary>
        /// <param name="definition">The index, text, k and optional kind/label constraints</param>
        /// <param name="cancellationToken">Aborts the provider call when the request is cancelled</param>
        /// <remarks>
        /// The text is embedded ONCE (with the configured query prefix); scores and ordering
        /// are exactly those of POST /scan/index/vector for the same vector.
        /// </remarks>
        /// <response code="200">The hits, best first, with raw scores</response>
        /// <response code="400">Missing text, invalid k/kind, or the index is not a vector index</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="403">The embedding provider is disabled (Fallen8:Embedding:Enabled)</response>
        /// <response code="404">No index with the given name exists</response>
        /// <response code="409">The index's dimension or declared model identity conflicts with the active provider</response>
        /// <response code="429">The sensitive-endpoint rate limit was exceeded</response>
        /// <response code="502">The embedding backend produced invalid output</response>
        /// <response code="503">The embedding backend is unavailable</response>
        [HttpPost("/embedding/search")]
        [EnableRateLimiting(Fallen8SecurityOptions.SensitiveRateLimitPolicy)]
        [RequestSizeLimit(1_048_576)]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(VectorSearchResultREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<IActionResult> SemanticSearch([FromBody] EmbeddingSearchSpecification definition,
            CancellationToken cancellationToken)
        {
            if (definition == null)
            {
                return BadRequest("A search specification is required.");
            }

            ValidateText(definition.Text, out var textError);
            if (textError != null)
            {
                return textError;
            }

            if (!_fallen8.IndexFactory.TryGetIndex(out var index, definition.IndexId))
            {
                return NotFound(String.Format("No index named '{0}'.", definition.IndexId));
            }

            if (!(index is IVectorIndex vectorIndex))
            {
                return BadRequest(String.Format("Index '{0}' is not a vector index.", definition.IndexId));
            }

            // FR-8: the identity contract - dimension always, the model identity when the
            // index declares one. Hard errors, never coercion.
            if (vectorIndex.Dimension != _provider.Identity.Dimension)
            {
                return Conflict(String.Format(
                    "The provider produces dimension {0}, but index '{1}' requires {2}.",
                    _provider.Identity.Dimension, definition.IndexId, vectorIndex.Dimension));
            }

            if (vectorIndex.Model != null &&
                !String.Equals(vectorIndex.Model, _provider.Identity.Stamp, StringComparison.Ordinal))
            {
                return Conflict(String.Format(
                    "Index '{0}' declares model identity '{1}', but the active provider is '{2}'.",
                    definition.IndexId, vectorIndex.Model, _provider.Identity.Stamp));
            }

            if (!NoSQL.GraphDB.App.Helper.VectorSearchConstraintBuilder.TryBuild(
                    definition.Kind, definition.Label, out var constraint, out var constraintError))
            {
                return BadRequest(constraintError);
            }

            var (vectors, embedError) = await TryEmbedAsync(new[] { _provider.ApplyQueryPrefix(definition.Text) }, cancellationToken);
            if (embedError != null)
            {
                return embedError;
            }

            if (!vectorIndex.TryNearestNeighbors(out var result, vectors[0], definition.K, constraint))
            {
                return BadRequest(String.Format(
                    "Invalid kNN query: k must be within [1, {0}], and a Cosine query must not be zero-norm.",
                    VectorIndex.MaxK));
            }

            var results = new List<VectorScoredElementREST>(result.Entries.Count);
            foreach (var entry in result.Entries)
            {
                results.Add(new VectorScoredElementREST { GraphElementId = entry.Element.Id, Score = entry.Score });
            }

            return Ok(new VectorSearchResultREST
            {
                Metric = result.Metric.ToString(),
                HigherIsBetter = result.HigherIsBetter,
                Results = results
            });
        }

        /// <summary>
        /// Embeds raw texts and returns the vectors
        /// </summary>
        /// <param name="definition">The texts (bounded by Fallen8:Embedding:MaxBatchSize)</param>
        /// <param name="cancellationToken">Aborts the provider call when the request is cancelled</param>
        /// <remarks>For clients driving the raw vector surfaces themselves (external pipelines,
        /// semantic path queries with a client-held vector, debugging).</remarks>
        /// <response code="200">The vectors, in input order, plus the model identity</response>
        /// <response code="400">Empty/oversized batch or a missing text</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="403">The embedding provider is disabled (Fallen8:Embedding:Enabled)</response>
        /// <response code="429">The sensitive-endpoint rate limit was exceeded</response>
        /// <response code="502">The embedding backend produced invalid output</response>
        /// <response code="503">The embedding backend is unavailable</response>
        [HttpPost("/embedding/text")]
        [EnableRateLimiting(Fallen8SecurityOptions.SensitiveRateLimitPolicy)]
        [RequestSizeLimit(1_048_576)]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(EmbeddingVectorsREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<IActionResult> EmbedText([FromBody] EmbedTextSpecification definition,
            CancellationToken cancellationToken)
        {
            if (definition?.Texts == null || definition.Texts.Count == 0)
            {
                return BadRequest("A non-empty texts list is required.");
            }

            if (definition.Texts.Count > _options.MaxBatchSize)
            {
                return BadRequest(String.Format("The batch ({0} texts) exceeds Fallen8:Embedding:MaxBatchSize ({1}).",
                    definition.Texts.Count, _options.MaxBatchSize));
            }

            foreach (var text in definition.Texts)
            {
                ValidateText(text, out var textError);
                if (textError != null)
                {
                    return textError;
                }
            }

            var (vectors, embedError) = await TryEmbedAsync(definition.Texts, cancellationToken);
            if (embedError != null)
            {
                return embedError;
            }

            return Ok(new EmbeddingVectorsREST
            {
                Model = _provider.Identity.Stamp,
                Dimension = _provider.Identity.Dimension,
                Vectors = vectors.ToList()
            });
        }
    }
}
