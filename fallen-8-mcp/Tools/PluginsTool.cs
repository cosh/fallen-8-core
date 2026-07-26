// MIT License
//
// PluginsTool.cs
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
using NoSQL.GraphDB.Mcp.Configuration;

namespace NoSQL.GraphDB.Mcp.Tools
{
    /// <summary>
    ///   <c>f8_plugins</c> — the per-namespace plugin registry (feature plugin-registration). It
    ///   replaces the removed DLL-upload surface: agents can <c>list</c>/<c>get</c> registered
    ///   plugins and <c>invoke</c> a stored graph function by name (all Read tier), <c>delete</c> a
    ///   plugin (needs the write capability), and <c>register_algorithm</c>/<c>register_function</c>
    ///   from C# source (needs the <c>code</c> capability — the same gate as inline C# fragments).
    ///
    ///   <para>The tool sits on the Read tier so listing/getting/invoking are always available; the
    ///   write/code ops are gated per-op AND hidden from the schema when their capability is off, so
    ///   the advertised <c>op</c> set matches what the caller may actually do (the f8_paths
    ///   schema-widening pattern). Registered algorithm plugins are invoked transparently by name
    ///   through f8_paths/f8_analytics/f8_subgraph - only graph functions need this tool's invoke.</para>
    ///
    ///   <para>HONESTY: a registered plugin runs IN-PROCESS WITH FULL TRUST on the target when
    ///   invoked. Registration is gated and validated; it is not a sandbox.</para>
    /// </summary>
    public sealed class PluginsTool : IMcpTool
    {
        private readonly Fallen8RestClient _bridge;

        public PluginsTool(Fallen8RestClient bridge)
        {
            _bridge = bridge;
        }

        public String Name => "f8_plugins";

        public ToolTier Tier => ToolTier.Read;

        public Tool Describe(McpToolsOptions tools)
        {
            var ops = new List<String> { "list", "get", "invoke" };
            if (tools.EnableWrite)
            {
                ops.Add("delete");
            }
            if (tools.EnableCode)
            {
                ops.Add("register_algorithm");
                ops.Add("register_function");
            }

            return new Tool
            {
                Name = Name,
                Title = "Plugin registry",
                Description =
                    "Per-namespace runtime plugins. list/get registered plugins; invoke a graph function by name. " +
                    "delete needs the write capability; register_algorithm/register_function compile C# source and need " +
                    "the code capability. Registered algorithms are invoked by name via f8_paths/f8_analytics/f8_subgraph.",
                InputSchema = SchemaBuilder.Create()
                    .Str("op", "The operation.", required: true, choices: ops)
                    .Str("namespace", "The namespace. Defaults to 'default'.")
                    .Str("name", "The plugin name (get/delete/invoke; and the registered name for register_*).")
                    .Str("contract", "For register_algorithm: 'Path', 'SubGraph' or 'Analytics'.",
                        choices: new[] { "Path", "SubGraph", "Analytics" })
                    .Str("description", "Optional description (register_*).")
                    .Str("sourceCode", "The whole-type C# source (register_*).")
                    .Obj("parameters", "String-valued parameters for a graph-function invoke.")
                    .Build(),
                Annotations = new ToolAnnotations
                {
                    Title = "Plugin registry",
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
            var name = ToolArgs.GetString(arguments, "name");

            switch (op)
            {
                case "list":
                {
                    var raw = await _bridge.RequestRawAsync(HttpMethod.Get, @namespace, "plugins", null, cancellationToken)
                        .ConfigureAwait(false);
                    var node = ToolResults.PassArray(raw);
                    var count = node is JsonArray arr ? arr.Count : 0;
                    return ToolResults.Ok($"{count} plugin(s) registered.", new JsonObject { ["plugins"] = node });
                }

                case "get":
                {
                    if (String.IsNullOrEmpty(name))
                    {
                        return ToolResults.Error(400, "Invalid arguments", "get requires 'name'.");
                    }
                    var raw = await _bridge.RequestRawAsync(HttpMethod.Get, @namespace,
                        $"plugins/{UrlSafety.EncodeSegment(name)}", null, cancellationToken).ConfigureAwait(false);
                    if (raw is null)
                    {
                        return ToolResults.Error(404, "Not found", $"No plugin named '{name}'.");
                    }
                    return ToolResults.Ok($"plugin '{name}'.", ToolResults.Pass(raw));
                }

                case "invoke":
                {
                    if (String.IsNullOrEmpty(name))
                    {
                        return ToolResults.Error(400, "Invalid arguments", "invoke requires the function 'name'.");
                    }
                    var body = new JsonObject { ["parameters"] = BuildStringParameters(arguments) };
                    var raw = await _bridge.RequestRawAsync(HttpMethod.Post, @namespace,
                        $"plugins/function/{UrlSafety.EncodeSegment(name)}/invoke", body, cancellationToken).ConfigureAwait(false);
                    var node = ToolResults.Pass(raw);
                    var (v, e) = CountResult(node);
                    return ToolResults.Ok($"function '{name}' returned {v} vertex/vertices, {e} edge(s).", node);
                }

                case "delete":
                {
                    if (!tools.EnableWrite)
                    {
                        return ToolResults.Error(403, "Forbidden", "Deleting a plugin needs the write capability (Mcp:Tools:EnableWrite).");
                    }
                    if (String.IsNullOrEmpty(name))
                    {
                        return ToolResults.Error(400, "Invalid arguments", "delete requires 'name'.");
                    }
                    await _bridge.RequestVoidAsync(HttpMethod.Delete, @namespace,
                        $"plugins/{UrlSafety.EncodeSegment(name)}", null, cancellationToken).ConfigureAwait(false);
                    return ToolResults.Ok($"plugin '{name}' deleted.", new JsonObject { ["deleted"] = true, ["name"] = name });
                }

                case "register_algorithm":
                {
                    if (!tools.EnableCode)
                    {
                        return ToolResults.Error(403, "Forbidden", "Registering a plugin needs the code capability (Mcp:Tools:EnableCode).");
                    }
                    var contract = ToolArgs.GetString(arguments, "contract");
                    var sourceCode = ToolArgs.GetString(arguments, "sourceCode");
                    if (String.IsNullOrEmpty(name) || String.IsNullOrEmpty(contract) || String.IsNullOrEmpty(sourceCode))
                    {
                        return ToolResults.Error(400, "Invalid arguments", "register_algorithm requires 'name', 'contract' and 'sourceCode'.");
                    }
                    var body = new JsonObject
                    {
                        ["name"] = name,
                        ["contract"] = contract,
                        ["description"] = ToolArgs.GetString(arguments, "description"),
                        ["sourceCode"] = sourceCode,
                    };
                    var raw = await _bridge.RequestRawAsync(HttpMethod.Post, @namespace, "plugins/algorithm", body, cancellationToken)
                        .ConfigureAwait(false);
                    return ToolResults.Ok($"algorithm plugin '{name}' registered.", ToolResults.Pass(raw));
                }

                case "register_function":
                {
                    if (!tools.EnableCode)
                    {
                        return ToolResults.Error(403, "Forbidden", "Registering a plugin needs the code capability (Mcp:Tools:EnableCode).");
                    }
                    var sourceCode = ToolArgs.GetString(arguments, "sourceCode");
                    if (String.IsNullOrEmpty(name) || String.IsNullOrEmpty(sourceCode))
                    {
                        return ToolResults.Error(400, "Invalid arguments", "register_function requires 'name' and 'sourceCode'.");
                    }
                    var body = new JsonObject
                    {
                        ["name"] = name,
                        ["description"] = ToolArgs.GetString(arguments, "description"),
                        ["sourceCode"] = sourceCode,
                    };
                    var raw = await _bridge.RequestRawAsync(HttpMethod.Post, @namespace, "plugins/function", body, cancellationToken)
                        .ConfigureAwait(false);
                    return ToolResults.Ok($"graph function '{name}' registered.", ToolResults.Pass(raw));
                }

                default:
                    return ToolResults.Error(400, "Invalid arguments",
                        "op must be list, get, invoke, delete, register_algorithm, or register_function.");
            }
        }

        /// <summary>Coerces the free-form <c>parameters</c> object into the string map the invoke
        /// endpoint expects (a non-string value becomes its raw JSON text).</summary>
        private static JsonObject BuildStringParameters(IReadOnlyDictionary<String, JsonElement> arguments)
        {
            var result = new JsonObject();
            if (ToolArgs.TryGetElement(arguments, "parameters", out var element) &&
                element.ValueKind == JsonValueKind.Object)
            {
                foreach (var member in element.EnumerateObject())
                {
                    result[member.Name] = member.Value.ValueKind == JsonValueKind.String
                        ? member.Value.GetString()
                        : member.Value.GetRawText();
                }
            }
            return result;
        }

        private static (Int32 Vertices, Int32 Edges) CountResult(JsonNode? node)
        {
            var v = node is JsonObject o && o["vertices"] is JsonArray va ? va.Count : 0;
            var e = node is JsonObject o2 && o2["edges"] is JsonArray ea ? ea.Count : 0;
            return (v, e);
        }
    }
}
