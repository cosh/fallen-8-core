// MIT License
//
// PathCostSpecification.cs
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
    ///   Specification for defining cost functions used in path calculations
    /// </summary>
    /// <remarks>
    ///   Cost functions determine how paths are weighted during shortest path calculations.
    ///   The string properties in this class are C# code fragments that are dynamically compiled
    ///   at runtime into delegate methods of an IPathTraverser implementation.
    ///
    ///   Each code fragment should be valid C# code that returns a numeric value representing
    ///   the cost of traversing a vertex or edge. Lower costs are preferred during path finding.
    ///
    ///   IMPORTANT: Each code fragment MUST use the 'return' keyword followed by a lambda expression:
    ///   1. Simple lambda expressions: "return (vertex) => vertex.TryGetProperty(out var age, \"age\") ? (double)age : 1.0;"
    ///   2. More complex logic: "return (vertex) => { if (vertex.Label == \"important\") return 0.5; return 1.0; };"
    ///   3. Default behavior: "return (vertex) => 1.0;" (uniform cost for all elements)
    ///
    ///   If no cost functions are provided, all elements will have a default cost of 1.0.
    /// </remarks>
    /// <example>
    /// {
    ///   "vertexCost": "return (v) => v.TryGetProperty(out var age, \"age\") ? (double)age : 1.0;",
    ///   "edgeCost": "return (e) => e.TryGetProperty(out var weight, \"weight\") ? (double)weight : 1.0;"
    /// }
    /// </example>
    public sealed class PathCostSpecification : IEquatable<PathCostSpecification>
    {
        /// <summary>
        /// Cost function for vertices during path traversal
        /// </summary>
        /// <remarks>
        /// Provide a C# expression that returns a numeric cost value for vertices.
        /// Lower costs are preferred in path calculations.
        ///
        /// Examples:
        /// - "return (vertex) => vertex.TryGetProperty(out var age, \"age\") ? (double)age : 1.0;" - Use age property as cost
        /// - "return (vertex) => vertex.Label == \"priority\" ? 0.5 : 1.0;" - Lower cost for priority vertices
        /// - "return (vertex) => 1.0;" - Uniform cost for all vertices (default)
        /// </remarks>
        /// <example>return (vertex) => vertex.TryGetProperty(out var age, "age") ? (double)age : 1.0;</example>
        [JsonPropertyName("vertexCost")]
        [DefaultValue("return (vertex) => 1.0;")]
        public String Vertex
        {
            get; set;
        } = "return (vertex) => 1.0;";

        /// <summary>
        /// Cost function for edges during path traversal
        /// </summary>
        /// <remarks>
        /// Provide a C# expression that returns a numeric cost value for edges.
        /// Lower costs are preferred in path calculations.
        ///
        /// Examples:
        /// - "return (edge) => edge.TryGetProperty(out var weight, \"weight\") ? (double)weight : 1.0;" - Use weight property as cost
        /// - "return (edge) => edge.Label == \"highway\" ? 0.5 : 2.0;" - Lower cost for highway edges
        /// - "return (edge) => 1.0;" - Uniform cost for all edges (default)
        /// </remarks>
        /// <example>return (edge) => edge.TryGetProperty(out var weight, "weight") ? (double)weight : 1.0;</example>
        [JsonPropertyName("edgeCost")]
        [DefaultValue("return (edge) => 1.0;")]
        public String Edge
        {
            get; set;
        } = "return (edge) => 1.0;";

        public override Boolean Equals(Object obj)
        {
            return Equals(obj as PathCostSpecification);
        }

        public Boolean Equals(PathCostSpecification other)
        {
            return other != null &&
                   Vertex == other.Vertex &&
                   Edge == other.Edge;
        }

        public override Int32 GetHashCode()
        {
            return HashCode.Combine(Vertex, Edge);
        }
    }
}
