// MIT License
//
// SubGraphController.cs
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
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.SubGraph;
using NoSQL.GraphDB.Core.App.Controllers.Model;
using NoSQL.GraphDB.Core.App.Helper;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.App.Controllers
{
    /// <summary>
    ///   Creates and manages pattern-matched subgraphs of the graph.
    /// </summary>
    /// <remarks>
    ///   A subgraph is a standalone extract of the graph. Filters and pattern predicates
    ///   are supplied as C# code fragments and compiled at runtime, exactly like the
    ///   path-finding API. See <see cref="SubGraphSpecification"/> for the request shape.
    /// </remarks>
    [ApiController]
    [Route("api/v{version:apiVersion}/[controller]")]
    [ApiVersion("0.1")]
    public class SubGraphController : ControllerBase
    {
        #region Data

        private readonly IFallen8 _fallen8;

        private readonly ILogger<SubGraphController> _logger;

        #endregion

        /// <summary>
        ///   The authorization service used for the request-shape-aware dynamic-code capability
        ///   check on <see cref="CreateSubGraph"/> (feature stored-query-library). Null when the
        ///   controller is constructed directly (unit tests) - the hosted pipeline always supplies
        ///   it, and the pipeline-level matrix tests pin the real gate behaviour.
        /// </summary>
        private readonly IAuthorizationService _authorizationService;

        /// <summary>
        ///   The embedding provider resolving <c>semantic.queryText</c> (feature
        ///   embedding-provider). Null when constructed directly (unit tests) - queryText then
        ///   answers 503.
        /// </summary>
        private readonly Embedding.Fallen8EmbeddingProvider _embeddingProvider;

        public SubGraphController(ILogger<SubGraphController> logger, IFallen8 fallen8,
            IAuthorizationService authorizationService = null,
            Embedding.Fallen8EmbeddingProvider embeddingProvider = null)
        {
            _logger = logger;
            _fallen8 = fallen8;
            _authorizationService = authorizationService;
            _embeddingProvider = embeddingProvider;
        }

        /// <summary>
        ///   Whether a subgraph request INTRODUCES code: any non-blank inline filter fragment on
        ///   the specification itself or on any of its patterns. Only such a request requires the
        ///   dynamic-code capability (feature stored-query-library); a storedQuery reference or a
        ///   fragment-less pattern compiles no user-supplied code.
        /// </summary>
        private static bool CarriesInlineCode(SubGraphSpecification specification)
        {
            if (!String.IsNullOrWhiteSpace(specification.VertexFilter) ||
                !String.IsNullOrWhiteSpace(specification.EdgeFilter))
            {
                return true;
            }

            if (specification.Patterns != null)
            {
                foreach (var pattern in specification.Patterns)
                {
                    if (pattern != null &&
                        (!String.IsNullOrWhiteSpace(pattern.GraphElementFilter) ||
                         !String.IsNullOrWhiteSpace(pattern.VertexFilter) ||
                         !String.IsNullOrWhiteSpace(pattern.EdgeFilter) ||
                         !String.IsNullOrWhiteSpace(pattern.EdgePropertyFilter)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        ///   Imperative dynamic-code capability check: evaluates the SAME
        ///   <see cref="Security.DynamicCapabilityRequirement"/> the declarative
        ///   <c>DynamicCodePolicy</c> uses (one source of truth) and returns the same 403 shape on
        ///   denial, or null when the capability is enabled. Null-service means direct construction
        ///   (unit tests bypass the pipeline exactly as they bypassed the former endpoint-level
        ///   policy); the hosted pipeline always supplies the service. The awaited handler is
        ///   synchronous (an options check), so blocking here cannot deadlock.
        /// </summary>
        private IActionResult DenyUnlessDynamicCodeCapability()
        {
            if (_authorizationService == null)
            {
                return null;
            }

            var authorization = _authorizationService.AuthorizeAsync(User, null,
                    new Security.DynamicCapabilityRequirement(Security.DynamicCapabilityRequirement.Capability.DynamicCodeExecution))
                .GetAwaiter().GetResult();

            return authorization.Succeeded ? null : Forbid();
        }

        /// <summary>
        /// Creates and registers a new subgraph from a specification.
        /// </summary>
        /// <param name="specification">The subgraph specification (name, filters, patterns)</param>
        /// <returns>A summary of the created subgraph</returns>
        /// <remarks>
        /// Filter and pattern predicates are C# code fragments prefixed with "return", compiled
        /// at runtime. A null/empty fragment matches everything.
        ///
        /// Sample request:
        ///
        ///     PUT /subgraph
        ///     {
        ///        "name": "friends-of-alice",
        ///        "vertexFilter": "return (ge) => ge.Label == \"person\";",
        ///        "patterns": [
        ///          { "type": "Vertex", "patternName": "start", "graphElementFilter": "return (ge) => ge.Label == \"person\";" },
        ///          { "type": "Edge", "patternName": "rel", "direction": "OutgoingEdge", "edgePropertyFilter": "return (p) => p == \"knows\";" },
        ///          { "type": "Vertex", "patternName": "end", "graphElementFilter": "return (ge) => ge.Label == \"person\";" }
        ///        ]
        ///     }
        /// </remarks>
        /// <param name="fromSubGraph">Optional name of an existing subgraph to source this one from (creates a nested subgraph). When omitted, the subgraph is sourced from the whole graph.</param>
        /// <response code="201">The subgraph was created and registered. A syntactically-valid pattern that matches nothing yields a registered EMPTY subgraph (201), identically whether the source graph is empty or populated.</response>
        /// <response code="400">The specification was invalid, the pattern was structurally invalid, a filter failed to compile, storedQuery was mixed with inline fragments, or the referenced stored query has the wrong kind</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="403">The request carries inline filter/pattern fragments and dynamic code execution is disabled on this server (Fallen8:Security:EnableDynamicCodeExecution). Requests referencing a storedQuery are NOT gated by that switch.</response>
        /// <response code="404">The source subgraph named by fromSubGraph does not exist, or no stored query with the referenced name exists</response>
        /// <response code="409">A subgraph with the same name already exists, a resource quota (subgraph count or materialized-element ceiling) was exceeded, or the referenced stored query is not invocable (its recompile on load failed)</response>
        /// <response code="500">The create transaction faulted with an internal error</response>
        /// <remarks>
        /// Instead of inline fragments, the body may reference a registered stored query of kind
        /// "SubGraph" via "storedQuery" (mutually exclusive with "vertexFilter"/"edgeFilter"/
        /// "patterns"): the stored template is instantiated under this request's "name" and
        /// nothing is compiled per request. The created subgraph is self-contained - deleting the
        /// stored query later does not affect it.
        ///
        /// SEMANTIC SUBGRAPHS (feature element-embeddings): an optional "semantic" block carries
        /// a query vector (or "queryText" via the embedding provider) bound at REGISTRATION -
        /// recalculation reuses it and never embeds anything. "minScore" becomes the code-free
        /// vertex pre-filter; compiled fragments read the same vector via "context". Pure data,
        /// not gated by the dynamic-code switch; not available on stored-template invocations.
        /// Full rules: features/element-embeddings README, "Semantic traversal".
        ///
        /// SECURITY: inline filter/pattern fragments are compiled with Roslyn and executed
        /// IN-PROCESS WITH FULL TRUST - a trust boundary, not a sandbox. The dynamic-code gate is
        /// REQUEST-SHAPE-AWARE (feature stored-query-library): only a request that INTRODUCES code
        /// (any inline fragment) requires an authenticated caller AND
        /// Fallen8:Security:EnableDynamicCodeExecution=true; instantiating an operator-registered
        /// stored query does not need the switch. An invoked stored query still runs with full
        /// trust: the library narrows who can introduce code, it is not a sandbox.
        /// </remarks>
        [HttpPut("/subgraph")]
        [EnableRateLimiting(Fallen8SecurityOptions.SensitiveRateLimitPolicy)]
        [RequestSizeLimit(1_048_576)]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(SubGraphSummary), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> CreateSubGraph([FromBody] SubGraphSpecification specification, [FromQuery] String fromSubGraph = null)
        {
            if (specification == null)
            {
                return BadRequest("A subgraph specification is required.");
            }

            // semantic.queryText resolves to a vector ONCE, at registration (feature
            // embedding-provider): capability-gated, embedded on this request thread -
            // recalculation reuses the bound vector and never embeds anything.
            var queryTextError = await SemanticTraversalHelper.TryResolveQueryTextAsync(
                specification.Semantic, _embeddingProvider, _authorizationService, User, HttpContext?.RequestAborted ?? default);
            if (queryTextError != null)
            {
                return queryTextError;
            }

            // Request-shape-aware dynamic-code gate (feature stored-query-library): only a request
            // that INTRODUCES code - any inline filter/pattern fragment - requires the
            // EnableDynamicCodeExecution capability. A storedQuery reference compiles no
            // user-supplied code and passes with the switch off. Authentication itself is
            // unchanged (the fallback policy applies as on every endpoint); this replaces the
            // former endpoint-level DynamicCodePolicy.
            if (CarriesInlineCode(specification))
            {
                var denied = DenyUnlessDynamicCodeCapability();
                if (denied != null)
                {
                    return denied;
                }
            }

            if (String.IsNullOrWhiteSpace(specification.Name))
            {
                return BadRequest("A subgraph name is required.");
            }

            try
            {
                if (_fallen8.SubGraphFactory.TryGetSubGraph(out _, specification.Name))
                {
                    return Conflict(String.Format("A subgraph named '{0}' already exists.", specification.Name));
                }

                if (!String.IsNullOrWhiteSpace(fromSubGraph) && !_fallen8.SubGraphFactory.TryGetSubGraph(out _, fromSubGraph))
                {
                    return NotFound(String.Format("Source subgraph '{0}' does not exist.", fromSubGraph));
                }

                // Quota: reject up front when the subgraph count ceiling is reached.
                var quota = _fallen8.SubGraphFactory.Quota;
                if (_fallen8.SubGraphFactory.SubGraphCount >= quota.MaxSubGraphCount)
                {
                    return Conflict(String.Format(
                        "The maximum number of subgraphs ({0}) has been reached.", quota.MaxSubGraphCount));
                }

                SubGraphDefinition definition;
                String specificationJson;

                if (!String.IsNullOrWhiteSpace(specification.StoredQuery))
                {
                    // Stored-query invocation (feature stored-query-library): instantiate the
                    // stored template under the per-request instance name - nothing is compiled.
                    // Mutually exclusive with inline fragments.
                    if (!String.IsNullOrWhiteSpace(specification.VertexFilter) ||
                        !String.IsNullOrWhiteSpace(specification.EdgeFilter) ||
                        (specification.Patterns != null && specification.Patterns.Count > 0))
                    {
                        return BadRequest("'storedQuery' is mutually exclusive with inline 'vertexFilter'/'edgeFilter'/'patterns' fragments.");
                    }

                    // A stored template's artifact pins delegates materialized at REGISTRATION;
                    // rebinding them with a per-invocation context is out of scope (feature
                    // element-embeddings, spec non-goal with trigger).
                    if (specification.Semantic != null)
                    {
                        return BadRequest("'semantic' is not available on a stored-query subgraph invocation; inline the filters instead.");
                    }

                    var resolutionError = StoredQueryResolver.TryResolveSubGraphTemplate(
                        _fallen8, specification.StoredQuery, out var template, out var templateBlock);
                    if (resolutionError != null)
                    {
                        return resolutionError;
                    }

                    // The filter/pattern objects are shared with the pinned template: they are
                    // immutable after compilation, so instances can share them safely. The
                    // in-flight reference also keeps the template's collectible load context
                    // alive if the stored query is deleted concurrently.
                    definition = new SubGraphDefinition
                    {
                        Name = specification.Name,
                        AdditionalInformation = specification.AdditionalInformation,
                        VertexFilter = template.VertexFilter,
                        EdgeFilter = template.EdgeFilter,
                        Pattern = template.Pattern
                    };

                    // The persisted recipe is the MATERIALIZED full specification (template
                    // fields + instance name), so the created subgraph's durability never
                    // depends on the stored query's continued existence: deleting the stored
                    // query later does not orphan this subgraph.
                    var materialized = new SubGraphSpecification
                    {
                        Name = specification.Name,
                        AdditionalInformation = specification.AdditionalInformation,
                        VertexFilter = templateBlock.VertexFilter,
                        EdgeFilter = templateBlock.EdgeFilter,
                        Patterns = templateBlock.Patterns
                    };
                    specificationJson = JsonSerializer.Serialize(materialized, AppJsonContext.Default.SubGraphSpecification);
                }
                else
                {
                    var compileError = CodeGenerationHelper.TryGenerateSubGraphDefinition(specification, out definition);
                    if (compileError != null)
                    {
                        return BadRequest(compileError);
                    }

                    specificationJson = JsonSerializer.Serialize(specification, AppJsonContext.Default.SubGraphSpecification);
                }

                // Pass the opaque specification text on the transaction so it attaches a persistable
                // recipe on success (built from the created result). Doing it in the transaction -
                // rather than here, after WaitUntilFinished - means the recipe is present when the
                // write-ahead log serializes the committed transaction on the writer thread, so
                // subgraphs are durable across a WAL replay, not just a Save/Load. The recipe fields
                // are identical to what this controller built before.
                var tx = new CreateSubGraphTransaction
                {
                    Definition = definition,
                    SourceSubGraphName = fromSubGraph,
                    SpecificationJson = specificationJson
                };

                // Feature observability: the algorithm-run span (tags: the plugin name and the
                // created subgraph's element counts - never the user's name or filter text).
                using var runSpan = Diagnostics.AppDiagnostics.Source.StartActivity("fallen8.subgraph.run");

                var txInfo = _fallen8.EnqueueTransaction(tx);
                txInfo.WaitUntilFinished();

                if (tx.SubGraphCreated != null)
                {
                    runSpan?.SetTag("algorithm", tx.SubGraphCreated.AlgorithmPluginName);
                    runSpan?.SetTag("result.vertices", tx.SubGraphCreated.SubGraph?.VertexCount ?? 0);
                    runSpan?.SetTag("result.edges", tx.SubGraphCreated.SubGraph?.EdgeCount ?? 0);
                }

                if (tx.SubGraphCreated == null)
                {
                    // The create produced no subgraph. The worker maps BOTH a thrown exception AND a
                    // clean TryExecute()==false to RolledBack; the recorded TransactionFailureReason
                    // classifies WHY, so a client-caused rollback (structurally-invalid pattern,
                    // quota breach, missing source, name conflict) maps to the right 4xx and only a
                    // genuine internal fault maps to 500. A syntactically-valid pattern that matches
                    // nothing is NOT a failure here - it yields a registered EMPTY subgraph (201).
                    return MapFailedSubGraphCreate(txInfo, specification.Name);
                }

                var summary = SubGraphSummary.FromResult(tx.SubGraphCreated,
                    _fallen8.SubGraphFactory.CanRecalculateSubGraph(specification.Name));

                return Created("/subgraph/" + Uri.EscapeDataString(specification.Name), summary);
            }
            catch (Exception ex)
            {
                // Compilation/plugin infrastructure failures should not surface as an
                // unhandled 500 with a stack trace; log and return a clean error.
                _logger?.LogError(ex, "Error creating subgraph '{0}'", specification.Name);
                return StatusCode(StatusCodes.Status500InternalServerError,
                    String.Format("An unexpected error occurred while creating subgraph '{0}'.", specification.Name));
            }
        }

        /// <summary>
        /// Lists the names of all registered subgraphs.
        /// </summary>
        /// <returns>The registered subgraph names</returns>
        /// <response code="200">Returns the list of subgraph names</response>
        [HttpGet("/subgraph")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(IEnumerable<String>), StatusCodes.Status200OK)]
        public IActionResult GetAllSubGraphNames()
        {
            return Ok(_fallen8.SubGraphFactory.GetAllSubGraphNames().ToList());
        }

        /// <summary>
        /// Gets a summary (metadata and element counts) of a registered subgraph.
        /// </summary>
        /// <param name="name">The subgraph name</param>
        /// <returns>The subgraph summary</returns>
        /// <response code="200">Returns the subgraph summary</response>
        /// <response code="404">No subgraph with the given name exists</response>
        [HttpGet("/subgraph/{name}")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(SubGraphSummary), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult GetSubGraph([FromRoute] String name)
        {
            if (!_fallen8.SubGraphFactory.TryGetSubGraph(out SubGraphResult result, name))
            {
                return NotFound(String.Format("No subgraph named '{0}'.", name));
            }

            return Ok(SubGraphSummary.FromResult(result, _fallen8.SubGraphFactory.CanRecalculateSubGraph(name)));
        }

        /// <summary>
        /// Gets the extracted contents (vertices and edges) of a registered subgraph.
        /// </summary>
        /// <param name="name">The subgraph name</param>
        /// <param name="maxElements">Maximum number of vertices and edges to return (default 1000)</param>
        /// <returns>The subgraph's vertices and edges</returns>
        /// <response code="200">Returns the subgraph contents</response>
        /// <response code="404">No subgraph with the given name exists</response>
        [HttpGet("/subgraph/{name}/graph")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(Graph), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult GetSubGraphContents([FromRoute] String name, [FromQuery] int maxElements = 1000)
        {
            if (!_fallen8.SubGraphFactory.TryGetSubGraph(out SubGraphResult result, name))
            {
                return NotFound(String.Format("No subgraph named '{0}'.", name));
            }

            var graph = new Graph
            {
                Edges = result.SubGraph.GetAllEdges().Take(maxElements).Select(e => new Edge(e)).ToList(),
                Vertices = result.SubGraph.GetAllVertices().Take(maxElements).Select(v => new Vertex(v)).ToList()
            };

            return Ok(graph);
        }

        /// <summary>
        /// Recalculates a subgraph against the current state of its source graph.
        /// </summary>
        /// <param name="name">The subgraph name</param>
        /// <returns>A summary of the recalculated subgraph</returns>
        /// <remarks>
        /// Only subgraphs created by an algorithm (with a stored source and plugin name)
        /// can be recalculated; manually registered subgraphs cannot.
        /// </remarks>
        /// <response code="200">Returns the recalculated subgraph summary</response>
        /// <response code="404">No subgraph with the given name exists</response>
        /// <response code="409">The subgraph exists but cannot be recalculated</response>
        [HttpPost("/subgraph/{name}/recalculate")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(SubGraphSummary), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public IActionResult RecalculateSubGraph([FromRoute] String name)
        {
            if (!_fallen8.SubGraphFactory.TryGetSubGraph(out _, name))
            {
                return NotFound(String.Format("No subgraph named '{0}'.", name));
            }

            if (!_fallen8.SubGraphFactory.TryRecalculateSubGraph(name))
            {
                return Conflict(String.Format(
                    "Subgraph '{0}' cannot be recalculated (missing source graph or algorithm plugin).", name));
            }

            if (!_fallen8.SubGraphFactory.TryGetSubGraph(out SubGraphResult updated, name))
            {
                // Removed concurrently between recalculation and re-fetch.
                return NotFound(String.Format("No subgraph named '{0}'.", name));
            }

            return Ok(SubGraphSummary.FromResult(updated, _fallen8.SubGraphFactory.CanRecalculateSubGraph(name)));
        }

        /// <summary>
        /// Deregisters (deletes) a subgraph.
        /// </summary>
        /// <param name="name">The subgraph name</param>
        /// <response code="204">The subgraph was deleted</response>
        /// <response code="404">No subgraph with the given name exists</response>
        /// <response code="500">The removal transaction was rolled back and did not complete</response>
        [HttpDelete("/subgraph/{name}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public IActionResult DeleteSubGraph([FromRoute] String name)
        {
            if (!_fallen8.SubGraphFactory.TryGetSubGraph(out _, name))
            {
                return NotFound(String.Format("No subgraph named '{0}'.", name));
            }

            var tx = new RemoveSubGraphTransaction { SubGraphName = name };
            var txInfo = _fallen8.EnqueueTransaction(tx);
            txInfo.WaitUntilFinished();

            // RemoveSubGraphTransaction.TryExecute returns false (→ terminal RolledBack) on its
            // failure paths. A rolled-back removal must not be reported to the client as a
            // successful 204 (correctness-fixes B6). The recorded reason classifies it: a subgraph
            // that no longer exists (removed concurrently after the up-front check) is a 404, any
            // other rollback an internal 500.
            if (txInfo.TransactionState == TransactionState.RolledBack)
            {
                if (txInfo.FailureReason == TransactionFailureReason.NotFound)
                {
                    return NotFound(String.Format("No subgraph named '{0}'.", name));
                }

                return StatusCode(StatusCodes.Status500InternalServerError,
                    String.Format("The removal of subgraph '{0}' was rolled back; the operation did not complete.", name));
            }

            return NoContent();
        }

        /// <summary>
        ///   Maps a failed (no-result) subgraph create to the correct HTTP status from the
        ///   transaction's structured <see cref="TransactionFailureReason"/>: a structurally-invalid
        ///   specification/pattern to 400, a missing source to 404, a resource-quota breach or a
        ///   name conflict to 409, and any genuine internal fault to 500.
        /// </summary>
        private IActionResult MapFailedSubGraphCreate(TransactionInformation txInfo, String name)
        {
            switch (txInfo.FailureReason)
            {
                case TransactionFailureReason.InvalidInput:
                    return BadRequest(String.Format(
                        "No valid subgraph was produced for '{0}': the pattern or specification was structurally invalid.", name));

                case TransactionFailureReason.NotFound:
                    return NotFound(String.Format(
                        "A source graph or subgraph required to create '{0}' does not exist.", name));

                case TransactionFailureReason.QuotaExceeded:
                    return Conflict(String.Format(
                        "Creation of subgraph '{0}' was rejected because a resource quota was exceeded.", name));

                case TransactionFailureReason.Conflict:
                    return Conflict(String.Format("A subgraph named '{0}' already exists.", name));

                default:
                    if (txInfo.Error != null)
                    {
                        _logger?.LogError(txInfo.Error, "Creation of subgraph '{0}' faulted and was rolled back.", name);
                    }
                    return StatusCode(StatusCodes.Status500InternalServerError,
                        String.Format("Creation of subgraph '{0}' failed due to an internal error.", name));
            }
        }
    }
}
