// MIT License
//
// StoredQueryREST.cs
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
using NoSQL.GraphDB.Core.StoredQueries;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   Summary of a registered stored query (list/detail responses).
    /// </summary>
    public class StoredQuerySummaryREST
    {
        /// <summary>The stored query name.</summary>
        /// <example>adults-shortest</example>
        [JsonPropertyName("name")]
        public String Name
        {
            get; set;
        }

        /// <summary>The stored query kind (<c>Path</c> or <c>SubGraph</c>).</summary>
        /// <example>Path</example>
        [JsonPropertyName("kind")]
        public String Kind
        {
            get; set;
        }

        /// <summary>The optional description.</summary>
        [JsonPropertyName("description")]
        public String Description
        {
            get; set;
        }

        /// <summary>When the query was registered (UTC).</summary>
        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt
        {
            get; set;
        }

        /// <summary>
        ///   The compile state: <c>Compiled</c> (invocable), <c>Failed</c> (a rehydration
        ///   recompile failed - see the detail response's diagnostics; invoking returns 409), or
        ///   <c>SourceOnly</c> (loaded without a compiler).
        /// </summary>
        /// <example>Compiled</example>
        [JsonPropertyName("compileState")]
        public String CompileState
        {
            get; set;
        }

        internal static StoredQuerySummaryREST FromEntry(StoredQueryEntry entry)
        {
            return new StoredQuerySummaryREST
            {
                Name = entry.Definition.Name,
                Kind = entry.Definition.Kind.ToString(),
                Description = entry.Definition.Description,
                CreatedAt = entry.Definition.CreatedAt,
                CompileState = entry.CompileState.ToString()
            };
        }
    }

    /// <summary>
    ///   Full detail of a registered stored query, INCLUDING its source specification - the
    ///   library is transparent to its operator (and <c>GET</c> covers manual migration between
    ///   instances).
    /// </summary>
    public sealed class StoredQueryDetailREST : StoredQuerySummaryREST
    {
        /// <summary>
        ///   The stored specification document (JSON text): the registration request's
        ///   <c>path</c> / <c>subGraph</c> block exactly as stored.
        /// </summary>
        [JsonPropertyName("specificationJson")]
        public String SpecificationJson
        {
            get; set;
        }

        /// <summary>
        ///   The compiler diagnostics of a failed rehydration recompile; null unless
        ///   <see cref="StoredQuerySummaryREST.CompileState"/> is <c>Failed</c>.
        /// </summary>
        [JsonPropertyName("compileDiagnostics")]
        public String CompileDiagnostics
        {
            get; set;
        }

        internal static StoredQueryDetailREST FromEntryDetail(StoredQueryEntry entry)
        {
            return new StoredQueryDetailREST
            {
                Name = entry.Definition.Name,
                Kind = entry.Definition.Kind.ToString(),
                Description = entry.Definition.Description,
                CreatedAt = entry.Definition.CreatedAt,
                CompileState = entry.CompileState.ToString(),
                SpecificationJson = entry.Definition.SpecificationJson,
                CompileDiagnostics = entry.CompileDiagnostics
            };
        }
    }
}
