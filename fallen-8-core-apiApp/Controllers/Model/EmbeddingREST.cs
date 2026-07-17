// MIT License
//
// EmbeddingREST.cs
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
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>Embed a text and store it as the element's named embedding (feature
    /// embedding-provider).</summary>
    /// <example>
    /// { "graphElementId": 42, "text": "a red bicycle", "name": "default" }
    /// </example>
    public sealed class EmbedElementSpecification
    {
        /// <summary>The target graph element id.</summary>
        /// <example>42</example>
        [Required]
        [JsonPropertyName("graphElementId")]
        public Int32 GraphElementId
        {
            get; set;
        }

        /// <summary>The text to embed.</summary>
        /// <example>a red bicycle</example>
        [Required]
        [JsonPropertyName("text")]
        public String Text
        {
            get; set;
        }

        /// <summary>The embedding name (default "default").</summary>
        /// <example>default</example>
        [JsonPropertyName("name")]
        public String Name
        {
            get; set;
        }
    }

    /// <summary>One item of a batch embed (feature embedding-provider).</summary>
    public sealed class EmbedElementItem
    {
        /// <summary>The target graph element id.</summary>
        /// <example>42</example>
        [Required]
        [JsonPropertyName("graphElementId")]
        public Int32 GraphElementId
        {
            get; set;
        }

        /// <summary>The text to embed.</summary>
        [Required]
        [JsonPropertyName("text")]
        public String Text
        {
            get; set;
        }
    }

    /// <summary>Embed a batch of texts onto elements in one provider batch and one
    /// transaction (feature embedding-provider) - the bulk-ingestion path.</summary>
    /// <example>
    /// { "name": "default", "items": [ { "graphElementId": 1, "text": "..." } ] }
    /// </example>
    public sealed class EmbedElementsSpecification
    {
        /// <summary>The embedding name for the whole batch (default "default").</summary>
        /// <example>default</example>
        [JsonPropertyName("name")]
        public String Name
        {
            get; set;
        }

        /// <summary>The batch (bounded by Fallen8:Embedding:MaxBatchSize).</summary>
        [Required]
        [JsonPropertyName("items")]
        public List<EmbedElementItem> Items
        {
            get; set;
        }
    }

    /// <summary>Semantic search: embed a query text and run kNN against a vector index
    /// (feature embedding-provider).</summary>
    /// <example>
    /// { "indexId": "embeddings", "text": "red bicycles", "k": 10, "kind": "vertex" }
    /// </example>
    public sealed class EmbeddingSearchSpecification
    {
        /// <summary>The vector index to query.</summary>
        /// <example>embeddings</example>
        [Required]
        [JsonPropertyName("indexId")]
        public String IndexId
        {
            get; set;
        }

        /// <summary>The query text (embedded once, with the configured query prefix).</summary>
        /// <example>red bicycles</example>
        [Required]
        [JsonPropertyName("text")]
        public String Text
        {
            get; set;
        }

        /// <summary>How many nearest neighbours to return (1..1024).</summary>
        /// <example>10</example>
        [Required]
        [JsonPropertyName("k")]
        public Int32 K
        {
            get; set;
        }

        /// <summary>Optional element-kind constraint: vertex, edge, or any (default).</summary>
        /// <example>vertex</example>
        [JsonPropertyName("kind")]
        public String Kind
        {
            get; set;
        }

        /// <summary>Optional exact (case-sensitive) label constraint.</summary>
        /// <example>person</example>
        [JsonPropertyName("label")]
        public String Label
        {
            get; set;
        }
    }

    /// <summary>Embed raw texts and return the vectors (feature embedding-provider) - for
    /// clients that drive the raw vector surfaces themselves.</summary>
    /// <example>
    /// { "texts": ["a red bicycle", "a blue car"] }
    /// </example>
    public sealed class EmbedTextSpecification
    {
        /// <summary>The texts to embed (bounded by Fallen8:Embedding:MaxBatchSize).</summary>
        [Required]
        [JsonPropertyName("texts")]
        public List<String> Texts
        {
            get; set;
        }
    }

    /// <summary>Raw embedding vectors plus the identity they came from.</summary>
    public sealed class EmbeddingVectorsREST
    {
        /// <summary>The provider's model-identity stamp.</summary>
        /// <example>bge-micro-v2#384#Cosine</example>
        [JsonPropertyName("model")]
        public String Model
        {
            get; set;
        }

        /// <summary>The vector dimension.</summary>
        /// <example>384</example>
        [JsonPropertyName("dimension")]
        public Int32 Dimension
        {
            get; set;
        }

        /// <summary>One vector per input text, in order.</summary>
        [JsonPropertyName("vectors")]
        public List<Single[]> Vectors
        {
            get; set;
        }
    }
}
