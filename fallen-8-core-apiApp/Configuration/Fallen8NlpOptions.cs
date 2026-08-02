// MIT License
//
// Fallen8NlpOptions.cs
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
    ///   The semantic-layer NLP enrichment configuration (feature semantic-layer), section
    ///   <c>Fallen8:Nlp</c>. Default OFF: ingestion runs without enrichment and the entity
    ///   network is simply empty (enrichment is ADDITIVE - never a hard failure). Unlike the
    ///   other capabilities there is no gated REST endpoint; the flag only tells the ingestion
    ///   pipeline whether to call the <c>fallen-8-nlp</c> sidecar.
    /// </summary>
    public sealed class Fallen8NlpOptions
    {
        public const String SectionName = "Fallen8:Nlp";

        /// <summary>Whether ingestion enriches chunks via the NLP sidecar. Default off.</summary>
        public Boolean Enabled
        {
            get; set;
        }

        /// <summary>The fallen-8-nlp endpoint (empty: not configured - no enrichment).</summary>
        public String Endpoint { get; set; } = String.Empty;

        /// <summary>Per-enrich request timeout.</summary>
        public Int32 TimeoutSeconds { get; set; } = 60;

        /// <summary>Chunk text longer than this is truncated before being sent (the sidecar has
        /// its own hard cap; this keeps requests small).</summary>
        public Int32 MaxCharsPerChunk { get; set; } = 20000;

        /// <summary>Chunks per enrich request batch.</summary>
        public Int32 MaxBatchSize { get; set; } = 128;

        /// <summary>Max <c>mentions</c> edges written per chunk from extracted entities.</summary>
        public Int32 MaxEntitiesPerChunk { get; set; } = 32;

        /// <summary>Max key terms stored per chunk.</summary>
        public Int32 MaxKeyTermsPerChunk { get; set; } = 32;

        /// <summary>Optional ISO-639-1 language hint sent with every request (empty: the sidecar
        /// detects per document).</summary>
        public String LanguageHint { get; set; } = String.Empty;
    }
}
