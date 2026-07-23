// MIT License
//
// SubGraphSemanticSummary.cs
//
// Copyright (c) 2026 Henning Rauch
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
    /// <summary>
    ///   The bound semantic state of a registered subgraph (feature
    ///   subgraph-semantic-thresholds), echoed on its summary: the query the subgraph's
    ///   filters were registered against and where its thresholds sit. The raw query vector is
    ///   deliberately NOT part of the echo - only its dimension; a registered subgraph reveals
    ///   its binding, not its payload.
    /// </summary>
    /// <example>
    /// {
    ///   "embeddingName": "default",
    ///   "metric": "Cosine",
    ///   "dimension": 384,
    ///   "queryText": "red bicycles",
    ///   "minScore": 0.7,
    ///   "patternThresholds": [ { "pattern": "start", "minScore": 0.6 } ]
    /// }
    /// </example>
    public sealed class SubGraphSemanticSummary
    {
        /// <summary>The named embedding the subgraph's filters score (effective, so never null).</summary>
        [JsonPropertyName("embeddingName")]
        public String EmbeddingName
        {
            get; set;
        }

        /// <summary>The metric the scores are computed under (effective, so never null).</summary>
        [JsonPropertyName("metric")]
        public String Metric
        {
            get; set;
        }

        /// <summary>The dimension of the bound query vector.</summary>
        [JsonPropertyName("dimension")]
        public Int32 Dimension
        {
            get; set;
        }

        /// <summary>
        ///   The query text the vector was resolved from at registration, when the subgraph was
        ///   created via <c>semantic.queryText</c>; null for client-supplied vectors. Documents
        ///   intent - the bound vector remains the truth and is never re-embedded.
        /// </summary>
        [JsonPropertyName("queryText")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public String QueryText
        {
            get; set;
        }

        /// <summary>The top-level vertex pre-filter threshold (<c>semantic.minScore</c>), when set.</summary>
        [JsonPropertyName("minScore")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Double? MinScore
        {
            get; set;
        }

        /// <summary>The vertex pattern steps carrying a <c>semanticMinScore</c>, when any.</summary>
        [JsonPropertyName("patternThresholds")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<SubGraphPatternThresholdSummary> PatternThresholds
        {
            get; set;
        }
    }

    /// <summary>One vertex pattern step's semantic threshold, identified by name or position.</summary>
    public sealed class SubGraphPatternThresholdSummary
    {
        /// <summary>The step's <c>patternName</c>, or its zero-based index when unnamed.</summary>
        [JsonPropertyName("pattern")]
        public String Pattern
        {
            get; set;
        }

        /// <summary>The step's threshold.</summary>
        [JsonPropertyName("minScore")]
        public Double MinScore
        {
            get; set;
        }
    }
}
