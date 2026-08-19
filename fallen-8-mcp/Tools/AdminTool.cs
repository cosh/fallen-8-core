// MIT License
//
// AdminTool.cs
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
    ///   <c>f8_admin</c> — durability &amp; maintenance (spec §3.2/§3.4/§3.7). Honest scoping:
    ///   <c>save</c>/<c>trim</c>/<c>tabula_rasa</c> are namespace-scoped; <c>list_savegames</c>,
    ///   <c>load</c> and <c>activate</c> are Fallen-8-level (<c>load</c> restores a registry entry by
    ///   id, with an optional <c>restoreNamespace</c> member selector; <c>activate</c> names a
    ///   namespace as a path segment rather than a scoping prefix).
    ///   <c>trim</c>/<c>tabula_rasa</c> are fire-and-forget (HEAD, 204): they report "enqueued",
    ///   never "applied".
    ///   <para><c>activate</c> lives here rather than on the write-tier <c>f8_namespace</c> because it
    ///   is a durability operation on the running process (it restores a checkpoint), not part of the
    ///   create/rename/drop lifecycle - and because it is how an agent recovers from the 503 that
    ///   every other tool gets for a not-loaded namespace.</para>
    /// </summary>
    public sealed class AdminTool : IMcpTool
    {
        private readonly Fallen8RestClient _bridge;

        public AdminTool(Fallen8RestClient bridge)
        {
            _bridge = bridge;
        }

        public String Name => "f8_admin";

        public ToolTier Tier => ToolTier.Admin;

        public Tool Describe(McpToolsOptions tools)
        {
            return new Tool
            {
                Name = Name,
                Title = "Admin & durability",
                Description =
                    "Durability, maintenance and instance configuration: save, load (by save-game id), " +
                    "list_savegames, activate, trim, tabula_rasa, get_settings, set_settings. " +
                    "trim/tabula_rasa are fire-and-forget (reported as enqueued). load/trim/tabula_rasa " +
                    "are destructive. activate loads a namespace that this process did not load at startup (the fix " +
                    "for a 503 'Namespace not loaded'); it is idempotent and does not change the startup policy - " +
                    "to make a namespace load on every boot, set its loadOnStartup with f8_namespace. " +
                    "get_settings reads every configuration key with its tier, effective value and the reason a key " +
                    "cannot be written; it is the first thing to read when a limit or a capability refuses a call. " +
                    "set_settings writes the writable ones: most take effect only after an operator restarts the " +
                    "server, and the result says so per key. A never-writable key is refused with its reason, and a " +
                    "key the server's environment declares is refused because a stored value could never win.",
                InputSchema = SchemaBuilder.Create()
                    .Str("op", "The operation.", required: true,
                        choices: new[]
                        {
                            "save", "load", "list_savegames", "activate", "trim", "tabula_rasa",
                            "get_settings", "set_settings",
                        })
                    .Str("namespace", "The namespace for save/trim/tabula_rasa, and the one to load for activate " +
                        "(required there). Defaults to 'default'.")
                    .Str("saveGameLocation", "Optional save path (save).")
                    .Int("savePartitions", "Optional partition count (save).")
                    .Str("id", "Save-game id (load).")
                    .Str("restoreNamespace", "Restore only this namespace from a multi-namespace save-game (load).")
                    .Obj("settings", "The settings to write (set_settings), as configuration keys mapped to string " +
                        "values, e.g. {\"Fallen8:Plugins:MaxCount\": \"128\"}. A null value clears a stored " +
                        "override and restores the value below it. Every key is validated before any is stored, so " +
                        "the batch applies whole or changes nothing.")
                    .Bool("writableOnly", "Return only the settings that can be written (get_settings). Defaults " +
                        "to false, which also lists the never-writable keys with the reason each is excluded.")
                    .Build(),
                Annotations = new ToolAnnotations
                {
                    Title = "Admin & durability",
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
                case "save":
                {
                    var body = new SaveSpecDto
                    {
                        SaveGameLocation = ToolArgs.GetString(arguments, "saveGameLocation"),
                        SavePartitions = ToolArgs.GetInt(arguments, "savePartitions"),
                    };
                    var saved = await _bridge.RequestRawAsync(HttpMethod.Put, @namespace, "save", body, cancellationToken)
                        .ConfigureAwait(false);
                    return ToolResults.Ok("save-game written.", ToolResults.Pass(saved));
                }

                case "list_savegames":
                {
                    // Fallen-8-level: no namespace scoping.
                    var list = await _bridge.RequestRawAsync(HttpMethod.Get, null, "savegames", null, cancellationToken)
                        .ConfigureAwait(false);
                    var node = ToolResults.PassArray(list);
                    var count = node is JsonArray arr ? arr.Count : 0;
                    return ToolResults.Ok($"{count} save-game(s).", new JsonObject { ["saveGames"] = node });
                }

                case "load":
                {
                    var id = ToolArgs.GetString(arguments, "id");
                    if (String.IsNullOrEmpty(id))
                    {
                        return ToolResults.Error(400, "Invalid arguments", "load requires a save-game 'id'.");
                    }
                    var suffix = $"savegames/{UrlSafety.EncodeSegment(id)}/load?waitForCompletion=true";
                    var restoreNamespace = ToolArgs.GetString(arguments, "restoreNamespace");
                    if (!String.IsNullOrEmpty(restoreNamespace))
                    {
                        suffix += $"&namespace={UrlSafety.EncodeSegment(restoreNamespace)}";
                    }
                    var loaded = await _bridge.RequestRawAsync(HttpMethod.Put, null, suffix, null, cancellationToken)
                        .ConfigureAwait(false);
                    return ToolResults.Ok($"save-game '{id}' loaded.", ToolResults.Pass(loaded));
                }

                case "activate":
                {
                    // Fallen-8-level: the namespace is a PATH SEGMENT of the route (never the
                    // /ns/{ns} scoping prefix the bridge builds for data routes), so it is validated
                    // and percent-encoded here. No default: activating "default" is always a no-op,
                    // so an omitted namespace is a mistake worth naming rather than answering.
                    if (!UrlSafety.TryEncodeNamespace(@namespace, out var encoded, out var nameError))
                    {
                        return ToolResults.Error(400, "Invalid arguments", "activate requires a 'namespace': " + nameError);
                    }

                    var activated = await _bridge.RequestRawAsync(HttpMethod.Post, null, $"ns/{encoded}/activate", null, cancellationToken)
                        .ConfigureAwait(false);
                    return ToolResults.Ok($"namespace '{@namespace}' is loaded.", ToolResults.Pass(activated));
                }

                case "trim":
                {
                    await _bridge.RequestVoidAsync(HttpMethod.Head, @namespace, "trim", null, cancellationToken).ConfigureAwait(false);
                    return ToolResults.Ok("trim enqueued (fire-and-forget; not awaited).",
                        new JsonObject { ["op"] = "trim", ["enqueued"] = true });
                }

                case "tabula_rasa":
                {
                    await _bridge.RequestVoidAsync(HttpMethod.Head, @namespace, "tabularasa", null, cancellationToken).ConfigureAwait(false);
                    return ToolResults.Ok("tabula_rasa enqueued (fire-and-forget; the namespace's data is erased).",
                        new JsonObject { ["op"] = "tabula_rasa", ["enqueued"] = true });
                }

                case "get_settings":
                {
                    // Fallen-8-level: configuration belongs to the instance, not to a namespace.
                    var config = await _bridge.RequestRawAsync(HttpMethod.Get, null, "config", null, cancellationToken)
                        .ConfigureAwait(false);
                    var view = ToolResults.Pass(config) as JsonObject;
                    var settings = view?["settings"] as JsonArray;
                    var pending = view?["pendingRestart"] as JsonArray;

                    if (settings != null && ToolArgs.GetBool(arguments, "writableOnly") == true)
                    {
                        // Backwards, and in place: a JsonNode has one parent, so moving nodes into a new
                        // array would have to detach them first.
                        for (var index = settings.Count - 1; index >= 0; index--)
                        {
                            if (settings[index] is JsonObject entry
                                && entry["tier"]?.GetValue<String>() == "notWritable")
                            {
                                settings.RemoveAt(index);
                            }
                        }
                    }

                    var pendingCount = pending?.Count ?? 0;
                    return ToolResults.Ok(
                        $"{settings?.Count ?? 0} setting(s); {pendingCount} awaiting a server restart."
                        + (pendingCount > 0
                            ? " A written value only takes effect once an operator restarts the server."
                            : String.Empty),
                        new JsonObject
                        {
                            ["settings"] = settings ?? new JsonArray(),
                            ["pendingRestart"] = pending ?? new JsonArray(),
                        });
                }

                case "set_settings":
                {
                    if (!arguments.TryGetValue("settings", out var requested)
                        || requested.ValueKind != JsonValueKind.Object)
                    {
                        return ToolResults.Error(400, "Invalid arguments",
                            "set_settings needs a settings object mapping configuration keys to string values.");
                    }

                    var body = JsonNode.Parse(requested.GetRawText());
                    var written = await _bridge.RequestRawAsync(HttpMethod.Patch, null, "config",
                        new JsonObject { ["settings"] = body }, cancellationToken).ConfigureAwait(false);
                    var result = ToolResults.Pass(written) as JsonObject;
                    var pendingAfter = (result?["pendingRestart"] as JsonArray)?.Count ?? 0;

                    return ToolResults.Ok(
                        pendingAfter > 0
                            ? $"settings written; {pendingAfter} now await a server restart. Nothing changes in the "
                                + "running process until an operator restarts it."
                            : "settings written and in effect.",
                        result);
                }

                default:
                    return ToolResults.Error(400, "Invalid arguments",
                        "op must be save, load, list_savegames, activate, trim, tabula_rasa, get_settings, or "
                        + "set_settings.");
            }
        }
    }
}
