// MIT License
//
// GraphGenerationResultREST.cs
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
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   Structured result of a benchmark-graph generation (GET /ns/{ns}/generate).
    /// </summary>
    /// <remarks>
    ///   Generation is ADDITIVE, so the created counts and the resulting totals are both reported:
    ///   the created pair says what this call did, the "after" pair what the namespace now holds.
    ///   <see cref="Namespace"/> names the graph that was written - generation targets the addressed
    ///   namespace and has no default, so a caller can always tell from the response alone which
    ///   graph grew.
    /// </remarks>
    public sealed class GraphGenerationResultREST
    {
        /// <summary>The namespace the vertices and edges were written into.</summary>
        /// <example>flights</example>
        [JsonPropertyName("namespace")]
        public String Namespace
        {
            get; set;
        }

        /// <summary>The number of vertices created by this call.</summary>
        /// <example>200</example>
        [JsonPropertyName("verticesCreated")]
        public Int32 VerticesCreated
        {
            get; set;
        }

        /// <summary>
        ///   The number of edges created by this call. Lower than
        ///   <c>verticesCreated * edgeCount</c> whenever the requested out-degree exceeds the number
        ///   of distinct targets available (targets are drawn distinct), and under
        ///   <c>preferential</c> by construction, because the earliest vertices have fewer earlier
        ///   vertices to attach to.
        /// </summary>
        /// <example>1000</example>
        [JsonPropertyName("edgesCreated")]
        public Int64 EdgesCreated
        {
            get; set;
        }

        /// <summary>The edge-target distribution that was used: "uniform" or "preferential".</summary>
        /// <example>uniform</example>
        [JsonPropertyName("distribution")]
        public String Distribution
        {
            get; set;
        }

        /// <summary>Wall-clock milliseconds spent creating and committing the graph.</summary>
        /// <example>412.8</example>
        [JsonPropertyName("elapsedMilliseconds")]
        public Double ElapsedMilliseconds
        {
            get; set;
        }

        /// <summary>The namespace's total vertex count once generation finished.</summary>
        /// <example>200</example>
        [JsonPropertyName("vertexCountAfter")]
        public Int32 VertexCountAfter
        {
            get; set;
        }

        /// <summary>The namespace's total edge count once generation finished.</summary>
        /// <example>1000</example>
        [JsonPropertyName("edgeCountAfter")]
        public Int32 EdgeCountAfter
        {
            get; set;
        }
    }
}
