// MIT License
//
// PluginDefinition.cs
//
// Copyright (c) 2025 Henning Rauch
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
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.Core.Plugins
{
    /// <summary>
    ///   A registered plugin's persisted identity + SOURCE (feature plugin-registration): the same
    ///   source-and-metadata-only split as stored queries and subgraph recipes. The engine holds the
    ///   C# source and metadata; it never compiles - a higher layer (the REST API's Roslyn path)
    ///   compiles it through the registered <see cref="IPluginCompiler"/>. Unlike a stored query,
    ///   whose source is a set of delegate FRAGMENTS the harness wraps into a fixed class, a plugin's
    ///   source is a WHOLE type implementing the category's contract - a DLL was a whole type, and its
    ///   source-based replacement is too. Definitions are immutable once registered (delete +
    ///   re-register to change one).
    /// </summary>
    public sealed class PluginDefinition
    {
        /// <summary>
        ///   The unique name the plugin is registered under (per namespace). Restricted to
        ///   <c>^[A-Za-z0-9_-]{1,128}$</c> (see <see cref="PluginRegistry.IsValidName"/>) and compared
        ///   ordinally, so a name is always a safe URL path segment. Must equal the compiled type's
        ///   <c>PluginName</c>, so the persisted name and the CLR name used at resolution never diverge.
        /// </summary>
        [JsonPropertyName("name")]
        public String Name
        {
            get; set;
        }

        /// <summary>
        ///   The top-level category. Persisted as its NAME (e.g. "Algorithm"/"Function"), not its
        ///   ordinal, so the on-disk manifest/WAL contract does not depend on enum member order.
        /// </summary>
        [JsonPropertyName("category")]
        [JsonConverter(typeof(JsonStringEnumConverter<PluginCategory>))]
        public PluginCategory Category
        {
            get; set;
        }

        /// <summary>
        ///   The exact contract the source must satisfy. Persisted as its NAME. Selects the CLR
        ///   interface the compiled type must implement and (for functions) how it is invoked.
        /// </summary>
        [JsonPropertyName("contract")]
        [JsonConverter(typeof(JsonStringEnumConverter<PluginContract>))]
        public PluginContract Contract
        {
            get; set;
        }

        /// <summary>
        ///   The C# source of the whole plugin type. Never compiled bytes - persistence stores source
        ///   and recompiles on load.
        /// </summary>
        [JsonPropertyName("sourceCode")]
        public String SourceCode
        {
            get; set;
        }

        /// <summary>An optional human-readable description.</summary>
        [JsonPropertyName("description")]
        public String Description
        {
            get; set;
        }

        /// <summary>When the plugin was registered (UTC).</summary>
        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt
        {
            get; set;
        }
    }
}
