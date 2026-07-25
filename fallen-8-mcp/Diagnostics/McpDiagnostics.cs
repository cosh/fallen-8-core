// MIT License
//
// McpDiagnostics.cs
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
using System.Diagnostics;
using System.Diagnostics.Metrics;
using NoSQL.GraphDB.Mcp.Tools;

namespace NoSQL.GraphDB.Mcp.Diagnostics
{
    /// <summary>
    ///   The MCP server's trace source and metric instruments (feature fleet-observability §3.6):
    ///   per-tool call count + duration and the tool span, recorded at the one dispatch seam
    ///   (<see cref="ToolCatalog.CallAsync"/>). STATIC, mirroring the apiApp's <c>AppDiagnostics</c>.
    ///   Tag values are bounded identifiers only - the tool's short name from its fixed
    ///   <c>f8_*</c> set (or <c>unknown</c>), the tier, and ok/error - never caller-supplied
    ///   argument content, so the tag-hygiene invariant holds.
    /// </summary>
    public static class McpDiagnostics
    {
        /// <summary>The MCP source/meter name.</summary>
        public const String SourceName = "NoSQL.GraphDB.Mcp";

        /// <summary>The meter name (same string as the source).</summary>
        public const String MeterName = SourceName;

        /// <summary>The per-tool span name.</summary>
        public const String ToolSpanName = "fallen8.mcp.tool";

        /// <summary>Spans for MCP tool dispatch.</summary>
        public static readonly ActivitySource Source = new ActivitySource(SourceName);

        private static readonly Meter _meter = new Meter(MeterName);

        private static readonly Counter<Int64> _toolCalls = _meter.CreateCounter<Int64>(
            "fallen8.mcp.tool.calls", "{call}", "MCP tool calls by tool, tier, and result.");

        private static readonly Histogram<Double> _toolDuration = _meter.CreateHistogram<Double>(
            "fallen8.mcp.tool.duration", "s", "MCP tool dispatch duration by tool, tier, and result.");

        /// <summary>The bounded short tool tag (f8_get -> get); <c>unknown</c> for an unresolved
        /// name (never the raw caller string).</summary>
        public static String ToolTag(IMcpTool? tool)
        {
            if (tool is null)
            {
                return "unknown";
            }

            var name = tool.Name;
            return name.StartsWith("f8_", StringComparison.Ordinal) ? name.Substring(3) : name;
        }

        /// <summary>The tier tag (read/write/admin); <c>unknown</c> for an unresolved tool.</summary>
        public static String TierTag(IMcpTool? tool)
        {
            return tool is null
                ? "unknown"
                : tool.Tier switch
                {
                    ToolTier.Read => "read",
                    ToolTier.Write => "write",
                    ToolTier.Admin => "admin",
                    _ => "unknown",
                };
        }

        // CONTAINMENT: Counter.Add / Histogram.Record invoke listener callbacks inline and the BCL
        // does not swallow their exceptions - observability must never fault the observed.
        internal static void RecordToolCall(String tool, String tier, String result, TimeSpan elapsed, Activity? activity)
        {
            try
            {
                var tags = new TagList
                {
                    { "tool", tool },
                    { "tier", tier },
                    { "result", result },
                };
                _toolCalls.Add(1, tags);
                _toolDuration.Record(elapsed.TotalSeconds, tags);

                activity?.SetTag("result", result);
                if (String.Equals(result, "error", StringComparison.Ordinal))
                {
                    activity?.SetStatus(ActivityStatusCode.Error);
                }
            }
            catch { /* contained */ }
        }
    }
}
