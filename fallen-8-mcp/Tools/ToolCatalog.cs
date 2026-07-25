// MIT License
//
// ToolCatalog.cs
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
using System.Diagnostics;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NoSQL.GraphDB.Mcp.Bridge;
using NoSQL.GraphDB.Mcp.Configuration;
using NoSQL.GraphDB.Mcp.Diagnostics;

namespace NoSQL.GraphDB.Mcp.Tools
{
    /// <summary>
    ///   The low-level <c>ListTools</c>/<c>CallTool</c> handler home (spec §3.2). Authoring tools
    ///   this way — rather than the SDK's typed-parameter attribute path — is what lets the
    ///   advertised tool set and each tool's schema vary by the enabled tiers (a disabled tier's
    ///   tools are absent from <c>tools/list</c> AND rejected on <c>tools/call</c>, defending
    ///   against a client replaying a cached list). Tier gating lives here; per-tool schema and
    ///   argument validation live in each <see cref="IMcpTool"/>.
    /// </summary>
    public sealed class ToolCatalog
    {
        private readonly IReadOnlyList<IMcpTool> _tools;
        private readonly IOptions<McpOptions> _options;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ILogger<ToolCatalog> _logger;

        public ToolCatalog(
            IEnumerable<IMcpTool> tools,
            IOptions<McpOptions> options,
            IHttpContextAccessor httpContextAccessor,
            ILogger<ToolCatalog> logger)
        {
            _tools = tools.ToList();
            _options = options;
            _httpContextAccessor = httpContextAccessor;
            _logger = logger;
        }

        /// <summary>
        ///   The tier flags in force for THIS call: the operator's server-side flags, and under
        ///   OAuth intersected FAIL-CLOSED with the caller's scopes (spec §3.6/§3.8). Read is the
        ///   floor (a valid token always gets it); write/admin/code each require BOTH the server
        ///   flag AND the matching <c>f8:*</c> scope, so a scope can never enable a tier the
        ///   operator disabled, and absence of a scope never grants one.
        /// </summary>
        private McpToolsOptions EffectiveCaps()
        {
            var configured = _options.Value.Tools;
            if (!_options.Value.Auth.IsOAuth)
            {
                return configured;
            }

            var scopes = ScopesOf(_httpContextAccessor.HttpContext?.User);
            return new McpToolsOptions
            {
                EnableWrite = configured.EnableWrite && scopes.Contains("f8:write"),
                EnableAdmin = configured.EnableAdmin && scopes.Contains("f8:admin"),
                EnableCode = configured.EnableCode && scopes.Contains("f8:code"),
            };
        }

        /// <summary>The granted scopes from the standard OAuth <c>scope</c>/<c>scp</c> claims
        /// (space-delimited or multi-valued).</summary>
        private static HashSet<String> ScopesOf(ClaimsPrincipal? user)
        {
            var scopes = new HashSet<String>(StringComparer.Ordinal);
            if (user is null)
            {
                return scopes;
            }
            foreach (var claim in user.Claims)
            {
                if (claim.Type is "scope" or "scp" or "http://schemas.microsoft.com/identity/claims/scope")
                {
                    foreach (var scope in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        scopes.Add(scope);
                    }
                }
            }
            return scopes;
        }

        private static Boolean TierEnabled(ToolTier tier, McpToolsOptions caps)
        {
            return tier switch
            {
                ToolTier.Read => true,
                ToolTier.Write => caps.EnableWrite,
                ToolTier.Admin => caps.EnableAdmin,
                _ => false,
            };
        }

        // --- Testable core (no protocol plumbing) --------------------------------------------

        /// <summary>The tools currently advertised: exactly those whose tier is enabled.</summary>
        public IReadOnlyList<Tool> ListTools()
        {
            var caps = EffectiveCaps();
            return _tools
                .Where(t => TierEnabled(t.Tier, caps))
                .Select(t => t.Describe(caps))
                .ToList();
        }

        /// <summary>Dispatches a call by tool name + arguments, rejecting an unknown or
        /// disabled-tier tool, and mapping any bridge failure to a compact <c>isError</c> result
        /// (never leaking the API key). This is the whole call semantics, independent of the MCP
        /// transport, so it is directly unit-testable.</summary>
        public async Task<CallToolResult> CallAsync(
            String? name,
            IReadOnlyDictionary<String, JsonElement> arguments,
            CancellationToken cancellationToken)
        {
            var caps = EffectiveCaps();
            var tool = name is null ? null : _tools.FirstOrDefault(t => t.Name == name);

            // Per-tool telemetry (feature fleet-observability §3.6). Bounded tag values resolved
            // once - the tool's short name from its fixed f8_* set (or "unknown") and its tier -
            // never the raw caller-supplied name, so the tag-hygiene invariant holds. The span is
            // the parent of the downstream REST client span (end-to-end MCP -> REST -> engine trace).
            var toolTag = McpDiagnostics.ToolTag(tool);
            var tierTag = McpDiagnostics.TierTag(tool);

            using var activity = McpDiagnostics.Source.StartActivity(McpDiagnostics.ToolSpanName);
            activity?.SetTag("tool", toolTag);
            activity?.SetTag("tier", tierTag);
            var start = Stopwatch.GetTimestamp();

            if (tool is null || !TierEnabled(tool.Tier, caps))
            {
                var miss = ToolResults.Error(404, "Unknown or disabled tool",
                    $"No enabled tool named '{name ?? "(null)"}'. Enable its tier (and, under OAuth, hold its scope) or check the name.");
                McpDiagnostics.RecordToolCall(toolTag, tierTag, "error", Stopwatch.GetElapsedTime(start), activity);
                return miss;
            }

            CallToolResult result;
            try
            {
                result = await tool.InvokeAsync(arguments, caps, cancellationToken).ConfigureAwait(false);
            }
            catch (BridgeError bridgeError)
            {
                result = ToolResults.Error(bridgeError);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Log the detail; return a generic message so nothing sensitive reaches the caller.
                _logger.LogError(ex, "Unhandled error invoking tool {Tool}", name);
                result = ToolResults.Error(500, "Internal error", "The tool failed unexpectedly.");
            }

            var resultTag = result.IsError == true ? "error" : "ok";
            McpDiagnostics.RecordToolCall(toolTag, tierTag, resultTag, Stopwatch.GetElapsedTime(start), activity);
            return result;
        }

        // --- MCP low-level handler adapters ---------------------------------------------------

        /// <summary>The <c>ListTools</c> handler registered with the SDK.</summary>
        public ValueTask<ListToolsResult> ListToolsHandlerAsync(
            RequestContext<ListToolsRequestParams> context,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new ListToolsResult { Tools = ListTools().ToList() });
        }

        /// <summary>The <c>CallTool</c> handler registered with the SDK.</summary>
        public async ValueTask<CallToolResult> CallToolHandlerAsync(
            RequestContext<CallToolRequestParams> context,
            CancellationToken cancellationToken)
        {
            // CallToolRequestParams.Arguments is an IDictionary?; normalize to a read-only map.
            IReadOnlyDictionary<String, JsonElement> arguments = context.Params?.Arguments is { } raw
                ? new Dictionary<String, JsonElement>(raw)
                : new Dictionary<String, JsonElement>();

            return await CallAsync(context.Params?.Name, arguments, cancellationToken).ConfigureAwait(false);
        }
    }
}
