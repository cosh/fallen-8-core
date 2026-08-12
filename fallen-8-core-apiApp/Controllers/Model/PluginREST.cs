// MIT License
//
// PluginREST.cs
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
using System.Linq;
using System.Text.Json.Serialization;
using NoSQL.GraphDB.Core.Plugins;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   A summary of a registered plugin (feature plugin-registration): everything but the source.
    /// </summary>
    public class PluginSummaryREST
    {
        /// <summary>The registered name.</summary>
        [JsonPropertyName("name")]
        public String Name { get; set; }

        /// <summary>
        ///   The category ("Algorithm" / "Function" / "Index" / "Service"). Only the first two can be
        ///   registered over REST; an "Index" or "Service" entry exists when the process hosting the
        ///   engine registered the type itself, and is listed here like any other.
        /// </summary>
        [JsonPropertyName("category")]
        public String Category { get; set; }

        /// <summary>The contract ("Path" / "SubGraph" / "Analytics" / "GraphFunction" / "Index" / "Service").</summary>
        [JsonPropertyName("contract")]
        public String Contract { get; set; }

        /// <summary>The optional description.</summary>
        [JsonPropertyName("description")]
        public String Description { get; set; }

        /// <summary>When the plugin was registered (UTC).</summary>
        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; }

        /// <summary>The compile state ("Compiled" / "Failed" / "SourceOnly").</summary>
        [JsonPropertyName("compileState")]
        public String CompileState { get; set; }

        /// <summary>Projects an entry to a summary.</summary>
        public static PluginSummaryREST FromEntry(PluginEntry entry)
        {
            return new PluginSummaryREST
            {
                Name = entry.Definition.Name,
                Category = entry.Definition.Category.ToString(),
                Contract = entry.Definition.Contract.ToString(),
                Description = entry.Definition.Description,
                CreatedAt = entry.Definition.CreatedAt,
                CompileState = entry.CompileState.ToString()
            };
        }
    }

    /// <summary>
    ///   The full detail of a registered plugin: its summary plus the stored source (for inspection
    ///   and manual cross-instance migration) and, for a <c>Failed</c> entry, the recompile
    ///   diagnostics.
    /// </summary>
    public sealed class PluginDetailREST : PluginSummaryREST
    {
        /// <summary>The stored whole-type C# source; null for an entry that has none, which is what a
        /// host-registered type (an "Index"/"Service" category) always is - a client must not assume
        /// every listed plugin can be read back as source.</summary>
        [JsonPropertyName("sourceCode")]
        public String SourceCode { get; set; }

        /// <summary>The recompile diagnostics, present only for a <c>Failed</c> entry.</summary>
        [JsonPropertyName("compileDiagnostics")]
        public String CompileDiagnostics { get; set; }

        /// <summary>Projects an entry to a full detail.</summary>
        public static PluginDetailREST FromEntryDetail(PluginEntry entry)
        {
            return new PluginDetailREST
            {
                Name = entry.Definition.Name,
                Category = entry.Definition.Category.ToString(),
                Contract = entry.Definition.Contract.ToString(),
                Description = entry.Definition.Description,
                CreatedAt = entry.Definition.CreatedAt,
                CompileState = entry.CompileState.ToString(),
                SourceCode = entry.Definition.SourceCode,
                CompileDiagnostics = entry.CompileDiagnostics
            };
        }
    }

    /// <summary>
    ///   The result of a graph-function invocation (feature plugin-registration): the selected
    ///   vertices and edges, projected with the SAME DTOs as <c>GET /vertex/{id}</c> /
    ///   <c>GET /edge/{id}</c> (a view of existing elements at call time).
    /// </summary>
    public sealed class GraphFunctionResultREST
    {
        /// <summary>The selected vertices.</summary>
        [JsonPropertyName("vertices")]
        public List<Vertex> Vertices { get; set; }

        /// <summary>The selected edges.</summary>
        [JsonPropertyName("edges")]
        public List<Edge> Edges { get; set; }

        /// <summary>Projects an engine result to its REST shape.</summary>
        public static GraphFunctionResultREST FromResult(GraphFunctionResult result)
        {
            return new GraphFunctionResultREST
            {
                Vertices = result.Vertices.Select(v => new Vertex(v)).ToList(),
                Edges = result.Edges.Select(e => new Edge(e)).ToList()
            };
        }
    }

    /// <summary>
    ///   The result of a side-effect-free plugin compile-check (feature plugin-registration): whether
    ///   the source compiled and satisfied its contract, and the diagnostics if not.
    /// </summary>
    public sealed class PluginValidationREST
    {
        /// <summary>Whether the source compiled and satisfied its contract.</summary>
        [JsonPropertyName("valid")]
        public Boolean Valid { get; set; }

        /// <summary>The compiler / contract diagnostics when invalid; null when valid.</summary>
        [JsonPropertyName("error")]
        public String Error { get; set; }
    }
}
