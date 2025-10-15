// MIT License
//
// EdgePattern.cs
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
    /// Defines a pattern for matching single edges in a subgraph query.
    /// </summary>
    /// <remarks>
    /// <para>
    /// EdgePattern extends <see cref="APattern"/> to provide edge-specific matching capabilities.
    /// It allows for matching individual edges based on direction, edge properties, and custom filtering logic.
    /// </para>
    /// <para>
    /// This pattern type is essential for defining direct relationships between vertices in graph queries,
    /// representing a single hop connection from one vertex to another.
    /// </para>
    /// </remarks>
    public class EdgePattern : GraphElementPattern
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="EdgePattern"/> class.
        /// </summary>
        public EdgePattern() : this(PatternType.Edge)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="EdgePattern"/> class with the specified pattern type.
        /// </summary>
        /// <param name="patternType">The type of pattern being created.</param>
        protected EdgePattern(PatternType patternType) : base(patternType)
        {
        }

        /// <summary>
        /// Gets or sets the edge property filter delegate.
        /// </summary>
        /// <value>
        /// A <see cref="Delegates.EdgePropertyFilter"/> delegate that filters edges based on their property identifier
        /// and traversal direction. Returns <c>false</c> to exclude edges from the match.
        /// </value>
        /// <remarks>
        /// This filter operates at the edge property level, allowing fine-grained control over which
        /// types of edges should be included in the pattern match based on their property identifiers.
        /// This is useful when edges are categorized by property types (e.g., "FRIEND", "COLLEAGUE", "FAMILY").
        /// </remarks>
        public Delegates.EdgePropertyFilter EdgeProperty
        {
            get; set;
        }

        public Direction Direction
        {
            get; set;
        }

        /// <summary>
        /// Gets or sets the edge filter delegate.
        /// </summary>
        /// <value>
        /// A <see cref="Delegates.EdgeFilter"/> delegate that evaluates individual edge instances
        /// and their traversal direction. Returns <c>false</c> to exclude specific edges from the match.
        /// </value>
        /// <remarks>
        /// This filter provides the most granular level of edge filtering, operating on individual
        /// <see cref="EdgeModel"/> instances. It allows for complex filtering logic based on edge properties,
        /// metadata, or any other characteristics of the edge. The filter receives both the edge and the
        /// traversal direction, enabling direction-aware filtering logic.
        /// </remarks>
        public Delegates.EdgeFilter Edge
        {
            get; set;
        }
    }
}
