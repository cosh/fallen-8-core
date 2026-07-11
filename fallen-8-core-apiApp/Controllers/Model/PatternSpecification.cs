// MIT License
//
// PatternSpecification.cs
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
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   A single element of a subgraph pattern sequence.
    /// </summary>
    /// <remarks>
    ///   A subgraph pattern is an ordered list of these specifications that alternates
    ///   vertex ↔ edge, describing a path to match (for example
    ///   Vertex → Edge → Vertex). The filter properties are C# code fragments that are
    ///   compiled at runtime into delegates, exactly like the path-finding API. Each
    ///   fragment must start with <c>return</c> followed by a single-parameter lambda,
    ///   for example <c>return (ge) => ge.Label == "person";</c>. A null or empty
    ///   fragment means "match everything".
    /// </remarks>
    public sealed class PatternSpecification
    {
        /// <summary>
        ///   The kind of pattern: <c>Vertex</c>, <c>Edge</c> or <c>VariableLengthEdge</c>.
        /// </summary>
        /// <example>Vertex</example>
        [JsonPropertyName("type")]
        [DefaultValue("Vertex")]
        public String Type
        {
            get; set;
        } = "Vertex";

        /// <summary>
        ///   Optional name used to identify this pattern element in the definition.
        /// </summary>
        /// <example>start</example>
        [JsonPropertyName("patternName")]
        public String PatternName
        {
            get; set;
        }

        /// <summary>
        ///   Filter applied to any graph element (vertex or edge). The lambda receives an
        ///   <c>AGraphElementModel</c>.
        /// </summary>
        /// <example>return (ge) => ge.Label == "person";</example>
        [JsonPropertyName("graphElementFilter")]
        public String GraphElementFilter
        {
            get; set;
        }

        /// <summary>
        ///   Vertex-specific filter. Only meaningful when <see cref="Type"/> is
        ///   <c>Vertex</c>. The lambda receives a <c>VertexModel</c>.
        /// </summary>
        /// <example>return (v) => v.TryGetProperty(out var age, "age") &amp;&amp; (int)age >= 18;</example>
        [JsonPropertyName("vertexFilter")]
        public String VertexFilter
        {
            get; set;
        }

        /// <summary>
        ///   Traversal direction for edge patterns: <c>OutgoingEdge</c>, <c>IncomingEdge</c>
        ///   or <c>UndirectedEdge</c>. Only meaningful for <c>Edge</c> /
        ///   <c>VariableLengthEdge</c>. Defaults to <c>OutgoingEdge</c>.
        /// </summary>
        /// <example>OutgoingEdge</example>
        [JsonPropertyName("direction")]
        [DefaultValue("OutgoingEdge")]
        public String Direction
        {
            get; set;
        } = "OutgoingEdge";

        /// <summary>
        ///   Filter applied to an edge's property id (a string) before the edge itself is
        ///   inspected. Only meaningful for edge patterns.
        /// </summary>
        /// <example>return (p) => p == "knows";</example>
        [JsonPropertyName("edgePropertyFilter")]
        public String EdgePropertyFilter
        {
            get; set;
        }

        /// <summary>
        ///   Edge-specific filter. Only meaningful for edge patterns. The lambda receives
        ///   an <c>EdgeModel</c>.
        /// </summary>
        /// <example>return (e) => e.Label == "knows";</example>
        [JsonPropertyName("edgeFilter")]
        public String EdgeFilter
        {
            get; set;
        }

        /// <summary>
        ///   Minimum number of hops for a <c>VariableLengthEdge</c> pattern.
        /// </summary>
        /// <example>1</example>
        [JsonPropertyName("minLength")]
        [DefaultValue((ushort)1)]
        public UInt16 MinLength
        {
            get; set;
        } = 1;

        /// <summary>
        ///   Maximum number of hops for a <c>VariableLengthEdge</c> pattern.
        /// </summary>
        /// <example>3</example>
        [JsonPropertyName("maxLength")]
        [DefaultValue((ushort)1)]
        public UInt16 MaxLength
        {
            get; set;
        } = 1;
    }
}
