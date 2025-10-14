// MIT License
//
// VariableLengthEdgePattern.cs
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

namespace NoSQL.GraphDB.Core.Algorithms.SubGraph
{
    /// <summary>
    /// Defines a pattern for matching edges in a subgraph query.
    /// </summary>
    /// <remarks>
    /// <para>
    /// EdgePattern extends <see cref="APattern"/> to provide edge-specific matching capabilities.
    /// It allows for sophisticated edge matching including variable-length paths, directional constraints,
    /// property filtering, and custom edge filtering logic.
    /// </para>
    /// <para>
    /// This pattern type is essential for defining relationships between vertices in graph queries,
    /// supporting both simple single-edge connections and complex path expressions.
    /// </para>
    /// </remarks>
    public class VariableLengthEdgePattern : EdgePattern
    {
        /// <summary>
        /// Gets or sets the maximum path length for this edge pattern.
        /// </summary>
        /// <value>
        /// The maximum number of edges to traverse when matching this pattern.
        /// Used in variable-length path matching scenarios.
        /// </value>
        /// <remarks>
        /// This property enables matching paths of variable length between vertices.
        /// When combined with <see cref="MinLength"/>, it defines a range of acceptable path lengths.
        /// For example, setting MinLength to 1 and MaxLength to 3 would match paths with 1, 2, or 3 edges.
        /// </remarks>
        public ushort MaxLength
        {
            get; set;
        }

        /// <summary>
        /// Gets or sets the minimum path length for this edge pattern.
        /// </summary>
        /// <value>
        /// The minimum number of edges required when matching this pattern.
        /// Used in variable-length path matching scenarios.
        /// </value>
        /// <remarks>
        /// This property defines the lower bound for path length matching.
        /// Together with <see cref="MaxLength"/>, it creates a range for variable-length path queries.
        /// A value of 1 represents a direct edge connection, while higher values allow for indirect paths.
        /// </remarks>
        public ushort MinLength
        {
            get; set;
        }
    }
}
