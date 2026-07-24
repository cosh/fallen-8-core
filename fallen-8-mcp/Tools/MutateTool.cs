// MIT License
//
// MutateTool.cs
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
using System.Net.Http;
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
    ///   <c>f8_mutate</c> — apply ONE mutation as a transaction (spec §3.2/§3.7). Always
    ///   <c>waitForCompletion=true</c>, so success means the transaction applied. Per-op honesty:
    ///   create_vertex/create_edge roll back to an error on a bad reference; set_property /
    ///   remove_property / remove_element are idempotent no-ops for an absent-but-in-range id
    ///   (success ≠ "the element existed"), and an out-of-range id surfaces as a 500 tool error.
    ///   Property/literal values are JSON-native — the bridge infers the .NET type.
    /// </summary>
    public sealed class MutateTool : IMcpTool
    {
        private const String Wait = "?waitForCompletion=true";

        private readonly Fallen8RestClient _bridge;

        public MutateTool(Fallen8RestClient bridge)
        {
            _bridge = bridge;
        }

        public String Name => "f8_mutate";

        public ToolTier Tier => ToolTier.Write;

        public Tool Describe(McpToolsOptions tools)
        {
            return new Tool
            {
                Name = Name,
                Title = "Mutate",
                Description =
                    "Apply one graph mutation (transactional; success means applied). Property/element " +
                    "removals are no-ops for an absent id. Creates do not return the new id — find it by search.",
                InputSchema = SchemaBuilder.Create()
                    .Str("namespace", "The namespace (graph). Defaults to 'default'.")
                    .Str("op", "The mutation.", required: true, choices: new[]
                    {
                        "create_vertex", "create_edge", "set_property", "remove_property", "remove_element", "set_embedding",
                    })
                    .Int("id", "Target element id (set_property/remove_property/remove_element/set_embedding).")
                    .Str("label", "Element label (create_vertex/create_edge).")
                    .Obj("properties", "Property map {key: value} with JSON-native values (create_vertex/create_edge).")
                    .Int("source", "Source vertex id (create_edge).")
                    .Int("target", "Target vertex id (create_edge).")
                    .Str("edgePropertyId", "Edge type/group key (create_edge).")
                    .Str("key", "Property key (set_property/remove_property).")
                    .Any("value", "Property value, JSON-native (set_property).")
                    .Str("name", "Embedding name (set_embedding).")
                    .NumArray("vector", "Embedding vector (set_embedding).")
                    .Build(),
                Annotations = new ToolAnnotations
                {
                    Title = "Mutate",
                    ReadOnlyHint = false,
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

            switch (op)
            {
                case "create_vertex":
                {
                    if (!BuildProperties(arguments, out var properties, out var error))
                    {
                        return ToolResults.Error(400, "Invalid arguments", error);
                    }
                    var body = new VertexSpecDto { Label = ToolArgs.GetString(arguments, "label"), Properties = properties };
                    await _bridge.RequestVoidAsync(HttpMethod.Put, @namespace, "vertex" + Wait, body, cancellationToken).ConfigureAwait(false);
                    return Applied("create_vertex", "vertex created (id assigned server-side, not returned by REST — find it via f8_search).");
                }

                case "create_edge":
                {
                    var source = ToolArgs.GetInt(arguments, "source");
                    var target = ToolArgs.GetInt(arguments, "target");
                    var edgePropertyId = ToolArgs.GetString(arguments, "edgePropertyId");
                    if (source is null || target is null || String.IsNullOrEmpty(edgePropertyId))
                    {
                        return ToolResults.Error(400, "Invalid arguments", "create_edge requires source, target, and edgePropertyId.");
                    }
                    if (!BuildProperties(arguments, out var properties, out var error))
                    {
                        return ToolResults.Error(400, "Invalid arguments", error);
                    }
                    var body = new EdgeSpecDto
                    {
                        SourceVertex = source.Value,
                        TargetVertex = target.Value,
                        EdgePropertyId = edgePropertyId,
                        Label = ToolArgs.GetString(arguments, "label"),
                        Properties = properties,
                    };
                    await _bridge.RequestVoidAsync(HttpMethod.Put, @namespace, "edge" + Wait, body, cancellationToken).ConfigureAwait(false);
                    return Applied("create_edge", "edge created (id assigned server-side, not returned by REST).");
                }

                case "set_property":
                {
                    var id = ToolArgs.GetInt(arguments, "id");
                    var key = ToolArgs.GetString(arguments, "key");
                    if (id is null || String.IsNullOrEmpty(key))
                    {
                        return ToolResults.Error(400, "Invalid arguments", "set_property requires id and key.");
                    }
                    if (!ToolArgs.TryGetElement(arguments, "value", out var value))
                    {
                        return ToolResults.Error(400, "Invalid arguments", "set_property requires a JSON-native 'value'.");
                    }
                    if (!ValueMapping.TryFromJson(value, out var literal, out var fqtn, out var error))
                    {
                        return ToolResults.Error(400, "Invalid arguments", "set_property value: " + error);
                    }
                    var body = new PropertySpecDto { PropertyId = key, PropertyValue = literal, FullQualifiedTypeName = fqtn };
                    await _bridge.RequestVoidAsync(HttpMethod.Put, @namespace,
                        $"graphelement/{id}/{UrlSafety.EncodeSegment(key)}" + Wait, body, cancellationToken).ConfigureAwait(false);
                    return Applied("set_property", $"property '{key}' set on element {id}.");
                }

                case "remove_property":
                {
                    var id = ToolArgs.GetInt(arguments, "id");
                    var key = ToolArgs.GetString(arguments, "key");
                    if (id is null || String.IsNullOrEmpty(key))
                    {
                        return ToolResults.Error(400, "Invalid arguments", "remove_property requires id and key.");
                    }
                    await _bridge.RequestVoidAsync(HttpMethod.Delete, @namespace,
                        $"graphelement/{id}/{UrlSafety.EncodeSegment(key)}" + Wait, null, cancellationToken).ConfigureAwait(false);
                    return Applied("remove_property", $"property '{key}' removed from element {id} (no-op if absent).");
                }

                case "remove_element":
                {
                    var id = ToolArgs.GetInt(arguments, "id");
                    if (id is null)
                    {
                        return ToolResults.Error(400, "Invalid arguments", "remove_element requires id.");
                    }
                    await _bridge.RequestVoidAsync(HttpMethod.Delete, @namespace,
                        $"graphelement/{id}" + Wait, null, cancellationToken).ConfigureAwait(false);
                    return Applied("remove_element", $"element {id} removed (no-op if absent).");
                }

                case "set_embedding":
                {
                    var id = ToolArgs.GetInt(arguments, "id");
                    var name = ToolArgs.GetString(arguments, "name");
                    var vector = ToolArgs.GetSingleArray(arguments, "vector");
                    if (id is null || String.IsNullOrEmpty(name) || vector is null || vector.Length == 0)
                    {
                        return ToolResults.Error(400, "Invalid arguments", "set_embedding requires id, name, and a numeric vector.");
                    }
                    var body = new EmbeddingWriteDto { Vector = vector };
                    await _bridge.RequestVoidAsync(HttpMethod.Put, @namespace,
                        $"graphelement/{id}/embedding/{UrlSafety.EncodeSegment(name)}" + Wait, body, cancellationToken).ConfigureAwait(false);
                    return Applied("set_embedding", $"embedding '{name}' set on element {id}.");
                }

                default:
                    return ToolResults.Error(400, "Invalid arguments",
                        "op must be create_vertex, create_edge, set_property, remove_property, remove_element, or set_embedding.");
            }
        }

        private static CallToolResult Applied(String op, String summary)
        {
            return ToolResults.Ok(summary, new JsonObject { ["op"] = op, ["applied"] = true });
        }

        private static Boolean BuildProperties(
            IReadOnlyDictionary<String, JsonElement> arguments,
            out List<PropertySpecDto> properties,
            out String error)
        {
            properties = new List<PropertySpecDto>();
            error = String.Empty;

            if (!arguments.TryGetValue("properties", out var map) || map.ValueKind != JsonValueKind.Object)
            {
                return true; // properties are optional
            }

            foreach (var property in map.EnumerateObject())
            {
                if (!ValueMapping.TryFromJson(property.Value, out var literal, out var fqtn, out error))
                {
                    error = $"property '{property.Name}': {error}";
                    return false;
                }
                properties.Add(new PropertySpecDto { PropertyId = property.Name, PropertyValue = literal, FullQualifiedTypeName = fqtn });
            }
            return true;
        }
    }
}
