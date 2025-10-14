// MIT License
//
// VertexPattern.cs
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
    /// Defines a pattern for matching vertices in a subgraph query.
    /// </summary>
    /// <remarks>
    /// <para>
    /// VertexPattern extends <see cref="APattern"/> to provide vertex-specific matching capabilities.
    /// It allows for filtering vertices based on custom logic defined through a vertex filter delegate.
    /// </para>
    /// <para>
    /// Vertex patterns are typically used in conjunction with <see cref="EdgePattern"/> instances
    /// to define complete graph patterns. They serve as the nodes in pattern matching queries,
    /// specifying constraints on which vertices should be included in the matched subgraph.
    /// </para>
    /// <para>
    /// The pattern inherits label filtering from <see cref="APattern.Label"/> and adds vertex-specific
    /// filtering through the <see cref="Vertex"/> property, enabling multi-layered matching criteria.
    /// </para>
    /// </remarks>
    public class VertexPattern : GraphElementPattern
    {
        /// <summary>
        /// Gets or sets the vertex filter delegate for this pattern.
        /// </summary>
        /// <value>
        /// A <see cref="Delegates.VertexFilter"/> delegate that evaluates individual vertex instances
        /// to determine whether they match the pattern. Returns <c>false</c> to exclude specific vertices from the match.
        /// </value>
        /// <remarks>
        /// <para>
        /// The vertex filter provides fine-grained control over vertex matching, operating on individual
        /// <see cref="VertexModel"/> instances. This allows for complex filtering logic based on:
        /// </para>
        /// <list type="bullet">
        /// <item><description>Vertex properties and their values</description></item>
        /// <item><description>Vertex metadata or identifiers</description></item>
        /// <item><description>Connectivity characteristics (degree, neighbor counts, etc.)</description></item>
        /// <item><description>Custom business logic or domain-specific constraints</description></item>
        /// </list>
        /// <para>
        /// This filter works in conjunction with the inherited <see cref="APattern.Label"/> filter,
        /// where the label filter is typically applied first as a coarse filter, followed by this
        /// more detailed vertex-level filtering.
        /// </para>
        /// </remarks>
        public Delegates.VertexFilter Vertex
        {
            get; set;
        }
    }
}
