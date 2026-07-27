// MIT License
//
// NamespaceTool.cs
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
    ///   <c>f8_namespace</c> — namespace lifecycle (spec §3.2/§3.4). A separate write-tier tool
    ///   (not folded into <c>f8_admin</c>) so namespace CRUD gates independently of durability.
    ///   Fallen-8-level: the name is a path segment, not a scoping prefix, and is validated +
    ///   percent-encoded before use. <c>drop</c> is destructive.
    /// </summary>
    public sealed class NamespaceTool : IMcpTool
    {
        private readonly Fallen8RestClient _bridge;

        public NamespaceTool(Fallen8RestClient bridge)
        {
            _bridge = bridge;
        }

        public String Name => "f8_namespace";

        public ToolTier Tier => ToolTier.Write;

        public Tool Describe(McpToolsOptions tools)
        {
            return new Tool
            {
                Name = Name,
                Title = "Namespace lifecycle",
                Description = "Create, rename, or drop a namespace (isolated graph). 'drop' is destructive and irreversible.",
                InputSchema = SchemaBuilder.Create()
                    .Str("op", "The lifecycle operation.", required: true, choices: new[] { "create", "rename", "drop" })
                    .Str("name", "The namespace name (target).", required: true)
                    .Str("newName", "The new name (rename only).")
                    .Build(),
                Annotations = new ToolAnnotations
                {
                    Title = "Namespace lifecycle",
                    ReadOnlyHint = false,
                    // Static per-tool hint: the tool CAN destroy (via 'drop'), so clients confirm.
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
            var name = ToolArgs.GetString(arguments, "name");

            if (!UrlSafety.TryEncodeNamespace(name, out var encoded, out var nameError))
            {
                return ToolResults.Error(400, "Invalid namespace", nameError);
            }

            switch (op)
            {
                case "create":
                {
                    var created = await _bridge.RequestRawAsync(HttpMethod.Put, null, $"ns/{encoded}", null, cancellationToken)
                        .ConfigureAwait(false);
                    return ToolResults.Ok($"namespace '{name}' created.", ToolResults.Pass(created));
                }

                case "rename":
                {
                    var newName = ToolArgs.GetString(arguments, "newName");
                    if (String.IsNullOrEmpty(newName))
                    {
                        return ToolResults.Error(400, "Invalid arguments", "rename requires 'newName'.");
                    }
                    var renamed = await _bridge.RequestRawAsync(HttpMethod.Patch, null, $"ns/{encoded}",
                        new NamespaceRenameDto { Name = newName }, cancellationToken).ConfigureAwait(false);
                    return ToolResults.Ok($"namespace '{name}' renamed to '{newName}'.", ToolResults.Pass(renamed));
                }

                case "drop":
                {
                    await _bridge.RequestVoidAsync(HttpMethod.Delete, null, $"ns/{encoded}", null, cancellationToken)
                        .ConfigureAwait(false);
                    return ToolResults.Ok($"namespace '{name}' dropped.", new JsonObject { ["dropped"] = true, ["name"] = name });
                }

                default:
                    return ToolResults.Error(400, "Invalid arguments", "op must be create, rename, or drop.");
            }
        }
    }
}
