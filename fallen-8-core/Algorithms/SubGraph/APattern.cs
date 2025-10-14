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
    /// providing shared properties like pattern names and label filtering capabilities.
    /// Patterns are used to define the structure and constraints when searching for matching subgraphs
    /// within a larger graph database.
    /// </remarks>
    public abstract class APattern
    {
        /// <summary>
        /// Gets or sets the name identifier for this pattern.
        /// </summary>
        /// <value>
        /// A string that uniquely identifies this pattern within a subgraph definition.
        /// This name can be used to link patterns together and to identify matched elements in query results.
        /// </value>
        /// <remarks>
        /// The pattern name serves as a named identifier that allows:
        /// <list type="bullet">
        /// <item><description>Referencing this pattern from other patterns in the same subgraph query</description></item>
        /// <item><description>Identifying matched graph elements in the query results</description></item>
        /// <item><description>Creating relationships between different parts of the pattern definition</description></item>
        /// </list>
        /// </remarks>
        public string PatternName
        {
            get; set;
        }
    }
}
