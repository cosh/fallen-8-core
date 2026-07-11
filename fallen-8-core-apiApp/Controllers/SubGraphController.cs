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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.SubGraph;
using NoSQL.GraphDB.Core.App.Controllers.Model;
using NoSQL.GraphDB.Core.App.Helper;
using NoSQL.GraphDB.Core.SubGraph;
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

        public SubGraphController(ILogger<SubGraphController> logger, IFallen8 fallen8)
        {
            _logger = logger;
            _fallen8 = fallen8;
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
        /// <response code="201">The subgraph was created and registered</response>
        /// <response code="400">The specification was invalid or a filter failed to compile</response>
        /// <response code="404">The source subgraph named by fromSubGraph does not exist</response>
        /// <response code="409">A subgraph with the same name already exists</response>
        [HttpPut("/subgraph")]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(SubGraphSummary), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code", Justification = "Trimming is disabled for this application; the specification type is a simple DTO")]
        public IActionResult CreateSubGraph([FromBody] SubGraphSpecification specification, [FromQuery] String fromSubGraph = null)
        {
            if (specification == null)
            {
                return BadRequest("A subgraph specification is required.");
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

                var compileError = CodeGenerationHelper.TryGenerateSubGraphDefinition(specification, out SubGraphDefinition definition);
                if (compileError != null)
                {
                    return BadRequest(compileError);
                }

                var tx = new CreateSubGraphTransaction { Definition = definition, SourceSubGraphName = fromSubGraph };
                _fallen8.EnqueueTransaction(tx).WaitUntilFinished();

                if (tx.SubGraphCreated == null)
                {
                    return BadRequest(String.Format(
                        "Failed to create subgraph '{0}'. The pattern sequence may be invalid.", specification.Name));
                }

                // Attach a recipe so the subgraph can be persisted and rebuilt on load. The
                // source id and own id come from the result so nested subgraphs persist with
                // the correct dependency.
                tx.SubGraphCreated.Recipe = new SubGraphRecipe
                {
                    Name = specification.Name,
                    SubGraphId = tx.SubGraphCreated.SubGraph.Id,
                    AlgorithmPluginName = tx.SubGraphCreated.AlgorithmPluginName,
                    SourceFallen8Id = tx.SubGraphCreated.SourceFallen8Id,
                    SpecificationJson = JsonSerializer.Serialize(specification)
                };

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
        [HttpDelete("/subgraph/{name}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult DeleteSubGraph([FromRoute] String name)
        {
            if (!_fallen8.SubGraphFactory.TryGetSubGraph(out _, name))
            {
                return NotFound(String.Format("No subgraph named '{0}'.", name));
            }

            var tx = new RemoveSubGraphTransaction { SubGraphName = name };
            _fallen8.EnqueueTransaction(tx).WaitUntilFinished();

            return NoContent();
        }
    }
}
