// MIT License
//
// SearchTool.cs
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
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using NoSQL.GraphDB.Mcp.Bridge;
using NoSQL.GraphDB.Mcp.Bridge.Dto;
using NoSQL.GraphDB.Mcp.Configuration;

namespace NoSQL.GraphDB.Mcp.Tools
{
    /// <summary>
    ///   <c>f8_search</c> — find elements across five modes (spec §3.2). Results are id-first
    ///   (<c>{id, score?}</c>; score for fulltext/vector/semantic) so a page is cheap; label and
    ///   property enrichment is opt-in via <c>fields</c> (which owns an N+1 element fetch per hit).
    ///   Paginated with a capped default <c>limit</c> and a stateless <c>cursor</c> = offset over
    ///   the deterministic result order (spec §3.5).
    /// </summary>
    public sealed class SearchTool : IMcpTool
    {
        private const Int32 DefaultLimit = 25;
        private const Int32 MaxLimit = 200;
        private const Int32 MaxK = 1024;

        private readonly Fallen8RestClient _bridge;

        public SearchTool(Fallen8RestClient bridge)
        {
            _bridge = bridge;
        }

        public String Name => "f8_search";

        public ToolTier Tier => ToolTier.Read;

        public Tool Describe(McpToolsOptions tools)
        {
            return new Tool
            {
                Name = Name,
                Title = "Search",
                Description =
                    "Find elements. Modes: index (indexed equality/comparison), property (un-indexed scan " +
                    "of ONE named key with an operator), properties (un-indexed case-insensitive contains scan " +
                    "across EVERY property value - cold discovery), fulltext, vector (kNN over a query vector), " +
                    "semantic (kNN over query text). Returns ids (+score); set 'fields' to enrich with properties.",
                InputSchema = SchemaBuilder.Create()
                    .Str("namespace", "The namespace (graph). Defaults to 'default'.")
                    .Str("mode", "Search mode.", required: true,
                        choices: new[] { "index", "property", "properties", "fulltext", "vector", "semantic" })
                    .Str("indexId", "Index name (index/fulltext/vector/semantic modes).")
                    .Str("key", "Property key (property mode - the single named key to scan).")
                    .Str("operator", "Comparison operator for 'value' (index/property modes).",
                        choices: new[] { "equal", "greater", "greater_or_equal", "less", "less_or_equal", "not_equal" })
                    .Any("value", "Comparison literal, JSON-native — the bridge infers the type (index/property modes).")
                    .Str("query", "Query text (fulltext/semantic modes; properties mode = the substring to find across all values).")
                    .NumArray("vector", "Query vector for kNN (vector mode).")
                    .Str("kind", "Restrict to a kind (honoured by index/property/properties/vector/semantic; ignored by fulltext).",
                        choices: new[] { "vertex", "edge", "any" })
                    .Str("label", "Restrict to elements with exactly this label (honoured by " +
                        "index/property/properties/vector/semantic; ignored by fulltext).")
                    .Int("limit", "Max hits per page (default 25, cap 200). Drives k for vector/semantic.")
                    .Int("cursor", "Offset into the result order (from a prior nextCursor).")
                    .StrArray("fields", "Enrich each hit with these property keys (costs one fetch per hit).")
                    .Build(),
                Annotations = new ToolAnnotations
                {
                    Title = "Search",
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
            var mode = ToolArgs.GetString(arguments, "mode");
            var @namespace = ToolArgs.GetString(arguments, "namespace");
            var kind = ToolArgs.GetString(arguments, "kind") ?? "any";
            var label = ToolArgs.GetString(arguments, "label");
            var limit = Math.Clamp(ToolArgs.GetInt(arguments, "limit") ?? DefaultLimit, 1, MaxLimit);
            var offset = Math.Max(0, ToolArgs.GetInt(arguments, "cursor") ?? 0);
            var fields = ToolArgs.GetStringSet(arguments, "fields");
            var end = offset + limit;

            // Gathered hits (id + optional score) in the mode's deterministic order. For the
            // bounded modes we over-fetch by one (end + 1) so hasMore can be decided by a strictly
            // extra hit rather than a full window (which would over-report at the exact boundary).
            var fetch = Math.Min(end + 1, MaxK);
            List<(Int32 Id, Double? Score)> gathered;

            switch (mode)
            {
                case "index":
                case "property":
                {
                    var validation = BuildScanLiteral(arguments, mode!, kind, label, out var request, out var propertyKey, out var error);
                    if (!validation)
                    {
                        return ToolResults.Error(400, "Invalid arguments", error);
                    }

                    var suffix = mode == "index" ? "scan/index/all" : $"scan/graph/property/{UrlSafety.EncodeSegment(propertyKey!)}";
                    var ids = await _bridge.PostAsync<List<Int32>>(@namespace, suffix, request!, cancellationToken).ConfigureAwait(false)
                              ?? new List<Int32>();
                    gathered = ToHits(ids);
                    break;
                }

                case "properties":
                {
                    // Cold contains scan across every property value. Uses 'query' as the term (no
                    // operator/value/indexId); kind -> resultType and label restrict the hits.
                    var term = ToolArgs.GetString(arguments, "query");
                    if (String.IsNullOrWhiteSpace(term))
                    {
                        return ToolResults.Error(400, "Invalid arguments", "properties mode requires a non-blank 'query' term.");
                    }

                    var request = new PropertySearchRequest { SearchTerm = term, ResultType = ResultType(kind), Label = label };
                    var ids = await _bridge.PostAsync<List<Int32>>(@namespace, "scan/graph/properties", request, cancellationToken).ConfigureAwait(false)
                              ?? new List<Int32>();
                    gathered = ToHits(ids);
                    break;
                }

                case "fulltext":
                {
                    var indexId = ToolArgs.GetString(arguments, "indexId");
                    var query = ToolArgs.GetString(arguments, "query");
                    if (String.IsNullOrEmpty(indexId) || String.IsNullOrEmpty(query))
                    {
                        return ToolResults.Error(400, "Invalid arguments", "fulltext mode requires 'indexId' and 'query'.");
                    }

                    var result = await _bridge.PostAsync<FulltextResultDto>(@namespace, "scan/index/fulltext",
                        new FulltextScanRequest { IndexId = indexId, RequestString = query }, cancellationToken).ConfigureAwait(false);

                    // The engine returns the SAME 204/null for "no such fulltext index" and for a
                    // real index with zero matches, so treat it as an empty page ("searched, no
                    // hits") — consistent with the index/property/vector modes — rather than
                    // dishonestly reporting the index as missing.
                    var elements = result?.Elements ?? new List<FulltextHitDto>();
                    gathered = new List<(Int32, Double?)>(elements.Count);
                    foreach (var hit in elements)
                    {
                        gathered.Add((hit.GraphElementId, hit.Score));
                    }
                    break;
                }

                case "vector":
                case "semantic":
                {
                    var indexId = ToolArgs.GetString(arguments, "indexId");
                    if (String.IsNullOrEmpty(indexId))
                    {
                        return ToolResults.Error(400, "Invalid arguments", $"{mode} mode requires 'indexId'.");
                    }

                    var k = fetch;
                    VectorResultDto? result;
                    if (mode == "vector")
                    {
                        var vector = ToolArgs.GetSingleArray(arguments, "vector");
                        if (vector is null || vector.Length == 0)
                        {
                            return ToolResults.Error(400, "Invalid arguments", "vector mode requires a numeric 'vector' array.");
                        }
                        result = await _bridge.PostAsync<VectorResultDto>(@namespace, "scan/index/vector",
                            new VectorScanRequest { IndexId = indexId, Query = vector, K = k, Kind = KindFilter(kind), Label = label },
                            cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        var query = ToolArgs.GetString(arguments, "query");
                        if (String.IsNullOrEmpty(query))
                        {
                            return ToolResults.Error(400, "Invalid arguments", "semantic mode requires 'query' text.");
                        }
                        result = await _bridge.PostAsync<VectorResultDto>(@namespace, "embedding/search",
                            new SemanticScanRequest { IndexId = indexId, Text = query, K = k, Kind = KindFilter(kind), Label = label },
                            cancellationToken).ConfigureAwait(false);
                    }

                    var hits = result?.Results ?? new List<VectorHitDto>();
                    gathered = new List<(Int32, Double?)>(hits.Count);
                    foreach (var hit in hits)
                    {
                        gathered.Add((hit.GraphElementId, hit.Score));
                    }
                    break;
                }

                default:
                    return ToolResults.Error(400, "Invalid arguments", "mode must be index, property, properties, fulltext, vector, or semantic.");
            }

            return await BuildPageAsync(mode!, @namespace, gathered, offset, end, fields, cancellationToken)
                .ConfigureAwait(false);
        }

        private static List<(Int32 Id, Double? Score)> ToHits(List<Int32> ids)
        {
            var hits = new List<(Int32, Double?)>(ids.Count);
            foreach (var id in ids)
            {
                hits.Add((id, null));
            }
            return hits;
        }

        private Boolean BuildScanLiteral(
            IReadOnlyDictionary<String, JsonElement> arguments,
            String mode,
            String kind,
            String? label,
            out Object? request,
            out String? propertyKey,
            out String error)
        {
            request = null;
            propertyKey = null;
            error = String.Empty;

            if (!ToolArgs.TryGetElement(arguments, "value", out var value))
            {
                error = $"{mode} mode requires a 'value' to compare.";
                return false;
            }
            if (!ValueMapping.TryFromJson(value, out var literal, out var fqtn, out error))
            {
                return false;
            }

            var op = OperatorCode(ToolArgs.GetString(arguments, "operator") ?? "equal");
            if (op is null)
            {
                error = "operator must be equal, greater, greater_or_equal, less, less_or_equal, or not_equal.";
                return false;
            }

            var resultType = ResultType(kind);
            var literalDto = new LiteralDto { Value = literal, FullQualifiedTypeName = fqtn };

            if (mode == "index")
            {
                var indexId = ToolArgs.GetString(arguments, "indexId");
                if (String.IsNullOrEmpty(indexId))
                {
                    error = "index mode requires 'indexId'.";
                    return false;
                }
                request = new IndexScanRequest { IndexId = indexId, Operator = op.Value, Literal = literalDto, ResultType = resultType, Label = label };
                return true;
            }

            propertyKey = ToolArgs.GetString(arguments, "key");
            if (String.IsNullOrEmpty(propertyKey))
            {
                error = "property mode requires 'key'.";
                return false;
            }
            request = new PropertyScanRequest { Operator = op.Value, Literal = literalDto, ResultType = resultType, Label = label };
            return true;
        }

        private async Task<CallToolResult> BuildPageAsync(
            String mode,
            String? @namespace,
            List<(Int32 Id, Double? Score)> gathered,
            Int32 offset,
            Int32 end,
            HashSet<String> fields,
            CancellationToken cancellationToken)
        {
            var items = new JsonArray();
            var pageCount = 0;
            for (var i = offset; i < gathered.Count && i < end; i++)
            {
                var (id, score) = gathered[i];
                var item = new JsonObject { ["id"] = id };
                if (score.HasValue)
                {
                    item["score"] = score.Value;
                }
                if (fields.Count > 0)
                {
                    var element = await _bridge.GetGraphElementAsync(@namespace, id, cancellationToken).ConfigureAwait(false);
                    if (element is not null)
                    {
                        item["element"] = ElementProjection.Compact(element.Value, fields, new HashSet<String>());
                    }
                }
                items.Add(item);
                pageCount++;
            }

            // We fetched end+1 for the bounded modes, so a strictly-extra hit means there is more.
            var hasMore = gathered.Count > end;
            var structured = new JsonObject
            {
                ["mode"] = mode,
                ["count"] = pageCount,
                ["hasMore"] = hasMore,
                ["items"] = items,
            };
            if (hasMore)
            {
                structured["nextCursor"] = end.ToString(CultureInfo.InvariantCulture);
            }

            var summary = $"{mode} search: {pageCount} hit(s)" +
                (hasMore ? $"; more available (cursor={end})." : ".");
            return ToolResults.Ok(summary, structured);
        }

        private static Int32? OperatorCode(String op)
        {
            return op switch
            {
                "equal" => 0,
                "greater" => 1,
                "greater_or_equal" => 2,
                "less" => 3,
                "less_or_equal" => 4,
                "not_equal" => 5,
                _ => null,
            };
        }

        private static String ResultType(String kind)
        {
            return kind switch
            {
                "vertex" => "Vertices",
                "edge" => "Edges",
                _ => "Both",
            };
        }

        private static String KindFilter(String kind)
        {
            return kind is "vertex" or "edge" ? kind : "any";
        }
    }
}
