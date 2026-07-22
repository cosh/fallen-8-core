// MIT License
//
// GraphController.Edge.cs
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
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;
using System.Threading.Tasks;

namespace NoSQL.GraphDB.App.Controllers
{
    public partial class GraphController
    {
        /// <summary>
        /// Retrieves an edge from the graph by its identifier
        /// </summary>
        /// <param name="edgeIdentifier">The ID of the edge to retrieve</param>
        /// <returns>The edge object if found, null otherwise</returns>
        /// <response code="200">Returns the edge object</response>
        /// <response code="204">Edge with the specified ID was not found</response>
        [HttpGet("/edge/{edgeIdentifier}")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(Edge), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public Edge GetEdge([FromRoute] Int32 edgeIdentifier)
        {
            EdgeModel edge;
            if (_fallen8.TryGetEdge(out edge, edgeIdentifier))
            {
                return new Edge(edge);
            }

            return null;
        }

        /// <summary>
        /// Creates a new edge between two vertices in the graph
        /// </summary>
        /// <param name="definition">The edge specification containing source, target and property information</param>
        /// <param name="waitForCompletion">When true, waits for the transaction to complete before responding</param>
        /// <remarks>
        /// Sample request:
        ///
        ///     PUT /edge
        ///     {
        ///        "label": "knows",
        ///        "creationDate": "2025-04-22T00:00:00",
        ///        "sourceVertex": 1,
        ///        "targetVertex": 2,
        ///        "edgePropertyId": "friendship",
        ///        "properties": {
        ///          "since": {
        ///            "propertyValue": "2024-01-01",
        ///            "fullQualifiedTypeName": "System.DateTime"
        ///          }
        ///        }
        ///     }
        /// </remarks>
        /// <response code="202">Edge creation accepted (and committed when waitForCompletion is true)</response>
        /// <response code="400">Invalid edge specification</response>
        /// <response code="404">A referenced source or target vertex does not exist (only when waitForCompletion is true)</response>
        /// <response code="500">The transaction was rolled back with an internal error (only when waitForCompletion is true)</response>
        [HttpPut("/edge")]
        [Consumes("application/json")]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> AddEdge(EdgeSpecification definition, [FromQuery] bool waitForCompletion = false)
        {
            #region initial checks

            if (definition == null)
            {
                return BadRequest("An edge specification is required.");
            }

            #endregion

            var tx = new CreateEdgeTransaction()
            {
                Definition = new EdgeDefinition()
                {
                    CreationDate = definition.CreationDate,
                    SourceVertexId = definition.SourceVertex,
                    EdgePropertyId = definition.EdgePropertyId,
                    TargetVertexId = definition.TargetVertex,
                    Label = definition.Label,
                    Properties = ServiceHelper.GenerateProperties(definition.Properties)
                }
            };

            return await AwaitAndAccept(_fallen8.EnqueueTransaction(tx), waitForCompletion);
        }

        /// <summary>
        /// Gets the source vertex ID for a specific edge
        /// </summary>
        /// <param name="edgeIdentifier">The ID of the edge</param>
        /// <returns>The ID of the source vertex</returns>
        /// <response code="200">Returns the source vertex ID</response>
        /// <response code="404">Edge with the specified ID was not found</response>
        [HttpGet("/edge/{edgeIdentifier}/source")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<int> GetSourceVertexForEdge([FromRoute] Int32 edgeIdentifier)
        {
            EdgeModel edge;
            if (_fallen8.TryGetEdge(out edge, edgeIdentifier))
            {
                return edge.SourceVertex.Id;
            }

            // A missing edge is the documented 404, not a thrown WebException -> 500
            // (feature api-error-contract E4).
            return NotFound(String.Format("Could not find edge with id {0}.", edgeIdentifier));
        }

        /// <summary>
        /// Gets the target vertex ID for a specific edge
        /// </summary>
        /// <param name="edgeIdentifier">The ID of the edge</param>
        /// <returns>The ID of the target vertex</returns>
        /// <response code="200">Returns the target vertex ID</response>
        /// <response code="404">Edge with the specified ID was not found</response>
        [HttpGet("/edge/{edgeIdentifier}/target")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<int> GetTargetVertexForEdge([FromRoute] Int32 edgeIdentifier)
        {
            EdgeModel edge;
            if (_fallen8.TryGetEdge(out edge, edgeIdentifier))
            {
                return edge.TargetVertex.Id;
            }

            // A missing edge is the documented 404, not a thrown WebException -> 500
            // (feature api-error-contract E4).
            return NotFound(String.Format("Could not find edge with id {0}.", edgeIdentifier));
        }
    }
}
