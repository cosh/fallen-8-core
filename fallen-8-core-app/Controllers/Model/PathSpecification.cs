// MIT License
//
// PathSpecification.cs
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
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   Specification for finding paths between vertices in the graph
    /// </summary>
    /// <example>
    /// {
    ///   "pathAlgorithmName": "BLS",
    ///   "maxDepth": 5,
    ///   "maxResults": 10,
    ///   "maxPathWeight": 100.0,
    ///   "filter": {
    ///     "edgePropertyFilter": "return (p) => true;",
    ///     "vertexFilter": "return (v) => true;",
    ///     "edgeFilter": "return (e) => true;"
    ///   },
    ///   "cost": {
    ///     "vertexCost": "return (v) => 1.0;",
    ///     "edgeCost": "return (e) => 1.0;"
    ///   }
    /// }
    /// </example>
    public sealed class PathSpecification : IEquatable<PathSpecification>
    {
        /// <summary>The ceiling on <see cref="TimeBudgetSeconds"/> (one hour): a request naming a
        /// larger budget is a 400, since anything beyond it is indistinguishable from unbounded.</summary>
        public const Double MaxTimeBudgetSeconds = 3_600d;

        /// <summary>
        ///   The algorithm to use for path finding.
        /// </summary>
        /// <remarks>
        ///   Two algorithms ship with the engine:
        ///   <list type="bullet">
        ///     <item>
        ///       <term>BLS</term>
        ///       <description>
        ///         Bidirectional level-synchronous search. A hop-count (unweighted) shortest path;
        ///         the <c>cost</c> block is ignored and every path's <c>totalWeight</c> is 0.
        ///       </description>
        ///     </item>
        ///     <item>
        ///       <term>DIJKSTRA</term>
        ///       <description>
        ///         Weighted single-source shortest path. Honours the <c>cost</c> block
        ///         (<c>edgeCost</c> + <c>vertexCost</c> per step, defaulting to 1 per edge) and the
        ///         <c>maxPathWeight</c> bound, so <c>totalWeight</c> reflects the real cost. With
        ///         <c>maxResults &gt; 1</c> it returns the K least-weight loop-free paths in
        ///         non-decreasing weight order (Yen's algorithm).
        ///       </description>
        ///     </item>
        ///   </list>
        /// </remarks>
        /// <example>BLS</example>
        [Required]
        [DefaultValue("BLS")]
        [JsonPropertyName("pathAlgorithmName")]
        public String PathAlgorithmName
        {
            get; set;
        } = "BLS";

        /// <summary>
        ///   The maximum number of edges in paths to consider
        /// </summary>
        /// <example>5</example>
        [Required]
        [DefaultValue((ushort)7)]
        [JsonPropertyName("maxDepth")]
        public UInt16 MaxDepth
        {
            get; set;
        } = 7;

        /// <summary>
        ///   The maximum number of paths to return in the result
        /// </summary>
        /// <remarks>
        ///   For <c>DIJKSTRA</c> this is the <c>K</c> in K-shortest paths and defaults high
        ///   (<c>65535</c>); a caller that only wants the single least-weight path should set
        ///   <c>maxResults</c> to <c>1</c> to avoid the additional cost of Yen's K-shortest search.
        /// </remarks>
        /// <example>10</example>
        [DefaultValue((ushort)65535)]
        [JsonPropertyName("maxResults")]
        public UInt16 MaxResults
        {
            get; set;
        } = UInt16.MaxValue;

        /// <summary>
        ///   The maximum allowed weight for a path to be included in results
        /// </summary>
        /// <remarks>
        ///   Omitted means UNBOUNDED: the runtime default is <c>Double.MaxValue</c>, so no weight
        ///   ceiling is applied. That is why this knob deliberately publishes NO schema default -
        ///   a published <c>100</c> would make a generated client that materialises schema defaults
        ///   prune every heavier path the server would have returned. The bound is inclusive: a path
        ///   whose cumulative weight equals <c>maxPathWeight</c> is allowed. Honoured by weighted
        ///   algorithms (<c>DIJKSTRA</c>); <c>BLS</c> ignores it.
        /// </remarks>
        /// <example>100.0</example>
        [JsonPropertyName("maxPathWeight")]
        public Double MaxPathWeight
        {
            get; set;
        } = Double.MaxValue;

        /// <summary>
        ///   Optional wall-clock budget for the traversal, in seconds.
        /// </summary>
        /// <remarks>
        ///   Omitted (the default) means UNBOUNDED, the historical behaviour. When present it must
        ///   be greater than 0 and at most <see cref="MaxTimeBudgetSeconds"/> (400 otherwise), and a
        ///   traversal that outlives it answers <c>408</c> - never an empty <c>200</c>, which would
        ///   read as "no path exists". Regardless of this value the traversal is always bound to the
        ///   request abort, so a client that gives up stops the work.
        ///
        ///   HONEST LIMIT: the budget is cooperative and is checked BETWEEN filter/cost delegate
        ///   invocations. It bounds an algorithmic blow-up (a huge <c>maxDepth</c> in a dense
        ///   component, an expensive fragment invoked a great many times) but cannot interrupt a
        ///   single fragment call that never returns - such a fragment still holds its thread. The
        ///   one home for that contract is the engine's
        ///   <c>ShortestPathDefinition.TimeBudget</c>.
        /// </remarks>
        /// <example>5.0</example>
        [JsonPropertyName("timeBudgetSeconds")]
        public Double? TimeBudgetSeconds
        {
            get; set;
        }

        /// <summary>
        ///   Filtering criteria for elements to include in path calculations
        /// </summary>
        [JsonPropertyName("filter")]
        public PathFilterSpecification Filter
        {
            get; set;
        }

        /// <summary>
        ///   Cost function specifications for weighting paths
        /// </summary>
        [JsonPropertyName("cost")]
        public PathCostSpecification Cost
        {
            get; set;
        }

        /// <summary>
        ///   The name of a registered stored query of kind <c>Path</c> to use instead of inline
        ///   <see cref="Filter"/>/<see cref="Cost"/> fragments (feature stored-query-library).
        ///   Mutually exclusive with them (400 when mixed). A stored-query request compiles
        ///   nothing (the pinned artifact runs); the numeric bounds and
        ///   <see cref="PathAlgorithmName"/> stay per-request.
        /// </summary>
        /// <example>adults-shortest</example>
        [JsonPropertyName("storedQuery")]
        public String StoredQuery
        {
            get; set;
        }

        /// <summary>
        ///   The declarative semantic block (feature element-embeddings): the query vector for
        ///   the traversal context plus optional code-free similarity filter/cost. Pure data -
        ///   it compiles no C#, and is deliberately NOT part of the compile
        ///   cache key (the context is a factory parameter, not source).
        /// </summary>
        [JsonPropertyName("semantic")]
        public SemanticTraversalSpecification Semantic
        {
            get; set;
        }

        public override Boolean Equals(Object obj)
        {
            return Equals(obj as PathSpecification);
        }

        public Boolean Equals(PathSpecification other)
        {
            return other != null &&
                   PathAlgorithmName == other.PathAlgorithmName &&
                   MaxDepth == other.MaxDepth &&
                   MaxResults == other.MaxResults &&
                   MaxPathWeight == other.MaxPathWeight &&
                   TimeBudgetSeconds == other.TimeBudgetSeconds &&
                   EqualityComparer<PathFilterSpecification>.Default.Equals(Filter, other.Filter) &&
                   EqualityComparer<PathCostSpecification>.Default.Equals(Cost, other.Cost) &&
                   StoredQuery == other.StoredQuery &&
                   EqualityComparer<SemanticTraversalSpecification>.Default.Equals(Semantic, other.Semantic);
        }

        public override Int32 GetHashCode()
        {
            // HashCode.Combine takes at most eight components, so the last two are folded first.
            return HashCode.Combine(PathAlgorithmName, MaxDepth, MaxResults, MaxPathWeight, TimeBudgetSeconds,
                Filter, Cost, HashCode.Combine(StoredQuery, Semantic));
        }
    }
}
