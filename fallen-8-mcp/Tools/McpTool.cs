// MIT License
//
// McpTool.cs
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
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using NoSQL.GraphDB.Mcp.Configuration;

namespace NoSQL.GraphDB.Mcp.Tools
{
    /// <summary>The opt-in security tier a tool belongs to (spec §3.6). <c>code</c> is a
    /// capability that widens tools rather than a tier, so it is not listed here.</summary>
    public enum ToolTier
    {
        Read,
        Write,
        Admin,
    }

    /// <summary>
    ///   One consolidated, capability-oriented tool (spec §3.2). Each tool describes its own
    ///   hand-authored flat schema and validates/executes its own arguments; the
    ///   <see cref="ToolCatalog"/> only decides which tools are visible/callable given the
    ///   enabled tiers. The <see cref="McpToolsOptions"/> flags are passed to <see cref="Describe"/>
    ///   and <see cref="InvokeAsync"/> so a tool can widen its schema under the <c>code</c>
    ///   capability without the catalog knowing the details.
    /// </summary>
    public interface IMcpTool
    {
        String Name { get; }

        ToolTier Tier { get; }

        Tool Describe(McpToolsOptions tools);

        Task<CallToolResult> InvokeAsync(
            IReadOnlyDictionary<String, JsonElement> arguments,
            McpToolsOptions tools,
            CancellationToken cancellationToken);
    }

    /// <summary>Argument-reading helpers shared by tool handlers (server-side validation lives in
    /// each tool; these just pull typed values out of the raw argument map).</summary>
    public static class ToolArgs
    {
        public static String? GetString(IReadOnlyDictionary<String, JsonElement> args, String name)
        {
            if (args.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
            return null;
        }

        public static Int32? GetInt(IReadOnlyDictionary<String, JsonElement> args, String name)
        {
            if (args.TryGetValue(name, out var value) &&
                value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt32(out var i))
            {
                return i;
            }
            return null;
        }

        public static Boolean? GetBool(IReadOnlyDictionary<String, JsonElement> args, String name)
        {
            if (args.TryGetValue(name, out var value) &&
                (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False))
            {
                return value.GetBoolean();
            }
            return null;
        }

        /// <summary>The raw element for a named argument (for values whose native type the tool
        /// interprets itself, e.g. a search literal via <see cref="Bridge.ValueMapping"/>).</summary>
        public static Boolean TryGetElement(IReadOnlyDictionary<String, JsonElement> args, String name, out JsonElement value)
        {
            return args.TryGetValue(name, out value) && value.ValueKind != JsonValueKind.Null;
        }

        /// <summary>A string-array argument as a set (e.g. <c>include</c>/<c>fields</c>).</summary>
        public static HashSet<String> GetStringSet(IReadOnlyDictionary<String, JsonElement> args, String name)
        {
            var set = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
            if (args.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var s = item.GetString();
                        if (!String.IsNullOrEmpty(s))
                        {
                            set.Add(s);
                        }
                    }
                }
            }
            return set;
        }

        /// <summary>A number-array argument as a float vector (e.g. the vector-search query).</summary>
        public static Single[]? GetSingleArray(IReadOnlyDictionary<String, JsonElement> args, String name)
        {
            if (!args.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            var list = new List<Single>(value.GetArrayLength());
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number)
                {
                    return null;
                }
                list.Add(item.GetSingle());
            }
            return list.ToArray();
        }
    }
}
