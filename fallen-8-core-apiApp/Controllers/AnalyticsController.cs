// MIT License
//
// AnalyticsController.cs
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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.App.Services;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms;
using NoSQL.GraphDB.Core.Algorithms.Analytics;
using NoSQL.GraphDB.Core.Plugin;

namespace NoSQL.GraphDB.App.Controllers
{
    /// <summary>
    ///   Whole-graph analytics (feature graph-analytics): PageRank, weakly connected
    ///   components, label propagation communities, degree centrality and triangle counting
    ///   over the in-memory adjacency, synchronously under a wall-clock budget.
    /// </summary>
    /// <remarks>
    ///   CONSISTENCY (honest): a run is a lock-free read concurrent with the single writer -
    ///   there is no global snapshot, so the result is only exact for a quiescent graph; under
    ///   concurrent mutation it is a best-effort mixture of states. Requests are plain data
    ///   (label/edge-property scoping, numeric knobs) - no dynamic code, so these endpoints
    ///   work with EnableDynamicCodeExecution=false.
    /// </remarks>
    [ApiController]
    [Route("api/v{version:apiVersion}/[controller]")]
    [ApiVersion("0.1")]
    public class AnalyticsController : ControllerBase
    {
        /// <summary>The per-request cap on returned rows (top-K scores, partition summaries,
        /// membership pages). The full result set's delivery vehicle is write-back.</summary>
        public const Int32 MaxResultsCeiling = 10_000;

        /// <summary>The default number of returned rows.</summary>
        public const Int32 DefaultMaxResults = 100;

        /// <summary>The longest a write-back property key may be.</summary>
        public const Int32 MaxPropertyKeyLength = 256;

        private readonly IFallen8 _fallen8;
        private readonly ILogger<AnalyticsController> _logger;
        private readonly Fallen8AnalyticsOptions _options;
        private readonly AnalyticsRunGate _gate;

        public AnalyticsController(ILogger<AnalyticsController> logger, IFallen8 fallen8,
            IOptions<Fallen8AnalyticsOptions> options, AnalyticsRunGate gate)
        {
            _logger = logger;
            _fallen8 = fallen8;
            _options = options?.Value ?? new Fallen8AnalyticsOptions();
            _gate = gate ?? new AnalyticsRunGate(Options.Create(_options));
        }

        /// <summary>
        /// Lists the discovered analytics algorithm plugins
        /// </summary>
        /// <returns>Plugin name -> description</returns>
        /// <remarks>
        /// The five built-ins are PAGERANK, WCC, LABELPROPAGATION, DEGREE and TRIANGLECOUNT;
        /// third-party IGraphAnalyticsAlgorithm plugins from assimilated assemblies appear here
        /// too (the same discovery as path and subgraph algorithms).
        /// </remarks>
        /// <response code="200">The available algorithms with their descriptions</response>
        [HttpGet("/analytics/algorithms")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(Dictionary<String, String>), StatusCodes.Status200OK)]
        public IActionResult GetAvailableAlgorithms()
        {
            PluginFactory.TryGetAvailablePluginsWithDescriptions<IGraphAnalyticsAlgorithm>(out var algorithms);
            return Ok(algorithms ?? new Dictionary<String, String>());
        }

        /// <summary>
        /// Runs an analytics algorithm over the graph
        /// </summary>
        /// <param name="algorithmName">The plugin name (e.g. PAGERANK, WCC, LABELPROPAGATION, DEGREE, TRIANGLECOUNT)</param>
        /// <param name="definition">Scoping, budgets, algorithm parameters, result bound and the optional write-back</param>
        /// <returns>Run metadata plus the bounded result projection</returns>
        /// <remarks>
        /// Runs synchronously under the wall-clock budget (default 30 s). Score algorithms
        /// return the top-K vertices by score (descending, ascending id tie-break); partition
        /// algorithms return partition summaries (largest first). The FULL per-vertex result's
        /// delivery vehicle is the opt-in property write-back ("writeBack": true), which lands
        /// through chunked plugin write transactions; write-back durability is SNAPSHOT-ONLY -
        /// a WAL-only replay with no intervening save loses the written properties (re-run to
        /// restore, overwrite is idempotent).
        ///
        /// Reaching the iteration cap is a NORMAL 200 (converged=false, values usable), as is
        /// budget exhaustion after at least one completed pass (budgetExhausted=true).
        ///
        /// Sample request:
        ///
        ///     POST /analytics/PAGERANK
        ///     {
        ///        "vertexLabel": "person",
        ///        "maxResults": 10,
        ///        "parameters": { "DampingFactor": 0.85 }
        ///     }
        /// </remarks>
        /// <response code="200">The run's result (including converged=false / budgetExhausted=true partials that carry usable values)</response>
        /// <response code="400">Unknown direction/parameter values, out-of-ceiling maxIterations/maxResults/timeBudgetSeconds, or a bad write-back property key</response>
        /// <response code="404">Unknown algorithm name</response>
        /// <response code="408">The wall-clock budget exhausted with no usable result</response>
        /// <response code="429">All concurrent-run slots are taken</response>
        [HttpPost("/analytics/{algorithmName}")]
        [Consumes("application/json")]
        [ProducesResponseType(typeof(AnalyticsResultREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status408RequestTimeout)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public IActionResult RunAnalytics([FromRoute] String algorithmName,
            [FromBody] AnalyticsSpecification definition)
        {
            definition ??= new AnalyticsSpecification();

            var validationError = ValidateAndBuild(out var engineDefinition, out var maxResults, definition);
            if (validationError != null)
            {
                return validationError;
            }

            if (!AlgorithmExists(algorithmName))
            {
                return NotFound(String.Format("No analytics algorithm named '{0}'.", algorithmName));
            }

            if (!_gate.TryEnter())
            {
                return Problem429();
            }

            try
            {
                if (!_fallen8.TryRunAnalytics(out var result, algorithmName, engineDefinition))
                {
                    // The definition was validated above, so a false here is the budget dying
                    // before any usable result (or client cancellation, which no longer cares).
                    return Problem408();
                }

                var response = Project(algorithmName, result, maxResults);

                if (definition.WriteBack)
                {
                    var writeBackError = ExecuteWriteBack(out var writeBack, algorithmName, definition, engineDefinition, result);
                    if (writeBackError != null)
                    {
                        return writeBackError;
                    }
                    response.WriteBack = writeBack;
                }

                return Ok(response);
            }
            finally
            {
                _gate.Exit();
            }
        }

        /// <summary>
        /// Returns one partition's membership page from a fresh run of a partition algorithm
        /// </summary>
        /// <param name="algorithmName">A partition algorithm (WCC or LABELPROPAGATION)</param>
        /// <param name="partitionId">The partition id from a previous run's summaries</param>
        /// <param name="definition">The same run specification, plus the page offset</param>
        /// <returns>The partition's member vertex ids (ascending), paged</returns>
        /// <remarks>
        /// Analytics runs are one-shot (no job store), so the page comes from a FRESH run with
        /// the same specification - deterministic for a quiescent graph. Use offset+maxResults
        /// to page; the page ceiling is 10000 rows.
        /// </remarks>
        /// <response code="200">The membership page</response>
        /// <response code="400">Invalid specification, a negative offset, or a score algorithm</response>
        /// <response code="404">Unknown algorithm or a partition id the run did not produce</response>
        /// <response code="408">The wall-clock budget exhausted with no usable result</response>
        /// <response code="429">All concurrent-run slots are taken</response>
        [HttpPost("/analytics/{algorithmName}/partition/{partitionId}")]
        [Consumes("application/json")]
        [ProducesResponseType(typeof(PartitionMembersREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status408RequestTimeout)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public IActionResult GetPartitionMembers([FromRoute] String algorithmName,
            [FromRoute] Int32 partitionId, [FromBody] AnalyticsSpecification definition)
        {
            definition ??= new AnalyticsSpecification();

            var validationError = ValidateAndBuild(out var engineDefinition, out var maxResults, definition);
            if (validationError != null)
            {
                return validationError;
            }

            if (definition.Offset < 0)
            {
                return BadRequest("offset must be non-negative.");
            }

            if (!AlgorithmExists(algorithmName))
            {
                return NotFound(String.Format("No analytics algorithm named '{0}'.", algorithmName));
            }

            if (!_gate.TryEnter())
            {
                return Problem429();
            }

            try
            {
                if (!_fallen8.TryRunAnalytics(out var result, algorithmName, engineDefinition))
                {
                    return Problem408();
                }

                if (result.VertexPartitions.Count == 0)
                {
                    return BadRequest(String.Format(
                        "'{0}' is not a partition algorithm (or the run produced no partitions); membership paging applies to WCC/LABELPROPAGATION.",
                        algorithmName));
                }

                var members = new List<Int32>();
                foreach (var pair in result.VertexPartitions)
                {
                    if (pair.Value == partitionId)
                    {
                        members.Add(pair.Key);
                    }
                }

                if (members.Count == 0)
                {
                    return NotFound(String.Format("The run produced no partition {0}.", partitionId));
                }

                members.Sort();

                return Ok(new PartitionMembersREST
                {
                    PartitionId = partitionId,
                    Size = members.Count,
                    Offset = definition.Offset,
                    Members = members.Skip(definition.Offset).Take(maxResults).ToList()
                });
            }
            finally
            {
                _gate.Exit();
            }
        }

        #region validation & projection

        private static Boolean AlgorithmExists(String algorithmName)
        {
            return PluginFactory.TryGetAvailablePlugins<IGraphAnalyticsAlgorithm>(out var names) &&
                   names.Contains(algorithmName);
        }

        /// <summary>Validates the REST specification and builds the engine definition; null on
        /// success, a 400 otherwise.</summary>
        private IActionResult ValidateAndBuild(out GraphAnalyticsDefinition engineDefinition,
            out Int32 maxResults, AnalyticsSpecification definition)
        {
            engineDefinition = null;
            maxResults = definition.MaxResults ?? DefaultMaxResults;

            if (maxResults < 1 || maxResults > MaxResultsCeiling)
            {
                return BadRequest(String.Format("maxResults must be within [1, {0}].", MaxResultsCeiling));
            }

            if (definition.MaxIterations < 0 || definition.MaxIterations > GraphAnalyticsDefinition.MaxIterationsCeiling)
            {
                return BadRequest(String.Format("maxIterations must be within [0, {0}] (0 = algorithm default).",
                    GraphAnalyticsDefinition.MaxIterationsCeiling));
            }

            if (definition.Epsilon < 0)
            {
                return BadRequest("epsilon must be non-negative.");
            }

            Direction? direction = null;
            switch (definition.Direction)
            {
                case null:
                case "":
                    break;
                case "in":
                    direction = Direction.IncomingEdge;
                    break;
                case "out":
                    direction = Direction.OutgoingEdge;
                    break;
                case "both":
                    direction = Direction.UndirectedEdge;
                    break;
                default:
                    return BadRequest(String.Format("'{0}' is not a valid direction. Expected in, out or both.",
                        definition.Direction));
            }

            var timeBudgetSeconds = definition.TimeBudgetSeconds ?? _options.DefaultTimeBudgetSeconds;
            if (timeBudgetSeconds < 1 || timeBudgetSeconds > _options.MaxTimeBudgetSeconds)
            {
                return BadRequest(String.Format("timeBudgetSeconds must be within [1, {0}].",
                    _options.MaxTimeBudgetSeconds));
            }

            if (definition.WriteBack && definition.WriteBackPropertyKey != null &&
                (definition.WriteBackPropertyKey.Length == 0 ||
                 definition.WriteBackPropertyKey.Trim().Length == 0 ||
                 definition.WriteBackPropertyKey.Length > MaxPropertyKeyLength))
            {
                return BadRequest(String.Format("writeBackPropertyKey must be non-empty and at most {0} characters.",
                    MaxPropertyKeyLength));
            }

            if (definition.Parameters != null &&
                definition.Parameters.TryGetValue("DampingFactor", out var damping) &&
                (damping < 0d || damping > 1d))
            {
                return BadRequest("DampingFactor must be within [0, 1].");
            }

            IDictionary<String, Object> parameters = null;
            if (definition.Parameters != null && definition.Parameters.Count > 0)
            {
                parameters = new Dictionary<String, Object>(definition.Parameters.Count);
                foreach (var pair in definition.Parameters)
                {
                    parameters[pair.Key] = pair.Value;
                }
            }

            engineDefinition = new GraphAnalyticsDefinition
            {
                VertexLabel = definition.VertexLabel,
                EdgePropertyId = definition.EdgePropertyId,
                Direction = direction,
                MaxIterations = definition.MaxIterations,
                Epsilon = definition.Epsilon,
                TimeBudget = TimeSpan.FromSeconds(timeBudgetSeconds),
                CancellationToken = HttpContext?.RequestAborted ?? default,
                Parameters = parameters
            };

            return null;
        }

        private static AnalyticsResultREST Project(String algorithmName, GraphAnalyticsResult result,
            Int32 maxResults)
        {
            var response = new AnalyticsResultREST
            {
                Algorithm = algorithmName,
                Converged = result.Converged,
                IterationsRun = result.IterationsRun,
                ElapsedMs = result.Elapsed.TotalMilliseconds,
                BudgetExhausted = result.BudgetExhausted,
                VertexCount = Math.Max(result.VertexScores.Count, result.VertexPartitions.Count),
                Statistics = new Dictionary<String, Double>(result.Statistics.Count)
            };

            foreach (var pair in result.Statistics)
            {
                response.Statistics[pair.Key] = Convert.ToDouble(pair.Value,
                    System.Globalization.CultureInfo.InvariantCulture);
            }

            if (result.VertexPartitions.Count > 0)
            {
                // Partition summaries: size descending, ascending partition id as tie-break.
                var sizes = new Dictionary<Int32, Int32>();
                foreach (var pair in result.VertexPartitions)
                {
                    sizes[pair.Value] = sizes.TryGetValue(pair.Value, out var current) ? current + 1 : 1;
                }

                response.Partitions = sizes
                    .OrderByDescending(p => p.Value)
                    .ThenBy(p => p.Key)
                    .Take(maxResults)
                    .Select(p => new PartitionSummaryREST { PartitionId = p.Key, Size = p.Value })
                    .ToList();
            }
            else
            {
                // Top-K by score descending, ascending vertex id as tie-break.
                response.Results = result.VertexScores
                    .OrderByDescending(p => p.Value)
                    .ThenBy(p => p.Key)
                    .Take(maxResults)
                    .Select(p => new ScoredVertexREST { GraphElementId = p.Key, Score = p.Value })
                    .ToList();
            }

            return response;
        }

        #endregion

        #region write-back

        /// <summary>The spec §3.5 property-key convention.</summary>
        private static String ConventionPropertyKey(String algorithmName, Direction? direction)
        {
            switch (algorithmName)
            {
                case "PAGERANK":
                    return "analytics.pagerank";
                case "WCC":
                    return "analytics.wcc";
                case "LABELPROPAGATION":
                    return "analytics.community";
                case "TRIANGLECOUNT":
                    return "analytics.triangles";
                case "DEGREE":
                    switch (direction ?? Direction.UndirectedEdge)
                    {
                        case Direction.IncomingEdge:
                            return "analytics.degree.in";
                        case Direction.OutgoingEdge:
                            return "analytics.degree.out";
                        default:
                            return "analytics.degree.both";
                    }
                default:
                    // Third-party plugin: a namespaced key derived from the plugin name.
                    return "analytics." + algorithmName.ToLowerInvariant();
            }
        }

        /// <summary>The spec §3.5 value-type convention (PageRank Double, WCC/community Int32,
        /// degree UInt32, triangles Int64; third-party score plugins fall back to Double).</summary>
        private static Object ConvertScore(String algorithmName, Double score)
        {
            switch (algorithmName)
            {
                case "DEGREE":
                    return (UInt32)score;
                case "TRIANGLECOUNT":
                    return (Int64)score;
                default:
                    return score;
            }
        }

        private IActionResult ExecuteWriteBack(out WriteBackResultREST writeBack, String algorithmName,
            AnalyticsSpecification definition, GraphAnalyticsDefinition engineDefinition,
            GraphAnalyticsResult result)
        {
            writeBack = null;

            var propertyKey = String.IsNullOrWhiteSpace(definition.WriteBackPropertyKey)
                ? ConventionPropertyKey(algorithmName, engineDefinition.Direction)
                : definition.WriteBackPropertyKey;

            // Ascending-id order for determinism; the value type follows the convention table.
            List<KeyValuePair<Int32, Object>> values;
            if (result.VertexPartitions.Count > 0)
            {
                values = result.VertexPartitions
                    .OrderBy(p => p.Key)
                    .Select(p => new KeyValuePair<Int32, Object>(p.Key, p.Value))
                    .ToList();
            }
            else
            {
                values = result.VertexScores
                    .OrderBy(p => p.Key)
                    .Select(p => new KeyValuePair<Int32, Object>(p.Key, ConvertScore(algorithmName, p.Value)))
                    .ToList();
            }

            if (!AnalyticsWriteBack.TryExecute(out var verticesWritten, out var chunks, _fallen8, values, propertyKey))
            {
                _logger?.LogError(
                    "Analytics write-back for {Algorithm} under '{PropertyKey}' failed after {Chunks} applied chunks ({Written} vertices) - earlier chunks stay applied; re-run to complete (idempotent overwrite).",
                    algorithmName, propertyKey, chunks, verticesWritten);

                var problem = new ProblemDetails
                {
                    Status = StatusCodes.Status500InternalServerError,
                    Title = "Analytics write-back failed mid-way",
                    Detail = String.Format(
                        "A write-back chunk was rolled back after {0} chunks ({1} vertices) were applied. Earlier chunks stay applied (chunk-atomic, not run-atomic); re-running the write-back overwrites idempotently.",
                        chunks, verticesWritten)
                };
                return new ObjectResult(problem)
                {
                    StatusCode = problem.Status,
                    ContentTypes = { "application/problem+json" }
                };
            }

            writeBack = new WriteBackResultREST
            {
                PropertyKey = propertyKey,
                VerticesWritten = verticesWritten,
                Chunks = chunks
            };
            return null;
        }

        #endregion

        #region problem responses

        private IActionResult Problem408()
        {
            var problem = new ProblemDetails
            {
                Status = StatusCodes.Status408RequestTimeout,
                Title = "Analytics budget exhausted",
                Detail = "The wall-clock budget (or cancellation) stopped the run before any usable result. Re-run with a larger timeBudgetSeconds, a narrower scope, or against a quieter graph."
            };
            return new ObjectResult(problem)
            {
                StatusCode = problem.Status,
                ContentTypes = { "application/problem+json" }
            };
        }

        private IActionResult Problem429()
        {
            var problem = new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Analytics run slots exhausted",
                Detail = String.Format(
                    "All {0} concurrent analytics run slot(s) are taken; retry when the running computation finishes (Fallen8:Analytics:MaxConcurrentRuns).",
                    _options.MaxConcurrentRuns)
            };
            return new ObjectResult(problem)
            {
                StatusCode = problem.Status,
                ContentTypes = { "application/problem+json" }
            };
        }

        #endregion
    }
}
