// MIT License
//
// GraphController.Path.cs
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
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.App.Helper;
using System.Threading.Tasks;

namespace NoSQL.GraphDB.App.Controllers
{
    public partial class GraphController
    {
        /// <summary>
        /// Calculates the shortest path between two vertices in the graph
        /// </summary>
        /// <param name="from">The ID of the source vertex</param>
        /// <param name="to">The ID of the target vertex</param>
        /// <param name="definition">Path specification with algorithm, depth, filters and other constraints</param>
        /// <returns>A list of paths between the vertices</returns>
        /// <remarks>
        /// The path specification allows for dynamic filtering and cost calculation using compiled C# code fragments.
        ///
        /// Select the algorithm via "pathAlgorithmName". "BLS" is a hop-count (unweighted) shortest
        /// path that ignores the "cost" block ("totalWeight" is 0), whereas "DIJKSTRA" is a weighted
        /// shortest path that honours the "cost" block and "maxPathWeight" (and returns the K
        /// least-weight loop-free paths when "maxResults" &gt; 1).
        ///
        /// IMPORTANT: Filter and cost properties must contain valid C# lambda expressions prefixed with a "return" statement.
        /// These are compiled at runtime into delegate methods.
        ///
        /// Correct format for filter expressions:
        /// - "return (parameter) => boolean_expression;"
        ///
        /// Correct format for cost expressions:
        /// - "return (parameter) => numeric_expression;"
        ///
        /// Instead of inline fragments, the body may reference a registered stored query of kind
        /// "Path" via "storedQuery" (mutually exclusive with "filter"/"cost"): the pre-compiled
        /// artifact is used and nothing is compiled per request. The numeric bounds and
        /// "pathAlgorithmName" stay per-request either way.
        ///
        /// SEMANTIC TRAVERSAL (feature element-embeddings): an optional "semantic" block carries
        /// a query vector (or, with the embedding provider enabled, a "queryText" embedded once,
        /// up front) plus code-free similarity options - "minScore" filters vertices by
        /// similarity against their named element embedding, "costBySimilarity" weights a
        /// DIJKSTRA search by it. The block is pure data (it compiles no C#);
        /// compiled fragments and stored queries read the same vector via the "context"
        /// parameter. Example: { "semantic": { "queryVector": [0.1, 0.2], "minScore": 0.7 } }.
        /// Full rules: features/element-embeddings README, "Semantic traversal".
        ///
        /// SECURITY: inline filter/cost fragments are compiled with Roslyn and executed IN-PROCESS
        /// WITH FULL TRUST. This endpoint is a trust boundary, not a sandbox: anyone permitted to
        /// introduce code is trusted as the server process. Dynamic code execution is ALWAYS ON -
        /// there is no switch to disable it - so authentication (required whenever an API key is
        /// configured) is the boundary. Invoking an operator-registered stored query narrows WHO can
        /// introduce code, but the invoked query still runs with full trust; it is not a sandbox.
        ///
        /// EXECUTION BUDGET: the optional "timeBudgetSeconds" bounds the traversal (omitted =
        /// unbounded, exactly as before), and the traversal is always bound to the request abort, so
        /// a client that gives up stops the work. Exhaustion answers 408. The budget is COOPERATIVE -
        /// it is checked between filter/cost invocations - so it contains an algorithmic blow-up but
        /// cannot interrupt a single fragment call that never returns.
        ///
        /// Sample request:
        ///
        ///     POST /path/1/to/5
        ///     {
        ///        "pathAlgorithmName": "BLS",
        ///        "maxDepth": 5,
        ///        "maxPathWeight": 100.0,
        ///        "maxResults": 10,
        ///        "filter": {
        ///          "vertexFilter": "return (v) => v.Label == \"Person\";",
        ///          "edgeFilter": "return (e) => e.Label == \"friendship\";",
        ///          "edgePropertyFilter": "return (p) => p == \"knows\";"
        ///        },
        ///        "cost": {
        ///          "vertexCost": "return (v) => v.TryGetProperty(out var age, \"age\") ? (double)age : 1.0;",
        ///          "edgeCost": "return (e) => e.TryGetProperty(out var weight, \"weight\") ? (double)weight : 1.0;"
        ///        }
        ///     }
        /// </remarks>
        /// <response code="200">Returns the found paths between the vertices</response>
        /// <response code="400">Invalid path specification, a fragment failed to compile, storedQuery was mixed with inline fragments, or the referenced stored query has the wrong kind</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="404">No stored query with the referenced name exists (a missing source/target vertex is not a 404: it yields a 200 with an empty path list)</response>
        /// <response code="408">The execution budget ("timeBudgetSeconds") or the client's own cancellation stopped the traversal before it finished; no result is reported, because an empty 200 would read as "no path exists"</response>
        /// <response code="409">The referenced stored query is not invocable (its recompile on load failed - see its diagnostics via GET /storedquery/{name})</response>
        /// <response code="413">The request body exceeds the code-endpoint size limit</response>
        /// <response code="429">The sensitive-endpoint rate limit was exceeded</response>
        /// <response code="500">An unexpected runtime fault occurred while calculating the path (e.g. a compiled filter/cost fragment threw)</response>
        [HttpPost("/path/{from}/to/{to}")]
        [EnableRateLimiting(Fallen8SecurityOptions.SensitiveRateLimitPolicy)]
        [RequestSizeLimit(1_048_576)]
        [Produces("application/json")]
        [Consumes("application/json")]
        [ProducesResponseType(typeof(List<PathREST>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status408RequestTimeout)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<List<PathREST>>> CalculateShortestPath([FromRoute] Int32 from, [FromRoute] Int32 to, [FromBody] PathSpecification definition)
        {
            // Always initialize with empty list to avoid returning null
            List<PathREST> result = new List<PathREST>();

            try
            {
                if (definition == null)
                {
                    definition = new PathSpecification();
                }

                // The optional execution budget is plain request data, so it is validated before
                // anything is embedded or compiled.
                if (definition.TimeBudgetSeconds.HasValue)
                {
                    var budgetSeconds = definition.TimeBudgetSeconds.Value;
                    if (Double.IsNaN(budgetSeconds) || budgetSeconds <= 0d ||
                        budgetSeconds > PathSpecification.MaxTimeBudgetSeconds)
                    {
                        return ProblemResults.BadRequest(String.Format(
                            "timeBudgetSeconds must be greater than 0 and at most {0}; omit it for an unbounded traversal.",
                            PathSpecification.MaxTimeBudgetSeconds));
                    }
                }

                // semantic.queryText resolves to a vector ONCE, before anything else runs
                // (feature embedding-provider): capability-gated, embedded on this request
                // thread - the traversal itself never sees text.
                var queryTextError = await SemanticTraversalHelper.TryResolveQueryTextAsync(
                    definition.Semantic, _embeddingProvider, _authorizationService, User, HttpContext?.RequestAborted ?? default);
                if (queryTextError != null)
                {
                    return queryTextError;
                }

                // Special case - when MaxDepth is 0, no paths can be found
                if (definition.MaxDepth <= 0)
                {
                    return result;
                }

                IPathTraverser traverser = null;

                if (!String.IsNullOrWhiteSpace(definition.StoredQuery))
                {
                    // Stored-query invocation (feature stored-query-library): resolve the name to
                    // its pinned, pre-compiled traverser - nothing is compiled and the inline
                    // caches are never consulted. Mutually exclusive with inline fragments; the
                    // trigger is actual code (a non-blank fragment), matching the capability gate
                    // and the /subgraph endpoint's semantics.
                    if (CarriesInlineCode(definition))
                    {
                        return ProblemResults.BadRequest("'storedQuery' is mutually exclusive with inline 'filter'/'cost' fragments.");
                    }

                    var resolutionError = StoredQueryResolver.TryResolvePathTraverser(_fallen8, definition.StoredQuery, out traverser);
                    if (resolutionError != null)
                    {
                        // The resolver returns concrete ActionResult subclasses (404/400/409).
                        return (ActionResult)resolutionError;
                    }
                }
                // Cache lookup keys on (Filter, Cost) only (feature codegen-cache-keying), so requests
                // that differ solely in a numeric bound / algorithm name reuse one compiled traverser.
                else if (!_cache.TryGetTraverser(definition, out traverser))
                {
                    //Traverser was not cached
                    var compilerMessage = CodeGenerationHelper.GeneratePathTraverser(out traverser, definition);

                    if (traverser != null)
                    {
                        _cache.AddTraverser(definition, traverser);
                    }
                    else
                    {
                        // A filter/cost fragment failed to compile: this is a client-caused, malformed
                        // request, so surface it as a 400 with the Roslyn diagnostics rather than a
                        // silent 200-empty (feature path-filter-arity-fix; the already-declared
                        // ProducesResponseType(400) is now reachable). A SUCCESSFUL compile that simply
                        // finds no path still returns 200 with []; only a compile failure is a 400.
                        _logger?.LogError(compilerMessage);
                        return ProblemResults.BadRequest(compilerMessage);
                    }
                }
                // On a cache hit, TryGetTraverser already set `traverser`.

                if (traverser != null)
                {
                    // Declarative semantic block (feature element-embeddings): validates and
                    // builds the traversal context (the query vector, embedded once, up front)
                    // plus the optional code-free filter/cost closures. Pure data - it compiles
                    // no C#.
                    var semanticError = SemanticTraversalHelper.TryBuild(definition.Semantic, allowCost: true, out var semantic);
                    if (semanticError != null)
                    {
                        return ProblemResults.BadRequest(semanticError);
                    }

                    // The context reaches every compiled fragment through the factory calls; the
                    // declarative closures fill EMPTY slots only (one owner per delegate slot).
                    var vertexFilter = traverser.VertexFilter(semantic.Context);
                    if (semantic.VertexFilter != null && vertexFilter != null)
                    {
                        return ProblemResults.BadRequest("semantic.minScore/costBySimilarity and a vertex filter fragment own the same delegate slot; use one.");
                    }

                    var vertexCost = traverser.VertexCost(semantic.Context);
                    if (semantic.VertexCost != null && vertexCost != null)
                    {
                        return ProblemResults.BadRequest("semantic.costBySimilarity and a vertex cost fragment own the same delegate slot; use one.");
                    }

                    var pathDefinition = new ShortestPathDefinition
                    {
                        SourceVertexId = from,
                        DestinationVertexId = to,
                        MaxDepth = definition.MaxDepth,
                        MaxPathWeight = definition.MaxPathWeight,
                        MaxResults = definition.MaxResults,
                        EdgePropertyFilter = traverser.EdgePropertyFilter(semantic.Context),
                        VertexFilter = vertexFilter ?? semantic.VertexFilter,
                        EdgeFilter = traverser.EdgeFilter(semantic.Context),
                        EdgeCost = traverser.EdgeCost(semantic.Context),
                        VertexCost = vertexCost ?? semantic.VertexCost,

                        // Cooperative execution budget: the caller's optional deadline plus the
                        // request abort, so a client that gives up stops the traversal. No deadline
                        // and no abortable token means unbounded, exactly as before. What the budget
                        // can and cannot do is documented on ShortestPathDefinition.TimeBudget.
                        TimeBudget = definition.TimeBudgetSeconds.HasValue
                            ? TimeSpan.FromSeconds(definition.TimeBudgetSeconds.Value)
                            : TimeSpan.Zero,
                        CancellationToken = HttpContext?.RequestAborted ?? default
                    };

                    // Feature observability: the algorithm-run span. The algorithm tag is set
                    // only AFTER the plugin resolved (TryCalculateShortestPath returned true),
                    // so the span never carries an arbitrary unresolved user string.
                    var algorithmName = definition.PathAlgorithmName ?? "BLS"; // Default to BLS if not specified
                    using var searchSpan = Diagnostics.AppDiagnostics.Source.StartActivity("fallen8.path.search");

                    List<Core.Algorithms.Path.Path> paths;
                    var found = _fallen8.TryCalculateShortestPath(
                        out paths,
                        algorithmName,
                        pathDefinition);

                    if (pathDefinition.BudgetExhausted)
                    {
                        // The deadline or the client's abort stopped the traversal, so the search
                        // never finished: an empty 200 would claim "no path exists". Mirrors the
                        // 408 that /analytics answers for its own budget.
                        searchSpan?.SetTag("budget.exhausted", true);
                        return ProblemResults.Create(StatusCodes.Status408RequestTimeout,
                            "Path budget exhausted",
                            "The execution budget (or the client's cancellation) stopped the path search before it finished, so no result is reported. Retry with a larger timeBudgetSeconds, a smaller maxDepth/maxResults, or cheaper filter/cost fragments. Note that the budget is checked BETWEEN filter/cost invocations: a fragment that never returns cannot be interrupted.");
                    }

                    if (found)
                    {
                        searchSpan?.SetTag("algorithm", algorithmName);
                        if (paths != null && paths.Count > 0)
                        {
                            searchSpan?.SetTag("result.count", paths.Count);
                            return new List<PathREST>(paths.Select(aPath => new PathREST(aPath)));
                        }
                    }

                    searchSpan?.SetTag("result.count", 0);
                }
            }
            catch (Exception ex)
            {
                // A runtime fault while executing the traversal or a compiled filter/cost fragment
                // must NOT be masked as a 200-empty "no path" result (feature api-error-contract E5):
                // an empty 200 is indistinguishable from a genuine no-path and misleads REST clients
                // and the MCP f8_paths tool. A compile failure is already a 400 (handled above) and a
                // genuine no-path is still a 200 with []; only an unexpected runtime fault reaches
                // here, and it is surfaced as a real 500 (mirroring /subgraph).
                _logger?.LogError(ex, "Error calculating path between vertices {0} and {1}", from, to);
                return ProblemResults.InternalServerError(
                    String.Format("An unexpected error occurred while calculating a path between vertices {0} and {1}.", from, to));
            }

            return result; // Always return the initialized list, never null
        }
    }
}
