// MIT License
//
// Fallen8IngestionOptions.cs
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

namespace NoSQL.GraphDB.App.Configuration
{
    /// <summary>
    ///   The unstructured-ingestion configuration (feature unstructured-ingestion), section
    ///   <c>Fallen8:Ingestion</c>. Default OFF: the document endpoints answer 403 and no
    ///   sidecar is contacted. Document conversion runs in a docling-serve sidecar the
    ///   operator provides; plain text and markdown ingest without it.
    /// </summary>
    public sealed class Fallen8IngestionOptions
    {
        public const String SectionName = "Fallen8:Ingestion";

        /// <summary>The authorization policy gating the document surface
        /// (<see cref="Security.DynamicCapabilityRequirement.Capability.Ingestion" />).</summary>
        public const String IngestionPolicy = "Fallen8.Ingestion";

        /// <summary>The capability flag. Default off.</summary>
        public Boolean Enabled
        {
            get; set;
        }

        /// <summary>Upload byte cap, enforced before any parsing (413 above it).</summary>
        public Int64 MaxUploadBytes { get; set; } = 33_554_432;

        /// <summary>Page cap, enforced after conversion from the converted document's page
        /// count; a longer document fails the ingest rather than being silently truncated.</summary>
        public Int32 MaxPages { get; set; } = 500;

        /// <summary>Chunk cap per document; crossing it fails the ingest.</summary>
        public Int32 MaxChunksPerDocument { get; set; } = 2_000;

        /// <summary>The per-namespace chunk ceiling (FR-14): reaching it rejects further
        /// ingestion with 507 instead of letting the in-memory engine grow toward OOM.</summary>
        public Int32 MaxChunksPerNamespace { get; set; } = 100_000;

        /// <summary>Depth of the single global ingestion queue (feature semantic-layer). Enqueue
        /// beyond this answers 503; one consumer drains it in arrival order.</summary>
        public Int32 MaxQueueLength { get; set; } = 256;

        /// <summary>Adjacent chunks below this merge (chars).</summary>
        public Int32 ChunkMinChars { get; set; } = 800;

        /// <summary>
        ///   Chunks above this split at paragraph / table-row boundaries (chars). It is a TOKEN
        ///   budget wearing a char unit: 3,600 keeps a chunk under ~1,800 <c>bge-m3</c> tokens, and
        ///   so under the 2,048-token per-input ceiling the embedding backends actually enforce, at
        ///   the 2.0 chars/token worst case measured over markdown tables (2.10), ARXML (2.23) and
        ///   punctuation-dense text (2.04). Latin prose runs ~4.0, so it costs ordinary documents
        ///   nothing.
        ///   <para>
        ///     It was 4,000, derived from the 8,192-token window <c>bge-m3</c> advertises. Neither
        ///     backend honours that window - measured, both the local Ollama sidecar and Nahil stop
        ///     at 2,048 - and above the real ceiling the backend used to shorten the input and
        ///     return a vector for its first ~2,046 tokens, so a chunk was indexed under a vector of
        ///     only part of itself with nothing in any log to say so. To be exact about the old
        ///     value rather than alarmed about it: 4,000 was INSIDE that ceiling for every
        ///     Latin-script, table and XML sample measured, but by under 70 tokens at the densest of
        ///     them (~1,980 of 2,048) - no margin for a denser page - and already OUTSIDE it for
        ///     Korean (~2,400) and Chinese (~3,010). 3,600 buys ~250 tokens of margin and costs
        ///     prose nothing.
        ///   </para>
        ///   <para>
        ///     The silence itself is closed at the other end, where the provider sends
        ///     <c>truncate: false</c> and the backend refuses instead of shortening (see
        ///     <see cref="Embedding.Fallen8EmbeddingProvider" />). This default is what keeps
        ///     ordinary documents from reaching that refusal; the two only work as a pair.
        ///   </para>
        ///   <para>
        ///     Two things it does NOT make impossible, both loud rather than silent now. A corpus in
        ///     a denser script wants a lower value - CJK measured 1.33-2.02 chars/token, so ~1,800
        ///     here for Chinese. And a single table ROW longer than this is still emitted whole, on
        ///     purpose: a row-window always carries at least one body row, so the alternative is
        ///     cutting a row in half. Either case now fails the ingest naming this key.
        ///   </para>
        /// </summary>
        public Int32 ChunkMaxChars { get; set; } = 3_600;

        /// <summary>Identifier tokens kept per chunk (first occurrence wins).</summary>
        public Int32 MaxIdentifiersPerChunk { get; set; } = 64;

        /// <summary>Hard cap for <c>mentions</c> edges per chunk (FR-13); a request may ask
        /// for less, never for more.</summary>
        public Int32 MaxLinksPerChunk { get; set; } = 16;

        /// <summary>The element-embedding name chunk vectors are written under.</summary>
        public String EmbeddingName { get; set; } = "default";

        /// <summary>Ensure a bound vector index over <see cref="EmbeddingName"/> on first
        /// ingest (FR-5). Disabling leaves kNN to operator-managed indices.</summary>
        public Boolean EnsureVectorIndex { get; set; } = true;

        /// <summary>The ensured vector index id.</summary>
        public String VectorIndexId { get; set; } = "documents";

        /// <summary>Ensure a fulltext index over chunk text on first ingest (FR-5). Disabling
        /// degrades fused search to dense-only (stated in /status).</summary>
        public Boolean EnsureFulltextIndex { get; set; } = true;

        /// <summary>The ensured fulltext index id.</summary>
        public String FulltextIndexId { get; set; } = "documents-text";

        /// <summary>Ensure the entity dedup index (feature semantic-layer): a dictionary index
        /// over the Entity vertices' dedup key, so an entity is one vertex per namespace.</summary>
        public Boolean EnsureEntityIndex { get; set; } = true;

        /// <summary>The ensured entity dedup index id.</summary>
        public String EntityIndexId { get; set; } = "documents-entities";

        /// <summary>docling-serve sidecar settings.</summary>
        public DoclingOptions Docling { get; set; } = new DoclingOptions();

        public sealed class DoclingOptions
        {
            /// <summary>The docling-serve endpoint (empty: not configured - text and markdown
            /// still ingest, binary formats answer 503).</summary>
            public String Endpoint { get; set; } = String.Empty;

            /// <summary>OVERALL budget for an async conversion (submit + poll loop + result).
            /// Async ingestion runs off-thread, so this can be generous for a large scanned PDF
            /// without holding any request open.</summary>
            public Int32 TimeoutSeconds { get; set; } = 600;

            /// <summary>Seconds between task-status polls.</summary>
            public Int32 PollIntervalSeconds { get; set; } = 2;

            /// <summary>Run OCR. Default FALSE: born-digital PDFs need none, and OCR is the
            /// dominant cost on large scanned documents. Turn on for scanned corpora, accepting
            /// the latency.</summary>
            public Boolean DoOcr
            {
                get; set;
            }

            /// <summary>Table structure detection: <c>fast</c> (default) or <c>accurate</c>.</summary>
            public String TableMode { get; set; } = "fast";

            /// <summary>Optional OCR engine name (docling default when empty).</summary>
            public String OcrEngine { get; set; } = String.Empty;
        }
    }
}
