// MIT License
//
// DocumentController.cs
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.App.Ingestion;

namespace NoSQL.GraphDB.App.Controllers
{
    /// <summary>
    ///   Unstructured ingestion (feature unstructured-ingestion): documents in, graph out.
    ///   Uploads become one Document vertex plus Chunk vertices with embedded text; every
    ///   action sits behind the Ingestion capability (403 while
    ///   <c>Fallen8:Ingestion:Enabled</c> is off). Binary formats convert in the docling
    ///   sidecar; txt/md ingest without it.
    /// </summary>
    [ApiController]
    [Route("api/v{version:apiVersion}/[controller]")]
    [ApiVersion("0.1")]
    [Authorize(Policy = Fallen8IngestionOptions.IngestionPolicy)]
    public class DocumentController : ControllerBase
    {
        /// <summary>The transport-level body bound; the configured cap
        /// (Fallen8:Ingestion:MaxUploadBytes) is enforced per request below it.</summary>
        private const Int64 TransportUploadLimit = 536_870_912;

        private readonly DocumentIngestionService _service;
        private readonly DocumentSearchService _search;
        private readonly Fallen8IngestionOptions _options;

        public DocumentController(DocumentIngestionService service, DocumentSearchService search,
            IOptions<Fallen8IngestionOptions> options)
        {
            _service = service;
            _search = search;
            _options = options.Value;
        }

        /// <summary>
        /// Ingests a document file into the graph
        /// </summary>
        /// <param name="file">The document (.pdf/.docx/.xlsx/.pptx/.html via docling, .txt/.md directly)</param>
        /// <param name="name">Document name (defaults to the file name)</param>
        /// <param name="embed">Embed the chunks (default true; requires the embedding provider)</param>
        /// <param name="sourceUri">Optional source pointer stored on the document vertex</param>
        /// <param name="replaceDocumentId">Replace this document on success (FR-15)</param>
        /// <param name="propertiesJson">User tags as a JSON object of string values</param>
        /// <param name="linkJson">Structural linking (FR-13) as JSON: {"indexIds":[...],"maxLinksPerChunk":n}</param>
        /// <param name="cancellationToken">Aborts conversion/embedding when the request is cancelled</param>
        /// <remarks>
        /// The pipeline is parse, chunk, embed, write: the Document vertex is created first
        /// (status <c>processing</c>, visible on the change feed), chunks are written only
        /// after embedding succeeded, and any later failure removes them again - a failed
        /// ingest leaves exactly one failed Document vertex and zero chunks.
        /// </remarks>
        /// <response code="200">The document was ingested; the summary reports chunk and link counts</response>
        /// <response code="400">Unsupported format, reserved tag key, invalid link allowlist, empty conversion, or no chunks</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="403">Ingestion is disabled (Fallen8:Ingestion:Enabled), or embed=true while the embedding provider is off</response>
        /// <response code="404">The replace target does not exist</response>
        /// <response code="409">Duplicate content hash, or an index shape/model conflict</response>
        /// <response code="413">Above Fallen8:Ingestion:MaxUploadBytes, MaxPages, or MaxChunksPerDocument</response>
        /// <response code="429">The sensitive-endpoint rate limit was exceeded</response>
        /// <response code="502">The embedding backend produced invalid output</response>
        /// <response code="503">The docling sidecar or the embedding backend is unavailable</response>
        /// <response code="507">The namespace chunk ceiling is reached (Fallen8:Ingestion:MaxChunksPerNamespace)</response>
        [HttpPost("/document")]
        [EnableRateLimiting(Fallen8SecurityOptions.SensitiveRateLimitPolicy)]
        [RequestSizeLimit(TransportUploadLimit)]
        [RequestFormLimits(MultipartBodyLengthLimit = TransportUploadLimit)]
        [Consumes("multipart/form-data")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(DocumentSummaryREST), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(DocumentSummaryREST), StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        [ProducesResponseType(StatusCodes.Status507InsufficientStorage)]
        public async Task<IActionResult> IngestFile(IFormFile file,
            [FromForm] String name,
            [FromForm] Boolean? embed,
            [FromForm] String sourceUri,
            [FromForm] Int32? replaceDocumentId,
            [FromForm] String propertiesJson,
            [FromForm] String linkJson,
            CancellationToken cancellationToken)
        {
            if (file == null || file.Length == 0)
            {
                return ProblemResults.BadRequest("A non-empty 'file' part is required.");
            }

            if (file.Length > _options.MaxUploadBytes)
            {
                return ProblemResults.Create(StatusCodes.Status413PayloadTooLarge, "Upload too large",
                    String.Format("The file ({0} bytes) exceeds Fallen8:Ingestion:MaxUploadBytes ({1}).",
                        file.Length, _options.MaxUploadBytes));
            }

            var format = (Path.GetExtension(file.FileName) ?? String.Empty).TrimStart('.').ToLowerInvariant();
            var isText = DocumentGraphSchema.TextFormats.Contains(format);
            if (!isText && !DocumentGraphSchema.BinaryFormats.Contains(format))
            {
                return ProblemResults.BadRequest(String.Format(
                    "Unsupported file type '{0}'. Allowed: {1}.", format,
                    String.Join(", ", DocumentGraphSchema.TextFormats.Concat(DocumentGraphSchema.BinaryFormats))));
            }

            if (!TryParseTags(propertiesJson, out var tags, out var tagError))
            {
                return ProblemResults.BadRequest(tagError);
            }

            if (!TryParseLink(linkJson, out var link, out var linkError))
            {
                return ProblemResults.BadRequest(linkError);
            }

            var request = new IngestionRequest
            {
                Name = String.IsNullOrWhiteSpace(name) ? file.FileName : name,
                SourceFormat = format,
                Embed = embed ?? true,
                Tags = tags,
                SourceUri = sourceUri,
                ReplaceDocumentId = replaceDocumentId,
                LinkIndexIds = link?.IndexIds,
                LinkMaxPerChunk = link?.MaxLinksPerChunk
            };

            if (isText)
            {
                using (var reader = new StreamReader(file.OpenReadStream()))
                {
                    request.Text = await reader.ReadToEndAsync(cancellationToken);
                }

                request.PlainText = String.Equals(format, "txt", StringComparison.Ordinal);
            }
            else
            {
                using (var buffer = new MemoryStream())
                {
                    await file.CopyToAsync(buffer, cancellationToken);
                    request.FileBytes = buffer.ToArray();
                }
            }

            return Render(await _service.IngestAsync(request, CurrentNamespaceName(), cancellationToken));
        }

        /// <summary>
        /// Ingests raw text or markdown into the graph
        /// </summary>
        /// <param name="definition">Name, text, format and the shared ingest options</param>
        /// <param name="cancellationToken">Aborts embedding when the request is cancelled</param>
        /// <remarks>The sidecar-free path (FR-3): markdown chunks along its headings, plain
        /// text as one bounded section. Same lifecycle and failure semantics as the file route.</remarks>
        /// <response code="200">The document was ingested</response>
        /// <response code="400">Missing name/text, an unknown format, a reserved tag key, or an invalid link allowlist</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="403">Ingestion is disabled, or embed=true while the embedding provider is off</response>
        /// <response code="404">The replace target does not exist</response>
        /// <response code="409">Duplicate content hash, or an index shape/model conflict</response>
        /// <response code="413">The text exceeds Fallen8:Ingestion:MaxUploadBytes, or the chunk cap</response>
        /// <response code="429">The sensitive-endpoint rate limit was exceeded</response>
        /// <response code="502">The embedding backend produced invalid output</response>
        /// <response code="503">The embedding backend is unavailable</response>
        /// <response code="507">The namespace chunk ceiling is reached</response>
        [HttpPost("/document/text")]
        [EnableRateLimiting(Fallen8SecurityOptions.SensitiveRateLimitPolicy)]
        [RequestSizeLimit(TransportUploadLimit)]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(DocumentSummaryREST), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(DocumentSummaryREST), StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        [ProducesResponseType(StatusCodes.Status507InsufficientStorage)]
        public async Task<IActionResult> IngestText([FromBody] IngestTextSpecification definition,
            CancellationToken cancellationToken)
        {
            if (definition == null || String.IsNullOrWhiteSpace(definition.Name))
            {
                return ProblemResults.BadRequest("A document name is required.");
            }

            if (String.IsNullOrWhiteSpace(definition.Text))
            {
                return ProblemResults.BadRequest("A non-empty text is required.");
            }

            var format = definition.Format ?? "markdown";
            if (!String.Equals(format, "markdown", StringComparison.Ordinal) &&
                !String.Equals(format, "plain", StringComparison.Ordinal))
            {
                return ProblemResults.BadRequest(String.Format(
                    "Unknown format '{0}'; use 'markdown' or 'plain'.", format));
            }

            var byteCount = System.Text.Encoding.UTF8.GetByteCount(definition.Text);
            if (byteCount > _options.MaxUploadBytes)
            {
                return ProblemResults.Create(StatusCodes.Status413PayloadTooLarge, "Text too large",
                    String.Format("The text ({0} bytes) exceeds Fallen8:Ingestion:MaxUploadBytes ({1}).",
                        byteCount, _options.MaxUploadBytes));
            }

            var plain = String.Equals(format, "plain", StringComparison.Ordinal);
            var request = new IngestionRequest
            {
                Name = definition.Name,
                SourceFormat = plain ? "txt" : "md",
                Text = definition.Text,
                PlainText = plain,
                Embed = definition.Embed ?? true,
                Tags = definition.Properties,
                SourceUri = definition.SourceUri,
                ReplaceDocumentId = definition.ReplaceDocumentId,
                LinkIndexIds = definition.Link?.IndexIds,
                LinkMaxPerChunk = definition.Link?.MaxLinksPerChunk
            };

            return Render(await _service.IngestAsync(request, CurrentNamespaceName(), cancellationToken));
        }

        /// <summary>
        /// Lists the namespace's documents
        /// </summary>
        /// <remarks>Summaries plus the chunk budget (FR-14) and the active embedding model,
        /// so stale documents (FR-16) are visible in one call.</remarks>
        /// <response code="200">The documents, chunk usage and ceiling</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="403">Ingestion is disabled (Fallen8:Ingestion:Enabled)</response>
        [HttpGet("/document")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(DocumentListREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public IActionResult List()
        {
            return Ok(_service.List());
        }

        /// <summary>
        /// Gets one document with its chunks
        /// </summary>
        /// <param name="documentId">The document vertex id</param>
        /// <remarks>Chunks carry previews and provenance; the full text stays one home, the
        /// chunk vertex's <c>text</c> property (graph element routes).</remarks>
        /// <response code="200">The document and its chunks in document order</response>
        /// <response code="400">The id is not a Document vertex</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="403">Ingestion is disabled (Fallen8:Ingestion:Enabled)</response>
        /// <response code="404">No vertex with this id</response>
        [HttpGet("/document/{documentId:int}")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(DocumentDetailREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult Get(Int32 documentId)
        {
            if (_service.TryGetDetail(documentId, out var detail, out var problem))
            {
                return Ok(detail);
            }

            return problem == null
                ? ProblemResults.NotFound(String.Format("No vertex with id {0}.", documentId))
                : ProblemResults.BadRequest(problem);
        }

        /// <summary>
        /// Deletes a document, its chunks and all their edges
        /// </summary>
        /// <param name="documentId">The document vertex id</param>
        /// <param name="waitForCompletion">Wait for the removal to commit</param>
        /// <remarks>One transactional removal (FR-7): edges (including <c>mentions</c> and
        /// user-drawn ones onto chunks) cascade with the vertices; the fulltext mirror is
        /// cleaned alongside.</remarks>
        /// <response code="202">The removal was enqueued (and committed when waitForCompletion)</response>
        /// <response code="400">The id is not a Document vertex</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="403">Ingestion is disabled (Fallen8:Ingestion:Enabled)</response>
        /// <response code="404">No vertex with this id</response>
        [HttpDelete("/document/{documentId:int}")]
        [Produces("application/json")]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Delete(Int32 documentId, [FromQuery] Boolean waitForCompletion = false)
        {
            var (found, problem) = await _service.DeleteAsync(documentId, waitForCompletion);
            if (!found)
            {
                return problem == null
                    ? ProblemResults.NotFound(String.Format("No vertex with id {0}.", documentId))
                    : ProblemResults.BadRequest(problem);
            }

            return Accepted();
        }

        /// <summary>
        /// Fused chunk search: dense kNN plus lexical fulltext, reciprocal rank fusion
        /// </summary>
        /// <param name="definition">The query, mode, k, sibling window and grouping</param>
        /// <param name="cancellationToken">Aborts the query embedding when the request is cancelled</param>
        /// <remarks>
        /// The default mode fuses both sides with RRF (k=60, candidate depth max(50, 4k));
        /// when one side is unavailable (provider off, index absent) the answer degrades and
        /// <c>modeUsed</c> says so. Hits are live Chunk vertices - use them directly as
        /// /path or /subgraph seeds. Scores: RRF when fused, raw kNN when dense, match count
        /// when lexical.
        /// </remarks>
        /// <response code="200">The hits (flat, or grouped per document)</response>
        /// <response code="400">Invalid k/window/mode, no usable query, a dimension mismatch, or the requested side is unavailable</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="403">Ingestion is disabled (Fallen8:Ingestion:Enabled)</response>
        /// <response code="409">The vector index's dimension or declared model identity conflicts with the active provider</response>
        /// <response code="429">The sensitive-endpoint rate limit was exceeded</response>
        /// <response code="502">The embedding backend produced invalid output</response>
        /// <response code="503">The embedding backend is unavailable</response>
        [HttpPost("/document/search")]
        [EnableRateLimiting(Fallen8SecurityOptions.SensitiveRateLimitPolicy)]
        [RequestSizeLimit(1_048_576)]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(DocumentSearchResultREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<IActionResult> Search([FromBody] DocumentSearchSpecification definition,
            CancellationToken cancellationToken)
        {
            if (definition == null)
            {
                return ProblemResults.BadRequest("A search specification is required.");
            }

            var outcome = await _search.SearchAsync(definition, cancellationToken);
            return outcome.Status == StatusCodes.Status200OK
                ? Ok(outcome.Result)
                : ProblemResults.Create(outcome.Status, outcome.Title, outcome.Error);
        }

        /// <summary>
        /// Reports the semantic layer's index binding state
        /// </summary>
        /// <remarks>The three indices the layer uses (vector, fulltext, entity), whether each
        /// exists and is usable, and whether ingestion is ready. The layer never creates an index
        /// implicitly (FR-7); bind them with POST /document/binding/ensure.</remarks>
        /// <response code="200">The binding state</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="403">Ingestion is disabled (Fallen8:Ingestion:Enabled)</response>
        [HttpGet("/document/binding")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(DocumentBindingREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public IActionResult GetBinding()
        {
            return Ok(_service.GetBinding());
        }

        /// <summary>
        /// Creates the required indices, binding the semantic layer
        /// </summary>
        /// <remarks>The explicit, idempotent bind (FR-7): creates the vector, fulltext and entity
        /// indices the configuration requires and that do not yet exist, then reports the state.
        /// This is the only path that creates a bound index; ingestion answers 428 until it runs.</remarks>
        /// <response code="200">The binding state after creation (Ready when all required indices exist)</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="403">Ingestion is disabled (Fallen8:Ingestion:Enabled)</response>
        /// <response code="409">An index with a bound id exists but is the wrong shape</response>
        [HttpPost("/document/binding/ensure")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(DocumentBindingREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public IActionResult EnsureBinding()
        {
            try
            {
                return Ok(_service.EnsureBinding());
            }
            catch (IngestionFailedException ex)
            {
                return ProblemResults.Create(ex.Status, ex.Title, ex.Message);
            }
        }

        /// <summary>
        /// Lists the entities the corpus mentions
        /// </summary>
        /// <param name="type">Optional entity-type filter (case-insensitive, e.g. PER/ORG/LOC)</param>
        /// <param name="contains">Optional case-insensitive substring the entity text must contain</param>
        /// <param name="limit">Page cap (default 200, max 10000)</param>
        /// <remarks>Deduplicated Entity vertices (feature semantic-layer) ranked by mention count.
        /// Each id is a valid /path or /subgraph seed. A bounded page; <c>total</c> reports the full
        /// match count.</remarks>
        /// <response code="200">The entity page and the total match count</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="403">Ingestion is disabled (Fallen8:Ingestion:Enabled)</response>
        [HttpGet("/document/entities")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(DocumentEntityListREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public IActionResult ListEntities([FromQuery] String type, [FromQuery] String contains,
            [FromQuery] Int32 limit = 200)
        {
            return Ok(_service.ListEntities(type, contains, limit));
        }

        #region helpers

        private IActionResult Render(IngestionOutcome outcome)
        {
            if (outcome.Status == StatusCodes.Status200OK)
            {
                return Ok(outcome.Summary);
            }

            // Async ingestion (feature semantic-layer): the stub was created and the job queued;
            // the row appears `processing` and flips live via the change feed.
            if (outcome.Status == StatusCodes.Status202Accepted)
            {
                return Accepted(outcome.Summary);
            }

            return ProblemResults.Create(outcome.Status, outcome.Title, outcome.Error, problem =>
            {
                if (outcome.FailedDocumentId != null)
                {
                    problem.Extensions["documentId"] = outcome.FailedDocumentId.Value;
                }
            });
        }

        /// <summary>The addressed namespace name for the ingestion job (null = default / bare
        /// route), read from the route the same way the engine resolver does.</summary>
        private String CurrentNamespaceName()
        {
            return RouteData.Values.TryGetValue(
                NoSQL.GraphDB.App.Namespaces.NamespaceRouteConvention.RouteParameterName, out var value)
                ? value as String
                : null;
        }

        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
            Justification = "Deserializes a flat string dictionary with default options; trimming is disabled for this application.")]
        private static Boolean TryParseTags(String propertiesJson, out Dictionary<String, String> tags, out String error)
        {
            tags = null;
            error = null;
            if (String.IsNullOrWhiteSpace(propertiesJson))
            {
                return true;
            }

            try
            {
                tags = JsonSerializer.Deserialize<Dictionary<String, String>>(propertiesJson);
                return true;
            }
            catch (JsonException ex)
            {
                error = String.Format("propertiesJson is not a JSON object of string values: {0}", ex.Message);
                return false;
            }
        }

        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
            Justification = "Deserializes the LinkSpecificationREST DTO with default options; trimming is disabled for this application.")]
        private static Boolean TryParseLink(String linkJson, out LinkSpecificationREST link, out String error)
        {
            link = null;
            error = null;
            if (String.IsNullOrWhiteSpace(linkJson))
            {
                return true;
            }

            try
            {
                link = JsonSerializer.Deserialize<LinkSpecificationREST>(linkJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return true;
            }
            catch (JsonException ex)
            {
                error = String.Format("linkJson is not a valid link specification: {0}", ex.Message);
                return false;
            }
        }

        #endregion
    }
}
