// MIT License
//
// PatternType.cs
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
    /// Defines the type of pattern used in subgraph matching.
    /// </summary>
    /// <remarks>
    /// This enum is used to identify the specific pattern type at runtime,
    /// enabling efficient type checking without reflection or type casting.
    /// Each pattern type represents a different matching strategy in graph queries.
    /// </remarks>
    public enum PatternType
    {
        /// <summary>
        /// Represents a pattern for matching vertices in the graph.
        /// </summary>
        /// <remarks>
        /// Vertex patterns are used to specify constraints on individual vertices,
        /// including property filters and custom matching logic.
        /// </remarks>
        Vertex,

        /// <summary>
        /// Represents a pattern for matching single edges in the graph.
        /// </summary>
        /// <remarks>
        /// Edge patterns define constraints for matching direct connections between vertices,
        /// including direction, edge properties, and custom filtering logic.
        /// </remarks>
        Edge,

        /// <summary>
        /// Represents a pattern for matching variable-length paths in the graph.
        /// </summary>
        /// <remarks>
        /// Variable-length edge patterns allow matching paths of varying lengths between vertices,
        /// with configurable minimum and maximum path lengths.
        /// </remarks>
        VariableLengthEdge
    }
}
