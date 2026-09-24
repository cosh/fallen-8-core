// MIT License
//
// DocumentModels.cs
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
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>Opt-in structural linking (spec unstructured-ingestion FR-13): exact-match
    /// identifier lookups against an explicit allowlist of equality-capable indices.</summary>
    public sealed class LinkSpecificationREST
    {
        /// <summary>The index ids to match extracted identifiers against. Each must be
        /// equality-capable (dictionary, range, single-value or fulltext); a vector or spatial
        /// index is rejected with 400.</summary>
        public List<String> IndexIds
        {
            get; set;
        }

        /// <summary>Optional per-request cap; never above Fallen8:Ingestion:MaxLinksPerChunk.</summary>
        /// <example>8</example>
        public Int32? MaxLinksPerChunk
        {
            get; set;
        }
    }

    /// <summary>Raw-text ingestion (spec unstructured-ingestion FR-3) - works without the
    /// docling sidecar.</summary>
    public sealed class IngestTextSpecification
    {
        /// <summary>The document name.</summary>
        /// <example>edge-server-notes</example>
        public String Name
        {
            get; set;
        }

        /// <summary>The content.</summary>
        public String Text
        {
            get; set;
        }

        /// <summary><c>markdown</c> (default, heading-aware chunking) or <c>plain</c>.</summary>
        /// <example>markdown</example>
        public String Format
        {
            get; set;
        }

        /// <summary>Embed the chunks (default true). Requires the embedding provider; pass
        /// false to ingest text-only.</summary>
        public Boolean? Embed
        {
            get; set;
        }

        /// <summary>User tag properties applied to the document and every chunk. Keys of the
        /// document graph model are reserved (400).</summary>
        public Dictionary<String, String> Properties
        {
            get; set;
        }

        /// <summary>Optional source pointer stored on the document vertex.</summary>
        /// <example>https://wiki.example/edge-servers</example>
        public String SourceUri
        {
            get; set;
        }

        /// <summary>Replace this document (FR-15): the new content is ingested fully first,
        /// the old document and its chunks are removed on success.</summary>
        public Int32? ReplaceDocumentId
        {
            get; set;
        }

        /// <summary>Opt-in structural linking (FR-13).</summary>
        public LinkSpecificationREST Link
        {
            get; set;
        }
    }

    /// <summary>One document as reported by the ingest/list/get surfaces (FR-2/FR-7).</summary>
    public sealed class DocumentSummaryREST
    {
        public Int32 DocumentId
        {
            get; set;
        }

        public String Name
        {
            get; set;
        }

        /// <example>pdf</example>
        public String SourceFormat
        {
            get; set;
        }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public String SourceUri
        {
            get; set;
        }

        /// <summary><c>processing</c>, <c>indexed</c> or <c>failed</c>.</summary>
        /// <example>indexed</example>
        public String Status
        {
            get; set;
        }

        /// <summary>The failure reason (failed documents only).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public String Error
        {
            get; set;
        }

        public Int32 ChunkCount
        {
            get; set;
        }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Int32? PageCount
        {
            get; set;
        }

        /// <summary>SHA-256 of the ingested bytes - the duplicate/re-ingestion currency.</summary>
        public String ContentHash
        {
            get; set;
        }

        /// <summary><c>docling-serve</c> or <c>none</c>.</summary>
        public String Converter
        {
            get; set;
        }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public String ChunkerConfig
        {
            get; set;
        }

        /// <summary>The model identity the chunks were embedded with (absent when unembedded).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public String EmbeddingModel
        {
            get; set;
        }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Int32? EmbeddingDimension
        {
            get; set;
        }

        public Boolean Embedded
        {
            get; set;
        }

        /// <summary>True when the recorded embedding model differs from the active provider
        /// (FR-16) - queries embed with the new model, these chunks carry the old one.</summary>
        public Boolean EmbeddingModelStale
        {
            get; set;
        }
    }

    /// <summary>GET /document: the namespace's documents plus the chunk budget (FR-14).</summary>
    public sealed class DocumentListREST
    {
        public List<DocumentSummaryREST> Documents
        {
            get; set;
        }

        /// <summary>Live chunks in this namespace.</summary>
        public Int32 NamespaceChunkCount
        {
            get; set;
        }

        /// <summary>Fallen8:Ingestion:MaxChunksPerNamespace.</summary>
        public Int32 ChunkCeiling
        {
            get; set;
        }

        /// <summary>The active provider's model identity (null while the provider is off) -
        /// the reference for <see cref="DocumentSummaryREST.EmbeddingModelStale"/>.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public String CurrentEmbeddingModel
        {
            get; set;
        }
    }

    /// <summary>One chunk on GET /document/{id} - a PREVIEW; the full text is the chunk
    /// vertex's <c>text</c> property, readable via the graph element routes.</summary>
    public sealed class ChunkSummaryREST
    {
        public Int32 ChunkId
        {
            get; set;
        }

        public Int32 Order
        {
            get; set;
        }

        /// <example>text</example>
        public String Kind
        {
            get; set;
        }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public String HeadingPath
        {
            get; set;
        }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Int32? PageFrom
        {
            get; set;
        }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Int32? PageTo
        {
            get; set;
        }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<String> Identifiers
        {
            get; set;
        }

        public String TextPreview
        {
            get; set;
        }
    }

    /// <summary>GET /document/{id}: the summary plus its chunks in document order.</summary>
    public sealed class DocumentDetailREST
    {
        public DocumentSummaryREST Summary
        {
            get; set;
        }

        public List<ChunkSummaryREST> Chunks
        {
            get; set;
        }
    }

    /// <summary>POST /document/search (spec unstructured-ingestion FR-11): fused chunk
    /// retrieval over the bound vector index and the fulltext index.</summary>
    public sealed class DocumentSearchSpecification
    {
        /// <summary>The query. Feeds the lexical side always, and the dense side when the
        /// embedding provider is on and no queryVector is supplied.</summary>
        /// <example>the server that terminates tls for the shop</example>
        public String QueryText
        {
            get; set;
        }

        /// <summary>Optional client-side dense query vector (bring-your-own-vector).</summary>
        public List<Single> QueryVector
        {
            get; set;
        }

        /// <summary><c>fused</c> (default), <c>dense</c> or <c>lexical</c>.</summary>
        /// <example>fused</example>
        public String Mode
        {
            get; set;
        }

        /// <summary>Results to return (default 10, max 100).</summary>
        /// <example>10</example>
        public Int32? K
        {
            get; set;
        }

        /// <summary>Sibling chunks each side of a hit over <c>next</c> edges (default 0, max 5).</summary>
        /// <example>1</example>
        public Int32? Window
        {
            get; set;
        }

        /// <summary>Group hits per document: documents by best hit, chunks by document
        /// position, duplicates collapsed (FR-11).</summary>
        public Boolean? GroupByDocument
        {
            get; set;
        }
    }

    /// <summary>The fused search answer. <c>modeUsed</c> states what actually ran: a fused
    /// request degrades honestly when one side is unavailable.</summary>
    public sealed class DocumentSearchResultREST
    {
        /// <example>fused</example>
        public String ModeUsed
        {
            get; set;
        }

        /// <summary>Flat hits, best first (absent when grouped).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ChunkHitREST> Hits
        {
            get; set;
        }

        /// <summary>Per-document groups (groupByDocument=true only).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<DocumentGroupREST> Documents
        {
            get; set;
        }
    }

    /// <summary>One retrieved chunk. The score is mode-dependent: RRF when fused, the raw
    /// kNN score when dense, the fulltext match count when lexical.</summary>
    public sealed class ChunkHitREST
    {
        public Int32 ChunkId
        {
            get; set;
        }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Int32? DocumentId
        {
            get; set;
        }

        public Single Score
        {
            get; set;
        }

        public Int32 Order
        {
            get; set;
        }

        public String Text
        {
            get; set;
        }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public String HeadingPath
        {
            get; set;
        }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Int32? PageFrom
        {
            get; set;
        }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Int32? PageTo
        {
            get; set;
        }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<String> Identifiers
        {
            get; set;
        }

        /// <summary>Sibling chunks in document order, the hit itself excluded (window &gt; 0).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ChunkWindowEntryREST> Window
        {
            get; set;
        }
    }

    /// <summary>One sibling chunk of a hit's window (FR-12).</summary>
    public sealed class ChunkWindowEntryREST
    {
        public Int32 ChunkId
        {
            get; set;
        }

        public Int32 Order
        {
            get; set;
        }

        public String Text
        {
            get; set;
        }
    }

    /// <summary>Hits of one document (groupByDocument=true).</summary>
    public sealed class DocumentGroupREST
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DocumentSummaryREST Document
        {
            get; set;
        }

        public Single BestScore
        {
            get; set;
        }

        public List<ChunkHitREST> Chunks
        {
            get; set;
        }
    }
}
