// MIT License
//
// NamespaceCatalog.cs
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
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.App.Namespaces
{
    /// <summary>
    ///   The persisted namespace catalog, <c>metadata/namespaces.json</c> (feature
    ///   graph-namespaces): the boot-time source of truth for which namespaces exist beyond the
    ///   implicit <c>default</c>. Written atomically on create/rename/drop; a corrupt catalog
    ///   fails startup loudly (the same posture as the save-game registry).
    /// </summary>
    public sealed class NamespaceCatalogDocument
    {
        [JsonPropertyName("schemaVersion")]
        public Int32 SchemaVersion
        {
            get; set;
        } = 1;

        [JsonPropertyName("namespaces")]
        public List<NamespaceCatalogEntry> Namespaces
        {
            get; set;
        } = new List<NamespaceCatalogEntry>();
    }

    /// <summary>One cataloged namespace: its immutable id (the on-disk key) and its current name.</summary>
    public sealed class NamespaceCatalogEntry
    {
        [JsonPropertyName("id")]
        public String Id
        {
            get; set;
        }

        [JsonPropertyName("name")]
        public String Name
        {
            get; set;
        }

        /// <summary>ISO-8601 UTC creation instant.</summary>
        [JsonPropertyName("createdAt")]
        public String CreatedAt
        {
            get; set;
        }
    }
}
