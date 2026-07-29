// MIT License
//
// PathsTool.cs
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
    ///   <c>f8_paths</c> — find paths between two elements (spec §3.2). The filterless /
    ///   stored-query form (no dynamic code); inline C# filter/cost fragments are the code
    ///   capability added in Phase 2. Note: the endpoint returns an empty result both for a
    ///   genuine no-path and for a masked internal traversal limit — an empty result is not proof
    ///   no path exists.
    /// </summary>
    public sealed class PathsTool : IMcpTool
    {
        private readonly Fallen8RestClient _bridge;

        public PathsTool(Fallen8RestClient bridge)
        {
            _bridge = bridge;
        }

        public String Name => "f8_paths";

        public ToolTier Tier => ToolTier.Read;

        public Tool Describe(McpToolsOptions tools)
        {
            var schema = SchemaBuilder.Create()
                .Str("namespace", "The namespace (graph). Defaults to 'default'.")
                .Int("from", "Source vertex id.", required: true)
                .Int("to", "Target vertex id.", required: true)
                .Str("algorithm", "Path algorithm.", choices: new[] { "BLS", "DIJKSTRA" })
                .Int("maxDepth", "Maximum hop depth (default 7).")
                .Int("maxResults", "Maximum number of paths.")
                .Str("storedQuery", "Name of a registered stored path query (mutually exclusive with algorithm knobs).");

            if (tools.EnableCode)
            {
                schema
                    .Str("vertexFilter", "Inline C# vertex filter (code capability). v.AnyPropertyValueMatches(s => ...) full-text-matches the element's string property values.")
                    .Str("edgeFilter", "Inline C# edge filter (code capability).")
                    .Str("edgePropertyFilter", "Inline C# edge-property filter (code capability).")
                    .Str("vertexCost", "Inline C# vertex cost (code capability; DIJKSTRA).")
                    .Str("edgeCost", "Inline C# edge cost (code capability; DIJKSTRA).");
            }

            return new Tool
            {
                Name = Name,
                Title = "Find paths",
                Description =
                    "Find paths between two vertices. Unfiltered or by a registered stored query. " +
                    "An empty result can also mean an internal traversal limit was hit, not only 'no path'.",
                InputSchema = schema.Build(),
                Annotations = new ToolAnnotations
                {
                    Title = "Find paths",
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
            var from = ToolArgs.GetInt(arguments, "from");
            var to = ToolArgs.GetInt(arguments, "to");
            if (from is null || to is null)
            {
                return ToolResults.Error(400, "Invalid arguments", "from and to (integer vertex ids) are required.");
            }

            var @namespace = ToolArgs.GetString(arguments, "namespace");
            var request = new PathRequest
            {
                PathAlgorithmName = ToolArgs.GetString(arguments, "algorithm") ?? "BLS",
                StoredQuery = ToolArgs.GetString(arguments, "storedQuery"),
            };
            if (ToolArgs.GetInt(arguments, "maxDepth") is { } depth)
            {
                request.MaxDepth = depth;
            }
            if (ToolArgs.GetInt(arguments, "maxResults") is { } maxResults)
            {
                request.MaxResults = maxResults;
            }

            // Inline fragments are honoured ONLY when the code capability is on (defence beyond the schema).
            if (tools.EnableCode)
            {
                var vertexFilter = ToolArgs.GetString(arguments, "vertexFilter");
                var edgeFilter = ToolArgs.GetString(arguments, "edgeFilter");
                var edgePropertyFilter = ToolArgs.GetString(arguments, "edgePropertyFilter");
                if (!String.IsNullOrEmpty(vertexFilter) || !String.IsNullOrEmpty(edgeFilter) || !String.IsNullOrEmpty(edgePropertyFilter))
                {
                    request.Filter = new PathFilterDto
                    {
                        VertexFilter = vertexFilter,
                        EdgeFilter = edgeFilter,
                        EdgePropertyFilter = edgePropertyFilter,
                    };
                }

                var vertexCost = ToolArgs.GetString(arguments, "vertexCost");
                var edgeCost = ToolArgs.GetString(arguments, "edgeCost");
                if (!String.IsNullOrEmpty(vertexCost) || !String.IsNullOrEmpty(edgeCost))
                {
                    request.Cost = new PathCostDto { VertexCost = vertexCost, EdgeCost = edgeCost };
                }
            }

            var paths = await _bridge.PostAsync<List<PathDto>>(@namespace, $"path/{from}/to/{to}", request, cancellationToken)
                .ConfigureAwait(false) ?? new List<PathDto>();

            var items = new JsonArray();
            foreach (var path in paths)
            {
                var hops = new JsonArray();
                foreach (var hop in path.PathElements)
                {
                    hops.Add(new JsonObject
                    {
                        ["from"] = hop.SourceVertexId,
                        ["to"] = hop.TargetVertexId,
                        ["edgeId"] = hop.EdgeId,
                        ["edgePropertyId"] = hop.EdgePropertyId,
                        ["direction"] = DirectionLabel(hop.Direction),
                    });
                }
                items.Add(new JsonObject
                {
                    ["length"] = path.PathElements.Count,
                    ["totalWeight"] = path.TotalWeight,
                    ["hops"] = hops,
                });
            }

            var summary = paths.Count == 0
                ? $"no path from {from} to {to} (or an internal limit was hit)."
                : $"{paths.Count} path(s) from {from} to {to}.";
            return ToolResults.Ok(summary, new JsonObject { ["count"] = paths.Count, ["paths"] = items });
        }

        private static String DirectionLabel(Int32 direction)
        {
            return direction switch
            {
                0 => "incoming",
                1 => "outgoing",
                2 => "undirected",
                _ => direction.ToString(),
            };
        }
    }
}
