// MIT License
//
// StoredQueryTool.cs
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
using NoSQL.GraphDB.Rest;

namespace NoSQL.GraphDB.Mcp.Tools
{
    /// <summary>
    ///   <c>f8_storedquery</c> - the stored-query library (feature mcp-plugin-gaps, spec section 8).
    ///   Read tier with per-op capability checks on the <c>f8_plugins</c> pattern: <c>list</c> and
    ///   <c>get</c> always, <c>delete</c> with the write capability, <c>register</c> with the code
    ///   capability because the body IS C# fragments. The op enum in the schema varies the same
    ///   way, so a read-only agent sees only what it can call. The server compiles, validates and
    ///   owns the name rule; its 400 (compiler message) and 409 (taken name) pass through.
    /// </summary>
    public sealed class StoredQueryTool : IMcpTool
    {
        private readonly Fallen8RestClient _bridge;

        public StoredQueryTool(Fallen8RestClient bridge)
        {
            _bridge = bridge;
        }

        public String Name => "f8_storedquery";

        public ToolTier Tier => ToolTier.Read;

        public Tool Describe(McpToolsOptions tools)
        {
            var ops = new List<String> { "list", "get" };
            if (tools.EnableWrite)
            {
                ops.Add("delete");
            }
            if (tools.EnableCode)
            {
                ops.Add("register");
            }

            var schema = SchemaBuilder.Create()
                .Str("op", "The operation.", required: true, choices: ops)
                .Str("namespace", "The namespace (graph). Defaults to 'default'.")
                .Str("name", "The stored query name (get/delete; the name to register under for register).");

            if (tools.EnableCode)
            {
                schema
                    .Str("kind", "What is stored (register): 'Path' (a filter/cost set for f8_paths) or 'SubGraph' (a template for f8_subgraph).",
                        choices: new[] { "Path", "SubGraph" })
                    .Str("description", "Optional description (register).")
                    .Obj("path", "Path block (register, kind Path): {filter: {vertexFilter, edgeFilter, edgePropertyFilter}, cost: {vertexCost, edgeCost}}, each a C# fragment.")
                    .Obj("subGraph", "SubGraph block (register, kind SubGraph): {vertexFilter, edgeFilter, patterns}; the fragments are C#.");
            }

            return new Tool
            {
                Name = Name,
                Title = "Stored queries",
                Description =
                    "The stored-query library: compile-once path filter/cost sets and subgraph templates, invoked by name through " +
                    "the storedQuery parameter of f8_paths and f8_subgraph. list/get show what exists and its compileState " +
                    "(Compiled, Failed, SourceOnly); delete needs the write capability; register compiles C# and needs the code capability.",
                InputSchema = schema.Build(),
                Annotations = new ToolAnnotations
                {
                    Title = "Stored queries",
                    // list/get are reads; delete and register are not, and they are present only
                    // when their capability is on, so the hint follows the WIDEST advertised surface.
                    ReadOnlyHint = !tools.EnableWrite && !tools.EnableCode,
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
            var op = ToolArgs.GetString(arguments, "op");
            var @namespace = ToolArgs.GetString(arguments, "namespace");
            var name = ToolArgs.GetString(arguments, "name");

            switch (op)
            {
                case "list":
                {
                    var raw = await _bridge.RequestRawAsync(HttpMethod.Get, @namespace, "storedquery", null, cancellationToken)
                        .ConfigureAwait(false);
                    var node = ToolResults.PassArray(raw);
                    var count = node is JsonArray arr ? arr.Count : 0;
                    return ToolResults.Ok($"{count} stored quer{(count == 1 ? "y" : "ies")} registered.", new JsonObject { ["storedQueries"] = node });
                }

                case "get":
                {
                    if (String.IsNullOrEmpty(name))
                    {
                        return ToolResults.Error(400, "Invalid arguments", "get requires 'name'.");
                    }
                    var raw = await _bridge.RequestRawAsync(HttpMethod.Get, @namespace,
                        $"storedquery/{UrlSafety.EncodeSegment(name)}", null, cancellationToken).ConfigureAwait(false);
                    if (raw is null)
                    {
                        return ToolResults.Error(404, "Not found", $"No stored query named '{name}'.");
                    }
                    var node = ToolResults.Pass(raw).AsObject();
                    // The server stores the specification as JSON TEXT; an agent should read an object,
                    // not a string it has to parse again.
                    if (node["specificationJson"] is JsonValue text && text.TryGetValue<String>(out var json) && !String.IsNullOrEmpty(json))
                    {
                        node.Remove("specificationJson");
                        node["specification"] = JsonNode.Parse(json);
                    }
                    return ToolResults.Ok($"stored query '{name}' ({node["kind"]}, {node["compileState"]}).", node);
                }

                case "delete":
                {
                    if (!tools.EnableWrite)
                    {
                        return ToolResults.Error(403, "Forbidden", "Deleting a stored query needs the write capability (Mcp:Tools:EnableWrite).");
                    }
                    if (String.IsNullOrEmpty(name))
                    {
                        return ToolResults.Error(400, "Invalid arguments", "delete requires 'name'.");
                    }
                    await _bridge.RequestVoidAsync(HttpMethod.Delete, @namespace,
                        $"storedquery/{UrlSafety.EncodeSegment(name)}", null, cancellationToken).ConfigureAwait(false);
                    return ToolResults.Ok($"stored query '{name}' deleted.", new JsonObject { ["deleted"] = true, ["name"] = name });
                }

                case "register":
                {
                    if (!tools.EnableCode)
                    {
                        return ToolResults.Error(403, "Forbidden", "Registering a stored query compiles C# and needs the code capability (Mcp:Tools:EnableCode).");
                    }
                    var kind = ToolArgs.GetString(arguments, "kind");
                    if (String.IsNullOrEmpty(name) || String.IsNullOrEmpty(kind))
                    {
                        return ToolResults.Error(400, "Invalid arguments", "register requires 'name' and 'kind' (Path or SubGraph).");
                    }
                    var body = new JsonObject { ["name"] = name, ["kind"] = kind };
                    var description = ToolArgs.GetString(arguments, "description");
                    if (!String.IsNullOrEmpty(description))
                    {
                        body["description"] = description;
                    }
                    // The blocks are forwarded as sent: the server validates that the one matching the
                    // kind is present and compiles what is in it.
                    foreach (var block in new[] { "path", "subGraph" })
                    {
                        if (ToolArgs.TryGetElement(arguments, block, out var element))
                        {
                            if (element.ValueKind != JsonValueKind.Object)
                            {
                                return ToolResults.Error(400, "Invalid arguments", $"'{block}' must be an object.");
                            }
                            body[block] = JsonNode.Parse(element.GetRawText());
                        }
                    }
                    var raw = await _bridge.RequestRawAsync(HttpMethod.Post, @namespace, "storedquery", body, cancellationToken)
                        .ConfigureAwait(false);
                    var node = ToolResults.Pass(raw);
                    return ToolResults.Ok($"stored query '{name}' registered ({node["compileState"]}).", node);
                }

                default:
                    return ToolResults.Error(400, "Invalid arguments",
                        tools.EnableCode
                            ? "op must be list, get, delete, or register."
                            : "op must be list, get, or delete (register needs the code capability).");
            }
        }
    }
}
