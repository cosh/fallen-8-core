// MIT License
//
// GetTool.cs
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
using NoSQL.GraphDB.Mcp.Configuration;

namespace NoSQL.GraphDB.Mcp.Tools
{
    /// <summary>
    ///   <c>f8_get</c> — fetch one vertex or edge and, on request, its neighbourhood. The single
    ///   REST getter already returns properties plus grouped adjacency, so <c>include</c>/
    ///   <c>fields</c> is a projection over ONE call (spec §3.2). The default is compact: id,
    ///   label, and scalar property values (vector/array values omitted — spec §3.5).
    /// </summary>
    public sealed class GetTool : IMcpTool
    {
        private readonly Fallen8RestClient _bridge;

        public GetTool(Fallen8RestClient bridge)
        {
            _bridge = bridge;
        }

        public String Name => "f8_get";

        public ToolTier Tier => ToolTier.Read;

        public Tool Describe(McpToolsOptions tools)
        {
            return new Tool
            {
                Name = Name,
                Title = "Get element",
                Description =
                    "Fetch a vertex or edge by id, with optional neighbourhood. Compact by default " +
                    "(id, label, scalar property values; vector values omitted).",
                InputSchema = SchemaBuilder.Create()
                    .Str("namespace", "The namespace (graph). Defaults to 'default'.")
                    .Str("kind", "Element kind.", required: true, choices: new[] { "vertex", "edge" })
                    .Int("id", "The element id.", required: true)
                    .StrArray("include", "Neighbourhood to add (already in the single fetch, no extra call).",
                        itemChoices: new[] { "out_edges", "in_edges", "source", "target", "degree" })
                    .StrArray("fields", "Restrict to these property keys (omit for all scalar values).")
                    .Build(),
                Annotations = new ToolAnnotations
                {
                    Title = "Get element",
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
            var kind = ToolArgs.GetString(arguments, "kind");
            if (kind is not "vertex" and not "edge")
            {
                return ToolResults.Error(400, "Invalid arguments", "kind must be 'vertex' or 'edge'.");
            }

            var id = ToolArgs.GetInt(arguments, "id");
            if (id is null)
            {
                return ToolResults.Error(400, "Invalid arguments", "id (integer) is required.");
            }

            var @namespace = ToolArgs.GetString(arguments, "namespace");
            var element = await _bridge.GetElementAsync(@namespace, kind, id.Value, cancellationToken).ConfigureAwait(false);

            if (element is null)
            {
                return ToolResults.Ok(
                    $"{kind} {id} not found.",
                    new JsonObject { ["found"] = false, ["kind"] = kind, ["id"] = id });
            }

            var fields = ToolArgs.GetStringSet(arguments, "fields");
            var include = ToolArgs.GetStringSet(arguments, "include");
            var projected = ElementProjection.Compact(element.Value, fields.Count > 0 ? fields : null, include);
            projected["found"] = true;

            var label = element.Value.TryGetProperty("label", out var l) ? l.GetString() : null;
            return ToolResults.Ok($"{kind} {id}" + (label is null ? "." : $" (label '{label}')."), projected);
        }
    }
}
