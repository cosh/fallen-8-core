// MIT License
//
// IFallen8Read.cs
//
// Copyright (c) 2011-2026 Henning Rauch
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
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using NoSQL.GraphDB.Core.Algorithms;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.Algorithms.SubGraph;
using NoSQL.GraphDB.Core.Expression;
using NoSQL.GraphDB.Core.Index.Fulltext;
using NoSQL.GraphDB.Core.Model;

namespace NoSQL.GraphDB.Core
{
    /// <summary>
    ///   Fallen8 read interface.
    /// </summary>
    public interface IFallen8Read
    {
        /// <summary>
        ///   Gets the unique identifier for this Fallen8 instance.
        /// </summary>
        Guid Id
        {
            get;
        }

        /// <summary>
        ///   Gets the count of vertices.
        /// </summary>
        Int32 VertexCount
        {
            get;
        }

        /// <summary>
        ///   Gets the count of edges.
        /// </summary>
        Int32 EdgeCount
        {
            get;
        }

        #region Get

        /// <summary>
        ///   Gets an graph element by its identifier.
        /// </summary>
        /// <returns> <c>true</c> if something was found; otherwise, <c>false</c> . </returns>
        /// <param name='result'> The graph element. </param>
        /// <param name='id'> System wide unique identifier. </param>
        Boolean TryGetGraphElement(out AGraphElementModel result, Int32 id);

        /// <summary>
        ///   Gets an edge by its identifier.
        /// </summary>
        /// <returns> <c>true</c> if something was found; otherwise, <c>false</c> . </returns>
        /// <param name='result'> The edge. </param>
        /// <param name='id'> System wide unique identifier. </param>
        Boolean TryGetEdge(out EdgeModel result, Int32 id);

        /// <summary>
        ///   Gets a vertex by its identifier.
        /// </summary>
        /// <returns> <c>true</c> if something was found; otherwise, <c>false</c> . </returns>
        /// <param name='result'> The vertex. </param>
        /// <param name='id'> System wide unique identifier. </param>
        Boolean TryGetVertex(out VertexModel result, Int32 id);

        #endregion

        /// <summary>
        ///   Gets all vertices
        /// </summary>
        /// <param name="interestingLabel">The interesting label</param>
        /// <returns>A list of vertices</returns>
        IReadOnlyList<VertexModel> GetAllVertices(String interestingLabel = null);

        /// <summary>
        ///   Gets all edges
        /// </summary>
        /// <param name="interestingLabel">The interesting label</param>
        /// <returns>A list of edge</returns>
        IReadOnlyList<EdgeModel> GetAllEdges(String interestingLabel = null);

        /// <summary>
        ///   Gets all graph elements
        /// </summary>
        /// <param name="interestingLabel">The interesting label</param>
        /// <returns>A list of graph elements</returns>
        IReadOnlyList<AGraphElementModel> GetAllGraphElements(String interestingLabel = null);

        #region search

        /// <summary>
        ///   Scan for graph elements by a specified propertyId, literal and binary operation.
        /// </summary>
        /// <returns> <c>true</c> if something was found; otherwise, <c>false</c> . </returns>
        /// <param name='result'> The resulting graph elements. </param>
        /// <param name='propertyId'> Property identifier. </param>
        /// <param name='literal'> Literal. </param>
        /// <param name='binOp'> Binary operator. </param>
        /// <param name="interestingLabel">The interesting label</param>
        Boolean GraphScan(out List<AGraphElementModel> result, String propertyId, IComparable literal,
                          BinaryOperator binOp = BinaryOperator.Equals, String interestingLabel = null);

        /// <summary>
        ///   Scan for graph elements whose ANY property value contains the search term. Each
        ///   non-reserved property value is rendered to its invariant-culture string form and
        ///   tested with a case-insensitive <c>Contains</c>. A cold, un-indexed O(elements x
        ///   properties) discovery scan (use an index for scale); reserved embedding entries are
        ///   never matched. A blank search term matches nothing.
        /// </summary>
        /// <returns> <c>true</c> if something was found; otherwise, <c>false</c> . </returns>
        /// <param name='result'> The resulting graph elements. </param>
        /// <param name='searchTerm'> The substring to look for across every property value. </param>
        /// <param name="interestingLabel">The interesting label</param>
        Boolean GraphScanAllProperties(out List<AGraphElementModel> result, String searchTerm,
                                       String interestingLabel = null);

        /// <summary>
        ///   Scan for graph elements by a specified index identifiert, a literal and a binary operation
        /// </summary>
        /// <returns> <c>true</c> if something was found; otherwise, <c>false</c> . </returns>
        /// <param name='result'> Result. </param>
        /// <param name='indexId'> Index identifier. </param>
        /// <param name='literal'> Literal. </param>
        /// <param name='binOp'> Binary operator. </param>
        Boolean IndexScan(out IReadOnlyList<AGraphElementModel> result, String indexId, IComparable literal,
                          BinaryOperator binOp = BinaryOperator.Equals);

        /// <summary>
        ///   Scan for graph elements by a specified property range.
        /// </summary>
        /// <returns> <c>true</c> if something was found; otherwise, <c>false</c> . </returns>
        /// <param name='result'> The resulting graph elements. </param>
        /// <param name='indexId'> Index identifier. </param>
        /// <param name='leftLimit'> Left limit. </param>
        /// <param name='rightLimit'> Right limit. </param>
        /// <param name='includeLeft'> Include left. </param>
        /// <param name='includeRight'> Include right. </param>
        Boolean RangeIndexScan(out IReadOnlyList<AGraphElementModel> result, String indexId, IComparable leftLimit,
                               IComparable rightLimit, Boolean includeLeft = true, Boolean includeRight = true);

        /// <summary>
        ///   Fulltext search for graph elements by a specified query string using an index.
        /// </summary>
        /// <returns> <c>true</c> if something was found; otherwise, <c>false</c> . </returns>
        /// <param name='result'> The resulting fulltext result. </param>
        /// <param name='indexId'> Index identifier. </param>
        /// <param name='searchQuery'> GraphScan query. </param>
        Boolean FulltextIndexScan(out FulltextSearchResult result, String indexId, String searchQuery);

        /// <summary>
        ///   k-nearest-neighbour scan over a vector index (feature vector-index). Returns false
        ///   for an unknown or non-vector index or invalid input (wrong query dimension, k out of
        ///   bounds, zero-norm query under Cosine).
        /// </summary>
        Boolean VectorIndexScan(out Index.Vector.VectorSearchResult result, String indexId,
            Single[] query, Int32 k, Index.Vector.VectorSearchConstraint constraint = null);

        #endregion

        #region calculations

        /// <summary>
        ///   Calculates the shortest path using a plugin named by STRING, resolved through plugin
        ///   discovery. NOT trim-safe for that reason: a trimmed application cannot keep a type that
        ///   exists only as a name in a string. The benign outcome is that discovery finds nothing and
        ///   this returns <c>false</c>; a partially trimmed assembly can instead throw out of discovery,
        ///   which is why <see cref="Plugin.PluginFactory" /> - the one home for this - spells out the
        ///   failure modes. Prefer <see cref="TryCalculateShortestPath{T}" /> where the algorithm is
        ///   known at compile time.
        /// </summary>
        /// <param name="result"> The resulting path. </param>
        /// <param name="plugin"> The name of the algorithm. </param>
        /// <param name="definition"> The shortest path calculation parameters. </param>
        /// <returns> True if at least one path was found, otherwise false. </returns>
        [RequiresUnreferencedCode(Plugin.PluginFactory.DiscoveryIsNotTrimSafe)]
        bool TryCalculateShortestPath(
            out List<Path> result,
            string plugin,
            ShortestPathDefinition definition);

        /// <summary>
        ///   Calculates the shortest path with an algorithm named by TYPE rather than by string, so no
        ///   assembly scanning and no name map is involved. It is not reflection-free - the algorithm is
        ///   still instantiated with <c>Activator.CreateInstance(typeof(T))</c> - but it is the
        ///   TRIM-SAFE overload: <typeparamref name="T"/> is annotated
        ///   <see cref="DynamicallyAccessedMemberTypes.PublicParameterlessConstructor"/>, so a trimmer
        ///   keeps the constructor of whatever type a caller substitutes. The string overload cannot make
        ///   that promise, because its plugin is resolved from scanned assemblies.
        /// </summary>
        /// <typeparam name="T">The generic variable for the IShortestPathPlugin type</typeparam>
        /// <param name="result"> The resulting path. </param>
        /// <param name="definition"> The shortest path calculation parameters. </param>
        /// <returns> True if at least one path was found, otherwise false. </returns>
        public bool TryCalculateShortestPath<
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(
            out List<Path> result,
            ShortestPathDefinition definition)
                where T : IShortestPathAlgorithm;

        /// <summary>
        ///   Runs a whole-graph analytics algorithm plugin (feature graph-analytics), mirroring
        ///   <see cref="TryCalculateShortestPath(out List{Path}, string, ShortestPathDefinition)"/>:
        ///   resolve via PluginFactory, initialize, cache, invoke. Returns false for an unknown
        ///   plugin, an invalid definition, or a run whose budget died before any usable result.
        /// </summary>
        /// <param name="result"> The analytics result (scores or partitions + run metadata). </param>
        /// <param name="algorithmName"> The plugin name, e.g. "PAGERANK". </param>
        /// <param name="definition"> Scoping, budgets and algorithm parameters. </param>
        [RequiresUnreferencedCode(Plugin.PluginFactory.DiscoveryIsNotTrimSafe)]
        bool TryRunAnalytics(
            out Algorithms.Analytics.GraphAnalyticsResult result,
            string algorithmName,
            Algorithms.Analytics.GraphAnalyticsDefinition definition);

        /// <summary>
        ///   Invokes a runtime-registered graph function by name (feature plugin-registration): a
        ///   stored graph procedure resolved from the per-namespace plugin registry (there are no
        ///   built-in graph functions), activated fresh, and run against this graph. Returns false for
        ///   an unknown/non-compiled function or an expected function-side failure. Read-only in v1.
        /// </summary>
        /// <param name="result"> The selected vertices/edges (a view of existing elements), or null. </param>
        /// <param name="name"> The registered function name. </param>
        /// <param name="parameters"> The call-time parameter bag (may be null/empty). </param>
        bool TryInvokeGraphFunction(
            out Plugins.GraphFunctionResult result,
            string name,
            IDictionary<String, Object> parameters);

        #endregion
    }
}
