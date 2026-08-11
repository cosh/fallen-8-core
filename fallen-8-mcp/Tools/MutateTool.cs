// MIT License
//
// MutateTool.cs
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
                    "Apply one graph mutation as a transaction (success means it applied). The batch ops " +
                    "create_vertices/create_edges are atomic and RETURN the assigned ids; single creates do not " +
                    "(find them by search). set_property/remove_property/remove_element are no-ops for an absent id.",
                InputSchema = SchemaBuilder.Create()
                    .Str("namespace", "The namespace (graph). Defaults to 'default'.")
                    .Str("op", "The mutation.", required: true, choices: new[]
                    {
                        "create_vertex", "create_edge", "create_vertices", "create_edges",
                        "set_property", "set_properties", "remove_property", "remove_element",
                        "remove_elements", "set_embedding",
                    })
                    .Int("id", "Target element id (set_property/remove_property/remove_element/set_embedding).")
                    .Str("label", "Element label (create_vertex/create_edge).")
                    .Obj("properties", "Property map {key: value} with JSON-native values (create_vertex/create_edge).")
                    .Int("source", "Source vertex id (create_edge).")
                    .Int("target", "Target vertex id (create_edge).")
                    .Str("edgePropertyId", "Edge type/group key (create_edge).")
                    .ObjArray("vertices", "Batch of {label?, properties?} (create_vertices); returns ids in order.")
                    .ObjArray("edges", "Batch of {source, target, edgePropertyId, label?, properties?} (create_edges); returns ids.")
                    .Str("key", "Property key (set_property/remove_property).")
                    .Any("value", "Property value, JSON-native (set_property).")
                    .ObjArray("properties", "Batch of {id, key, value} or {id, key, remove:true} (set_properties); ONE atomic transaction, values REPLACE, an equal value is a no-op.")
                    .ObjArray("ids", "Batch of element ids (remove_elements); ONE atomic transaction.")
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
                    var body = new VertexSpecDto { CreationDate = NowEpoch(), Label = ToolArgs.GetString(arguments, "label"), Properties = properties };
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
                        CreationDate = NowEpoch(),
                        SourceVertex = source.Value,
                        TargetVertex = target.Value,
                        EdgePropertyId = edgePropertyId,
                        Label = ToolArgs.GetString(arguments, "label"),
                        Properties = properties,
                    };
                    await _bridge.RequestVoidAsync(HttpMethod.Put, @namespace, "edge" + Wait, body, cancellationToken).ConfigureAwait(false);
                    return Applied("create_edge", "edge created (id assigned server-side, not returned by REST).");
                }

                case "create_vertices":
                {
                    if (!ToolArgs.TryGetElement(arguments, "vertices", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    {
                        return ToolResults.Error(400, "Invalid arguments", "create_vertices requires a 'vertices' array.");
                    }
                    var specs = new List<VertexSpecDto>();
                    foreach (var v in arr.EnumerateArray())
                    {
                        if (v.ValueKind != JsonValueKind.Object)
                        {
                            return ToolResults.Error(400, "Invalid arguments", "each vertex must be an object.");
                        }
                        if (!BuildPropertiesFrom(v, out var properties, out var error))
                        {
                            return ToolResults.Error(400, "Invalid arguments", error);
                        }
                        specs.Add(new VertexSpecDto { CreationDate = NowEpoch(), Label = ElementString(v, "label"), Properties = properties });
                    }
                    var vertexIds = await _bridge.RequestAsync<List<Int32>>(HttpMethod.Put, @namespace, "vertices" + Wait, specs, cancellationToken)
                        .ConfigureAwait(false) ?? new List<Int32>();
                    return AppliedIds("create_vertices", vertexIds, $"{vertexIds.Count} vertices created.");
                }

                case "create_edges":
                {
                    if (!ToolArgs.TryGetElement(arguments, "edges", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    {
                        return ToolResults.Error(400, "Invalid arguments", "create_edges requires an 'edges' array.");
                    }
                    var specs = new List<EdgeSpecDto>();
                    foreach (var e in arr.EnumerateArray())
                    {
                        if (e.ValueKind != JsonValueKind.Object)
                        {
                            return ToolResults.Error(400, "Invalid arguments", "each edge must be an object.");
                        }
                        var src = ElementInt(e, "source");
                        var tgt = ElementInt(e, "target");
                        var epid = ElementString(e, "edgePropertyId");
                        if (src is null || tgt is null || String.IsNullOrEmpty(epid))
                        {
                            return ToolResults.Error(400, "Invalid arguments", "each edge needs source, target, and edgePropertyId.");
                        }
                        if (!BuildPropertiesFrom(e, out var properties, out var error))
                        {
                            return ToolResults.Error(400, "Invalid arguments", error);
                        }
                        specs.Add(new EdgeSpecDto
                        {
                            CreationDate = NowEpoch(),
                            SourceVertex = src.Value,
                            TargetVertex = tgt.Value,
                            EdgePropertyId = epid,
                            Label = ElementString(e, "label"),
                            Properties = properties,
                        });
                    }
                    var edgeIds = await _bridge.RequestAsync<List<Int32>>(HttpMethod.Put, @namespace, "edges" + Wait, specs, cancellationToken)
                        .ConfigureAwait(false) ?? new List<Int32>();
                    return AppliedIds("create_edges", edgeIds, $"{edgeIds.Count} edges created.");
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
                    return Applied("set_property", $"set_property '{key}' applied to element {id} (no change if it does not exist).");
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

                case "set_properties":
                {
                    // Batch set-or-remove, one atomic transaction (feature platform-integrity-audit W2).
                    // The only atomic way to change several property values at once; the singular ops
                    // are one transaction each.
                    if (!ToolArgs.TryGetElement(arguments, "properties", out var writeArr) || writeArr.ValueKind != JsonValueKind.Array)
                    {
                        return ToolResults.Error(400, "Invalid arguments", "set_properties requires a 'properties' array.");
                    }
                    var writes = new List<PropertyWriteDto>();
                    foreach (var w in writeArr.EnumerateArray())
                    {
                        if (w.ValueKind != JsonValueKind.Object)
                        {
                            return ToolResults.Error(400, "Invalid arguments", "each property write must be an object.");
                        }
                        var targetId = ElementInt(w, "id");
                        var writeKey = ElementString(w, "key");
                        if (targetId is null || String.IsNullOrEmpty(writeKey))
                        {
                            return ToolResults.Error(400, "Invalid arguments", "each property write needs 'id' and 'key'.");
                        }
                        var isRemoval = w.TryGetProperty("remove", out var removeFlag)
                            && removeFlag.ValueKind == JsonValueKind.True;
                        if (isRemoval)
                        {
                            writes.Add(new PropertyWriteDto { GraphElementId = targetId.Value, PropertyId = writeKey, Remove = true });
                            continue;
                        }
                        if (!w.TryGetProperty("value", out var writeValue))
                        {
                            return ToolResults.Error(400, "Invalid arguments",
                                $"property write '{writeKey}' needs a JSON-native 'value' (or \"remove\": true).");
                        }
                        if (!ValueMapping.TryFromJson(writeValue, out var writeLiteral, out var writeFqtn, out var writeError))
                        {
                            return ToolResults.Error(400, "Invalid arguments", $"property write '{writeKey}': " + writeError);
                        }
                        writes.Add(new PropertyWriteDto
                        {
                            GraphElementId = targetId.Value,
                            PropertyId = writeKey,
                            PropertyValue = writeLiteral,
                            FullQualifiedTypeName = writeFqtn
                        });
                    }
                    await _bridge.RequestVoidAsync(HttpMethod.Put, @namespace, "graphelements/properties" + Wait,
                        writes, cancellationToken).ConfigureAwait(false);
                    return Applied("set_properties",
                        $"{writes.Count} property writes applied in one transaction (equal values are no-ops).");
                }

                case "remove_elements":
                {
                    // Batch removal, one atomic transaction. An out-of-range id removes nothing; an
                    // absent-but-in-range id is a no-op, matching the singular op.
                    if (!ToolArgs.TryGetElement(arguments, "ids", out var idArr) || idArr.ValueKind != JsonValueKind.Array)
                    {
                        return ToolResults.Error(400, "Invalid arguments", "remove_elements requires an 'ids' array.");
                    }
                    var ids = new List<Int32>();
                    foreach (var candidate in idArr.EnumerateArray())
                    {
                        if (candidate.ValueKind != JsonValueKind.Number || !candidate.TryGetInt32(out var parsed))
                        {
                            return ToolResults.Error(400, "Invalid arguments", "every id must be an integer.");
                        }
                        ids.Add(parsed);
                    }
                    await _bridge.RequestVoidAsync(HttpMethod.Delete, @namespace, "graphelements" + Wait,
                        ids, cancellationToken).ConfigureAwait(false);
                    return Applied("remove_elements", $"{ids.Count} elements removed in one transaction (absent ids are no-ops).");
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
                        "op must be create_vertex, create_edge, create_vertices, create_edges, set_property, " +
                        "set_properties, remove_property, remove_element, remove_elements, or set_embedding.");
            }
        }

        private static CallToolResult Applied(String op, String summary)
        {
            return ToolResults.Ok(summary, new JsonObject { ["op"] = op, ["applied"] = true });
        }

        private static CallToolResult AppliedIds(String op, List<Int32> ids, String summary)
        {
            var arr = new JsonArray();
            foreach (var id in ids)
            {
                arr.Add(id);
            }
            return ToolResults.Ok(summary, new JsonObject { ["op"] = op, ["applied"] = true, ["ids"] = arr });
        }

        private static String? ElementString(JsonElement element, String name)
        {
            return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        private static Int32? ElementInt(JsonElement element, String name)
        {
            return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i)
                ? i
                : null;
        }

        /// <summary>The current time as the Unix-second creation stamp Fallen-8 expects (rather
        /// than the DTO's placeholder), so MCP-created elements carry an honest creationDate.</summary>
        private static UInt32 NowEpoch()
        {
            return (UInt32)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        /// <summary>Properties from the tool's top-level <c>properties</c> argument (single create).</summary>
        private static Boolean BuildProperties(
            IReadOnlyDictionary<String, JsonElement> arguments,
            out List<PropertySpecDto> properties,
            out String error)
        {
            var map = arguments.TryGetValue("properties", out var m) ? m : default;
            return PropertiesFromMap(map, out properties, out error);
        }

        /// <summary>Properties from a batch element object's own <c>properties</c> key.</summary>
        private static Boolean BuildPropertiesFrom(
            JsonElement container,
            out List<PropertySpecDto> properties,
            out String error)
        {
            var map = container.ValueKind == JsonValueKind.Object && container.TryGetProperty("properties", out var m)
                ? m
                : default;
            return PropertiesFromMap(map, out properties, out error);
        }

        private static Boolean PropertiesFromMap(JsonElement map, out List<PropertySpecDto> properties, out String error)
        {
            properties = new List<PropertySpecDto>();
            error = String.Empty;

            if (map.ValueKind != JsonValueKind.Object)
            {
                return true; // properties are optional/absent
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
