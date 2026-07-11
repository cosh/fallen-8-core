// MIT License
//
// SubGraphSummary.cs
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
using System.Text.Json.Serialization;
using NoSQL.GraphDB.Core.Algorithms.SubGraph;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   A lightweight summary of a registered subgraph (metadata and element counts),
    ///   without the full vertex/edge payload.
    /// </summary>
    /// <example>
    /// {
    ///   "name": "friends-of-alice",
    ///   "vertexCount": 3,
    ///   "edgeCount": 2,
    ///   "algorithmPluginName": "Breadth First Search Subgraph Algorithm",
    ///   "sourceFallen8Id": "6f1e...",
    ///   "canRecalculate": true,
    ///   "additionalInformation": { "category": "social" }
    /// }
    /// </example>
    public sealed class SubGraphSummary
    {
        /// <summary>The name the subgraph is registered under.</summary>
        [JsonPropertyName("name")]
        public String Name
        {
            get; set;
        }

        /// <summary>Number of vertices in the extracted subgraph.</summary>
        [JsonPropertyName("vertexCount")]
        public Int32 VertexCount
        {
            get; set;
        }

        /// <summary>Number of edges in the extracted subgraph.</summary>
        [JsonPropertyName("edgeCount")]
        public Int32 EdgeCount
        {
            get; set;
        }

        /// <summary>The algorithm plugin used to create the subgraph.</summary>
        [JsonPropertyName("algorithmPluginName")]
        public String AlgorithmPluginName
        {
            get; set;
        }

        /// <summary>The id of the source graph the subgraph was created from.</summary>
        [JsonPropertyName("sourceFallen8Id")]
        public Guid SourceFallen8Id
        {
            get; set;
        }

        /// <summary>Whether this subgraph can be recalculated against its source.</summary>
        [JsonPropertyName("canRecalculate")]
        public Boolean CanRecalculate
        {
            get; set;
        }

        /// <summary>Metadata attached to the subgraph definition.</summary>
        [JsonPropertyName("additionalInformation")]
        public Dictionary<String, String> AdditionalInformation
        {
            get; set;
        }

        /// <summary>
        ///   Builds a summary from an engine <see cref="SubGraphResult"/>.
        /// </summary>
        public static SubGraphSummary FromResult(SubGraphResult result, Boolean canRecalculate)
        {
            if (result == null)
            {
                return null;
            }

            return new SubGraphSummary
            {
                Name = result.Definitions?.Name,
                VertexCount = result.SubGraph?.VertexCount ?? 0,
                EdgeCount = result.SubGraph?.EdgeCount ?? 0,
                AlgorithmPluginName = result.AlgorithmPluginName,
                SourceFallen8Id = result.SourceFallen8Id,
                CanRecalculate = canRecalculate,
                AdditionalInformation = result.Definitions?.AdditionalInformation
            };
        }
    }
}
