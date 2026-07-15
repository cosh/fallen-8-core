// MIT License
//
// StoredQueryDefinition.cs
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

namespace NoSQL.GraphDB.Core.StoredQueries
{
    /// <summary>
    ///   A stored query definition: the persisted identity + SOURCE of a named, pre-validated
    ///   query (feature stored-query-library). The engine holds source and metadata only - the
    ///   same split as subgraph recipes: the specification text is opaque to the engine and is
    ///   compiled by a higher layer (the REST API's Roslyn paths) through the registered
    ///   <see cref="IStoredQueryCompiler"/>. Definitions are immutable once registered
    ///   (delete + re-register to change one).
    /// </summary>
    public sealed class StoredQueryDefinition
    {
        /// <summary>
        ///   The unique name the query is registered under. Restricted to
        ///   <c>^[A-Za-z0-9_-]{1,128}$</c> (see <see cref="StoredQueryLibrary.IsValidName"/>) and
        ///   compared ordinally, so a name is always a safe URL path segment.
        /// </summary>
        [JsonPropertyName("name")]
        public String Name
        {
            get; set;
        }

        /// <summary>The kind of artifact this definition compiles into.</summary>
        [JsonPropertyName("kind")]
        public StoredQueryKind Kind
        {
            get; set;
        }

        /// <summary>
        ///   The opaque specification document (JSON) the hosting layer compiles: for
        ///   <see cref="StoredQueryKind.Path"/> a filter/cost block, for
        ///   <see cref="StoredQueryKind.SubGraph"/> a pattern template block. Never compiled
        ///   bytes - persistence stores source and recompiles on load.
        /// </summary>
        [JsonPropertyName("specificationJson")]
        public String SpecificationJson
        {
            get; set;
        }

        /// <summary>An optional human-readable description.</summary>
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
    }
}
