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
                .Str("storedQuery", "Name of a registered subgraph template (code-free).")
                // Pure data the server already accepts (feature mcp-plugin-gaps, spec section 7).
                .Obj("semantic", "Semantic block, forwarded as sent: {queryVector | queryText, embeddingName, metric, minScore}. " +
                    "minScore admits only vertices similar to the query; queryText needs the target's embedding provider.")
                .ObjArray("patterns", "Ordered Vertex/Edge/VariableLengthEdge steps, forwarded as sent: {type, patternName?, direction?, minLength?, maxLength?, semanticMinScore?}. " +
                    "A step carrying vertexFilter/edgeFilter/edgePropertyFilter is C# and needs the code capability.");

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

            // The pure-data blocks (feature mcp-plugin-gaps, spec section 7). A pattern step that
            // carries a C# fragment is code, and the code capability is the agreed gate for inline
            // code wherever it appears, so it is checked here before anything is sent.
            JsonElement? semantic = null;
            if (ToolArgs.TryGetElement(arguments, "semantic", out var semanticElement))
            {
                if (semanticElement.ValueKind != JsonValueKind.Object)
                {
                    return ToolResults.Error(400, "Invalid arguments", "semantic must be an object.");
                }
                semantic = semanticElement;
            }
            JsonElement? patterns = null;
            if (ToolArgs.TryGetElement(arguments, "patterns", out var patternsElement))
            {
                if (patternsElement.ValueKind != JsonValueKind.Array)
                {
                    return ToolResults.Error(400, "Invalid arguments", "patterns must be an array of steps.");
                }
                if (!tools.EnableCode && PatternsCarryCode(patternsElement))
                {
                    return ToolResults.Error(403, "Forbidden",
                        "a pattern step with vertexFilter, edgeFilter or edgePropertyFilter is C# and needs the code capability (Mcp:Tools:EnableCode).");
                }
                patterns = patternsElement;
            }

            if (String.IsNullOrEmpty(storedQuery) && String.IsNullOrEmpty(vertexFilter) && String.IsNullOrEmpty(edgeFilter)
                && semantic is null && patterns is null)
            {
                return ToolResults.Error(400, "Invalid arguments",
                    tools.EnableCode
                        ? "provide a storedQuery, a semantic block, patterns, or an inline vertexFilter/edgeFilter."
                        : "provide a storedQuery, a semantic block, or code-free patterns (inline filters require the code capability).");
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
            if (semantic is { } semanticBlock)
            {
                body["semantic"] = JsonNode.Parse(semanticBlock.GetRawText());
            }
            if (patterns is { } patternSteps)
            {
                body["patterns"] = JsonNode.Parse(patternSteps.GetRawText());
            }

            var summary = await _bridge.RequestRawAsync(HttpMethod.Put, @namespace, "subgraph", body, cancellationToken)
                .ConfigureAwait(false);
            var structured = ToolResults.Pass(summary).AsObject();
            var counts = summary is { } s && s.TryGetProperty("vertexCount", out var vc)
                ? $" ({vc.GetInt32()} vertices)"
                : String.Empty;
            return ToolResults.Ok($"subgraph '{name}' defined{counts}.", structured);
        }

        /// <summary>Whether any step carries one of the three C# fragment fields with a value.</summary>
        private static Boolean PatternsCarryCode(JsonElement patterns)
        {
            foreach (var step in patterns.EnumerateArray())
            {
                if (step.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                foreach (var field in new[] { "vertexFilter", "edgeFilter", "edgePropertyFilter" })
                {
                    if (step.TryGetProperty(field, out var fragment)
                        && fragment.ValueKind == JsonValueKind.String
                        && !String.IsNullOrWhiteSpace(fragment.GetString()))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
