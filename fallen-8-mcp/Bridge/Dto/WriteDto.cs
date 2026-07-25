// MIT License
//
// WriteDto.cs
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
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.Mcp.Bridge.Dto
{
    /// <summary>A typed property on the wire: value is always a string + an FQTN (the bridge
    /// builds these from JSON-native values via <see cref="ValueMapping"/>).</summary>
    public sealed class PropertySpecDto
    {
        public String PropertyId { get; set; } = String.Empty;

        public String FullQualifiedTypeName { get; set; } = "System.String";

        public String PropertyValue { get; set; } = String.Empty;
    }

    /// <summary>Body of <c>PUT /vertex</c>. <c>creationDate</c> is a Unix-timestamp number.</summary>
    public sealed class VertexSpecDto
    {
        public UInt32 CreationDate { get; set; } = 1;

        public String? Label { get; set; }

        public List<PropertySpecDto> Properties { get; set; } = new();
    }

    /// <summary>Body of <c>PUT /edge</c>.</summary>
    public sealed class EdgeSpecDto
    {
        public UInt32 CreationDate { get; set; } = 1;

        public Int32 SourceVertex { get; set; }

        public Int32 TargetVertex { get; set; }

        public String EdgePropertyId { get; set; } = String.Empty;

        public String? Label { get; set; }

        public List<PropertySpecDto> Properties { get; set; } = new();
    }

    /// <summary>Body of <c>PUT /graphelement/{id}/embedding/{name}</c>.</summary>
    public sealed class EmbeddingWriteDto
    {
        public Single[] Vector { get; set; } = Array.Empty<Single>();
    }

    /// <summary>Body of <c>PUT /save</c> (both optional).</summary>
    public sealed class SaveSpecDto
    {
        public String? SaveGameLocation { get; set; }

        public Int32? SavePartitions { get; set; }
    }

    /// <summary>Body of <c>PATCH /ns/{name}</c> — the new name (wire field <c>name</c>).</summary>
    public sealed class NamespaceRenameDto
    {
        public String Name { get; set; } = String.Empty;
    }

    /// <summary>Body of <c>PUT /subgraph</c>. The <c>*Filter</c> fields are inline C# fragments
    /// (the code capability); <c>storedQuery</c> and <c>name</c> are code-free. Null fragments are
    /// omitted so a code-free request compiles nothing and passes with dynamic code off.</summary>
    public sealed class SubGraphSpecDto
    {
        public String Name { get; set; } = String.Empty;

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public String? StoredQuery { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public String? VertexFilter { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public String? EdgeFilter { get; set; }
    }

    /// <summary>The inline path filter fragments (code capability), added to a <see cref="PathRequest"/>.</summary>
    public sealed class PathFilterDto
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public String? VertexFilter { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public String? EdgeFilter { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public String? EdgePropertyFilter { get; set; }
    }

    /// <summary>The inline path cost fragments (code capability).</summary>
    public sealed class PathCostDto
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public String? VertexCost { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public String? EdgeCost { get; set; }
    }
}
