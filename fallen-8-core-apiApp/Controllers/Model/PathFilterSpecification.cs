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
    ///   Used to constrain which vertices and edges are considered during path traversal
    /// </remarks>
    /// <example>
    /// {
    ///   "edgeProperty": "knows",
    ///   "vertex": "person",
    ///   "edge": "friendship"
    /// }
    /// </example>
    public sealed class PathFilterSpecification : IEquatable<PathFilterSpecification>
    {
        /// <summary>
        /// Filter to apply on edge properties during path traversal
        /// </summary>
        /// <remarks>
        /// Only edges with this property identifier will be traversed
        /// </remarks>
        /// <example>knows</example>
        [Required]
        [JsonPropertyName("edgePropertyFilter")]
        public String EdgeProperty
        {
            get; set;
        }

        /// <summary>
        /// Filter to apply on vertices during path traversal
        /// </summary>
        /// <remarks>
        /// Only vertices with this label will be included in paths
        /// </remarks>
        /// <example>person</example>
        [Required]
        [JsonPropertyName("vertexFilter")]
        public String Vertex
        {
            get; set;
        }

        /// <summary>
        /// Filter to apply on edges during path traversal
        /// </summary>
        /// <remarks>
        /// Only edges with this label will be traversed
        /// </remarks>
        /// <example>friendship</example>
        [Required]
        [JsonPropertyName("edgeFilter")]
        public String Edge
        {
            get; set;
        }

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
