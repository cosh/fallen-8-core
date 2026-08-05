// MIT License
//
// StoredQueriesController.cs
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
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.StoredQueries;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.App.Controllers
{
    /// <summary>
    ///   Manages the stored query library: named, validated, pre-compiled query definitions the
    ///   path/subgraph endpoints can reference by name.
    /// </summary>
    /// <remarks>
    ///   The operating model (feature stored-query-library): register a vetted set of queries once,
    ///   then reference them by name from the path/subgraph endpoints. Each fragment is validated
    ///   and compiled a SINGLE time at registration (not per request), and an operator curates the
    ///   catalog, so callers reuse named, pre-approved queries instead of re-sending raw C#.
    ///
    ///   HONESTY NOTE: an invoked stored query still runs in-process with full trust, and inline
    ///   fragments remain available - dynamic code execution is always on. The library is a
    ///   reuse/curation convenience (compile once, name once), NOT a sandbox or a lockdown: it does
    ///   not disable inline code.
    /// </remarks>
    [ApiController]
    [Route("api/v{version:apiVersion}/[controller]")]
    [ApiVersion("0.1")]
    public class StoredQueriesController : ControllerBase
    {
        #region Data

        private readonly IFallen8 _fallen8;

        private readonly ILogger<StoredQueriesController> _logger;

        /// <summary>
        ///   The compile bridge used to validate-and-compile at registration. Stateless; the same
        ///   implementation is registered on the engine for load-time rehydration.
        /// </summary>
        private static readonly StoredQueryCompiler _compiler = new StoredQueryCompiler();

        #endregion

        public StoredQueriesController(ILogger<StoredQueriesController> logger, IFallen8 fallen8)
        {
            _logger = logger;
            _fallen8 = fallen8;
        }

        /// <summary>
        /// Registers a stored query: validates and compiles the specification, then publishes it under a unique name.
        /// </summary>
        /// <param name="specification">The stored query specification (name, kind, and the matching path/subGraph block)</param>
        /// <returns>A summary of the registered stored query</returns>
        /// <remarks>
        /// Exactly one of "path" / "subGraph" must be present and must match "kind". The code
        /// fragments are compiled ONCE here, with the same bounds as the inline endpoints; a
        /// compile failure rejects the registration with the compiler diagnostics. Entries are
        /// immutable: to change one, delete and re-register.
        ///
        /// SECURITY: registration compiles C# fragments with Roslyn that later execute IN-PROCESS
        /// WITH FULL TRUST. It carries the same authentication as the inline code endpoints
        /// (required whenever an API key is configured); dynamic code execution itself is always on.
        ///
        /// Sample request:
        ///
        ///     POST /storedquery
        ///     {
        ///        "name": "adults-shortest",
        ///        "kind": "Path",
        ///        "description": "age&gt;30 vertices, weight-by-distance",
        ///        "path": {
        ///          "filter": { "vertexFilter": "return (v) =&gt; v.TryGetProperty(out int age, \"age\") &amp;&amp; age &gt; 30;" },
        ///          "cost":   { "edgeCost": "return (e) =&gt; 1.0;" }
        ///        }
        ///     }
        /// </remarks>
        /// <response code="201">The stored query was compiled and registered</response>
        /// <response code="400">The specification was malformed (name/kind/block), or a fragment failed to compile (the body carries the compiler diagnostics)</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="409">A stored query with the same name already exists, or the library quota (Fallen8:StoredQueries:MaxCount) was reached</response>
        /// <response code="413">The request body exceeds the code-endpoint size limit</response>
        /// <response code="429">The sensitive-endpoint rate limit was exceeded</response>
        /// <response code="500">The registration transaction faulted with an internal error</response>
        [HttpPost("/storedquery")]
        [EnableRateLimiting(Fallen8SecurityOptions.SensitiveRateLimitPolicy)]
        [RequestSizeLimit(1_048_576)]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(StoredQuerySummaryREST), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> RegisterStoredQuery([FromBody] StoredQuerySpecification specification)
        {
            if (specification == null)
            {
                return ProblemResults.BadRequest("A stored query specification is required.");
            }

            if (!StoredQueryLibrary.IsValidName(specification.Name))
            {
                return ProblemResults.BadRequest(String.Format(
                    "'{0}' is not a valid stored query name. Names must match ^[A-Za-z0-9_-]{{1,{1}}}$.",
                    specification.Name, StoredQueryLibrary.MaxNameLength));
            }

            // Strict literal names only (Enum.TryParse would also accept numeric strings).
            StoredQueryKind kind;
            switch (specification.Kind)
            {
                case "Path":
                    kind = StoredQueryKind.Path;
                    break;
                case "SubGraph":
                    kind = StoredQueryKind.SubGraph;
                    break;
                default:
                    return ProblemResults.BadRequest(String.Format(
                        "'{0}' is not a valid stored query kind. Expected 'Path' or 'SubGraph'.", specification.Kind));
            }

            // Exactly one block, matching the declared kind.
            var blockError = ValidateBlockShape(specification, kind, out var specificationJson);
            if (blockError != null)
            {
                return ProblemResults.BadRequest(blockError);
            }

            try
            {
                // Fail fast on the request thread (the transaction re-checks on the writer thread,
                // so a TOCTOU race still resolves correctly).
                if (_fallen8.StoredQueries.TryGet(out _, specification.Name))
                {
                    return ProblemResults.Conflict(String.Format("A stored query named '{0}' already exists.", specification.Name));
                }

                if (_fallen8.StoredQueries.Count >= _fallen8.StoredQueries.MaxCount)
                {
                    return ProblemResults.Conflict(String.Format(
                        "The maximum number of stored queries ({0}) has been reached.", _fallen8.StoredQueries.MaxCount));
                }

                var definition = new StoredQueryDefinition
                {
                    Name = specification.Name,
                    Kind = kind,
                    SpecificationJson = specificationJson,
                    Description = specification.Description,
                    CreatedAt = DateTime.UtcNow
                };

                // Compile BEFORE enqueueing: validation must fail fast with diagnostics, and
                // Roslyn must never occupy the single writer thread.
                if (!_compiler.TryCompile(definition, out var artifact, out var compileError))
                {
                    return ProblemResults.BadRequest(compileError);
                }

                // Keep the constructed entry in a local: the transaction releases its own
                // reference at completion, so re-reading tx.Entry here would race that release.
                var entry = new StoredQueryEntry(definition, StoredQueryCompileState.Compiled, artifact);
                var tx = new RegisterStoredQueryTransaction { Entry = entry };
                var txInfo = _fallen8.EnqueueTransaction(tx);
                await txInfo.Completion;

                if (txInfo.TransactionState == TransactionState.RolledBack)
                {
                    return MapFailedRegistration(txInfo, specification.Name);
                }

                return Created("/storedquery/" + Uri.EscapeDataString(specification.Name),
                    StoredQuerySummaryREST.FromEntry(entry));
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error registering stored query '{0}'", specification.Name);
                return ProblemResults.InternalServerError(
                    String.Format("An unexpected error occurred while registering stored query '{0}'.", specification.Name));
            }
        }

        /// <summary>
        /// Lists all registered stored queries.
        /// </summary>
        /// <returns>Summaries of all registered stored queries</returns>
        /// <response code="200">Returns the stored query summaries</response>
        /// <response code="401">No valid credential was supplied</response>
        [HttpGet("/storedquery")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(IEnumerable<StoredQuerySummaryREST>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public IActionResult GetAllStoredQueries()
        {
            var summaries = _fallen8.StoredQueries.GetAll()
                .Select(StoredQuerySummaryREST.FromEntry)
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .ToList();

            return Ok(summaries);
        }

        /// <summary>
        /// Gets the full definition of a stored query, including its source specification.
        /// </summary>
        /// <param name="name">The stored query name</param>
        /// <returns>The stored query detail</returns>
        /// <remarks>
        /// The response includes the stored specification JSON (the registration request's
        /// path/subGraph block), which also covers manual migration between instances, and - for
        /// a Failed entry - the recompile diagnostics.
        /// </remarks>
        /// <response code="200">Returns the stored query detail</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="404">No stored query with the given name exists</response>
        [HttpGet("/storedquery/{name}")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(StoredQueryDetailREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult GetStoredQuery([FromRoute] String name)
        {
            if (!_fallen8.StoredQueries.TryGet(out var entry, name))
            {
                return ProblemResults.NotFound(String.Format("No stored query named '{0}'.", name));
            }

            return Ok(StoredQueryDetailREST.FromEntryDetail(entry));
        }

        /// <summary>
        /// Deletes (deregisters) a stored query.
        /// </summary>
        /// <param name="name">The stored query name</param>
        /// <remarks>
        /// Deletion drops the pinned compiled artifact so its collectible load context can unload
        /// once in-flight invocations finish. Removal compiles nothing; it carries only the
        /// standard authentication (the API key when one is configured).
        /// </remarks>
        /// <response code="204">The stored query was deleted</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="404">No stored query with the given name exists</response>
        /// <response code="500">The removal transaction was rolled back and did not complete</response>
        [HttpDelete("/storedquery/{name}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> DeleteStoredQuery([FromRoute] String name)
        {
            if (!_fallen8.StoredQueries.TryGet(out _, name))
            {
                return ProblemResults.NotFound(String.Format("No stored query named '{0}'.", name));
            }

            var tx = new RemoveStoredQueryTransaction { Name = name };
            var txInfo = _fallen8.EnqueueTransaction(tx);
            await txInfo.Completion;

            // A rolled-back removal must not be reported as a successful 204 (the DeleteSubGraph
            // failure-reason mapping): a concurrent removal after the up-front check is a 404,
            // any other rollback an internal 500.
            if (txInfo.TransactionState == TransactionState.RolledBack)
            {
                if (txInfo.FailureReason == TransactionFailureReason.NotFound)
                {
                    return ProblemResults.NotFound(String.Format("No stored query named '{0}'.", name));
                }

                return ProblemResults.InternalServerError(
                    String.Format("The removal of stored query '{0}' was rolled back; the operation did not complete.", name));
            }

            return NoContent();
        }

        #region private helpers

        /// <summary>
        ///   Validates the exactly-one-block-matching-the-kind contract and serializes the block
        ///   as the stored specification document. Returns an error message, or null when valid.
        /// </summary>
        private static String ValidateBlockShape(StoredQuerySpecification specification, StoredQueryKind kind,
            out String specificationJson)
        {
            specificationJson = null;

            var hasPath = specification.Path != null;
            var hasSubGraph = specification.SubGraph != null;

            if (hasPath == hasSubGraph)
            {
                return "Exactly one of 'path' / 'subGraph' must be present.";
            }

            if (kind == StoredQueryKind.Path)
            {
                if (!hasPath)
                {
                    return "Kind 'Path' requires the 'path' block.";
                }

                specificationJson = JsonSerializer.Serialize(specification.Path, AppJsonContext.Default.StoredPathQueryBlock);
                return null;
            }

            if (!hasSubGraph)
            {
                return "Kind 'SubGraph' requires the 'subGraph' block.";
            }

            specificationJson = JsonSerializer.Serialize(specification.SubGraph, AppJsonContext.Default.StoredSubGraphQueryBlock);
            return null;
        }

        /// <summary>
        ///   Maps a rolled-back registration to the correct HTTP status from the transaction's
        ///   structured <see cref="TransactionFailureReason"/> (the writer-thread re-check wins a
        ///   TOCTOU race against the controller's fail-fast checks).
        /// </summary>
        private IActionResult MapFailedRegistration(TransactionInformation txInfo, String name)
        {
            String detail;
            switch (txInfo.FailureReason)
            {
                case TransactionFailureReason.InvalidInput:
                    detail = String.Format("The stored query '{0}' was structurally invalid.", name);
                    break;
                case TransactionFailureReason.Conflict:
                    detail = String.Format("A stored query named '{0}' already exists.", name);
                    break;
                case TransactionFailureReason.QuotaExceeded:
                    detail = String.Format(
                        "Registration of stored query '{0}' was rejected because the library quota was reached.", name);
                    break;
                default:
                    // NotFound is unreachable for a registration rollback; None/InternalError (and any
                    // future unmapped reason) stay a 500 here rather than folding through the mapper.
                    if (txInfo.Error != null)
                    {
                        _logger?.LogError(txInfo.Error, "Registration of stored query '{0}' faulted and was rolled back.", name);
                    }
                    return ProblemResults.InternalServerError(
                        String.Format("Registration of stored query '{0}' failed due to an internal error.", name));
            }

            return ProblemResults.StatusCode(ProblemResults.StatusForFailureReason(txInfo.FailureReason), detail);
        }

        #endregion
    }
}
