// MIT License
//
// PathFilterSpecification.cs
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
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   Specification for filtering graph elements during path finding operations
    /// </summary>
    /// <remarks>
    ///   Used to constrain which vertices and edges are considered during path traversal.
    ///   The string properties in this class are C# code fragments that are dynamically compiled
    ///   at runtime into delegate methods of an IPathTraverser implementation.
    ///
    ///   Each code fragment should contain valid C# code that returns a boolean value to
    ///   determine if a graph element should be included in path calculations.
    ///
    ///   These values are compiled into lambda functions matching the delegate signatures
    ///   in the Delegates class.
    ///
    ///   IMPORTANT: Each code fragment MUST use the 'return' keyword followed by a lambda expression:
    ///   1. Simple lambda expressions: "return (vertex) => vertex.Label == \"person\";"
    ///   2. More complex logic: "return (vertex) => { if (vertex.Label == \"person\") return true; return false; };"
    ///   3. Default behavior: "return (vertex) => true;" (accept all elements)
    /// </remarks>
    /// <example>
    /// {
    ///   "edgePropertyFilter": "return (p,d) => p == \"knows\";",
    ///   "vertexFilter": "return (v) => v.Label == \"person\";",
    ///   "edgeFilter": "return (e,d) => e.Label == \"friendship\";"
    /// }
    /// </example>
    public sealed class PathFilterSpecification : IEquatable<PathFilterSpecification>
    {
        /// <summary>
        /// Filter to apply on edge properties during path traversal
        /// </summary>
        /// <remarks>
        /// Provide a C# expression that returns true for edge property identifiers to include
        /// in the traversal, or false to exclude them.
        ///
        /// Examples:
        /// - "return (p,d) => p == \"knows\";" - Include only "knows" edges
        /// - "return (p,d) => p.StartsWith(\"rel_\");" - Include edges with properties starting with "rel_"
        /// - "return (p,d) => true;" - Include all edges (default)
        /// </remarks>
        /// <example>return (p,d) => p == "knows";</example>
        [Required]
        [JsonPropertyName("edgePropertyFilter")]
        [DefaultValue("return (p,d) => true;")]
        public String EdgeProperty
        {
            get; set;
        } = "return (p,d) => true;";

        /// <summary>
        /// Filter to apply on vertices during path traversal
        /// </summary>
        /// <remarks>
        /// Provide a C# expression that returns true for vertices to include
        /// in the traversal, or false to exclude them.
        ///
        /// Examples:
        /// - "return (v) => v.Label == \"person\";" - Include only vertices labeled "person"
        /// - "return (v) => v.TryGetProperty(out var age, \"age\") && (int)age > 18;" - Include only vertices with age > 18
        /// - "return (v) => true;" - Include all vertices (default)
        /// </remarks>
        /// <example>return (v) => v.Label == "person";</example>
        [Required]
        [JsonPropertyName("vertexFilter")]
        [DefaultValue("return (v) => true;")]
        public String Vertex
        {
            get; set;
        } = "return (v) => true;";

        /// <summary>
        /// Filter to apply on edges during path traversal
        /// </summary>
        /// <remarks>
        /// Provide a C# expression that returns true for edges to include
        /// in the traversal, or false to exclude them.
        ///
        /// Examples:
        /// - "return (e,d) => e.Label == \"friendship\";" - Include only edges labeled "friendship"
        /// - "return (e,d) => e.TryGetProperty(out var weight, \"weight\") && (double)weight < 10;" - Include only edges with weight < 10
        /// - "return (e,d) => true;" - Include all edges (default)
        /// </remarks>
        /// <example>return (e,d) => e.Label == "friendship";</example>
        [Required]
        [JsonPropertyName("edgeFilter")]
        [DefaultValue("return (e,d) => true;")]
        public String Edge
        {
            get; set;
        } = "return (e,d) => true;";

        public override Boolean Equals(Object obj)
        {
            return Equals(obj as PathFilterSpecification);
        }

        public Boolean Equals(PathFilterSpecification other)
        {
            return other != null &&
                   EdgeProperty == other.EdgeProperty &&
                   Vertex == other.Vertex &&
                   Edge == other.Edge;
        }

        public override Int32 GetHashCode()
        {
            return HashCode.Combine(EdgeProperty, Vertex, Edge);
        }
    }
}
