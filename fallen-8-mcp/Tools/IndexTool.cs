// MIT License
//
// IndexTool.cs
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
using NoSQL.GraphDB.Rest;

namespace NoSQL.GraphDB.Mcp.Tools
{
    /// <summary>
    ///   <c>f8_index</c> - the index lifecycle (feature mcp-plugin-gaps, spec section 6): create an
    ///   index, populate it one element or many at a time, write an element's vector into a vector
    ///   index, remove an element or a key, backfill from a property, delete. Write tier, like
    ///   <c>f8_documents op:bind</c> and <c>f8_subgraph</c>: a per-namespace data structure, not a
    ///   process-level act. The REST routes answer a bare boolean for most of these, and a
    ///   <c>false</c> cannot say WHY; the bridge turns it into an error whose message names every
    ///   cause the server could have had, and says that the status is the bridge's reading.
    /// </summary>
    public sealed class IndexTool : IMcpTool
    {
        private readonly Fallen8RestClient _bridge;

        public IndexTool(Fallen8RestClient bridge)
        {
            _bridge = bridge;
        }

        public String Name => "f8_index";

        public ToolTier Tier => ToolTier.Write;

        public Tool Describe(McpToolsOptions tools)
        {
            return new Tool
            {
                Name = Name,
                Title = "Index lifecycle",
                Description =
                    "Create, populate, repair and delete indices in a namespace. create needs pluginType (one of " +
                    "f8_overview's availableIndexPlugins; a VectorIndex takes options {dimension, metric, embeddingName, model}); " +
                    "add/add_many put elements under a key; add_vector writes an element's vector; backfill fills an index from " +
                    "a property across the live graph; remove_element/remove_key/delete undo. Search the result with f8_search.",
                InputSchema = SchemaBuilder.Create()
                    .Str("op", "The operation.", required: true, choices: new[]
                    {
                        "create", "add", "add_many", "add_vector", "remove_element", "remove_key", "delete", "backfill",
                    })
                    .Str("namespace", "The namespace (graph). Defaults to 'default'.")
                    .Str("indexId", "The index id (every op).", required: true)
                    .Str("pluginType", "Index plugin type (create): e.g. DictionaryIndex, RangeIndex, SingleValueIndex, VectorIndex.")
                    .Obj("options", "Plugin options with JSON-native values (create), e.g. {dimension: 384, metric: \"Cosine\", embeddingName: \"text\"}.")
                    .Int("id", "Element id (add/add_vector/remove_element).")
                    .Any("key", "Index key, JSON-native (add/remove_key); the bridge infers the type.")
                    .ObjArray("entries", "Batch of {id, key} (add_many); the server answers how many it accepted and declined.")
                    .NumArray("vector", "The vector to store (add_vector); exactly one of vector / propertyId.")
                    .Str("propertyId", "add_vector: the element's float[] property to copy from (exactly one of vector / propertyId). backfill: the property whose values fill the index (required).")
                    .Bool("replace", "Clear the index before filling it (backfill). Default false.")
                    .Bool("prefix", "Index string values by prefix as well (backfill). Default false.")
                    .Str("label", "Only elements with this label (backfill).")
                    .Build(),
                Annotations = new ToolAnnotations
                {
                    Title = "Index lifecycle",
                    ReadOnlyHint = false,
                    // delete drops an index and backfill with replace empties one: clients confirm.
                    DestructiveHint = true,
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
            var indexId = ToolArgs.GetString(arguments, "indexId");
            if (String.IsNullOrEmpty(indexId))
            {
                return ToolResults.Error(400, "Invalid arguments", "indexId is required.");
            }
            var encodedIndex = UrlSafety.EncodeSegment(indexId);

            switch (op)
            {
                case "create":
                {
                    var pluginType = ToolArgs.GetString(arguments, "pluginType");
                    if (String.IsNullOrEmpty(pluginType))
                    {
                        return ToolResults.Error(400, "Invalid arguments", "create requires pluginType.");
                    }
                    var body = new IndexCreateDto { UniqueId = indexId, PluginType = pluginType };
                    if (ToolArgs.TryGetElement(arguments, "options", out var options))
                    {
                        if (options.ValueKind != JsonValueKind.Object)
                        {
                            return ToolResults.Error(400, "Invalid arguments", "options must be an object of JSON-native values.");
                        }
                        foreach (var option in options.EnumerateObject())
                        {
                            if (!TryProperty(option.Name, option.Value, out var spec, out var error))
                            {
                                return ToolResults.Error(400, "Invalid arguments", $"option '{option.Name}': {error}");
                            }
                            body.PluginOptions[option.Name] = spec;
                        }
                    }
                    var created = await _bridge.RequestAsync<Boolean>(HttpMethod.Post, @namespace, "index", body, cancellationToken)
                        .ConfigureAwait(false);
                    if (!created)
                    {
                        // POST /index answers 200 false for both causes and cannot tell them apart; the
                        // 409 is this bridge's reading of that answer, and the message says so.
                        return ToolResults.Error(409, "Index not created",
                            $"the target refused to create '{indexId}' (it answers false, not a reason): either the id is already " +
                            "taken or the plugin type is unknown. f8_overview lists availableIndexPlugins and, with detail " +
                            "'statistics', the existing index ids. Status 409 is the bridge's reading of a bare false.");
                    }
                    return Applied("create", indexId, $"index '{indexId}' ({pluginType}) created.");
                }

                case "add":
                {
                    var id = ToolArgs.GetInt(arguments, "id");
                    if (id is null || !ToolArgs.TryGetElement(arguments, "key", out var key))
                    {
                        return ToolResults.Error(400, "Invalid arguments", "add requires id and a JSON-native key.");
                    }
                    if (!TryProperty("key", key, out var spec, out var error))
                    {
                        return ToolResults.Error(400, "Invalid arguments", "key: " + error);
                    }
                    var added = await _bridge.RequestAsync<Boolean>(HttpMethod.Put, @namespace, $"index/{encodedIndex}",
                        new IndexAddDto { GraphElementId = id.Value, Key = spec }, cancellationToken).ConfigureAwait(false);
                    return added
                        ? Applied("add", indexId, $"element {id} indexed under the key in '{indexId}'.")
                        : NothingChanged("add", $"element {id} was not added: there is no index '{indexId}' or no element {id}.");
                }

                case "add_many":
                {
                    if (!ToolArgs.TryGetElement(arguments, "entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                    {
                        return ToolResults.Error(400, "Invalid arguments", "add_many requires an 'entries' array of {id, key}.");
                    }
                    var batch = new List<IndexAddDto>();
                    foreach (var entry in entries.EnumerateArray())
                    {
                        if (entry.ValueKind != JsonValueKind.Object
                            || !entry.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number
                            || !idElement.TryGetInt32(out var entryId)
                            || !entry.TryGetProperty("key", out var entryKey))
                        {
                            return ToolResults.Error(400, "Invalid arguments", "each entry needs an integer 'id' and a 'key'.");
                        }
                        if (!TryProperty("key", entryKey, out var spec, out var error))
                        {
                            return ToolResults.Error(400, "Invalid arguments", $"entry {entryId} key: {error}");
                        }
                        batch.Add(new IndexAddDto { GraphElementId = entryId, Key = spec });
                    }
                    var result = await _bridge.RequestRawAsync(HttpMethod.Put, @namespace, $"index/{encodedIndex}/batch", batch, cancellationToken)
                        .ConfigureAwait(false);
                    var node = ToolResults.Pass(result);
                    node["op"] = "add_many";
                    node["indexId"] = indexId;
                    return ToolResults.Ok($"{batch.Count} entries sent to '{indexId}'; see accepted/declined.", node);
                }

                case "add_vector":
                {
                    var id = ToolArgs.GetInt(arguments, "id");
                    var vector = ToolArgs.GetSingleArray(arguments, "vector");
                    var propertyId = ToolArgs.GetString(arguments, "propertyId");
                    if (id is null)
                    {
                        return ToolResults.Error(400, "Invalid arguments", "add_vector requires id.");
                    }
                    if ((vector is null) == String.IsNullOrEmpty(propertyId))
                    {
                        return ToolResults.Error(400, "Invalid arguments", "add_vector takes exactly one of 'vector' or 'propertyId'.");
                    }
                    await _bridge.RequestAsync<Boolean>(HttpMethod.Put, @namespace, $"index/vector/{encodedIndex}",
                        new VectorIndexAddDto { GraphElementId = id.Value, Vector = vector, PropertyId = propertyId }, cancellationToken)
                        .ConfigureAwait(false);
                    // The route answers true or a problem (400/404), never a bare false.
                    return Applied("add_vector", indexId, $"vector for element {id} stored in '{indexId}'.");
                }

                case "remove_element":
                {
                    var id = ToolArgs.GetInt(arguments, "id");
                    if (id is null)
                    {
                        return ToolResults.Error(400, "Invalid arguments", "remove_element requires id.");
                    }
                    var removed = await _bridge.RequestRawAsync(HttpMethod.Delete, @namespace, $"index/{encodedIndex}/{id}", null, cancellationToken)
                        .ConfigureAwait(false);
                    return IsTrue(removed)
                        ? Applied("remove_element", indexId, $"element {id} removed from '{indexId}'.")
                        : NothingChanged("remove_element", $"nothing removed: there is no index '{indexId}' or element {id} is not in it.");
                }

                case "remove_key":
                {
                    if (!ToolArgs.TryGetElement(arguments, "key", out var key))
                    {
                        return ToolResults.Error(400, "Invalid arguments", "remove_key requires a JSON-native key.");
                    }
                    if (!TryProperty("key", key, out var spec, out var error))
                    {
                        return ToolResults.Error(400, "Invalid arguments", "key: " + error);
                    }
                    var removed = await _bridge.RequestAsync<Boolean>(HttpMethod.Delete, @namespace, $"index/{encodedIndex}/propertyValue", spec, cancellationToken)
                        .ConfigureAwait(false);
                    return removed
                        ? Applied("remove_key", indexId, $"key removed from '{indexId}'.")
                        : NothingChanged("remove_key", $"nothing removed: there is no index '{indexId}' or the key is not in it.");
                }

                case "delete":
                {
                    var deleted = await _bridge.RequestRawAsync(HttpMethod.Delete, @namespace, $"index/{encodedIndex}", null, cancellationToken)
                        .ConfigureAwait(false);
                    return IsTrue(deleted)
                        ? Applied("delete", indexId, $"index '{indexId}' deleted.")
                        : NothingChanged("delete", $"nothing deleted: there is no index '{indexId}'.");
                }

                case "backfill":
                {
                    var propertyId = ToolArgs.GetString(arguments, "propertyId");
                    if (String.IsNullOrEmpty(propertyId))
                    {
                        return ToolResults.Error(400, "Invalid arguments", "backfill requires propertyId.");
                    }
                    var body = new IndexBackfillDto
                    {
                        PropertyId = propertyId,
                        Replace = ToolArgs.GetBool(arguments, "replace") ?? false,
                        Prefix = ToolArgs.GetBool(arguments, "prefix") ?? false,
                        Label = ToolArgs.GetString(arguments, "label"),
                    };
                    var result = await _bridge.RequestRawAsync(HttpMethod.Post, @namespace, $"index/backfill/{encodedIndex}", body, cancellationToken)
                        .ConfigureAwait(false);
                    var node = ToolResults.Pass(result);
                    node["op"] = "backfill";
                    return ToolResults.Ok($"index '{indexId}' backfilled from '{propertyId}'; see scannedElements/indexedElements.", node);
                }

                default:
                    return ToolResults.Error(400, "Invalid arguments",
                        "op must be create, add, add_many, add_vector, remove_element, remove_key, delete, or backfill.");
            }
        }

        private static CallToolResult Applied(String op, String indexId, String summary)
        {
            return ToolResults.Ok(summary, new JsonObject { ["op"] = op, ["indexId"] = indexId, ["applied"] = true });
        }

        /// <summary>A bare <c>false</c> from a route that cannot say why. 404 is the bridge's reading
        /// (the causes are all "something named here does not exist"), and the message says so.</summary>
        private static CallToolResult NothingChanged(String op, String detail)
        {
            return ToolResults.Error(404, $"{op}: nothing changed", detail + " Status 404 is the bridge's reading of a bare false.");
        }

        /// <summary>The bare boolean body of a body-less DELETE route (<c>true</c>, <c>false</c>, or
        /// nothing on a soft 204).</summary>
        private static Boolean IsTrue(JsonElement? raw)
        {
            return raw.HasValue && raw.Value.ValueKind == JsonValueKind.True;
        }

        private static Boolean TryProperty(String propertyId, JsonElement value, out PropertySpecDto spec, out String error)
        {
            if (!ValueMapping.TryFromJson(value, out var literal, out var fqtn, out error))
            {
                spec = new PropertySpecDto();
                return false;
            }
            spec = new PropertySpecDto { PropertyId = propertyId, PropertyValue = literal, FullQualifiedTypeName = fqtn };
            return true;
        }
    }
}
