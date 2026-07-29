// MIT License
//
// ElementProjection.cs
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
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NoSQL.GraphDB.Mcp.Bridge
{
    /// <summary>
    ///   Renders a raw Fallen-8 element (the JSON of <c>GET /vertex|/edge/{id}</c>) into the
    ///   token-frugal shape agents consume (spec §3.5): <c>id</c> + <c>label</c> (+ an edge's
    ///   <c>edgePropertyId</c>) + <em>scalar</em>
    ///   property values by default, with large values bounded so a single embedding never blows
    ///   the budget — a vector/array-typed value (FQTN ending <c>[]</c>) is omitted in favour of
    ///   its key + type + length, and a long string is truncated with a marker. Optional
    ///   <c>fields</c> narrows to named properties; <c>include</c> adds the neighbourhood the
    ///   single getter already returned (so it costs no extra round-trip).
    /// </summary>
    public static class ElementProjection
    {
        private const Int32 MaxStringLength = 256;

        public static JsonObject Compact(JsonElement element, ISet<String>? fields, ISet<String> include)
        {
            var node = new JsonObject();

            if (element.TryGetProperty("id", out var id))
            {
                node["id"] = id.GetInt32();
            }
            if (element.TryGetProperty("label", out var label))
            {
                node["label"] = label.GetString();
            }
            // The edge's type (its adjacency group) - present on edge payloads only.
            if (element.TryGetProperty("edgePropertyId", out var edgePropertyId))
            {
                node["edgePropertyId"] = edgePropertyId.GetString();
            }

            if (element.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Array)
            {
                var propsNode = new JsonObject();
                foreach (var property in properties.EnumerateArray())
                {
                    var key = property.TryGetProperty("propertyId", out var k) ? k.GetString() : null;
                    if (key is null || (fields is not null && !fields.Contains(key)))
                    {
                        continue;
                    }

                    var fqtn = property.TryGetProperty("fullQualifiedTypeName", out var t) ? t.GetString() : null;
                    var raw = property.TryGetProperty("propertyValue", out var v) ? v.GetString() : null;
                    propsNode[key] = ValueNode(raw, fqtn);
                }
                node["properties"] = propsNode;
            }

            // Adjacency is already in the single getter's payload — include is a projection, not a call.
            if (include.Contains("out_edges") && element.TryGetProperty("outEdges", out var outEdges))
            {
                node["outEdges"] = JsonNode.Parse(outEdges.GetRawText());
            }
            if (include.Contains("in_edges") && element.TryGetProperty("inEdges", out var inEdges))
            {
                node["inEdges"] = JsonNode.Parse(inEdges.GetRawText());
            }
            if (include.Contains("source") && element.TryGetProperty("sourceVertex", out var source))
            {
                node["sourceVertex"] = source.GetInt32();
            }
            if (include.Contains("target") && element.TryGetProperty("targetVertex", out var target))
            {
                node["targetVertex"] = target.GetInt32();
            }
            if (include.Contains("degree"))
            {
                node["degree"] = Degree(element);
            }

            return node;
        }

        /// <summary>The count of outgoing + incoming edges, from the grouped adjacency lists.</summary>
        private static Int32 Degree(JsonElement element)
        {
            var degree = 0;
            foreach (var group in new[] { "outEdges", "inEdges" })
            {
                if (element.TryGetProperty(group, out var g) && g.ValueKind == JsonValueKind.Object)
                {
                    foreach (var kv in g.EnumerateObject())
                    {
                        if (kv.Value.ValueKind == JsonValueKind.Array)
                        {
                            degree += kv.Value.GetArrayLength();
                        }
                    }
                }
            }
            return degree;
        }

        /// <summary>
        ///   Maps a stored property (string value + FQTN) to a compact JSON node: the native
        ///   scalar for a scalar type, an omission stub for an array/vector type, and a truncated
        ///   string for an over-long value.
        /// </summary>
        private static JsonNode? ValueNode(String? raw, String? fqtn)
        {
            if (raw is null)
            {
                return null;
            }

            // Array/vector values (e.g. System.Single[] embeddings) are omitted by default — key,
            // type and length only — so one element can never blow the token budget (spec §3.5).
            if (fqtn is not null && fqtn.EndsWith("[]", StringComparison.Ordinal))
            {
                return new JsonObject
                {
                    ["type"] = fqtn,
                    ["omitted"] = true,
                    ["length"] = raw.Length,
                };
            }

            switch (fqtn)
            {
                case "System.Int16":
                case "System.Int32":
                case "System.Int64":
                    if (Int64.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                    {
                        return JsonValue.Create(l);
                    }
                    break;

                case "System.Single":
                case "System.Double":
                case "System.Decimal":
                    if (Double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    {
                        return JsonValue.Create(d);
                    }
                    break;

                case "System.Boolean":
                    if (Boolean.TryParse(raw, out var b))
                    {
                        return JsonValue.Create(b);
                    }
                    break;
            }

            if (raw.Length > MaxStringLength)
            {
                return JsonValue.Create(raw.Substring(0, MaxStringLength) + $"…(truncated, {raw.Length} chars)");
            }

            return JsonValue.Create(raw);
        }
    }
}
