// MIT License
//
// SubGraphDefinition.cs
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

namespace NoSQL.GraphDB.Core.Algorithms.SubGraph
{
    /// <summary>
    /// Defines the structure and constraints for a subgraph query.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SubGraphDefinition encapsulates all the information needed to execute a subgraph query,
    /// including a name and a collection of patterns that define the structure
    /// and constraints for matching subgraphs within a larger graph database.
    /// </para>
    /// <para>
    /// The definition uses a list of <see cref="APattern"/> instances (which can be
    /// <see cref="VertexPattern"/> or <see cref="EdgePattern"/> objects) to describe
    /// the topology and filtering criteria for the desired subgraph. These patterns work
    /// together to form a complete query specification.
    /// </para>
    /// <para>
    /// This class is typically passed to implementations of <see cref="ISubGraphAlgorithm"/>
    /// to execute the subgraph matching operation.
    /// </para>
    /// </remarks>
    /// <example>
    /// Example of creating a subgraph definition:
    /// <code>
    /// var definition = new SubGraphDefinition
    /// {
    ///     Name = "FindFriendsOfFriends",
    ///     Pattern = new List&lt;APattern&gt;
    ///     {
    ///         new VertexPattern { PatternName = "person", Label = label => label == "Person" },
    ///         new EdgePattern { PatternName = "knows", Label = label => label == "KNOWS", MinLength = 2, MaxLength = 2 },
    ///         new VertexPattern { PatternName = "friendOfFriend", Label = label => label == "Person" }
    ///     },
    ///     AdditionalInformation = new Dictionary&lt;string, string&gt;
    ///     {
    ///         { "description", "Find friends of friends" },
    ///         { "category", "social" }
    ///     }
    /// };
    /// </code>
    /// </example>
    public class SubGraphDefinition
    {
        /// <summary>
        /// Gets or sets the name for this subgraph definition.
        /// </summary>
        /// <value>
        /// A string that names this subgraph definition.
        /// This can be used for tracking, logging, or referencing the query.
        /// </value>
        /// <remarks>
        /// The name is particularly useful when managing multiple subgraph queries,
        /// caching query results, or debugging complex pattern matching operations.
        /// It provides a human-readable identifier for the subgraph definition.
        /// </remarks>
        public string Name
        {
            get; set;
        }

        /// <summary>
        /// Gets or sets additional information for this subgraph definition.
        /// </summary>
        /// <value>
        /// A dictionary containing additional key-value pairs of information.
        /// Both keys and values are strings.
        /// </value>
        /// <remarks>
        /// This property allows you to attach arbitrary metadata to the subgraph definition,
        /// such as descriptions, categories, author information, version numbers, or any
        /// other contextual information that might be useful for managing or documenting
        /// the query.
        /// </remarks>
        public Dictionary<String, String> AdditionalInformation
        {
            get; set;
        }

        /// <summary>
        /// Gets or sets the vertex filter for initial subgraph population.
        /// </summary>
        /// <value>
        /// A <see cref="GraphElementPattern"/> that specifies which vertices should be copied from the source graph
        /// to the subgraph before pattern evaluation. If null, all vertices will be copied.
        /// </value>
        /// <remarks>
        /// <para>
        /// This filter is applied in the first phase of subgraph creation to determine which vertices
        /// from the source graph should be included in the subgraph. The pattern evaluation (via <see cref="Pattern"/>)
        /// is then applied in a subsequent phase to further refine the subgraph.
        /// </para>
        /// <para>
        /// Using a vertex filter can significantly improve performance by reducing the initial
        /// working set before pattern matching begins.
        /// </para>
        /// </remarks>
        public GraphElementPattern VertexFilter
        {
            get; set;
        }

        /// <summary>
        /// Gets or sets the edge filter for initial subgraph population.
        /// </summary>
        /// <value>
        /// A <see cref="GraphElementPattern"/> that specifies which edges should be copied from the source graph
        /// to the subgraph before pattern evaluation. If null, all edges will be copied (as long as both
        /// source and target vertices are in the subgraph).
        /// </value>
        /// <remarks>
        /// <para>
        /// This filter is applied in the second phase of subgraph creation, after vertices have been copied.
        /// Only edges where both the source and target vertices exist in the subgraph will be considered.
        /// The pattern evaluation (via <see cref="Pattern"/>) is then applied in a subsequent phase.
        /// </para>
        /// <para>
        /// Using an edge filter can help reduce the complexity of the graph before pattern matching begins,
        /// improving both performance and memory usage.
        /// </para>
        /// </remarks>
        public GraphElementPattern EdgeFilter
        {
            get; set;
        }

        /// <summary>
        /// Gets or sets the list of patterns that define the structure of the subgraph.
        /// </summary>
        /// <value>
        /// A list of <see cref="APattern"/> instances (either <see cref="VertexPattern"/> or
        /// <see cref="EdgePattern"/>) that together describe the topology and constraints
        /// for matching subgraphs.
        /// </value>
        /// <remarks>
        /// <para>
        /// The patterns in this list define the complete structure of the subgraph query.
        /// The order and composition of patterns is significant:
        /// </para>
        /// <list type="bullet">
        /// <item><description>Patterns are typically arranged in a sequence that describes a path or graph structure</description></item>
        /// <item><description>Vertex patterns define the nodes to match</description></item>
        /// <item><description>Edge patterns define the connections between vertices</description></item>
        /// <item><description>Pattern references link related patterns together</description></item>
        /// </list>
        /// <para>
        /// A typical pattern list alternates between vertex and edge patterns to describe
        /// connected paths: VertexPattern -> EdgePattern -> VertexPattern -> EdgePattern -> ...
        /// </para>
        /// <para>
        /// Pattern evaluation is performed after the initial vertex and edge filtering (via <see cref="VertexFilter"/>
        /// and <see cref="EdgeFilter"/>). The algorithm will find all valid paths matching these patterns,
        /// then extract the vertices and edges from those paths to form the final subgraph. This ensures
        /// no cycles and that all elements are part of valid matching paths.
        /// </para>
        /// </remarks>
        public List<APattern> Pattern
        {
            get;
            set;
        }
    }
}

