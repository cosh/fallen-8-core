// MIT License
//
// ISubGraphAlgorithm.cs
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

#region Usings

using System;
using NoSQL.GraphDB.Core.Plugin;

#endregion

namespace NoSQL.GraphDB.Core.Algorithms.SubGraph
{
    /// <summary>
    /// Defines the contract for subgraph pattern matching algorithms.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This interface extends <see cref="IPlugin"/> to provide a pluggable architecture for
    /// different subgraph matching algorithm implementations. Implementations of this interface
    /// can use various strategies and optimizations for finding matching subgraphs within a
    /// larger graph database.
    /// </para>
    /// <para>
    /// Subgraph algorithms take a <see cref="SubGraphDefinition"/> that describes the desired
    /// graph pattern through a collection of vertex and edge patterns, and attempt to find
    /// matching instances of that pattern within the graph. The results are returned as a
    /// <see cref="SubGraphResult"/> containing the matched subgraph.
    /// </para>
    /// <para>
    /// Common use cases for subgraph algorithms include:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Pattern matching queries (e.g., finding specific relationship patterns)</description></item>
    /// <item><description>Graph analysis and discovery (e.g., detecting communities or structures)</description></item>
    /// <item><description>Data extraction (e.g., extracting relevant portions of a large graph)</description></item>
    /// <item><description>Query optimization (e.g., identifying query-relevant graph regions)</description></item>
    /// </list>
    /// </remarks>
    public interface ISubGraphAlgorithm : IPlugin
    {
        /// <summary>
        /// Attempts to create a subgraph by matching the specified pattern definition against the graph.
        /// </summary>
        /// <param name="result">
        /// When this method returns, contains the <see cref="SubGraphResult"/> if the operation was successful,
        /// or <c>null</c> (or a default value) if the operation failed or no matches were found.
        /// This parameter is passed uninitialized.
        /// </param>
        /// <param name="definition">
        /// The <see cref="SubGraphDefinition"/> that specifies the patterns, constraints, and structure
        /// of the subgraph to find. This includes vertex patterns, edge patterns, and their filtering criteria.
        /// </param>
        /// <returns>
        /// <c>true</c> if the subgraph was successfully created and one or more matches were found;
        /// otherwise, <c>false</c> if no matches were found, an error occurred, or the definition was invalid.
        /// </returns>
        /// <remarks>
        /// <para>
        /// This method follows the TryParse pattern commonly used in .NET, where the return value indicates
        /// success or failure, and the actual result is returned via an out parameter. This design allows
        /// callers to easily check for success without requiring exception handling for normal "not found" cases.
        /// </para>
        /// <para>
        /// Implementations of this method should:
        /// </para>
        /// <list type="bullet">
        /// <item><description>Validate the provided definition before executing the query</description></item>
        /// <item><description>Apply all pattern constraints (vertex filters, edge filters, labels, etc.)</description></item>
        /// <item><description>Handle variable-length path matching as specified by edge patterns</description></item>
        /// <item><description>Construct a new <see cref="Fallen8"/> instance containing the matched subgraph</description></item>
        /// <item><description>Return <c>false</c> for invalid definitions or when no matches are found</description></item>
        /// <item><description>Return <c>true</c> and populate the result parameter when matches are found</description></item>
        /// </list>
        /// <para>
        /// Performance considerations: Subgraph matching can be computationally expensive for complex patterns
        /// or large graphs. Implementations should consider optimization strategies such as indexing, pruning,
        /// and early termination where appropriate.
        /// </para>
        /// </remarks>
        /// <example>
        /// Example usage:
        /// <code>
        /// ISubGraphAlgorithm algorithm = GetAlgorithm();
        /// var definition = new SubGraphDefinition
        /// {
        ///     Id = "FindPaths",
        ///     Pattern = new List&lt;APattern&gt; { /* pattern definitions */ }
        /// };
        ///
        /// if (algorithm.TryCreateSubgraph(out SubGraphResult result, definition))
        /// {
        ///     // Process the matched subgraph
        ///     var matchedGraph = result.SubGraph;
        /// }
        /// else
        /// {
        ///     // No matches found or error occurred
        /// }
        /// </code>
        /// </example>
        Boolean TryCreateSubgraph(out SubGraphResult result, SubGraphDefinition definition);
    }
}
