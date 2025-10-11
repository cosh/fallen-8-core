// MIT License
//
// SubGraphResult.cs
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
    /// Represents the result of a subgraph query execution.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SubGraphResult encapsulates both the original query definition and the matched subgraph
    /// data. This class is returned by implementations of <see cref="ISubGraphAlgorithm.TryCreateSubgraph"/>
    /// when a subgraph query is successfully executed.
    /// </para>
    /// <para>
    /// The result contains a reference to the original <see cref="SubGraphDefinition"/> used
    /// to execute the query, which provides context for interpreting the matched subgraph.
    /// The actual matched graph data is provided as a <see cref="Fallen8"/> instance,
    /// which is a complete graph database containing only the vertices and edges that
    /// matched the specified patterns.
    /// </para>
    /// <para>
    /// This design allows consumers to inspect both what was queried for (via Definitions)
    /// and what was found (via SubGraph), facilitating debugging, analysis, and further
    /// processing of the results.
    /// </para>
    /// </remarks>
    public class SubGraphResult
    {
        /// <summary>
        /// Gets or sets the subgraph definition that was used to generate this result.
        /// </summary>
        /// <value>
        /// The <see cref="SubGraphDefinition"/> that specified the patterns and constraints
        /// for the subgraph query that produced this result.
        /// </value>
        /// <remarks>
        /// <para>
        /// This property provides access to the original query specification, which is useful for:
        /// </para>
        /// <list type="bullet">
        /// <item><description>Understanding what patterns were matched in the result</description></item>
        /// <item><description>Correlating pattern references with matched graph elements</description></item>
        /// <item><description>Debugging query results</description></item>
        /// <item><description>Logging or auditing query operations</description></item>
        /// <item><description>Reusing or modifying queries based on previous results</description></item>
        /// </list>
        /// <para>
        /// The definition includes the pattern list and query ID that can help identify
        /// and track the source of this result.
        /// </para>
        /// </remarks>
        public SubGraphDefinition Definitions
        {
            get; set;
        }

        /// <summary>
        /// Gets or sets the matched subgraph as a Fallen8 graph database instance.
        /// </summary>
        /// <value>
        /// A <see cref="Fallen8"/> instance containing all vertices and edges that matched
        /// the patterns specified in the <see cref="Definitions"/>. This is a standalone
        /// graph database containing only the matched subset of data.
        /// </value>
        /// <remarks>
        /// <para>
        /// The SubGraph property contains the actual query results as a complete, self-contained
        /// graph database. This design provides several benefits:
        /// </para>
        /// <list type="bullet">
        /// <item><description>The result can be queried, analyzed, or manipulated like any other graph</description></item>
        /// <item><description>Further graph algorithms can be applied to the result</description></item>
        /// <item><description>The subgraph can be serialized, exported, or visualized independently</description></item>
        /// <item><description>All relationships within the matched subgraph are preserved</description></item>
        /// </list>
        /// <para>
        /// Note that the SubGraph is a separate instance from the original graph database,
        /// containing copies or references to the matched elements. Modifications to the
        /// SubGraph do not affect the original graph (unless specifically implemented to do so).
        /// </para>
        /// </remarks>
        public Fallen8 SubGraph
        {
            get; set;
        }
    }
}
