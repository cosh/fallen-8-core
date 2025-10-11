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

using System.Collections.Generic;

namespace NoSQL.GraphDB.Core.Algorithms.SubGraph
{
    /// <summary>
    /// Defines the structure and constraints for a subgraph query.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SubGraphDefinition encapsulates all the information needed to execute a subgraph query,
    /// including a unique identifier and a collection of patterns that define the structure
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
    ///     Id = "FindFriendsOfFriends",
    ///     Pattern = new List&lt;APattern&gt;
    ///     {
    ///         new VertexPattern { Reference = "person", Label = label => label == "Person" },
    ///         new EdgePattern { Reference = "knows", Label = label => label == "KNOWS", MinLength = 2, MaxLength = 2 },
    ///         new VertexPattern { Reference = "friendOfFriend", Label = label => label == "Person" }
    ///     }
    /// };
    /// </code>
    /// </example>
    public class SubGraphDefinition
    {
        /// <summary>
        /// Gets or sets the unique identifier for this subgraph definition.
        /// </summary>
        /// <value>
        /// A string that uniquely identifies this subgraph definition.
        /// This can be used for tracking, logging, or referencing the query.
        /// </value>
        /// <remarks>
        /// The identifier is particularly useful when managing multiple subgraph queries,
        /// caching query results, or debugging complex pattern matching operations.
        /// While not strictly required to be globally unique, it should be unique within
        /// the context of your application to avoid confusion.
        /// </remarks>
        public string Id
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
        /// </remarks>
        public List<APattern> Pattern
        {
            get;
            set;
        }
    }
}
