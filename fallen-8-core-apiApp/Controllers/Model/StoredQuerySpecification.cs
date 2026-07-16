// MIT License
//
// StoredQuerySpecification.cs
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
    ///   Specification for registering a stored query (feature stored-query-library): a named,
    ///   validated, pre-compiled query definition that the path/subgraph endpoints can afterwards
    ///   reference by name - including when dynamic code execution is disabled.
    /// </summary>
    /// <remarks>
    ///   Exactly one of <see cref="Path"/> / <see cref="SubGraph"/> must be present and must match
    ///   <see cref="Kind"/>. The fragments inside the block are the same C# code fragments the
    ///   inline endpoints accept, and are compiled (and validated) once at registration.
    /// </remarks>
    /// <example>
    /// {
    ///   "name": "adults-shortest",
    ///   "kind": "Path",
    ///   "description": "age&gt;30 vertices, weight-by-distance",
    ///   "path": {
    ///     "filter": { "vertexFilter": "return (v) =&gt; v.TryGetProperty(out int age, \"age\") &amp;&amp; age &gt; 30;" },
    ///     "cost": { "edgeCost": "return (e) =&gt; 1.0;" }
    ///   }
    /// }
    /// </example>
    public sealed class StoredQuerySpecification
    {
        /// <summary>
        ///   The unique name to register the query under. Restricted to
        ///   <c>^[A-Za-z0-9_-]{1,128}$</c> (always a safe URL path segment); compared
        ///   case-sensitively.
        /// </summary>
        /// <example>adults-shortest</example>
        [Required]
        [JsonPropertyName("name")]
        public String Name
        {
            get; set;
        }

        /// <summary>
        ///   The stored query kind: <c>Path</c> (a filter/cost set for <c>POST /path</c>) or
        ///   <c>SubGraph</c> (a pattern template for <c>PUT /subgraph</c>).
        /// </summary>
        /// <example>Path</example>
        [Required]
        [JsonPropertyName("kind")]
        public String Kind
        {
            get; set;
        }

        /// <summary>An optional human-readable description.</summary>
        /// <example>age&gt;30 vertices, weight-by-distance</example>
        [JsonPropertyName("description")]
        public String Description
        {
            get; set;
        }

        /// <summary>
        ///   The path filter/cost block (required iff <see cref="Kind"/> is <c>Path</c>).
        /// </summary>
        [JsonPropertyName("path")]
        public StoredPathQueryBlock Path
        {
            get; set;
        }

        /// <summary>
        ///   The subgraph pattern template block (required iff <see cref="Kind"/> is
        ///   <c>SubGraph</c>). The subgraph instance name and additional information stay
        ///   per-request on <c>PUT /subgraph</c> and are not part of the stored template.
        /// </summary>
        [JsonPropertyName("subGraph")]
        public StoredSubGraphQueryBlock SubGraph
        {
            get; set;
        }
    }

    /// <summary>
    ///   The stored form of a path query: the <c>filter</c>/<c>cost</c> blocks of a
    ///   <see cref="PathSpecification"/>. The numeric bounds (<c>maxDepth</c>, <c>maxResults</c>,
    ///   <c>maxPathWeight</c>) and the algorithm name stay per-request.
    /// </summary>
    public sealed class StoredPathQueryBlock
    {
        /// <summary>Filtering criteria for elements to include in path calculations.</summary>
        [JsonPropertyName("filter")]
        public PathFilterSpecification Filter
        {
            get; set;
        }

        /// <summary>Cost function specifications for weighting paths.</summary>
        [JsonPropertyName("cost")]
        public PathCostSpecification Cost
        {
            get; set;
        }
    }

    /// <summary>
    ///   The stored form of a subgraph query: a <see cref="SubGraphSpecification"/> WITHOUT the
    ///   per-instance <c>name</c>/<c>additionalInformation</c> fields.
    /// </summary>
    public sealed class StoredSubGraphQueryBlock
    {
        /// <summary>
        ///   Optional pre-filter selecting which vertices are copied into the subgraph
        ///   (a C# fragment receiving an <c>AGraphElementModel</c>).
        /// </summary>
        /// <example>return (ge) =&gt; ge.Label == "person";</example>
        [JsonPropertyName("vertexFilter")]
        public String VertexFilter
        {
            get; set;
        }

        /// <summary>
        ///   Optional pre-filter selecting which edges are copied into the subgraph
        ///   (a C# fragment receiving an <c>AGraphElementModel</c>).
        /// </summary>
        /// <example>return (ge) =&gt; ge.Label == "knows";</example>
        [JsonPropertyName("edgeFilter")]
        public String EdgeFilter
        {
            get; set;
        }

        /// <summary>
        ///   Ordered pattern sequence describing the paths to keep (see
        ///   <see cref="SubGraphSpecification.Patterns"/>).
        /// </summary>
        [JsonPropertyName("patterns")]
        public List<PatternSpecification> Patterns
        {
            get; set;
        }
    }
}
