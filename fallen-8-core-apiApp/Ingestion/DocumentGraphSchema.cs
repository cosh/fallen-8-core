// MIT License
//
// DocumentGraphSchema.cs
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

namespace NoSQL.GraphDB.App.Ingestion
{
    /// <summary>
    ///   THE single home for the document graph model (spec unstructured-ingestion FR-4):
    ///   labels, edge types, property keys and status values. Documents and chunks are
    ///   ordinary vertices - these constants are a naming convention, not an engine concept.
    /// </summary>
    public static class DocumentGraphSchema
    {
        public const String DocumentLabel = "Document";
        public const String ChunkLabel = "Chunk";

        public const String ContainsEdge = "contains";
        public const String NextEdge = "next";
        public const String MentionsEdge = "mentions";

        public const String NameProperty = "name";
        public const String SourceFormatProperty = "sourceFormat";
        public const String SourceUriProperty = "sourceUri";
        public const String StatusProperty = "status";
        public const String ErrorProperty = "error";
        public const String ChunkCountProperty = "chunkCount";
        public const String PageCountProperty = "pageCount";
        public const String ContentHashProperty = "contentHash";
        public const String ConverterProperty = "converter";
        public const String ChunkerConfigProperty = "chunkerConfig";
        public const String EmbeddingModelProperty = "embeddingModel";
        public const String EmbeddingDimensionProperty = "embeddingDimension";

        public const String TextProperty = "text";
        public const String OrderProperty = "order";
        public const String KindProperty = "kind";
        public const String HeadingPathProperty = "headingPath";
        public const String PageFromProperty = "pageFrom";
        public const String PageToProperty = "pageTo";

        /// <summary>Space-joined so the value stays inside the bulk-export literal
        /// allow-list (a string, not a list type).</summary>
        public const String IdentifiersProperty = "identifiers";

        public const String StatusProcessing = "processing";
        public const String StatusIndexed = "indexed";
        public const String StatusFailed = "failed";

        public const String DoclingConverter = "docling-serve";
        public const String NoConverter = "none";

        /// <summary>Chunk preview length on GET /document/{id} - the full text stays one
        /// home, the chunk vertex's <see cref="TextProperty"/>.</summary>
        public const Int32 TextPreviewLength = 240;

        /// <summary>Keys a user tag may not shadow (400 on collision).</summary>
        public static readonly HashSet<String> ReservedPropertyKeys = new HashSet<String>(StringComparer.Ordinal)
        {
            NameProperty, SourceFormatProperty, SourceUriProperty, StatusProperty, ErrorProperty,
            ChunkCountProperty, PageCountProperty, ContentHashProperty, ConverterProperty,
            ChunkerConfigProperty, EmbeddingModelProperty, EmbeddingDimensionProperty,
            TextProperty, OrderProperty, KindProperty, HeadingPathProperty,
            PageFromProperty, PageToProperty, IdentifiersProperty
        };

        /// <summary>Formats that ingest without the sidecar.</summary>
        public static readonly String[] TextFormats = { "txt", "md" };

        /// <summary>Formats that need the docling sidecar.</summary>
        public static readonly String[] BinaryFormats = { "pdf", "docx", "xlsx", "pptx", "html" };
    }
}
