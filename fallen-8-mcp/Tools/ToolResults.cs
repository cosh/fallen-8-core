// MIT License
//
// ToolResults.cs
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
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using NoSQL.GraphDB.Mcp.Bridge;

namespace NoSQL.GraphDB.Mcp.Tools
{
    /// <summary>
    ///   Builds <see cref="CallToolResult"/>s to the token-economy contract (spec §3.5): the
    ///   human-readable <c>content</c> is a tiny O(1) stat/pointer line — never a
    ///   re-serialization of the structured payload — and the machine-readable data rides in
    ///   <c>structuredContent</c>. Errors are the compact <c>{status,title,detail}</c> form
    ///   (spec §3.2); the Fallen-8 API key never reaches this layer.
    /// </summary>
    public static class ToolResults
    {
        public static CallToolResult Ok(String summary, JsonNode? structured = null)
        {
            // CallToolResult.StructuredContent is a JsonElement?; serialize the node we built.
            JsonElement? element = structured is null ? null : JsonSerializer.SerializeToElement(structured);
            return new CallToolResult
            {
                Content = new List<ContentBlock> { new TextContentBlock { Text = summary } },
                StructuredContent = element,
                IsError = false,
            };
        }

        public static CallToolResult Error(Int32 status, String title, String detail)
        {
            return new CallToolResult
            {
                Content = new List<ContentBlock> { new TextContentBlock { Text = $"{status} {title}: {detail}" } },
                IsError = true,
            };
        }

        public static CallToolResult Error(BridgeError error)
        {
            var suffix = error.Retryable ? " (retryable)" : String.Empty;
            return Error(error.Status, error.Title, error.Detail + suffix);
        }

        /// <summary>Relays a raw bridge reply body as a structured payload node, defaulting a null
        /// reply (a 204 / literal-null soft-not-found) to an empty object. One home for the
        /// previously per-tool-triplicated <c>Pass</c> helper (feature code-quality: no-duplication).</summary>
        public static JsonNode Pass(JsonElement? raw)
        {
            return raw is null ? new JsonObject() : JsonNode.Parse(raw.Value.GetRawText())!;
        }

        /// <summary>As <see cref="Pass"/> but defaults a null reply to an empty ARRAY, for endpoints
        /// whose success body is a JSON array (e.g. list / register replies).</summary>
        public static JsonNode PassArray(JsonElement? raw)
        {
            return raw is null ? new JsonArray() : JsonNode.Parse(raw.Value.GetRawText())!;
        }
    }
}
