// MIT License
//
// IngestionStatsREST.cs
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
using System.Threading;
using System.Threading.Tasks;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Ingestion;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   The unstructured-ingestion capability state on the discovery surfaces (spec FR-1,
    ///   /status and /statistics): the flag, the formats each path accepts, the sidecar
    ///   probe (cached short-TTL, never a conversion), the enforced limits and the ensured
    ///   index ids. F8 Studio gates its upload UI on this block.
    /// </summary>
    public sealed class IngestionStatsREST
    {
        public Boolean Enabled
        {
            get; set;
        }

        /// <summary>Formats that ingest without the sidecar.</summary>
        public List<String> TextFormats
        {
            get; set;
        }

        /// <summary>Formats that need the docling sidecar.</summary>
        public List<String> BinaryFormats
        {
            get; set;
        }

        public DoclingStatsREST Docling
        {
            get; set;
        }

        public IngestionLimitsREST Limits
        {
            get; set;
        }

        /// <summary>The element-embedding name chunk vectors are written under.</summary>
        public String EmbeddingName
        {
            get; set;
        }

        /// <summary>The ensured bound vector index id (null when EnsureVectorIndex is off).</summary>
        public String VectorIndexId
        {
            get; set;
        }

        /// <summary>The ensured fulltext index id (null when EnsureFulltextIndex is off -
        /// fused search then degrades to dense-only).</summary>
        public String FulltextIndexId
        {
            get; set;
        }

        /// <summary>Builds the block; null when the host wired no ingestion options (direct
        /// unit construction).</summary>
        public static async Task<IngestionStatsREST> From(Fallen8IngestionOptions options,
            IDoclingConverter converter, CancellationToken cancellationToken)
        {
            if (options == null)
            {
                return null;
            }

            var configured = converter != null && converter.Configured;
            return new IngestionStatsREST
            {
                Enabled = options.Enabled,
                TextFormats = new List<String>(DocumentGraphSchema.TextFormats),
                BinaryFormats = new List<String>(DocumentGraphSchema.BinaryFormats),
                Docling = new DoclingStatsREST
                {
                    Configured = configured,
                    Reachable = configured && await converter.IsReachableAsync(cancellationToken)
                },
                Limits = new IngestionLimitsREST
                {
                    MaxUploadBytes = options.MaxUploadBytes,
                    MaxPages = options.MaxPages,
                    MaxChunksPerDocument = options.MaxChunksPerDocument,
                    MaxChunksPerNamespace = options.MaxChunksPerNamespace,
                    MaxLinksPerChunk = options.MaxLinksPerChunk
                },
                EmbeddingName = options.EmbeddingName,
                VectorIndexId = options.EnsureVectorIndex ? options.VectorIndexId : null,
                FulltextIndexId = options.EnsureFulltextIndex ? options.FulltextIndexId : null
            };
        }
    }

    /// <summary>The sidecar's config/health state (spec FR-1).</summary>
    public sealed class DoclingStatsREST
    {
        public Boolean Configured
        {
            get; set;
        }

        /// <summary>A cached, short-TTL health probe - reading status stays cheap.</summary>
        public Boolean Reachable
        {
            get; set;
        }
    }

    /// <summary>The enforced ingestion bounds (spec FR-1/FR-14).</summary>
    public sealed class IngestionLimitsREST
    {
        public Int64 MaxUploadBytes
        {
            get; set;
        }

        public Int32 MaxPages
        {
            get; set;
        }

        public Int32 MaxChunksPerDocument
        {
            get; set;
        }

        public Int32 MaxChunksPerNamespace
        {
            get; set;
        }

        public Int32 MaxLinksPerChunk
        {
            get; set;
        }
    }
}
