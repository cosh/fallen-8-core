// MIT License
//
// DocumentsTool.cs
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
    ///   <c>f8_documents</c> - unstructured ingestion (feature unstructured-ingestion): agents
    ///   write knowledge into the graph as Document/Chunk vertices and retrieve chunks with
    ///   fused (dense + lexical) search; a hit is a vertex and seeds f8_paths like any other.
    ///
    ///   <para><c>list</c>/<c>get</c>/<c>search</c> are Read tier; <c>ingest_text</c>/<c>delete</c>
    ///   are gated on the write capability AND hidden from the schema when it is off (the
    ///   f8_plugins pattern). Binary file upload is deliberately NOT bridged: base64 payloads
    ///   through tool calls are token-hostile; agents hold text, and POST /document is one
    ///   curl away (recorded deferral).</para>
    /// </summary>
    public sealed class DocumentsTool : IMcpTool
    {
        private readonly Fallen8RestClient _bridge;

        public DocumentsTool(Fallen8RestClient bridge)
        {
            _bridge = bridge;
        }

        public String Name => "f8_documents";

        public ToolTier Tier => ToolTier.Read;

        public Tool Describe(McpToolsOptions tools)
        {
            var ops = new List<String> { "list", "get", "search" };
            if (tools.EnableWrite)
            {
                ops.Add("ingest_text");
                ops.Add("delete");
            }

            return new Tool
            {
                Name = Name,
                Title = "Documents",
                Description =
                    "Unstructured ingestion: documents live in the graph as Document/Chunk vertices with embedded text. " +
                    "search fuses semantic and exact-token retrieval (hits are chunk vertex ids - seed f8_paths with them); " +
                    "list/get inspect documents. ingest_text/delete need the write capability. " +
                    "Requires ingestion to be enabled on the target (Fallen8:Ingestion:Enabled, see f8_overview).",
                InputSchema = SchemaBuilder.Create()
                    .Str("op", "The operation.", required: true, choices: ops)
                    .Str("namespace", "The namespace. Defaults to 'default'.")
                    .Int("documentId", "The document vertex id (get/delete).")
                    .Str("query", "The search query (search). Feeds both the semantic and the exact-token side.")
                    .Str("mode", "Search mode (search). Default fused.", choices: new[] { "fused", "dense", "lexical" })
                    .Int("k", "Results to return (search). Default 10, max 100.")
                    .Int("window", "Sibling chunks each side of a hit (search). Default 0, max 5.")
                    .Bool("groupByDocument", "Group search hits per document (search).")
                    .Str("name", "The document name (ingest_text).")
                    .Str("text", "The content (ingest_text).")
                    .Str("format", "Content format (ingest_text). Default markdown.", choices: new[] { "markdown", "plain" })
                    .Bool("embed", "Embed the chunks (ingest_text). Default true; needs the target's embedding provider.")
                    .Str("sourceUri", "Optional source pointer stored on the document (ingest_text).")
                    .Int("replaceDocumentId", "Replace this document on success (ingest_text).")
                    .Obj("properties", "User tag properties (string values) applied to document and chunks (ingest_text).")
                    .StrArray("linkIndexIds", "Structural linking: equality-capable index ids to match extracted identifiers against (ingest_text).")
                    .Int("maxLinksPerChunk", "Per-chunk cap for structural links (ingest_text).")
                    .Build(),
                Annotations = new ToolAnnotations
                {
                    Title = "Documents",
                    ReadOnlyHint = false,
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

            switch (op)
            {
                case "list":
                {
                    var raw = await _bridge.RequestRawAsync(HttpMethod.Get, @namespace, "document", null, cancellationToken)
                        .ConfigureAwait(false);
                    var node = ToolResults.Pass(raw);
                    var documents = node?["documents"] as JsonArray;
                    var chunks = node?["namespaceChunkCount"]?.GetValue<Int32>() ?? 0;
                    var ceiling = node?["chunkCeiling"]?.GetValue<Int32>() ?? 0;
                    return ToolResults.Ok(
                        $"{documents?.Count ?? 0} document(s); {chunks} of {ceiling} chunks used.", node);
                }

                case "get":
                {
                    var documentId = ToolArgs.GetInt(arguments, "documentId");
                    if (documentId is null)
                    {
                        return ToolResults.Error(400, "Invalid arguments", "get requires 'documentId'.");
                    }
                    var raw = await _bridge.RequestRawAsync(HttpMethod.Get, @namespace,
                        $"document/{documentId.Value}", null, cancellationToken).ConfigureAwait(false);
                    if (raw is null)
                    {
                        return ToolResults.Error(404, "Not found", $"No document with id {documentId.Value}.");
                    }
                    var node = ToolResults.Pass(raw);
                    var status = node?["summary"]?["status"]?.GetValue<String>() ?? "?";
                    var chunkCount = node?["chunks"] is JsonArray chunkArray ? chunkArray.Count : 0;
                    return ToolResults.Ok($"document {documentId.Value} ({status}), {chunkCount} chunk(s).", node);
                }

                case "search":
                {
                    var query = ToolArgs.GetString(arguments, "query");
                    if (String.IsNullOrWhiteSpace(query))
                    {
                        return ToolResults.Error(400, "Invalid arguments", "search requires 'query'.");
                    }
                    var body = new JsonObject { ["queryText"] = query };
                    var mode = ToolArgs.GetString(arguments, "mode");
                    if (mode != null)
                    {
                        body["mode"] = mode;
                    }
                    var k = ToolArgs.GetInt(arguments, "k");
                    if (k != null)
                    {
                        body["k"] = k.Value;
                    }
                    var window = ToolArgs.GetInt(arguments, "window");
                    if (window != null)
                    {
                        body["window"] = window.Value;
                    }
                    var group = ToolArgs.GetBool(arguments, "groupByDocument");
                    if (group != null)
                    {
                        body["groupByDocument"] = group.Value;
                    }

                    var raw = await _bridge.RequestRawAsync(HttpMethod.Post, @namespace, "document/search", body,
                        cancellationToken).ConfigureAwait(false);
                    var node = ToolResults.Pass(raw);
                    var modeUsed = node?["modeUsed"]?.GetValue<String>() ?? "?";
                    var hitCount = node?["hits"] is JsonArray hits
                        ? hits.Count
                        : (node?["documents"] as JsonArray)?.Count ?? 0;
                    return ToolResults.Ok($"{hitCount} hit(s)/group(s) via {modeUsed}.", node);
                }

                case "ingest_text":
                {
                    if (!tools.EnableWrite)
                    {
                        return ToolResults.Error(403, "Forbidden",
                            "Ingesting needs the write capability (Mcp:Tools:EnableWrite).");
                    }
                    var name = ToolArgs.GetString(arguments, "name");
                    var text = ToolArgs.GetString(arguments, "text");
                    if (String.IsNullOrWhiteSpace(name) || String.IsNullOrWhiteSpace(text))
                    {
                        return ToolResults.Error(400, "Invalid arguments", "ingest_text requires 'name' and 'text'.");
                    }

                    var body = new JsonObject { ["name"] = name, ["text"] = text };
                    var format = ToolArgs.GetString(arguments, "format");
                    if (format != null)
                    {
                        body["format"] = format;
                    }
                    var embed = ToolArgs.GetBool(arguments, "embed");
                    if (embed != null)
                    {
                        body["embed"] = embed.Value;
                    }
                    var sourceUri = ToolArgs.GetString(arguments, "sourceUri");
                    if (sourceUri != null)
                    {
                        body["sourceUri"] = sourceUri;
                    }
                    var replaceDocumentId = ToolArgs.GetInt(arguments, "replaceDocumentId");
                    if (replaceDocumentId != null)
                    {
                        body["replaceDocumentId"] = replaceDocumentId.Value;
                    }
                    if (ToolArgs.TryGetElement(arguments, "properties", out var properties) &&
                        properties.ValueKind == JsonValueKind.Object)
                    {
                        var tags = new JsonObject();
                        foreach (var member in properties.EnumerateObject())
                        {
                            tags[member.Name] = member.Value.ValueKind == JsonValueKind.String
                                ? member.Value.GetString()
                                : member.Value.GetRawText();
                        }
                        body["properties"] = tags;
                    }
                    var linkIndexIds = ToolArgs.GetStringSet(arguments, "linkIndexIds");
                    if (linkIndexIds.Count > 0)
                    {
                        var link = new JsonObject { ["indexIds"] = new JsonArray() };
                        foreach (var indexId in linkIndexIds)
                        {
                            ((JsonArray)link["indexIds"]!).Add(indexId);
                        }
                        var maxLinks = ToolArgs.GetInt(arguments, "maxLinksPerChunk");
                        if (maxLinks != null)
                        {
                            link["maxLinksPerChunk"] = maxLinks.Value;
                        }
                        body["link"] = link;
                    }

                    var raw = await _bridge.RequestRawAsync(HttpMethod.Post, @namespace, "document/text", body,
                        cancellationToken).ConfigureAwait(false);
                    var node = ToolResults.Pass(raw);
                    var chunkCount = node?["chunkCount"]?.GetValue<Int32>() ?? 0;
                    var links = node?["linksCreated"]?.GetValue<Int32>() ?? 0;
                    var documentId = node?["documentId"]?.GetValue<Int32>();
                    return ToolResults.Ok(
                        $"ingested '{name}' as document {documentId}: {chunkCount} chunk(s), {links} link(s).", node);
                }

                case "delete":
                {
                    if (!tools.EnableWrite)
                    {
                        return ToolResults.Error(403, "Forbidden",
                            "Deleting a document needs the write capability (Mcp:Tools:EnableWrite).");
                    }
                    var documentId = ToolArgs.GetInt(arguments, "documentId");
                    if (documentId is null)
                    {
                        return ToolResults.Error(400, "Invalid arguments", "delete requires 'documentId'.");
                    }
                    await _bridge.RequestVoidAsync(HttpMethod.Delete, @namespace,
                        $"document/{documentId.Value}?waitForCompletion=true", null, cancellationToken).ConfigureAwait(false);
                    return ToolResults.Ok($"document {documentId.Value} deleted (chunks and edges cascade).",
                        new JsonObject { ["deleted"] = true, ["documentId"] = documentId.Value });
                }

                default:
                    return ToolResults.Error(400, "Invalid arguments",
                        "op must be list, get, search, ingest_text, or delete.");
            }
        }
    }
}
