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

        /// <summary>Chunks above this split at paragraph / table-row boundaries (chars).</summary>
        public Int32 ChunkMaxChars { get; set; } = 4_000;

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
            /// dominant cost on large scanned documents (the reason the Gutachten was slow).
            /// Turn on for scanned corpora, accepting the latency.</summary>
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
