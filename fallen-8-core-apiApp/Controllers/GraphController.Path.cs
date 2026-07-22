// MIT License
//
// GraphController.Path.cs
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
        /// <response code="403">The request carries inline filter/cost fragments and dynamic code execution is disabled on this server (Fallen8:Security:EnableDynamicCodeExecution). Requests referencing a storedQuery - or carrying no fragments at all - are NOT gated by that switch.</response>
        /// <response code="404">Source or target vertex not found, or no stored query with the referenced name exists</response>
        /// <response code="409">The referenced stored query is not invocable (its recompile on load failed - see its diagnostics via GET /storedquery/{name})</response>
        /// <response code="413">The request body exceeds the code-endpoint size limit</response>
        /// <response code="429">The sensitive-endpoint rate limit was exceeded</response>
        /// <remarks>
        /// Instead of inline fragments, the body may reference a registered stored query of kind
        /// "Path" via "storedQuery" (mutually exclusive with "filter"/"cost"): the pre-compiled
        /// artifact is used and nothing is compiled per request. The numeric bounds and
        /// "pathAlgorithmName" stay per-request either way.
        ///
        /// SEMANTIC TRAVERSAL (feature element-embeddings): an optional "semantic" block carries
        /// a query vector (or, with the embedding provider enabled, a "queryText" embedded once,
        /// up front) plus code-free similarity options - "minScore" filters vertices by
        /// similarity against their named element embedding, "costBySimilarity" weights a
        /// DIJKSTRA search by it. The block is pure data (not gated by the dynamic-code switch);
        /// compiled fragments and stored queries read the same vector via the "context"
        /// parameter. Example: { "semantic": { "queryVector": [0.1, 0.2], "minScore": 0.7 } }.
        /// Full rules: features/element-embeddings README, "Semantic traversal".
        ///
        /// SECURITY: inline filter/cost fragments are compiled with Roslyn and executed IN-PROCESS
        /// WITH FULL TRUST. This endpoint is a trust boundary, not a sandbox: anyone permitted to
        /// introduce code is trusted as the server process. The dynamic-code gate is
        /// REQUEST-SHAPE-AWARE (feature stored-query-library): only a request that INTRODUCES code
        /// (any inline fragment) requires Fallen8:Security:EnableDynamicCodeExecution=true; invoking
        /// an operator-registered stored query - or a filterless search, which compiles no
        /// user-supplied code - does not. An invoked stored query still runs with full trust: the
        /// library narrows who can introduce code, it is not a sandbox.
        /// </remarks>
        [HttpPost("/path/{from}/to/{to}")]
        [EnableRateLimiting(Fallen8SecurityOptions.SensitiveRateLimitPolicy)]
        [RequestSizeLimit(1_048_576)]
        [Produces("application/json")]
        [Consumes("application/json")]
        [ProducesResponseType(typeof(List<PathREST>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
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

                // semantic.queryText resolves to a vector ONCE, before anything else runs
                // (feature embedding-provider): capability-gated, embedded on this request
                // thread - the traversal itself never sees text.
                var queryTextError = await SemanticTraversalHelper.TryResolveQueryTextAsync(
                    definition.Semantic, _embeddingProvider, _authorizationService, User, HttpContext?.RequestAborted ?? default);
                if (queryTextError != null)
                {
                    return queryTextError;
                }

                // Request-shape-aware dynamic-code gate (feature stored-query-library): only a
                // request that INTRODUCES code - any inline filter/cost fragment - requires the
                // EnableDynamicCodeExecution capability. A storedQuery reference or a filterless
                // request compiles no user-supplied code and passes with the switch off.
                // Authentication itself is unchanged (the fallback policy applies as on every
                // endpoint); this replaces the former endpoint-level DynamicCodePolicy, which
                // gated the whole endpoint regardless of request shape.
                if (CarriesInlineCode(definition) &&
                    Security.DynamicCodeCapabilityGate.IsDenied(_authorizationService, User))
                {
                    return Forbid();
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
                        return BadRequest("'storedQuery' is mutually exclusive with inline 'filter'/'cost' fragments.");
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
                        return BadRequest(compilerMessage);
                    }
                }
                // On a cache hit, TryGetTraverser already set `traverser`.

                if (traverser != null)
                {
                    // Declarative semantic block (feature element-embeddings): validates and
                    // builds the traversal context (the query vector, embedded once, up front)
                    // plus the optional code-free filter/cost closures. Pure data - never gated
                    // by the dynamic-code capability.
                    var semanticError = SemanticTraversalHelper.TryBuild(definition.Semantic, allowCost: true, out var semantic);
                    if (semanticError != null)
                    {
                        return BadRequest(semanticError);
                    }

                    // The context reaches every compiled fragment through the factory calls; the
                    // declarative closures fill EMPTY slots only (one owner per delegate slot).
                    var vertexFilter = traverser.VertexFilter(semantic.Context);
                    if (semantic.VertexFilter != null && vertexFilter != null)
                    {
                        return BadRequest("semantic.minScore/costBySimilarity and a vertex filter fragment own the same delegate slot; use one.");
                    }

                    var vertexCost = traverser.VertexCost(semantic.Context);
                    if (semantic.VertexCost != null && vertexCost != null)
                    {
                        return BadRequest("semantic.costBySimilarity and a vertex cost fragment own the same delegate slot; use one.");
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
                        VertexCost = vertexCost ?? semantic.VertexCost
                    };

                    // Feature observability: the algorithm-run span. The algorithm tag is set
                    // only AFTER the plugin resolved (TryCalculateShortestPath returned true),
                    // so the span never carries an arbitrary unresolved user string.
                    var algorithmName = definition.PathAlgorithmName ?? "BLS"; // Default to BLS if not specified
                    using var searchSpan = Diagnostics.AppDiagnostics.Source.StartActivity("fallen8.path.search");

                    List<Core.Algorithms.Path.Path> paths;
                    if (_fallen8.TryCalculateShortestPath(
                        out paths,
                        algorithmName,
                        pathDefinition))
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
                // Log the exception but don't let it propagate
                _logger?.LogError(ex, "Error calculating path between vertices {0} and {1}", from, to);
            }

            return result; // Always return the initialized list, never null
        }
    }
}
