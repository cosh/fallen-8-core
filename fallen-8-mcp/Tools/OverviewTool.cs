// MIT License
//
// OverviewTool.cs
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
    ///   <c>f8_overview</c> — the single cheap discovery call (spec §3.2/§3.5). With no
    ///   <c>namespace</c> it is the namespace directory (list + ceiling); with a <c>namespace</c>
    ///   it reports that graph's status: counts, index inventory, the available path/analytics
    ///   algorithms, and the embedding-provider state — everything an agent needs to know "what
    ///   is here and what can I do" in one round-trip. It reports only what <c>/status</c> truly
    ///   exposes; dynamic code execution is unconditional (no switch), so there is nothing to
    ///   report for it — the MCP-side <c>code</c> capability is a surface toggle, not engine state.
    /// </summary>
    public sealed class OverviewTool : IMcpTool
    {
        /// <summary>
        ///   The wire spelling the target uses for "cataloged, but with no engine in this process"
        ///   (the apiApp's <c>Namespace.WireName</c> owns it).
        /// </summary>
        private const String NotLoadedState = "notLoaded";

        private readonly Fallen8RestClient _bridge;

        public OverviewTool(Fallen8RestClient bridge)
        {
            _bridge = bridge;
        }

        public String Name => "f8_overview";

        public ToolTier Tier => ToolTier.Read;

        public Tool Describe(McpToolsOptions tools)
        {
            return new Tool
            {
                Name = Name,
                Title = "Fallen-8 overview",
                Description =
                    "Discover a Fallen-8: omit 'namespace' to list all namespaces; set it to inspect one " +
                    "graph's counts, indices, available algorithms and embedding state. Start here.",
                InputSchema = SchemaBuilder.Create()
                    .Str("namespace",
                        "The namespace (graph) to inspect. Omit to list all namespaces. Defaults to 'default'.")
                    .Str("detail", "'statistics' adds the full graph-shape snapshot (label/key cardinalities, " +
                        "degree distribution, index inventory).", choices: new[] { "status", "statistics" })
                    .Build(),
                Annotations = new ToolAnnotations
                {
                    Title = "Fallen-8 overview",
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

            if (@namespace is null)
            {
                var list = await _bridge.ListNamespacesAsync(cancellationToken).ConfigureAwait(false);
                return ToolResults.Ok(
                    $"{list.Namespaces.Count} namespace(s); ceiling {list.MaxNamespaces}.",
                    BuildNamespaceDirectory(list));
            }

            var status = await _bridge.GetStatusAsync(@namespace, cancellationToken).ConfigureAwait(false);
            if (status is null)
            {
                return ToolResults.Error(404, "Namespace not found", $"No status for namespace '{@namespace}'.");
            }

            var node = BuildStatus(@namespace, status);

            // Residency, straight off the body /status already answered. /status is the ONE
            // namespace-scoped route that answers for a namespace the target did not load; every
            // other one refuses with 503, and the bridge turns any non-2xx into a thrown error - so
            // following up on a not-loaded namespace would cost the agent the residency answer it is
            // holding right here (feature namespace-startup-load). An older target sends no
            // namespaceState at all, and its present counts are what says it is loaded.
            var loaded = status.VertexCount.HasValue
                && !String.Equals(status.NamespaceState, NotLoadedState, StringComparison.Ordinal);
            var wantsStatistics = String.Equals(
                ToolArgs.GetString(arguments, "detail"), "statistics", StringComparison.OrdinalIgnoreCase);

            if (wantsStatistics && loaded)
            {
                var statistics = await _bridge.GetAsync<JsonElement>(@namespace, "statistics", cancellationToken).ConfigureAwait(false);
                if (statistics.ValueKind == JsonValueKind.Object)
                {
                    node["statistics"] = JsonNode.Parse(statistics.GetRawText());
                }
            }
            else if (wantsStatistics)
            {
                // Say WHY it is missing. A silently absent block reads as "nothing to report", which
                // is a different and wrong conclusion about a namespace that holds data.
                node["statisticsUnavailable"] =
                    "the namespace is not loaded in this process, so /statistics refuses with 503";
            }

            // A not-loaded namespace has no counts to report: say so in the summary line rather than
            // printing an absent count as nothing or as zero, because "0 vertices" is what an agent
            // would act on. The unavailable statistics block is named for the same reason - silence
            // reads as "there is nothing to report".
            var summary = loaded
                ? $"namespace '{@namespace}': {status.VertexCount} vertices, {status.EdgeCount} edges."
                : $"namespace '{@namespace}' is not loaded in this process (namespaceState " +
                  $"'{status.NamespaceState ?? NotLoadedState}'), so it reports no counts, no inventories and no " +
                  $"statistics; its data on disk is untouched. Load it with f8_admin op:'activate' " +
                  $"(admin tier), which is also what makes every other tool stop answering 503 for it.";

            return ToolResults.Ok(summary, node);
        }

        private static JsonNode BuildNamespaceDirectory(NamespacesDto list)
        {
            var namespaces = new JsonArray();
            foreach (var ns in list.Namespaces)
            {
                namespaces.Add(new JsonObject
                {
                    ["name"] = ns.Name,
                    ["state"] = ns.State,
                    ["vertexCount"] = ns.VertexCount,
                    ["edgeCount"] = ns.EdgeCount,
                });
            }

            return new JsonObject
            {
                ["namespaces"] = namespaces,
                ["maxNamespaces"] = list.MaxNamespaces,
            };
        }

        private static JsonNode BuildStatus(String @namespace, StatusDto status)
        {
            var node = new JsonObject
            {
                ["namespace"] = @namespace,
                ["namespaceState"] = status.NamespaceState,
                ["vertexCount"] = status.VertexCount,
                ["edgeCount"] = status.EdgeCount,
                ["usedMemoryBytes"] = status.UsedMemory,
                // Absent, not 0, when the target reported no inventory at all (a not-loaded
                // namespace): "0 indices" is a fact an agent would act on.
                ["indexCount"] = status.Indices?.Count,
                ["apiKeyRequired"] = status.ApiKeyRequired,
                ["authenticated"] = status.Authenticated,
            };

            node["availablePathAlgorithms"] = ToJsonArray(status.AvailablePathPlugins);
            node["availableAnalyticsAlgorithms"] = ToJsonArray(status.AvailableAnalyticsPlugins);
            node["availableIndexPlugins"] = ToJsonArray(status.AvailableIndexPlugins);
            node["embeddingEnabled"] = status.Embedding?.Enabled ?? false;
            node["embeddingBackend"] = status.Embedding?.Backend;
            node["chatEnabled"] = status.Chat?.Enabled ?? false;
            // Both backends stay null when unreported, for the reason indexCount stays absent above.
            node["chatBackend"] = status.Chat?.Backend;

            return node;
        }

        /// <summary>
        ///   Null in, null out: an absent inventory (a not-loaded namespace reports none) must not
        ///   become an empty array claiming the namespace can run nothing.
        /// </summary>
        private static JsonArray? ToJsonArray(List<String>? values)
        {
            if (values is null)
            {
                return null;
            }

            var arr = new JsonArray();
            foreach (var v in values)
            {
                arr.Add(v);
            }

            return arr;
        }
    }
}
