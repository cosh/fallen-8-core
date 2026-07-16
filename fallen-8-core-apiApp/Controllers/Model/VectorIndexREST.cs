// MIT License
//
// VectorIndexREST.cs
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
    /// <summary>
    ///   Adds (or replaces) an element's vector in a vector index (feature vector-index).
    ///   Exactly one mode: an explicit <see cref="Vector"/>, or <see cref="PropertyId"/> naming
    ///   a <c>float[]</c> property on the element to read the vector from.
    /// </summary>
    /// <example>
    /// { "graphElementId": 42, "vector": [0.12, -0.5, 0.33] }
    /// </example>
    public sealed class VectorIndexAddSpecification
    {
        /// <summary>The element to index.</summary>
        /// <example>42</example>
        [Required]
        [JsonPropertyName("graphElementId")]
        public Int32 GraphElementId
        {
            get; set;
        }

        /// <summary>The embedding vector (explicit mode). Must match the index dimension.</summary>
        [JsonPropertyName("vector")]
        public Single[] Vector
        {
            get; set;
        }

        /// <summary>The element property holding the vector (property mode); the property must
        /// be a float[] of the index dimension.</summary>
        /// <example>embedding</example>
        [JsonPropertyName("propertyId")]
        public String PropertyId
        {
            get; set;
        }
    }

    /// <summary>
    ///   A k-nearest-neighbour query against a vector index (feature vector-index).
    /// </summary>
    /// <example>
    /// { "indexId": "myEmbeddings", "query": [0.1, 0.2, 0.3], "k": 10, "kind": "vertex", "label": "person" }
    /// </example>
    public sealed class VectorIndexScanSpecification
    {
        /// <summary>The vector index to query.</summary>
        /// <example>myEmbeddings</example>
        [Required]
        [JsonPropertyName("indexId")]
        public String IndexId
        {
            get; set;
        }

        /// <summary>The query vector; must match the index dimension.</summary>
        [Required]
        [JsonPropertyName("query")]
        public Single[] Query
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

        /// <summary>Optional exact (case-sensitive) label constraint; an unlabeled element never
        /// matches.</summary>
        /// <example>person</example>
        [JsonPropertyName("label")]
        public String Label
        {
            get; set;
        }
    }

    /// <summary>One kNN hit: the element id and its RAW score under the index metric.</summary>
    public sealed class VectorScoredElementREST
    {
        /// <summary>The element id.</summary>
        /// <example>7</example>
        [JsonPropertyName("graphElementId")]
        public Int32 GraphElementId
        {
            get; set;
        }

        /// <summary>The raw score (no normalization; interpret via metric/higherIsBetter).</summary>
        /// <example>0.93</example>
        [JsonPropertyName("score")]
        public Single Score
        {
            get; set;
        }
    }

    /// <summary>
    ///   A kNN result: hits best-first plus the metric and its direction, so an L2 distance can
    ///   never be misread as a similarity.
    /// </summary>
    public sealed class VectorSearchResultREST
    {
        /// <summary>The index's metric: Cosine, DotProduct or L2.</summary>
        /// <example>Cosine</example>
        [JsonPropertyName("metric")]
        public String Metric
        {
            get; set;
        }

        /// <summary>Whether a HIGHER score is better (false for L2).</summary>
        /// <example>true</example>
        [JsonPropertyName("higherIsBetter")]
        public Boolean HigherIsBetter
        {
            get; set;
        }

        /// <summary>The hits, best first; ties broken by ascending element id.</summary>
        [JsonPropertyName("results")]
        public List<VectorScoredElementREST> Results
        {
            get; set;
        }
    }
}
