// MIT License
//
// AnalyticsTool.cs
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
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using NoSQL.GraphDB.Mcp.Bridge;
using NoSQL.GraphDB.Mcp.Bridge.Dto;
using NoSQL.GraphDB.Mcp.Configuration;

namespace NoSQL.GraphDB.Mcp.Tools
{
    /// <summary>
    ///   <c>f8_analytics</c> — run a declarative whole-graph algorithm (PageRank, WCC,
    ///   communities, centrality, triangle-count) and return the results to the caller
    ///   (spec §3.2). Read-only in v1: no write-back (that is a deferred write-tier variant).
    ///   Omit <c>algorithm</c> to list the available algorithms.
    /// </summary>
    public sealed class AnalyticsTool : IMcpTool
    {
        private const Int32 DefaultMaxResults = 25;

        private readonly Fallen8RestClient _bridge;

        public AnalyticsTool(Fallen8RestClient bridge)
        {
            _bridge = bridge;
        }

        public String Name => "f8_analytics";

        public ToolTier Tier => ToolTier.Read;

        public Tool Describe(McpToolsOptions tools)
        {
            return new Tool
            {
                Name = Name,
                Title = "Analytics",
                Description =
                    "Run a whole-graph algorithm (e.g. PAGERANK, WCC, LABELPROPAGATION, DEGREE, " +
                    "TRIANGLECOUNT) and return its results. Omit 'algorithm' to list what's available.",
                InputSchema = SchemaBuilder.Create()
                    .Str("namespace", "The namespace (graph). Defaults to 'default'.")
                    .Str("algorithm", "Algorithm name (omit to list available algorithms).")
                    .Str("vertexLabel", "Restrict to vertices with this label (default: whole graph).")
                    .Str("edgePropertyId", "Restrict traversal to this edge type (default: all edges).")
                    .Str("direction", "Edge direction to follow.", choices: new[] { "in", "out", "both" })
                    .Int("maxResults", "Max scored vertices to return (default 25).")
                    .Int("maxIterations", "Iteration cap for iterative algorithms (e.g. PAGERANK, LABELPROPAGATION); 0 = the algorithm's default.")
                    .Obj("parameters", "Algorithm-specific numeric knobs, e.g. {\"DampingFactor\": 0.85} for PAGERANK.")
                    .Build(),
                Annotations = new ToolAnnotations
                {
                    Title = "Analytics",
                    ReadOnlyHint = true,
                    IdempotentHint = true,
                    OpenWorldHint = false,
                },
            };
        }

        public async Task<CallToolResult> InvokeAsync(
            IReadOnlyDictionary<String, JsonElement> arguments,
            McpToolsOptions tools,
            CancellationToken cancellationToken)
        {
            var @namespace = ToolArgs.GetString(arguments, "namespace");
            var algorithm = ToolArgs.GetString(arguments, "algorithm");

            if (String.IsNullOrEmpty(algorithm))
            {
                var algorithms = await _bridge.GetAsync<Dictionary<String, String>>(@namespace, "analytics/algorithms", cancellationToken)
                    .ConfigureAwait(false) ?? new Dictionary<String, String>();

                var node = new JsonObject();
                foreach (var kv in algorithms)
                {
                    node[kv.Key] = kv.Value;
                }
                return ToolResults.Ok($"{algorithms.Count} analytics algorithm(s) available.",
                    new JsonObject { ["algorithms"] = node });
            }

            var request = new AnalyticsRequest
            {
                VertexLabel = ToolArgs.GetString(arguments, "vertexLabel"),
                EdgePropertyId = ToolArgs.GetString(arguments, "edgePropertyId"),
                Direction = ToolArgs.GetString(arguments, "direction"),
                MaxResults = ToolArgs.GetInt(arguments, "maxResults") ?? DefaultMaxResults,
                MaxIterations = ToolArgs.GetInt(arguments, "maxIterations") ?? 0,
                Parameters = ParseNumericParameters(arguments),
                WriteBack = false,
            };

            var result = await _bridge.PostAsync<AnalyticsResultDto>(
                @namespace, $"analytics/{UrlSafety.EncodeSegment(algorithm)}", request, cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                return ToolResults.Error(502, "Empty analytics result", "The analytics run returned no result.");
            }

            var structured = new JsonObject
            {
                ["algorithm"] = result.Algorithm,
                ["converged"] = result.Converged,
                ["iterationsRun"] = result.IterationsRun,
                ["elapsedMs"] = result.ElapsedMs,
                ["budgetExhausted"] = result.BudgetExhausted,
                ["vertexCount"] = result.VertexCount,
            };

            if (result.Statistics is not null)
            {
                var stats = new JsonObject();
                foreach (var kv in result.Statistics)
                {
                    stats[kv.Key] = kv.Value;
                }
                structured["statistics"] = stats;
            }

            if (result.Results is not null)
            {
                var scored = new JsonArray();
                foreach (var s in result.Results)
                {
                    scored.Add(new JsonObject { ["id"] = s.GraphElementId, ["score"] = s.Score });
                }
                structured["results"] = scored;
            }

            if (result.Partitions is not null)
            {
                var partitions = new JsonArray();
                foreach (var p in result.Partitions)
                {
                    partitions.Add(new JsonObject { ["partitionId"] = p.PartitionId, ["size"] = p.Size });
                }
                structured["partitions"] = partitions;
            }

            var summary = $"{result.Algorithm}: {result.VertexCount} vertices, " +
                (result.Results is not null ? $"{result.Results.Count} scored" : $"{result.Partitions?.Count ?? 0} partitions") +
                $" ({result.ElapsedMs:F0} ms).";
            return ToolResults.Ok(summary, structured);
        }

        /// <summary>Reads the optional free-form "parameters" object into the numeric knob map the REST
        /// analytics endpoint accepts (e.g. { "DampingFactor": 0.85 }); non-numeric entries are dropped
        /// and an empty/absent map becomes null (the algorithm default).</summary>
        private static Dictionary<String, Double>? ParseNumericParameters(IReadOnlyDictionary<String, JsonElement> arguments)
        {
            if (!ToolArgs.TryGetElement(arguments, "parameters", out var element) || element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var result = new Dictionary<String, Double>();
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out var value))
                {
                    result[property.Name] = value;
                }
            }
            return result.Count > 0 ? result : null;
        }
    }
}
