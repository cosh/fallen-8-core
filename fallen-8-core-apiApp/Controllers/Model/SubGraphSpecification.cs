// MIT License
//
// SubGraphSpecification.cs
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
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   Specification for creating a subgraph by pattern matching against the graph.
    /// </summary>
    /// <remarks>
    ///   Mirrors the engine's <c>SubGraphDefinition</c>, but expresses every filter as a
    ///   C# code fragment (starting with <c>return</c>) that is compiled at runtime, in
    ///   the same way as the path-finding API. The optional <see cref="VertexFilter"/> and
    ///   <see cref="EdgeFilter"/> pre-select which elements are copied into the subgraph
    ///   before <see cref="Patterns"/> are evaluated to prune everything not on a matching
    ///   path. If <see cref="Patterns"/> is empty, the copied elements are the result.
    /// </remarks>
    /// <example>
    /// {
    ///   "name": "friends-of-alice",
    ///   "vertexFilter": "return (ge) => ge.Label == \"person\";",
    ///   "edgeFilter": "return (ge) => ge.Label == \"knows\";",
    ///   "patterns": [
    ///     { "type": "Vertex", "patternName": "start", "graphElementFilter": "return (ge) => ge.Label == \"person\";" },
    ///     { "type": "Edge", "patternName": "rel", "direction": "OutgoingEdge", "edgePropertyFilter": "return (p) => p == \"knows\";" },
    ///     { "type": "Vertex", "patternName": "end", "graphElementFilter": "return (ge) => ge.Label == \"person\";" }
    ///   ]
    /// }
    /// </example>
    public sealed class SubGraphSpecification
    {
        /// <summary>
        ///   Unique name the subgraph is registered under.
        /// </summary>
        /// <example>friends-of-alice</example>
        [Required]
        [JsonPropertyName("name")]
        public String Name
        {
            get; set;
        }

        /// <summary>
        ///   Optional metadata attached to the subgraph definition.
        /// </summary>
        [JsonPropertyName("additionalInformation")]
        public Dictionary<String, String> AdditionalInformation
        {
            get; set;
        }

        /// <summary>
        ///   Optional pre-filter selecting which vertices are copied into the subgraph.
        ///   Null or empty copies all vertices. The lambda receives an
        ///   <c>AGraphElementModel</c>.
        /// </summary>
        /// <example>return (ge) => ge.Label == "person";</example>
        [JsonPropertyName("vertexFilter")]
        public String VertexFilter
        {
            get; set;
        }

        /// <summary>
        ///   Optional pre-filter selecting which edges are copied into the subgraph. Null
        ///   or empty copies all edges whose endpoints were copied. The lambda receives an
        ///   <c>AGraphElementModel</c>.
        /// </summary>
        /// <example>return (ge) => ge.Label == "knows";</example>
        [JsonPropertyName("edgeFilter")]
        public String EdgeFilter
        {
            get; set;
        }

        /// <summary>
        ///   Ordered pattern sequence describing the paths to keep. Should alternate
        ///   vertex ↔ edge and start with a vertex pattern. Empty means "no pruning".
        /// </summary>
        [JsonPropertyName("patterns")]
        public List<PatternSpecification> Patterns
        {
            get; set;
        }

        /// <summary>
        ///   The name of a registered stored query of kind <c>SubGraph</c> to instantiate instead
        ///   of inline <see cref="VertexFilter"/>/<see cref="EdgeFilter"/>/<see cref="Patterns"/>
        ///   fragments (feature stored-query-library). Mutually exclusive with them (400 when
        ///   mixed); <see cref="Name"/> (and optional <see cref="AdditionalInformation"/>) stay
        ///   required per instance. A stored-query request compiles nothing and works with
        ///   dynamic code execution disabled.
        /// </summary>
        /// <example>person-net</example>
        [JsonPropertyName("storedQuery")]
        public String StoredQuery
        {
            get; set;
        }

        /// <summary>
        ///   The declarative semantic block (feature element-embeddings): the query vector the
        ///   compiled filters' traversal context carries, plus an optional code-free
        ///   <c>minScore</c> vertex pre-filter. Bound at REGISTRATION time - recalculation
        ///   reuses the same delegates and never embeds anything. Pure data (not gated by the
        ///   dynamic-code switch); mutually exclusive with <see cref="StoredQuery"/> and, when
        ///   <c>minScore</c> is set, with an inline <see cref="VertexFilter"/> fragment.
        /// </summary>
        [JsonPropertyName("semantic")]
        public SemanticTraversalSpecification Semantic
        {
            get; set;
        }
    }
}
