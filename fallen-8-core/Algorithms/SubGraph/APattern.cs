// MIT License
//
// APattern.cs
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
    /// Abstract base class for graph pattern matching in subgraph queries.
    /// Provides common functionality for defining patterns used to match vertices and edges in a graph.
    /// </summary>
    /// <remarks>
    /// This class serves as the foundation for both <see cref="VertexPattern"/> and <see cref="EdgePattern"/>,
    /// providing shared properties like reference identifiers and label filtering capabilities.
    /// Patterns are used to define the structure and constraints when searching for matching subgraphs
    /// within a larger graph database.
    /// </remarks>
    public abstract class APattern
    {
        /// <summary>
        /// Gets or sets the reference identifier for this pattern.
        /// </summary>
        /// <value>
        /// A string that uniquely identifies this pattern within a subgraph definition.
        /// This reference can be used to link patterns together and to identify matched elements in query results.
        /// </value>
        /// <remarks>
        /// The reference serves as a named identifier that allows:
        /// <list type="bullet">
        /// <item><description>Referencing this pattern from other patterns in the same subgraph query</description></item>
        /// <item><description>Identifying matched graph elements in the query results</description></item>
        /// <item><description>Creating relationships between different parts of the pattern definition</description></item>
        /// </list>
        /// </remarks>
        public string Reference
        {
            get; set;
        }

        /// <summary>
        /// Gets or sets the label filter delegate for this pattern.
        /// </summary>
        /// <value>
        /// A <see cref="Delegates.LabelFilter"/> delegate that determines whether a graph element's label
        /// should be included in the pattern match. Returns <c>false</c> to filter out elements with non-matching labels.
        /// </value>
        /// <remarks>
        /// The label filter provides a flexible way to constrain pattern matching based on the labels
        /// associated with graph elements (vertices or edges). This is commonly used to match elements
        /// of specific types or categories within the graph.
        /// </remarks>
        public Delegates.LabelFilter Label
        {
            get; set;
        }
    }
}
