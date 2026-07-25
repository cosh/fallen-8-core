// MIT License
//
// PathAndAnalyticsDto.cs
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

namespace NoSQL.GraphDB.Mcp.Bridge.Dto
{
    /// <summary>Request body for <c>POST /path/{from}/to/{to}</c>. The filterless / stored-query
    /// form is read-tier; the inline <c>Filter</c>/<c>Cost</c> fragments are the code capability
    /// (present only when enabled). MaxDepth defaults to 7 because a serialized 0 makes the
    /// endpoint short-circuit to an empty result.</summary>
    public sealed class PathRequest
    {
        public String PathAlgorithmName { get; set; } = "BLS";

        public Int32 MaxDepth { get; set; } = 7;

        public Int32 MaxResults { get; set; } = 65535;

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public String? StoredQuery { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public PathFilterDto? Filter { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public PathCostDto? Cost { get; set; }
    }

    /// <summary>One element of a <c>PathREST</c> (an edge hop). <c>Direction</c> is the numeric
    /// engine enum (Incoming=0, Outgoing=1, Undirected=2).</summary>
    public sealed class PathElementDto
    {
        public Int32 SourceVertexId { get; set; }

        public Int32 TargetVertexId { get; set; }

        public Int32 EdgeId { get; set; }

        public String? EdgePropertyId { get; set; }

        public Int32 Direction { get; set; }

        public Double Weight { get; set; }
    }

    /// <summary>Response element of <c>POST /path/...</c> (the endpoint returns a bare array).</summary>
    public sealed class PathDto
    {
        public List<PathElementDto> PathElements { get; set; } = new();

        public Double TotalWeight { get; set; }
    }

    /// <summary>Request body for <c>POST /analytics/{algorithmName}</c>. Read-only in v1:
    /// <c>WriteBack</c> stays false (the write-back variant is a deferred write-tier tool).</summary>
    public sealed class AnalyticsRequest
    {
        public String? VertexLabel { get; set; }

        public String? EdgePropertyId { get; set; }

        /// <summary>"in" / "out" / "both" (null = algorithm default). NOT the numeric Direction enum.</summary>
        public String? Direction { get; set; }

        public Int32 MaxIterations { get; set; }

        public Int32? MaxResults { get; set; }

        public Dictionary<String, Double>? Parameters { get; set; }

        public Boolean WriteBack { get; set; }
    }

    /// <summary>Response of <c>POST /analytics/{algorithmName}</c>.</summary>
    public sealed class AnalyticsResultDto
    {
        public String Algorithm { get; set; } = String.Empty;

        public Boolean Converged { get; set; }

        public Int32 IterationsRun { get; set; }

        public Double ElapsedMs { get; set; }

        public Boolean BudgetExhausted { get; set; }

        public Int32 VertexCount { get; set; }

        public Dictionary<String, Double>? Statistics { get; set; }

        public List<ScoredVertexDto>? Results { get; set; }

        public List<PartitionSummaryDto>? Partitions { get; set; }
    }

    public sealed class ScoredVertexDto
    {
        public Int32 GraphElementId { get; set; }

        public Double Score { get; set; }
    }

    public sealed class PartitionSummaryDto
    {
        public Int32 PartitionId { get; set; }

        public Int32 Size { get; set; }
    }
}
