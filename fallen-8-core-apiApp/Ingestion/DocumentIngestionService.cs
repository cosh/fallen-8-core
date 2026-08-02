// MIT License
//
// DocumentIngestionService.cs
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
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Embedding;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Helper;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Index.Fulltext;
using NoSQL.GraphDB.Core.Index.Vector;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.App.Ingestion
{
    #region pipeline inputs and outcome

    /// <summary>One validated ingestion request, format-agnostic: exactly one of
    /// <see cref="FileBytes"/> (binary path) or <see cref="Text"/> (text path) is set.</summary>
    public sealed class IngestionRequest
    {
        public String Name;
        public String SourceFormat;
        public Byte[] FileBytes;
        public String Text;
        public Boolean PlainText;
        public Boolean Embed = true;
        public Dictionary<String, String> Tags;
        public String SourceUri;
        public Int32? ReplaceDocumentId;
        public List<String> LinkIndexIds;
        public Int32? LinkMaxPerChunk;
    }

    /// <summary>The pipeline outcome: HTTP semantics decided here, rendered by the
    /// controller. <see cref="FailedDocumentId"/> is set when the failure happened after the
    /// stub (spec FR-2: a failed ingest leaves exactly one failed Document vertex).</summary>
    public sealed class IngestionOutcome
    {
        public Int32 Status = StatusCodes.Status200OK;
        public String Title;
        public String Error;
        public Int32? FailedDocumentId;
        public Controllers.Model.DocumentSummaryREST Summary;

        internal static IngestionOutcome Fail(Int32 status, String title, String error, Int32? failedDocumentId = null)
        {
            return new IngestionOutcome { Status = status, Title = title, Error = error, FailedDocumentId = failedDocumentId };
        }
    }

    /// <summary>Internal control-flow signal of the post-stub pipeline; always caught and
    /// turned into a failed Document stub plus an <see cref="IngestionOutcome"/>.</summary>
    internal sealed class IngestionFailedException : Exception
    {
        public Int32 Status
        {
            get;
        }

        public String Title
        {
            get;
        }

        public IngestionFailedException(Int32 status, String title, String detail) : base(detail)
        {
            Status = status;
            Title = title;
        }
    }

    #endregion

    /// <summary>
    ///   The ingestion pipeline (spec unstructured-ingestion FR-2..FR-5, FR-13..FR-15):
    ///   validate, create the Document stub (progress rides the change feed as committed
    ///   property writes), parse, chunk, embed BEFORE any chunk write, resolve links, write
    ///   (vertices, edges, embeddings, fulltext), finish the stub. Any post-stub failure
    ///   removes every created chunk and leaves exactly one failed Document vertex.
    /// </summary>
    public sealed class DocumentIngestionService
    {
        private readonly IFallen8 _fallen8;
        private readonly IDoclingConverter _docling;
        private readonly Fallen8EmbeddingProvider _provider;
        private readonly Fallen8IngestionOptions _options;
        private readonly Fallen8EmbeddingOptions _embeddingOptions;
        private readonly NoSQL.GraphDB.App.Namespaces.Fallen8Namespaces _namespaces;
        private readonly IngestionJobQueue _queue;
        private readonly ILogger<DocumentIngestionService> _logger;

        public DocumentIngestionService(IFallen8 fallen8, IDoclingConverter docling,
            Fallen8EmbeddingProvider provider,
            IOptions<Fallen8IngestionOptions> options,
            IOptions<Fallen8EmbeddingOptions> embeddingOptions,
            NoSQL.GraphDB.App.Namespaces.Fallen8Namespaces namespaces,
            IngestionJobQueue queue,
            ILogger<DocumentIngestionService> logger)
        {
            _fallen8 = fallen8;
            _docling = docling;
            _provider = provider;
            _options = options.Value;
            _embeddingOptions = embeddingOptions.Value;
            _namespaces = namespaces;
            _queue = queue;
            _logger = logger;
        }

        #region ingest

        /// <summary>Request-thread entry (FR-3): validate, create the processing stub, and
        /// enqueue the heavy pipeline onto the global FIFO queue. Returns 202 with the stub
        /// summary; the worker finishes it off-thread. <paramref name="namespaceName"/> (null =
        /// default) is carried on the job so the worker re-resolves the same engine.</summary>
        public async Task<IngestionOutcome> IngestAsync(IngestionRequest request, String namespaceName,
            CancellationToken cancellationToken)
        {
            // ---- pre-stub validation: nothing below touches the graph.
            var isBinary = request.FileBytes != null;

            if (request.Embed && !_embeddingOptions.Enabled)
            {
                return IngestionOutcome.Fail(StatusCodes.Status403Forbidden, "Embedding provider disabled",
                    "The embedding provider is off (Fallen8:Embedding:Enabled); pass embed=false to ingest without vectors.");
            }

            if (isBinary && !_docling.Configured)
            {
                return IngestionOutcome.Fail(StatusCodes.Status503ServiceUnavailable, "Document conversion unavailable",
                    String.Format("No docling endpoint is configured (Fallen8:Ingestion:Docling:Endpoint); only {0} ingest without it.",
                        String.Join("/", DocumentGraphSchema.TextFormats)));
            }

            foreach (var key in request.Tags?.Keys ?? Enumerable.Empty<String>())
            {
                if (DocumentGraphSchema.ReservedPropertyKeys.Contains(key))
                {
                    return IngestionOutcome.Fail(StatusCodes.Status400BadRequest, "Reserved property key",
                        String.Format("The tag key '{0}' is reserved by the document graph model.", key));
                }
            }

            if (request.ReplaceDocumentId != null)
            {
                if (!_fallen8.TryGetVertex(out var replaceTarget, request.ReplaceDocumentId.Value))
                {
                    return IngestionOutcome.Fail(StatusCodes.Status404NotFound, "Replace target not found",
                        String.Format("No vertex with id {0}.", request.ReplaceDocumentId.Value));
                }

                if (!String.Equals(replaceTarget.Label, DocumentGraphSchema.DocumentLabel, StringComparison.Ordinal))
                {
                    return IngestionOutcome.Fail(StatusCodes.Status400BadRequest, "Replace target is not a Document",
                        String.Format("Vertex {0} carries label '{1}'.", request.ReplaceDocumentId.Value, replaceTarget.Label));
                }
            }

            var contentHash = ComputeContentHash(request);
            var duplicate = FindByContentHash(contentHash, request.ReplaceDocumentId);
            if (duplicate != null)
            {
                return IngestionOutcome.Fail(StatusCodes.Status409Conflict, "Duplicate content",
                    String.Format("Document {0} already carries this exact content (hash {1}); delete it or pass replaceDocumentId.",
                        duplicate.Value, contentHash), null);
            }

            var chunksBefore = CountChunks();
            if (chunksBefore >= _options.MaxChunksPerNamespace)
            {
                return IngestionOutcome.Fail(StatusCodes.Status507InsufficientStorage, "Chunk ceiling reached",
                    String.Format("This namespace holds {0} chunks; the ceiling is {1} (Fallen8:Ingestion:MaxChunksPerNamespace).",
                        chunksBefore, _options.MaxChunksPerNamespace));
            }

            if (request.Embed)
            {
                var conflict = BoundIndexContract.FindConflict(_fallen8, _options.EmbeddingName, _provider);
                if (conflict != null)
                {
                    return IngestionOutcome.Fail(StatusCodes.Status409Conflict, "Embedding contract conflict", conflict);
                }
            }

            var linkError = ValidateLinkRequest(request, out var linkCap);
            if (linkError != null)
            {
                return linkError;
            }

            // ---- the stub, then hand off. The heavy pipeline runs on the worker; a failure
            // there marks THIS stub failed (FR-2 invariant preserved), off the request thread.
            var documentId = await CreateDocumentStub(request, contentHash);

            var job = new IngestionJob
            {
                Namespace = namespaceName,
                DocumentId = documentId,
                Request = request,
                LinkIndexIds = request.LinkIndexIds,
                LinkCap = linkCap
            };

            if (!_queue.TryEnqueue(job))
            {
                // The queue is full: fail the stub we just created and tell the caller to retry.
                await FailDocument(documentId, "The ingestion queue is full; retry shortly.");
                return IngestionOutcome.Fail(StatusCodes.Status503ServiceUnavailable, "Ingestion queue full",
                    String.Format("The global ingestion queue is at capacity ({0}); retry shortly.",
                        _options.MaxQueueLength), documentId);
            }

            _fallen8.TryGetVertex(out var stub, documentId);
            return new IngestionOutcome
            {
                Status = StatusCodes.Status202Accepted,
                Summary = Summarize(stub)
            };
        }

        /// <summary>Worker-thread entry (FR-3): runs the heavy pipeline for one job on the
        /// engine of its namespace. Binds that namespace so every graph call resolves it (the
        /// request-thread addressing AsyncLocal does not flow here). Never returns an HTTP
        /// outcome - success finishes the document, failure marks it failed with cleanup.</summary>
        public async Task ProcessJobAsync(IngestionJob job, CancellationToken cancellationToken)
        {
            using (NoSQL.GraphDB.App.Namespaces.AddressedFallen8.PushNamespace(job.Namespace))
            {
                // The namespace may have been dropped, or a reload may have reassigned ids, since
                // the job was enqueued. Verify the stub is still a processing Document; else skip.
                try
                {
                    if (!_fallen8.TryGetVertex(out var stub, job.DocumentId) ||
                        !String.Equals(stub.Label, DocumentGraphSchema.DocumentLabel, StringComparison.Ordinal) ||
                        !(stub.TryGetProperty<String>(out var status, DocumentGraphSchema.StatusProperty) &&
                          status == DocumentGraphSchema.StatusProcessing))
                    {
                        _logger.LogInformation("Skipping ingestion job for document {DocumentId}: no processing stub (namespace dropped or reloaded)",
                            job.DocumentId);
                        return;
                    }
                }
                catch (NoSQL.GraphDB.App.Namespaces.UnknownNamespaceException)
                {
                    _logger.LogInformation("Skipping ingestion job for document {DocumentId}: namespace '{Namespace}' is gone",
                        job.DocumentId, job.Namespace ?? "default");
                    return;
                }

                var request = job.Request;
                var chunksBefore = CountChunks();
                var writtenChunkIds = new List<Int32>();
                IIndex fulltextIndex = null;

                try
                {
                    var (chunks, pageCount, chunkerConfig) = await ParseAndChunk(request, cancellationToken);

                    if (chunks.Count > _options.MaxChunksPerDocument)
                    {
                        throw new IngestionFailedException(StatusCodes.Status413PayloadTooLarge, "Too many chunks",
                            String.Format("The document yields {0} chunks; the per-document cap is {1}.",
                                chunks.Count, _options.MaxChunksPerDocument));
                    }

                    if (chunksBefore + chunks.Count > _options.MaxChunksPerNamespace)
                    {
                        throw new IngestionFailedException(StatusCodes.Status507InsufficientStorage, "Chunk ceiling reached",
                            String.Format("{0} existing plus {1} new chunks would cross the ceiling of {2}.",
                                chunksBefore, chunks.Count, _options.MaxChunksPerNamespace));
                    }

                    Single[][] vectors = null;
                    if (request.Embed)
                    {
                        EnsureVectorIndex();
                        vectors = await EmbedChunks(chunks, cancellationToken);
                    }

                    fulltextIndex = EnsureFulltextIndex();
                    var mentions = ResolveLinks(chunks, job.LinkIndexIds, job.LinkCap, job.DocumentId);

                    await WriteChunks(job.DocumentId, request, chunks, vectors, fulltextIndex, mentions, writtenChunkIds);
                    await FinishDocument(job.DocumentId, request, chunks.Count, pageCount, chunkerConfig);

                    _logger.LogInformation("Ingested document {DocumentId} ({ChunkCount} chunks, {Links} links)",
                        job.DocumentId, writtenChunkIds.Count, mentions.Count);

                    if (request.ReplaceDocumentId != null)
                    {
                        await DeleteAsync(request.ReplaceDocumentId.Value, true);
                    }
                }
                catch (Exception ex) when (ex is IngestionFailedException || ex is DoclingUnavailableException
                    || ex is EmbeddingProviderUnavailableException || ex is EmbeddingProviderOutputException)
                {
                    await CleanupAndFail(job.DocumentId, writtenChunkIds, fulltextIndex, ex.Message);
                    _logger.LogWarning("Ingestion of document {DocumentId} failed: {Reason}", job.DocumentId, ex.Message);
                }
            }
        }

        /// <summary>Startup sweep (FR-5): a Document left <c>processing</c> by a worker that died
        /// across a restart is flipped to <c>failed:interrupted</c> in every namespace, so the
        /// list never shows a permanent zombie.</summary>
        public void SweepInterruptedDocuments()
        {
            foreach (var ns in _namespaces.Snapshot())
            {
                using (NoSQL.GraphDB.App.Namespaces.AddressedFallen8.PushNamespace(ns.Name))
                {
                    foreach (var document in _fallen8.GetAllVertices(DocumentGraphSchema.DocumentLabel))
                    {
                        if (document.TryGetProperty<String>(out var status, DocumentGraphSchema.StatusProperty) &&
                            status == DocumentGraphSchema.StatusProcessing)
                        {
                            // Best-effort; a sweep failure must not stop the worker from starting.
                            try
                            {
                                FailDocument(document.Id, "interrupted").GetAwaiter().GetResult();
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to sweep interrupted document {DocumentId}", document.Id);
                            }
                        }
                    }
                }
            }
        }

        private async Task<(List<DocumentChunk> chunks, Int32? pageCount, String chunkerConfig)> ParseAndChunk(
            IngestionRequest request, CancellationToken cancellationToken)
        {
            List<DocumentChunk> chunks;
            Int32? pageCount = null;
            String chunkerConfig;

            if (request.FileBytes != null)
            {
                var conversion = await _docling.ConvertAsync(request.FileBytes, request.Name, cancellationToken);
                pageCount = conversion.PageCount;
                if (pageCount != null && pageCount.Value > _options.MaxPages)
                {
                    throw new IngestionFailedException(StatusCodes.Status413PayloadTooLarge, "Too many pages",
                        String.Format("The document has {0} pages; the cap is {1} (Fallen8:Ingestion:MaxPages).",
                            pageCount.Value, _options.MaxPages));
                }

                if (conversion.Document != null)
                {
                    chunks = DocumentChunker.ChunkStructured(conversion.Document, _options);
                    chunkerConfig = ChunkerConfig("structured/v1");
                }
                else if (!String.IsNullOrWhiteSpace(conversion.Markdown))
                {
                    chunks = DocumentChunker.ChunkMarkdown(conversion.Markdown, _options);
                    chunkerConfig = ChunkerConfig("markdown/v1");
                }
                else
                {
                    throw new IngestionFailedException(StatusCodes.Status400BadRequest, "Empty conversion",
                        "The conversion returned no content.");
                }
            }
            else if (request.PlainText)
            {
                chunks = DocumentChunker.ChunkPlainText(request.Text, _options);
                chunkerConfig = ChunkerConfig("plain/v1");
            }
            else
            {
                chunks = DocumentChunker.ChunkMarkdown(request.Text, _options);
                chunkerConfig = ChunkerConfig("markdown/v1");
            }

            if (chunks.Count == 0)
            {
                throw new IngestionFailedException(StatusCodes.Status400BadRequest, "No chunks",
                    "The document yields no chunks (empty or whitespace-only content).");
            }

            return (chunks, pageCount, chunkerConfig);
        }

        private String ChunkerConfig(String pipeline)
        {
            return String.Format("{0};min={1};max={2}", pipeline, _options.ChunkMinChars, _options.ChunkMaxChars);
        }

        #endregion

        #region indices (FR-5)

        private void EnsureVectorIndex()
        {
            if (!_options.EnsureVectorIndex)
            {
                return;
            }

            if (_fallen8.IndexFactory.TryGetIndex(out var existing, _options.VectorIndexId))
            {
                ValidateVectorIndexShape(existing);
                return;
            }

            var parameters = new Dictionary<String, Object>
            {
                { "dimension", _provider.Identity.Dimension },
                { "metric", _embeddingOptions.IntendedMetric },
                { "embeddingName", _options.EmbeddingName },
                { "model", _provider.Identity.Stamp }
            };

            if (_fallen8.IndexFactory.TryCreateIndex(out _, _options.VectorIndexId, "VectorIndex", parameters))
            {
                _logger.LogInformation("Ensured bound vector index '{IndexId}' ({Dimension} dims)",
                    _options.VectorIndexId, _provider.Identity.Dimension);
                return;
            }

            // TryCreateIndex returns false when the name already exists - which is exactly what a
            // concurrent first-ingest into the same namespace does (both miss TryGetIndex, both
            // create, the loser sees "already exists"). Re-check: an existing index of the right
            // shape IS success, not a 500. Only a genuinely absent index is a real failure.
            if (_fallen8.IndexFactory.TryGetIndex(out var raced, _options.VectorIndexId))
            {
                ValidateVectorIndexShape(raced);
                return;
            }

            throw new IngestionFailedException(StatusCodes.Status500InternalServerError, "Index creation failed",
                String.Format("The bound vector index '{0}' could not be created.", _options.VectorIndexId));
        }

        private void ValidateVectorIndexShape(IIndex index)
        {
            if (!(index is IVectorIndex vectorIndex))
            {
                throw new IngestionFailedException(StatusCodes.Status409Conflict, "Index shape conflict",
                    String.Format("Index '{0}' exists but is not a vector index.", _options.VectorIndexId));
            }

            if (!String.Equals(vectorIndex.EmbeddingName, _options.EmbeddingName, StringComparison.Ordinal))
            {
                throw new IngestionFailedException(StatusCodes.Status409Conflict, "Index shape conflict",
                    String.Format("Index '{0}' is bound to embedding '{1}', not '{2}'.",
                        _options.VectorIndexId, vectorIndex.EmbeddingName ?? "(unbound)", _options.EmbeddingName));
            }

            // Dimension and model identity against the provider are covered by the
            // BoundIndexContract pre-check (one home).
        }

        /// <summary>Returns the fulltext index chunk text is mirrored into, or null when the
        /// lexical side is disabled (fused search then degrades to dense, stated in /status).</summary>
        private IIndex EnsureFulltextIndex()
        {
            if (!_options.EnsureFulltextIndex)
            {
                return null;
            }

            if (_fallen8.IndexFactory.TryGetIndex(out var existing, _options.FulltextIndexId))
            {
                return ValidateFulltextIndexShape(existing);
            }

            if (_fallen8.IndexFactory.TryCreateIndex(out var created, _options.FulltextIndexId, "RegExIndex"))
            {
                _logger.LogInformation("Ensured fulltext index '{IndexId}'", _options.FulltextIndexId);
                return created;
            }

            // Lost the create race (see EnsureVectorIndex): an existing fulltext index is success.
            if (_fallen8.IndexFactory.TryGetIndex(out var raced, _options.FulltextIndexId))
            {
                return ValidateFulltextIndexShape(raced);
            }

            throw new IngestionFailedException(StatusCodes.Status500InternalServerError, "Index creation failed",
                String.Format("The fulltext index '{0}' could not be created.", _options.FulltextIndexId));
        }

        private IIndex ValidateFulltextIndexShape(IIndex index)
        {
            if (!(index is IFulltextIndex))
            {
                throw new IngestionFailedException(StatusCodes.Status409Conflict, "Index shape conflict",
                    String.Format("Index '{0}' exists but is not a fulltext index.", _options.FulltextIndexId));
            }

            return index;
        }

        #endregion

        #region embedding (embed BEFORE write)

        private async Task<Single[][]> EmbedChunks(List<DocumentChunk> chunks, CancellationToken cancellationToken)
        {
            var vectors = new Single[chunks.Count][];
            var batchSize = Math.Max(1, _embeddingOptions.MaxBatchSize);
            for (var offset = 0; offset < chunks.Count; offset += batchSize)
            {
                var count = Math.Min(batchSize, chunks.Count - offset);
                var texts = new List<String>(count);
                for (var i = 0; i < count; i++)
                {
                    texts.Add(chunks[offset + i].Text);
                }

                var batch = await _provider.EmbedAsync(texts, cancellationToken);
                for (var i = 0; i < count; i++)
                {
                    vectors[offset + i] = batch[i];
                }
            }

            return vectors;
        }

        #endregion

        #region structural linking (FR-13)

        private IngestionOutcome ValidateLinkRequest(IngestionRequest request, out Int32 linkCap)
        {
            linkCap = 0;

            if (request.LinkIndexIds == null || request.LinkIndexIds.Count == 0)
            {
                return null;
            }

            if (request.LinkMaxPerChunk != null &&
                (request.LinkMaxPerChunk.Value < 1 || request.LinkMaxPerChunk.Value > _options.MaxLinksPerChunk))
            {
                return IngestionOutcome.Fail(StatusCodes.Status400BadRequest, "Invalid link cap",
                    String.Format("maxLinksPerChunk must be within [1, {0}] (Fallen8:Ingestion:MaxLinksPerChunk).",
                        _options.MaxLinksPerChunk));
            }

            foreach (var indexId in request.LinkIndexIds)
            {
                if (!_fallen8.IndexFactory.TryGetIndex(out var index, indexId))
                {
                    return IngestionOutcome.Fail(StatusCodes.Status400BadRequest, "Unknown link index",
                        String.Format("No index named '{0}'.", indexId));
                }

                if (index is IVectorIndex || index is IFulltextIndex)
                {
                    return IngestionOutcome.Fail(StatusCodes.Status400BadRequest, "Link index not equality-capable",
                        String.Format("Index '{0}' is a {1} index; linking needs exact-equality lookups (dictionary family).",
                            indexId, index is IVectorIndex ? "vector" : "fulltext"));
                }
            }

            linkCap = request.LinkMaxPerChunk ?? _options.MaxLinksPerChunk;
            return null;
        }

        /// <summary>Exact ordinal identifier-to-index-key matching, deterministic (token order,
        /// then index order, then ascending element id), capped per chunk, live vertices only.
        /// Re-resolves the allowlisted index IDs against the current (worker-bound) engine, so a
        /// dropped index since enqueue is simply skipped. Chunk vertices of THIS ingest cannot be
        /// targets: they are created after this runs.</summary>
        private List<KeyValuePair<Int32, Int32>> ResolveLinks(List<DocumentChunk> chunks,
            List<String> linkIndexIds, Int32 linkCap, Int32 documentId)
        {
            var mentions = new List<KeyValuePair<Int32, Int32>>();
            if (linkIndexIds == null || linkIndexIds.Count == 0)
            {
                return mentions;
            }

            var linkIndices = new List<IIndex>(linkIndexIds.Count);
            foreach (var indexId in linkIndexIds)
            {
                if (_fallen8.IndexFactory.TryGetIndex(out var index, indexId) &&
                    !(index is IVectorIndex) && !(index is IFulltextIndex))
                {
                    linkIndices.Add(index);
                }
            }

            if (linkIndices.Count == 0)
            {
                return mentions;
            }

            for (var chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
            {
                var seen = new HashSet<Int32>();
                var linked = 0;
                foreach (var token in chunks[chunkIndex].Identifiers)
                {
                    if (linked >= linkCap)
                    {
                        break;
                    }

                    foreach (var index in linkIndices)
                    {
                        if (linked >= linkCap)
                        {
                            break;
                        }

                        if (!index.TryGetValue(out var hits, token))
                        {
                            continue;
                        }

                        foreach (var hit in hits.OrderBy(element => element.Id))
                        {
                            if (linked >= linkCap)
                            {
                                break;
                            }

                            // Live VERTICES only (an edge cannot be an edge target), never the
                            // document stub itself, each target once per chunk.
                            if (!(hit is VertexModel) || hit.Id == documentId || !seen.Add(hit.Id) ||
                                !_fallen8.TryGetVertex(out _, hit.Id))
                            {
                                continue;
                            }

                            mentions.Add(new KeyValuePair<Int32, Int32>(chunkIndex, hit.Id));
                            linked++;
                        }
                    }
                }
            }

            return mentions;
        }

        #endregion

        #region graph writes

        private async Task<Int32> CreateDocumentStub(IngestionRequest request, String contentHash)
        {
            var properties = new Dictionary<String, Object>
            {
                { DocumentGraphSchema.NameProperty, request.Name },
                { DocumentGraphSchema.SourceFormatProperty, request.SourceFormat },
                { DocumentGraphSchema.StatusProperty, DocumentGraphSchema.StatusProcessing },
                { DocumentGraphSchema.ContentHashProperty, contentHash },
                {
                    DocumentGraphSchema.ConverterProperty,
                    request.FileBytes != null ? DocumentGraphSchema.DoclingConverter : DocumentGraphSchema.NoConverter
                }
            };
            if (!String.IsNullOrWhiteSpace(request.SourceUri))
            {
                properties[DocumentGraphSchema.SourceUriProperty] = request.SourceUri;
            }

            AddTags(properties, request.Tags);

            var tx = new CreateVertexTransaction
            {
                Definition = new VertexDefinition
                {
                    CreationDate = DateHelper.GetNowStamp(),
                    Label = DocumentGraphSchema.DocumentLabel,
                    Properties = properties
                }
            };
            await Enqueue(tx, "document stub creation");
            return tx.VertexCreated.Id;
        }

        /// <summary>Writes the chunk vertices, their edges, embeddings and fulltext mirror.
        /// Created chunk ids are appended to <paramref name="chunkIdSink"/> as soon as they
        /// commit, so the caller's single catch owns cleanup for a failure at ANY later step
        /// (edges, embeddings, fulltext, or FinishDocument) - there is no cleanup here.</summary>
        private async Task WriteChunks(Int32 documentId, IngestionRequest request,
            List<DocumentChunk> chunks, Single[][] vectors, IIndex fulltextIndex,
            List<KeyValuePair<Int32, Int32>> mentions, List<Int32> chunkIdSink)
        {
            var now = DateHelper.GetNowStamp();

            var createChunks = new CreateVerticesTransaction();
            foreach (var chunk in chunks)
            {
                var properties = new Dictionary<String, Object>
                {
                    { DocumentGraphSchema.TextProperty, chunk.Text },
                    { DocumentGraphSchema.OrderProperty, chunk.Order },
                    { DocumentGraphSchema.KindProperty, chunk.Kind }
                };
                if (chunk.HeadingPath != null)
                {
                    properties[DocumentGraphSchema.HeadingPathProperty] = chunk.HeadingPath;
                }

                if (chunk.PageFrom != null)
                {
                    properties[DocumentGraphSchema.PageFromProperty] = chunk.PageFrom.Value;
                }

                if (chunk.PageTo != null)
                {
                    properties[DocumentGraphSchema.PageToProperty] = chunk.PageTo.Value;
                }

                if (chunk.Identifiers.Count > 0)
                {
                    properties[DocumentGraphSchema.IdentifiersProperty] = String.Join(" ", chunk.Identifiers);
                }

                AddTags(properties, request.Tags);
                createChunks.AddVertex(now, DocumentGraphSchema.ChunkLabel, properties);
            }

            await Enqueue(createChunks, "chunk creation");
            var chunkIds = createChunks.GetCreatedVertices().Select(vertex => vertex.Id).ToList();
            // Publish the ids to the caller's cleanup sink BEFORE the dependent writes, so a
            // failure in any of them (or in FinishDocument afterwards) removes these chunks.
            chunkIdSink.AddRange(chunkIds);

            var createEdges = new CreateEdgesTransaction();
            for (var i = 0; i < chunkIds.Count; i++)
            {
                createEdges.AddEdge(documentId, DocumentGraphSchema.ContainsEdge, chunkIds[i], now);
                if (i + 1 < chunkIds.Count)
                {
                    createEdges.AddEdge(chunkIds[i], DocumentGraphSchema.NextEdge, chunkIds[i + 1], now);
                }
            }

            foreach (var mention in mentions)
            {
                createEdges.AddEdge(chunkIds[mention.Key], DocumentGraphSchema.MentionsEdge, mention.Value, now);
            }

            await Enqueue(createEdges, "edge creation");

            if (vectors != null)
            {
                var setEmbeddings = new SetEmbeddingsTransaction();
                for (var i = 0; i < chunkIds.Count; i++)
                {
                    setEmbeddings.SetEmbedding(chunkIds[i], _options.EmbeddingName, vectors[i], _provider.Identity.Stamp);
                }

                await Enqueue(setEmbeddings, "embedding write");
            }

            if (fulltextIndex != null)
            {
                for (var i = 0; i < chunkIds.Count; i++)
                {
                    if (_fallen8.TryGetGraphElement(out var element, chunkIds[i]))
                    {
                        // The same request-thread population path the index REST surface uses
                        // (PUT /index/{id}); commit happened above. AddOrUpdate skips a removed
                        // element under its own write lock (RegExIndex guard), closing the
                        // add-after-remove race for a concurrent DELETE.
                        fulltextIndex.AddOrUpdate(chunks[i].Text, element);
                    }
                }
            }
        }

        /// <summary>Removes any chunks this ingest committed, then marks the Document failed -
        /// the single cleanup path for every post-stub failure (FR-2 invariant).</summary>
        private async Task CleanupAndFail(Int32 documentId, List<Int32> writtenChunkIds,
            IIndex fulltextIndex, String reason)
        {
            if (writtenChunkIds.Count > 0)
            {
                await RemoveChunkSubtree(writtenChunkIds, fulltextIndex);
            }

            await FailDocument(documentId, reason);
        }

        private async Task FinishDocument(Int32 documentId, IngestionRequest request, Int32 chunkCount,
            Int32? pageCount, String chunkerConfig)
        {
            await SetProperty(documentId, DocumentGraphSchema.ChunkCountProperty, chunkCount);
            if (pageCount != null)
            {
                await SetProperty(documentId, DocumentGraphSchema.PageCountProperty, pageCount.Value);
            }

            await SetProperty(documentId, DocumentGraphSchema.ChunkerConfigProperty, chunkerConfig);
            if (request.Embed)
            {
                await SetProperty(documentId, DocumentGraphSchema.EmbeddingModelProperty, _provider.Identity.Stamp);
                await SetProperty(documentId, DocumentGraphSchema.EmbeddingDimensionProperty, _provider.Identity.Dimension);
            }

            await UpdateProperty(documentId, DocumentGraphSchema.StatusProperty, DocumentGraphSchema.StatusIndexed);
        }

        private async Task FailDocument(Int32 documentId, String reason)
        {
            // Best-effort by design: the stub must survive to carry the failure, so a failing
            // property write here must not mask the original fault.
            try
            {
                await SetProperty(documentId, DocumentGraphSchema.ErrorProperty, reason ?? "unknown");
                await UpdateProperty(documentId, DocumentGraphSchema.StatusProperty, DocumentGraphSchema.StatusFailed);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to mark document {DocumentId} as failed", documentId);
            }
        }

        private async Task RemoveChunkSubtree(List<Int32> chunkIds, IIndex fulltextIndex)
        {
            try
            {
                if (fulltextIndex != null)
                {
                    foreach (var chunkId in chunkIds)
                    {
                        if (_fallen8.TryGetGraphElement(out var element, chunkId))
                        {
                            fulltextIndex.RemoveValue(element);
                        }
                    }
                }

                await Enqueue(new RemoveGraphElementsTransaction { GraphElementIds = chunkIds }, "chunk cleanup");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Chunk cleanup after a failed ingest left orphans: {ChunkIds}",
                    String.Join(",", chunkIds));
            }
        }

        private async Task SetProperty(Int32 elementId, String propertyId, Object value)
        {
            await Enqueue(new AddPropertyTransaction
            {
                Definition = new PropertyAddDefinition
                {
                    GraphElementId = elementId,
                    PropertyId = propertyId,
                    Property = value
                }
            }, String.Format("property write '{0}'", propertyId));
        }

        /// <summary>Updates an EXISTING property: the engine's documented update path is
        /// remove-then-set (SetProperty is add-or-must-equal), each step a granular change-feed
        /// event - never a DelegateTransaction, whose commit forces feed subscribers to resync.</summary>
        /// <summary>Changes an EXISTING property (the engine's SetProperty is add-or-must-equal,
        /// so an update is remove-then-set). The removal's state is deliberately not asserted: an
        /// absent property is a valid no-op, and a genuine write fault surfaces on the SetProperty
        /// below (Enqueue checks TransactionState). ACCEPTED LIMITATION: these are two transactions,
        /// so there is a brief window where the property reads as absent, and a crash between them
        /// leaves it unset - tolerable because ingestion is already a multi-transaction operation
        /// with no cross-transaction atomicity, and status is only ever read advisorily.</summary>
        private async Task UpdateProperty(Int32 elementId, String propertyId, Object value)
        {
            var removal = _fallen8.EnqueueTransaction(new RemovePropertyTransaction
            {
                GraphElementId = elementId,
                PropertyId = propertyId
            });
            await removal.Completion;

            await SetProperty(elementId, propertyId, value);
        }

        private async Task Enqueue(ATransaction transaction, String what)
        {
            var info = _fallen8.EnqueueTransaction(transaction);
            await info.Completion;
            if (info.TransactionState != TransactionState.Finished)
            {
                throw new IngestionFailedException(StatusCodes.Status500InternalServerError, "Graph write failed",
                    String.Format("The {0} did not commit ({1}).", what, info.FailureReason));
            }
        }

        private static void AddTags(Dictionary<String, Object> properties, Dictionary<String, String> tags)
        {
            foreach (var tag in tags ?? new Dictionary<String, String>())
            {
                properties[tag.Key] = tag.Value;
            }
        }

        #endregion

        #region reads (FR-7)

        public Controllers.Model.DocumentListREST List()
        {
            var documents = new List<Controllers.Model.DocumentSummaryREST>();
            foreach (var vertex in _fallen8.GetAllVertices(DocumentGraphSchema.DocumentLabel))
            {
                documents.Add(Summarize(vertex));
            }

            documents.Sort((a, b) => a.DocumentId.CompareTo(b.DocumentId));
            return new Controllers.Model.DocumentListREST
            {
                Documents = documents,
                NamespaceChunkCount = CountChunks(),
                ChunkCeiling = _options.MaxChunksPerNamespace,
                CurrentEmbeddingModel = _embeddingOptions.Enabled ? _provider.Identity.Stamp : null
            };
        }

        public Boolean TryGetDetail(Int32 documentId, out Controllers.Model.DocumentDetailREST detail, out String problem)
        {
            detail = null;
            problem = null;

            if (!_fallen8.TryGetVertex(out var vertex, documentId))
            {
                return false;
            }

            if (!String.Equals(vertex.Label, DocumentGraphSchema.DocumentLabel, StringComparison.Ordinal))
            {
                problem = String.Format("Vertex {0} carries label '{1}', not '{2}'.",
                    documentId, vertex.Label, DocumentGraphSchema.DocumentLabel);
                return false;
            }

            var chunks = new List<Controllers.Model.ChunkSummaryREST>();
            foreach (var chunkVertex in ChunksOf(vertex))
            {
                chunks.Add(SummarizeChunk(chunkVertex));
            }

            chunks.Sort((a, b) => a.Order.CompareTo(b.Order));
            detail = new Controllers.Model.DocumentDetailREST
            {
                Summary = Summarize(vertex),
                Chunks = chunks
            };
            return true;
        }

        public async Task<(Boolean found, String problem)> DeleteAsync(Int32 documentId, Boolean waitForCompletion)
        {
            if (!_fallen8.TryGetVertex(out var vertex, documentId))
            {
                return (false, null);
            }

            if (!String.Equals(vertex.Label, DocumentGraphSchema.DocumentLabel, StringComparison.Ordinal))
            {
                return (false, String.Format("Vertex {0} carries label '{1}', not '{2}'.",
                    documentId, vertex.Label, DocumentGraphSchema.DocumentLabel));
            }

            var ids = new List<Int32> { documentId };
            _fallen8.IndexFactory.TryGetIndex(out var fulltextIndex, _options.FulltextIndexId);
            foreach (var chunkVertex in ChunksOf(vertex))
            {
                ids.Add(chunkVertex.Id);
                if (fulltextIndex is IFulltextIndex)
                {
                    fulltextIndex.RemoveValue(chunkVertex);
                }
            }

            var info = _fallen8.EnqueueTransaction(new RemoveGraphElementsTransaction { GraphElementIds = ids });
            if (waitForCompletion)
            {
                await info.Completion;
            }

            return (true, null);
        }

        private IEnumerable<VertexModel> ChunksOf(VertexModel documentVertex)
        {
            if (!documentVertex.TryGetOutEdge(out var containsEdges, DocumentGraphSchema.ContainsEdge))
            {
                yield break;
            }

            foreach (var edge in containsEdges)
            {
                // Re-fetch for liveness: adjacency may still hold removed targets briefly.
                if (_fallen8.TryGetVertex(out var chunkVertex, edge.TargetVertex.Id) &&
                    String.Equals(chunkVertex.Label, DocumentGraphSchema.ChunkLabel, StringComparison.Ordinal))
                {
                    yield return chunkVertex;
                }
            }
        }

        /// <summary>Maps a Document vertex to its summary (shared with the search grouping).</summary>
        public Controllers.Model.DocumentSummaryREST Summarize(VertexModel vertex)
        {
            var summary = new Controllers.Model.DocumentSummaryREST
            {
                DocumentId = vertex.Id,
                Name = StringProperty(vertex, DocumentGraphSchema.NameProperty),
                SourceFormat = StringProperty(vertex, DocumentGraphSchema.SourceFormatProperty),
                SourceUri = StringProperty(vertex, DocumentGraphSchema.SourceUriProperty),
                Status = StringProperty(vertex, DocumentGraphSchema.StatusProperty),
                Error = StringProperty(vertex, DocumentGraphSchema.ErrorProperty),
                ContentHash = StringProperty(vertex, DocumentGraphSchema.ContentHashProperty),
                Converter = StringProperty(vertex, DocumentGraphSchema.ConverterProperty),
                ChunkerConfig = StringProperty(vertex, DocumentGraphSchema.ChunkerConfigProperty),
                EmbeddingModel = StringProperty(vertex, DocumentGraphSchema.EmbeddingModelProperty)
            };

            if (vertex.TryGetProperty<Int32>(out var chunkCount, DocumentGraphSchema.ChunkCountProperty))
            {
                summary.ChunkCount = chunkCount;
            }

            if (vertex.TryGetProperty<Int32>(out var pageCount, DocumentGraphSchema.PageCountProperty))
            {
                summary.PageCount = pageCount;
            }

            if (vertex.TryGetProperty<Int32>(out var dimension, DocumentGraphSchema.EmbeddingDimensionProperty))
            {
                summary.EmbeddingDimension = dimension;
            }

            summary.Embedded = summary.EmbeddingModel != null;
            summary.EmbeddingModelStale = summary.EmbeddingModel != null && _embeddingOptions.Enabled &&
                !String.Equals(summary.EmbeddingModel, _provider.Identity.Stamp, StringComparison.Ordinal);
            return summary;
        }

        private Controllers.Model.DocumentSummaryREST BuildSummary(Int32 documentId, Int32 linksCreated)
        {
            _fallen8.TryGetVertex(out var vertex, documentId);
            var summary = Summarize(vertex);
            summary.LinksCreated = linksCreated;
            return summary;
        }

        private static Controllers.Model.ChunkSummaryREST SummarizeChunk(VertexModel vertex)
        {
            var chunk = new Controllers.Model.ChunkSummaryREST
            {
                ChunkId = vertex.Id,
                Kind = StringProperty(vertex, DocumentGraphSchema.KindProperty),
                HeadingPath = StringProperty(vertex, DocumentGraphSchema.HeadingPathProperty)
            };

            if (vertex.TryGetProperty<Int32>(out var order, DocumentGraphSchema.OrderProperty))
            {
                chunk.Order = order;
            }

            if (vertex.TryGetProperty<Int32>(out var pageFrom, DocumentGraphSchema.PageFromProperty))
            {
                chunk.PageFrom = pageFrom;
            }

            if (vertex.TryGetProperty<Int32>(out var pageTo, DocumentGraphSchema.PageToProperty))
            {
                chunk.PageTo = pageTo;
            }

            var identifiers = StringProperty(vertex, DocumentGraphSchema.IdentifiersProperty);
            if (identifiers != null)
            {
                chunk.Identifiers = new List<String>(identifiers.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }

            var text = StringProperty(vertex, DocumentGraphSchema.TextProperty) ?? String.Empty;
            chunk.TextPreview = text.Length <= DocumentGraphSchema.TextPreviewLength
                ? text
                : text.Substring(0, DocumentGraphSchema.TextPreviewLength);
            return chunk;
        }

        private static String StringProperty(VertexModel vertex, String propertyId)
        {
            return vertex.TryGetProperty<String>(out var value, propertyId) ? value : null;
        }

        #endregion

        #region shared lookups

        public Int32 CountChunks()
        {
            return _fallen8.GetAllVertices(DocumentGraphSchema.ChunkLabel).Count;
        }

        private Int32? FindByContentHash(String contentHash, Int32? excludedId)
        {
            foreach (var vertex in _fallen8.GetAllVertices(DocumentGraphSchema.DocumentLabel))
            {
                if (vertex.Id == excludedId)
                {
                    continue;
                }

                if (vertex.TryGetProperty<String>(out var hash, DocumentGraphSchema.ContentHashProperty) &&
                    String.Equals(hash, contentHash, StringComparison.Ordinal))
                {
                    return vertex.Id;
                }
            }

            return null;
        }

        private static String ComputeContentHash(IngestionRequest request)
        {
            var bytes = request.FileBytes ?? Encoding.UTF8.GetBytes(request.Text ?? String.Empty);
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }

        #endregion
    }
}
