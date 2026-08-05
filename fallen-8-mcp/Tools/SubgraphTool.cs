// MIT License
//
// SubgraphTool.cs
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
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using NoSQL.GraphDB.Mcp.Bridge;
using NoSQL.GraphDB.Mcp.Configuration;

namespace NoSQL.GraphDB.Mcp.Tools
{
    /// <summary>
    ///   <c>f8_subgraph</c> — define/compute a subgraph (spec §3.2). Code-free by a registered
    ///   <c>storedQuery</c>; the inline <c>vertexFilter</c>/<c>edgeFilter</c> C# fragments appear
    ///   only when the MCP <c>code</c> capability is enabled (the target engine always accepts
    ///   them, auth permitting — this is purely an MCP-side exposure choice).
    /// </summary>
    public sealed class SubgraphTool : IMcpTool
    {
        private readonly Fallen8RestClient _bridge;

        public SubgraphTool(Fallen8RestClient bridge)
        {
            _bridge = bridge;
        }

        public String Name => "f8_subgraph";

        public ToolTier Tier => ToolTier.Write;

        public Tool Describe(McpToolsOptions tools)
        {
            var schema = SchemaBuilder.Create()
                .Str("namespace", "The namespace (graph). Defaults to 'default'.")
                .Str("name", "A name for the computed subgraph.", required: true)
                // Free-form on purpose (engine -> REST -> MCP): PUT /subgraph resolves a built-in or
                // runtime-registered SubGraph plugin by name, so agents get the same choice every
                // other client has. An unknown name comes back as a 400 listing the available ones.
                .Str("algorithm", "Subgraph algorithm plugin name (a built-in or a registered SubGraph plugin). Omit for the built-in breadth-first search; an unknown name is rejected with the list of available names.")
                .Str("storedQuery", "Name of a registered subgraph template (code-free).");

            if (tools.EnableCode)
            {
                schema
                    .Str("vertexFilter", "Inline C# vertex filter, e.g. \"return (v) => v.Label == \\\"person\\\";\" (code capability). v.AnyPropertyValueMatches(s => ...) full-text-matches the element's string property values.")
                    .Str("edgeFilter", "Inline C# edge filter (code capability).");
            }

            return new Tool
            {
                Name = Name,
                Title = "Define subgraph",
                Description = "Compute/register a subgraph from a stored template (or inline C# filters when the code capability is on).",
                InputSchema = schema.Build(),
                Annotations = new ToolAnnotations
                {
                    Title = "Define subgraph",
                    ReadOnlyHint = false,
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
            var name = ToolArgs.GetString(arguments, "name");
            if (String.IsNullOrEmpty(name))
            {
                return ToolResults.Error(400, "Invalid arguments", "subgraph 'name' is required.");
            }

            var algorithm = ToolArgs.GetString(arguments, "algorithm");
            var storedQuery = ToolArgs.GetString(arguments, "storedQuery");
            // Fragments are honoured ONLY when the code capability is on (defence beyond the schema).
            var vertexFilter = tools.EnableCode ? ToolArgs.GetString(arguments, "vertexFilter") : null;
            var edgeFilter = tools.EnableCode ? ToolArgs.GetString(arguments, "edgeFilter") : null;

            if (String.IsNullOrEmpty(storedQuery) && String.IsNullOrEmpty(vertexFilter) && String.IsNullOrEmpty(edgeFilter))
            {
                return ToolResults.Error(400, "Invalid arguments",
                    tools.EnableCode
                        ? "provide a storedQuery, or an inline vertexFilter/edgeFilter."
                        : "provide a storedQuery (inline filters require the code capability).");
            }

            var @namespace = ToolArgs.GetString(arguments, "namespace");

            // The PUT /subgraph body, assembled here so the optional algorithm selector rides along
            // with the code-free/inline fields. Absent fields are OMITTED (never sent as null or
            // empty): REST reads every one of them with IsNullOrWhiteSpace, so a code-free request
            // still compiles nothing, and an omitted algorithm still means the built-in BFS.
            var body = new JsonObject { ["name"] = name };
            if (!String.IsNullOrEmpty(algorithm))
            {
                body["algorithm"] = algorithm;
            }
            if (!String.IsNullOrEmpty(storedQuery))
            {
                body["storedQuery"] = storedQuery;
            }
            if (!String.IsNullOrEmpty(vertexFilter))
            {
                body["vertexFilter"] = vertexFilter;
            }
            if (!String.IsNullOrEmpty(edgeFilter))
            {
                body["edgeFilter"] = edgeFilter;
            }

            var summary = await _bridge.RequestRawAsync(HttpMethod.Put, @namespace, "subgraph", body, cancellationToken)
                .ConfigureAwait(false);
            var structured = ToolResults.Pass(summary).AsObject();
            var counts = summary is { } s && s.TryGetProperty("vertexCount", out var vc)
                ? $" ({vc.GetInt32()} vertices)"
                : String.Empty;
            return ToolResults.Ok($"subgraph '{name}' defined{counts}.", structured);
        }
    }
}
