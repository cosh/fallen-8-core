// MIT License
//
// SemanticTraversalSpecification.cs
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
using System.Linq;
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   The declarative semantic block of a path or subgraph request (feature
    ///   element-embeddings). Carries the query vector for the traversal context - embedded
    ///   ONCE, before the traversal starts - plus optional code-free filter/cost derivations.
    ///   Pure data, so it needs no dynamic-code capability; conflicts with a C# fragment or a
    ///   stored query owning the same delegate slot are rejected (one owner per slot).
    /// </summary>
    /// <example>
    /// { "queryVector": [0.1, 0.2], "embeddingName": "default", "metric": "Cosine", "minScore": 0.7 }
    /// </example>
    public sealed class SemanticTraversalSpecification : IEquatable<SemanticTraversalSpecification>
    {
        /// <summary>The query vector to score elements against.</summary>
        [JsonPropertyName("queryVector")]
        public Single[] QueryVector
        {
            get; set;
        }

        /// <summary>
        ///   A query TEXT to embed instead of supplying <see cref="QueryVector" /> (feature
        ///   embedding-provider; mutually exclusive with it). Embedded ONCE, before the
        ///   traversal starts, by the active provider - requires the EmbeddingProvider
        ///   capability (403 when <c>Fallen8:Embedding:Enabled</c> is off).
        /// </summary>
        /// <example>red bicycles</example>
        [JsonPropertyName("queryText")]
        public String QueryText
        {
            get; set;
        }

        /// <summary>The embedding name to score (default "default").</summary>
        /// <example>default</example>
        [JsonPropertyName("embeddingName")]
        public String EmbeddingName
        {
            get; set;
        }

        /// <summary>The metric: Cosine (default), DotProduct or L2.</summary>
        /// <example>Cosine</example>
        [JsonPropertyName("metric")]
        public String Metric
        {
            get; set;
        }

        /// <summary>
        ///   Optional declarative filter threshold: an element passes when its named embedding
        ///   scores at least this well (at most, under L2); elements without the embedding are
        ///   filtered.
        /// </summary>
        /// <example>0.7</example>
        [JsonPropertyName("minScore")]
        public Double? MinScore
        {
            get; set;
        }

        /// <summary>
        ///   Optional declarative vertex cost (path requests only): Cosine maps to
        ///   <c>1 - score</c>, L2 to the distance itself; DotProduct has no honest non-negative
        ///   mapping and is rejected. Vertices without the embedding are filtered.
        /// </summary>
        /// <example>true</example>
        [JsonPropertyName("costBySimilarity")]
        public Boolean CostBySimilarity
        {
            get; set;
        }

        public Boolean Equals(SemanticTraversalSpecification other)
        {
            if (other is null)
            {
                return false;
            }

            var vectorsEqual = QueryVector == other.QueryVector ||
                (QueryVector != null && other.QueryVector != null && QueryVector.SequenceEqual(other.QueryVector));

            return vectorsEqual &&
                   String.Equals(QueryText, other.QueryText, StringComparison.Ordinal) &&
                   String.Equals(EmbeddingName, other.EmbeddingName, StringComparison.Ordinal) &&
                   String.Equals(Metric, other.Metric, StringComparison.Ordinal) &&
                   Nullable.Equals(MinScore, other.MinScore) &&
                   CostBySimilarity == other.CostBySimilarity;
        }

        public override Boolean Equals(Object obj) => Equals(obj as SemanticTraversalSpecification);

        public override Int32 GetHashCode()
        {
            return HashCode.Combine(QueryVector?.Length ?? -1, QueryText, EmbeddingName, Metric, MinScore, CostBySimilarity);
        }
    }
}
