// MIT License
//
// DocumentSearchService.cs
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
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Embedding;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Index.Fulltext;
using NoSQL.GraphDB.Core.Index.Vector;
using NoSQL.GraphDB.Core.Model;

namespace NoSQL.GraphDB.App.Ingestion
{
    /// <summary>The search outcome: HTTP semantics decided here, rendered by the controller.</summary>
    public sealed class SearchOutcome
    {
        public Int32 Status = StatusCodes.Status200OK;
        public String Title;
        public String Error;
        public DocumentSearchResultREST Result;

        internal static SearchOutcome Fail(Int32 status, String title, String error)
        {
            return new SearchOutcome { Status = status, Title = title, Error = error };
        }
    }

    /// <summary>
    ///   Fused chunk retrieval (spec unstructured-ingestion FR-11/FR-12): dense kNN over the
    ///   bound vector index plus the lexical fulltext index, fused with reciprocal rank
    ///   fusion; sibling windows over <c>next</c> edges; optional per-document grouping.
    ///   Degrades honestly (<c>modeUsed</c>) when one side is unavailable.
    /// </summary>
    public sealed class DocumentSearchService
    {
        /// <summary>The standard RRF constant: score = sum over sides of 1/(RrfK + rank).</summary>
        private const Int32 RrfK = 60;

        private const Int32 MaxResults = 100;
        private const Int32 MaxWindow = 5;

        private readonly IFallen8 _fallen8;
        private readonly Fallen8EmbeddingProvider _provider;
        private readonly Fallen8IngestionOptions _options;
        private readonly Fallen8EmbeddingOptions _embeddingOptions;
        private readonly DocumentIngestionService _documents;

        public DocumentSearchService(IFallen8 fallen8, Fallen8EmbeddingProvider provider,
            IOptions<Fallen8IngestionOptions> options,
            IOptions<Fallen8EmbeddingOptions> embeddingOptions,
            DocumentIngestionService documents)
        {
            _fallen8 = fallen8;
            _provider = provider;
            _options = options.Value;
            _embeddingOptions = embeddingOptions.Value;
            _documents = documents;
        }

        public async Task<SearchOutcome> SearchAsync(DocumentSearchSpecification spec, CancellationToken cancellationToken)
        {
            // ---- validation
            var k = spec.K ?? 10;
            if (k < 1 || k > MaxResults)
            {
                return SearchOutcome.Fail(StatusCodes.Status400BadRequest, "Invalid k",
                    String.Format("k must be within [1, {0}].", MaxResults));
            }

            var window = spec.Window ?? 0;
            if (window < 0 || window > MaxWindow)
            {
                return SearchOutcome.Fail(StatusCodes.Status400BadRequest, "Invalid window",
                    String.Format("window must be within [0, {0}].", MaxWindow));
            }

            var mode = spec.Mode ?? "fused";
            if (mode != "fused" && mode != "dense" && mode != "lexical")
            {
                return SearchOutcome.Fail(StatusCodes.Status400BadRequest, "Invalid mode",
                    "mode must be 'fused', 'dense' or 'lexical'.");
            }

            var hasText = !String.IsNullOrWhiteSpace(spec.QueryText);
            var hasVector = spec.QueryVector != null && spec.QueryVector.Count > 0;
            if (!hasText && !hasVector)
            {
                return SearchOutcome.Fail(StatusCodes.Status400BadRequest, "No query",
                    "queryText (or queryVector for the dense side) is required.");
            }

            if (mode == "lexical" && !hasText)
            {
                return SearchOutcome.Fail(StatusCodes.Status400BadRequest, "No lexical query",
                    "lexical mode needs queryText.");
            }

            var depth = Math.Min(Math.Max(50, 4 * k), VectorIndex.MaxK);

            // ---- dense side
            List<KeyValuePair<VertexModel, Single>> dense = null;
            String denseUnavailable = null;
            if (mode != "lexical")
            {
                var outcome = await TryDenseSide(spec, hasText, hasVector, depth, cancellationToken);
                if (outcome.error != null)
                {
                    return outcome.error;
                }

                dense = outcome.hits;
                denseUnavailable = outcome.unavailable;
                if (denseUnavailable != null && mode == "dense")
                {
                    return SearchOutcome.Fail(StatusCodes.Status400BadRequest, "Dense side unavailable", denseUnavailable);
                }
            }

            // ---- lexical side
            List<KeyValuePair<VertexModel, Single>> lexical = null;
            String lexicalUnavailable = null;
            if (mode != "dense" && hasText)
            {
                (lexical, lexicalUnavailable) = LexicalSide(spec.QueryText, depth);
                if (lexicalUnavailable != null && mode == "lexical")
                {
                    return SearchOutcome.Fail(StatusCodes.Status400BadRequest, "Lexical side unavailable", lexicalUnavailable);
                }
            }
            else if (mode == "fused" && !hasText)
            {
                lexicalUnavailable = "no queryText for the lexical side";
            }

            // ---- fuse / degrade (FR-11: state what actually ran)
            String modeUsed;
            List<KeyValuePair<VertexModel, Single>> ranked;
            if (mode == "dense" || (mode == "fused" && lexical == null && dense != null))
            {
                modeUsed = "dense";
                ranked = dense;
            }
            else if (mode == "lexical" || (mode == "fused" && dense == null && lexical != null))
            {
                modeUsed = "lexical";
                ranked = lexical;
            }
            else if (dense != null && lexical != null)
            {
                modeUsed = "fused";
                ranked = Fuse(dense, lexical);
            }
            else
            {
                return SearchOutcome.Fail(StatusCodes.Status400BadRequest, "No side available",
                    String.Format("dense: {0}; lexical: {1}.", denseUnavailable ?? "unavailable",
                        lexicalUnavailable ?? "unavailable"));
            }

            // ---- enrich
            var hits = new List<ChunkHitREST>(Math.Min(k, ranked.Count));
            foreach (var entry in ranked.Take(k))
            {
                hits.Add(BuildHit(entry.Key, entry.Value, window));
            }

            var result = new DocumentSearchResultREST { ModeUsed = modeUsed };
            if (spec.GroupByDocument == true)
            {
                result.Documents = GroupByDocument(hits);
            }
            else
            {
                result.Hits = hits;
            }

            return new SearchOutcome { Result = result };
        }

        #region sides

        private async Task<(List<KeyValuePair<VertexModel, Single>> hits, String unavailable, SearchOutcome error)>
            TryDenseSide(DocumentSearchSpecification spec, Boolean hasText, Boolean hasVector, Int32 depth,
                CancellationToken cancellationToken)
        {
            if (!_fallen8.IndexFactory.TryGetIndex(out var index, _options.VectorIndexId) ||
                !(index is IVectorIndex vectorIndex))
            {
                return (null, String.Format("no vector index '{0}' in this namespace", _options.VectorIndexId), null);
            }

            Single[] query;
            if (hasVector)
            {
                if (spec.QueryVector.Count != vectorIndex.Dimension)
                {
                    return (null, null, SearchOutcome.Fail(StatusCodes.Status400BadRequest, "Dimension mismatch",
                        String.Format("queryVector has {0} components, index '{1}' requires {2}.",
                            spec.QueryVector.Count, _options.VectorIndexId, vectorIndex.Dimension)));
                }

                query = spec.QueryVector.ToArray();
            }
            else if (hasText && _embeddingOptions.Enabled)
            {
                // The existing provider consistency contract (embedding-provider FR-8),
                // one home: BoundIndexContract. The problem+json title stays per-kind via a
                // cheap dimension re-check so observable behavior is identical.
                var contractConflict = BoundIndexContract.FindConflictForIndex(vectorIndex, _options.VectorIndexId, _provider);
                if (contractConflict != null)
                {
                    var title = vectorIndex.Dimension != _provider.Identity.Dimension
                        ? "Dimension conflict"
                        : "Model identity conflict";
                    return (null, null, SearchOutcome.Fail(StatusCodes.Status409Conflict, title, contractConflict));
                }

                try
                {
                    var vectors = await _provider.EmbedAsync(
                        new[] { _provider.ApplyQueryPrefix(spec.QueryText) }, cancellationToken);
                    query = vectors[0];
                }
                catch (Exception ex) when (ex is EmbeddingProviderUnavailableException || ex is EmbeddingProviderOutputException)
                {
                    var (status, title) = EmbeddingProviderProblem.Map(ex);
                    return (null, null, SearchOutcome.Fail(status, title, ex.Message));
                }
            }
            else
            {
                return (null, "the embedding provider is off and no queryVector was supplied", null);
            }

            var constraint = new VectorSearchConstraint
            {
                Kind = VectorSearchElementKind.Vertex,
                Label = DocumentGraphSchema.ChunkLabel
            };
            if (!vectorIndex.TryNearestNeighbors(out var knn, query, Math.Min(depth, VectorIndex.MaxK), constraint))
            {
                return (null, null, SearchOutcome.Fail(StatusCodes.Status400BadRequest, "Invalid kNN query",
                    "the query vector is invalid for this index (zero-norm under Cosine, or wrong dimension)."));
            }

            var hits = new List<KeyValuePair<VertexModel, Single>>(knn.Entries.Count);
            foreach (var entry in knn.Entries)
            {
                // Defense-in-depth liveness re-check (spec Unverified: fulltext liveness).
                if (_fallen8.TryGetVertex(out var vertex, entry.Element.Id))
                {
                    hits.Add(new KeyValuePair<VertexModel, Single>(vertex, entry.Score));
                }
            }

            return (hits, null, null);
        }

        private (List<KeyValuePair<VertexModel, Single>>, String) LexicalSide(String queryText, Int32 depth)
        {
            if (!_fallen8.IndexFactory.TryGetIndex(out var index, _options.FulltextIndexId) ||
                !(index is IFulltextIndex fulltextIndex))
            {
                return (null, String.Format("no fulltext index '{0}' in this namespace", _options.FulltextIndexId));
            }

            var pattern = BuildLexicalPattern(queryText);
            if (pattern == null)
            {
                return (null, "queryText contains no usable tokens");
            }

            if (!fulltextIndex.TryQuery(out var result, pattern) || result == null)
            {
                return (new List<KeyValuePair<VertexModel, Single>>(), null);
            }

            var hits = new List<KeyValuePair<VertexModel, Single>>();
            foreach (var element in result.Elements)
            {
                if (_fallen8.TryGetVertex(out var vertex, element.GraphElement.Id) &&
                    String.Equals(vertex.Label, DocumentGraphSchema.ChunkLabel, StringComparison.Ordinal))
                {
                    hits.Add(new KeyValuePair<VertexModel, Single>(vertex, (Single)element.Score));
                }
            }

            // Best first, ties by ascending id - the deterministic rank RRF consumes.
            hits.Sort((a, b) => a.Value != b.Value
                ? b.Value.CompareTo(a.Value)
                : a.Key.Id.CompareTo(b.Key.Id));
            if (hits.Count > depth)
            {
                hits.RemoveRange(depth, hits.Count - depth);
            }

            return (hits, null);
        }

        /// <summary>Whitespace-tokenizes the query and ORs the ESCAPED tokens - the fulltext
        /// index takes a regex, user text must never inject one.</summary>
        public static String BuildLexicalPattern(String queryText)
        {
            var tokens = (queryText ?? String.Empty)
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(Regex.Escape)
                .ToList();
            return tokens.Count == 0 ? null : String.Join("|", tokens);
        }

        /// <summary>Reciprocal rank fusion over both sides (rank is 1-based position; a chunk
        /// missing from a side contributes nothing), ties by ascending element id.</summary>
        public static List<KeyValuePair<VertexModel, Single>> Fuse(
            List<KeyValuePair<VertexModel, Single>> dense,
            List<KeyValuePair<VertexModel, Single>> lexical)
        {
            var byId = new Dictionary<Int32, (VertexModel vertex, Single score)>();

            void Accumulate(List<KeyValuePair<VertexModel, Single>> side)
            {
                for (var rank = 0; rank < side.Count; rank++)
                {
                    var vertex = side[rank].Key;
                    var contribution = 1f / (RrfK + rank + 1);
                    byId[vertex.Id] = byId.TryGetValue(vertex.Id, out var current)
                        ? (vertex, current.score + contribution)
                        : (vertex, contribution);
                }
            }

            Accumulate(dense);
            Accumulate(lexical);

            return byId.Values
                .OrderByDescending(entry => entry.score)
                .ThenBy(entry => entry.vertex.Id)
                .Select(entry => new KeyValuePair<VertexModel, Single>(entry.vertex, entry.score))
                .ToList();
        }

        #endregion

        #region enrichment (FR-11/FR-12)

        private ChunkHitREST BuildHit(VertexModel chunk, Single score, Int32 window)
        {
            var hit = new ChunkHitREST
            {
                ChunkId = chunk.Id,
                Score = score,
                Text = StringProperty(chunk, DocumentGraphSchema.TextProperty),
                HeadingPath = StringProperty(chunk, DocumentGraphSchema.HeadingPathProperty)
            };

            if (chunk.TryGetProperty<Int32>(out var order, DocumentGraphSchema.OrderProperty))
            {
                hit.Order = order;
            }

            if (chunk.TryGetProperty<Int32>(out var pageFrom, DocumentGraphSchema.PageFromProperty))
            {
                hit.PageFrom = pageFrom;
            }

            if (chunk.TryGetProperty<Int32>(out var pageTo, DocumentGraphSchema.PageToProperty))
            {
                hit.PageTo = pageTo;
            }

            var identifiers = StringProperty(chunk, DocumentGraphSchema.IdentifiersProperty);
            if (identifiers != null)
            {
                hit.Identifiers = new List<String>(identifiers.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }

            if (chunk.TryGetInEdge(out var containsEdges, DocumentGraphSchema.ContainsEdge) && containsEdges.Count > 0)
            {
                hit.DocumentId = containsEdges[0].SourceVertex.Id;
            }

            if (window > 0)
            {
                hit.Window = BuildWindow(chunk, window);
            }

            return hit;
        }

        /// <summary>Up to <paramref name="window"/> chunks each side over <c>next</c> edges,
        /// in document order, the hit itself excluded.</summary>
        private List<ChunkWindowEntryREST> BuildWindow(VertexModel chunk, Int32 window)
        {
            var entries = new List<ChunkWindowEntryREST>();

            var backward = chunk;
            for (var i = 0; i < window; i++)
            {
                if (!backward.TryGetInEdge(out var previous, DocumentGraphSchema.NextEdge) || previous.Count == 0 ||
                    !_fallen8.TryGetVertex(out backward, previous[0].SourceVertex.Id))
                {
                    break;
                }

                entries.Insert(0, WindowEntry(backward));
            }

            var forward = chunk;
            for (var i = 0; i < window; i++)
            {
                if (!forward.TryGetOutEdge(out var next, DocumentGraphSchema.NextEdge) || next.Count == 0 ||
                    !_fallen8.TryGetVertex(out forward, next[0].TargetVertex.Id))
                {
                    break;
                }

                entries.Add(WindowEntry(forward));
            }

            return entries;
        }

        private static ChunkWindowEntryREST WindowEntry(VertexModel chunk)
        {
            var entry = new ChunkWindowEntryREST
            {
                ChunkId = chunk.Id,
                Text = StringProperty(chunk, DocumentGraphSchema.TextProperty)
            };
            if (chunk.TryGetProperty<Int32>(out var order, DocumentGraphSchema.OrderProperty))
            {
                entry.Order = order;
            }

            return entry;
        }

        /// <summary>The FR-11 grouping contract: documents ordered by their best hit, chunks
        /// within a document by document position, duplicates collapsed.</summary>
        private List<DocumentGroupREST> GroupByDocument(List<ChunkHitREST> hits)
        {
            var groups = new List<DocumentGroupREST>();
            foreach (var byDocument in hits.GroupBy(hit => hit.DocumentId))
            {
                var group = new DocumentGroupREST
                {
                    BestScore = byDocument.Max(hit => hit.Score),
                    Chunks = byDocument
                        .GroupBy(hit => hit.ChunkId).Select(duplicates => duplicates.First())
                        .OrderBy(hit => hit.Order)
                        .ToList()
                };

                if (byDocument.Key != null && _fallen8.TryGetVertex(out var documentVertex, byDocument.Key.Value))
                {
                    group.Document = _documents.Summarize(documentVertex);
                }

                groups.Add(group);
            }

            return groups
                .OrderByDescending(group => group.BestScore)
                .ThenBy(group => group.Document?.DocumentId ?? Int32.MaxValue)
                .ToList();
        }

        private static String StringProperty(VertexModel vertex, String propertyId)
        {
            return vertex.TryGetProperty<String>(out var value, propertyId) ? value : null;
        }

        #endregion
    }
}
