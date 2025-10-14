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
    /// Subgraph algorithms operate in multiple phases:
    /// </para>
    /// <list type="number">
    /// <item>
    /// <description>
    /// <b>Graph Creation:</b> Create a new empty Fallen8 instance for the subgraph
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <b>Vertex Filtering:</b> Deep copy all vertices from the source graph that match
    /// the <see cref="SubGraphDefinition.VertexFilter"/> (or all vertices if the filter is null)
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <b>Edge Filtering:</b> Deep copy all edges from the source graph that match
    /// the <see cref="SubGraphDefinition.EdgeFilter"/> (or all edges if the filter is null).
    /// Only edges where both source and target vertices exist in the subgraph are copied.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <b>Pattern Evaluation:</b> If patterns are defined in <see cref="SubGraphDefinition.Pattern"/>,
    /// find all valid paths that match the patterns. Extract vertices and edges from these paths
    /// and remove any elements not part of valid paths. This ensures cycle-free graphs where all
    /// elements participate in pattern-matching paths.
    /// </description>
    /// </item>
    /// </list>
    /// <para>
    /// The results are returned as a <see cref="SubGraphResult"/> containing the matched subgraph
    /// along with metadata about the source graph and algorithm used.
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
        /// The <see cref="SubGraphDefinition"/> that specifies the patterns, constraints, and filters
        /// for the subgraph to create. This includes optional vertex and edge filters for initial
        /// population, and optional patterns for further refinement.
        /// </param>
        /// <returns>
        /// <c>true</c> if the subgraph was successfully created;
        /// otherwise, <c>false</c> if an error occurred or the definition was invalid.
        /// Note: An empty subgraph (no vertices) may return <c>true</c> or <c>false</c> depending on implementation.
        /// </returns>
        /// <remarks>
        /// <para>
        /// This method follows the TryParse pattern commonly used in .NET, where the return value indicates
        /// success or failure, and the actual result is returned via an out parameter. This design allows
        /// callers to easily check for success without requiring exception handling for normal "not found" cases.
        /// </para>
        /// <para>
        /// Implementations of this method should follow this execution strategy:
        /// </para>
        /// <list type="number">
        /// <item>
        /// <description>
        /// <b>Validate:</b> Validate the provided definition before executing
        /// </description>
        /// </item>
        /// <item>
        /// <description>
        /// <b>Create Graph:</b> Create a new empty Fallen8 instance for the subgraph
        /// </description>
        /// </item>
        /// <item>
        /// <description>
        /// <b>Copy Vertices:</b> Deep copy all vertices matching <see cref="SubGraphDefinition.VertexFilter"/>
        /// (or all vertices if null) from the source graph to the new subgraph. Maintain a mapping of
        /// old vertex IDs to new vertex IDs.
        /// </description>
        /// </item>
        /// <item>
        /// <description>
        /// <b>Copy Edges:</b> Deep copy all edges matching <see cref="SubGraphDefinition.EdgeFilter"/>
        /// (or all valid edges if null) from the source graph to the new subgraph. An edge is only valid
        /// if both its source and target vertices exist in the subgraph. Preserve edge property IDs.
        /// </description>
        /// </item>
        /// <item>
        /// <description>
        /// <b>Evaluate Patterns:</b> If <see cref="SubGraphDefinition.Pattern"/> is defined, find all
        /// valid paths that match the pattern sequences. Extract all vertices and edges that are part
        /// of these valid paths. Remove any vertices or edges from the subgraph that are not part of
        /// valid paths. This ensures the final subgraph contains only elements that participate in
        /// pattern-matching paths and prevents cycles by tracking visited vertices during path traversal.
        /// </description>
        /// </item>
        /// <item>
        /// <description>
        /// <b>Return Result:</b> Construct a <see cref="SubGraphResult"/> containing the new subgraph,
        /// the original definition, and metadata about the source graph and algorithm used.
        /// </description>
        /// </item>
        /// </list>
        /// <para>
        /// Performance considerations: Subgraph creation involves deep copying vertices and edges,
        /// which can be memory and time intensive for large graphs. The vertex and edge filters
        /// can significantly improve performance by reducing the initial working set before pattern
        /// matching begins. Pattern evaluation uses breadth-first traversal to avoid deep recursion
        /// and ensure all paths are explored systematically.
        /// </para>
        /// </remarks>
        /// <example>
        /// Example usage:
        /// <code>
        /// ISubGraphAlgorithm algorithm = GetAlgorithm();
        /// var definition = new SubGraphDefinition
        /// {
        ///     Name = "FindPaths",
        ///     VertexFilter = new VertexPattern
        ///     {
        ///         Label = label => label == "Person"
        ///     },
        ///     EdgeFilter = new EdgePattern
        ///     {
        ///         Label = label => label == "KNOWS",
        ///         Direction = Direction.OutgoingEdge
        ///     },
        ///     Pattern = new List&lt;APattern&gt;
        ///     {
        ///         new VertexPattern { PatternName = "start", Label = label => label == "Person" },
        ///         new EdgePattern { PatternName = "rel", Label = label => label == "KNOWS" },
        ///         new VertexPattern { PatternName = "end", Label = label => label == "Person" }
        ///     }
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
