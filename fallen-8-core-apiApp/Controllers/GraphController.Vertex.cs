// MIT License
//
// GraphController.Vertex.cs
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
        /// Creates a new vertex in the graph
        /// </summary>
        /// <param name="definition">The vertex specification containing label and property information</param>
        /// <param name="waitForCompletion">When true, waits for the transaction to complete before responding</param>
        /// <remarks>
        /// Sample request:
        ///
        ///     PUT /vertex
        ///     {
        ///        "label": "person",
        ///        "creationDate": "2025-04-22T00:00:00",
        ///        "properties": {
        ///          "name": {
        ///            "propertyValue": "John Doe",
        ///            "fullQualifiedTypeName": "System.String"
        ///          }
        ///        }
        ///     }
        /// </remarks>
        /// <response code="202">Vertex creation accepted (and committed when waitForCompletion is true)</response>
        /// <response code="400">Invalid vertex specification</response>
        /// <response code="500">The transaction was rolled back with an internal error (only when waitForCompletion is true)</response>
        [HttpPut("/vertex")]
        [Consumes("application/json")]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> AddVertex([FromBody] VertexSpecification definition, [FromQuery] bool waitForCompletion = false)
        {
            #region initial checks

            if (definition == null)
            {
                return BadRequest("A vertex specification is required.");
            }

            #endregion

            var tx = new CreateVertexTransaction()
            {
                Definition = new VertexDefinition()
                {
                    CreationDate = definition.CreationDate,
                    Label = definition.Label,
                    Properties = ServiceHelper.GenerateProperties(definition.Properties)
                }
            };

            return await AwaitAndAccept(_fallen8.EnqueueTransaction(tx), waitForCompletion);
        }

        /// <summary>
        /// Retrieves a vertex from the graph by its identifier
        /// </summary>
        /// <param name="vertexIdentifier">The ID of the vertex to retrieve</param>
        /// <returns>The vertex object if found, null otherwise</returns>
        /// <response code="200">Returns the vertex object</response>
        /// <response code="204">Vertex with the specified ID was not found</response>
        [HttpGet("/vertex/{vertexIdentifier}")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(Vertex), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public Vertex GetVertex([FromRoute] Int32 vertexIdentifier)
        {
            VertexModel vertex;
            if (_fallen8.TryGetVertex(out vertex, vertexIdentifier))
            {
                return new Vertex(vertex);
            }

            return null;
        }

        /// <summary>
        /// Gets all available outgoing edge property IDs for a vertex
        /// </summary>
        /// <param name="vertexIdentifier">The ID of the vertex</param>
        /// <returns>A list of edge property IDs for outgoing edges</returns>
        /// <response code="200">Returns the list of outgoing edge property IDs</response>
        /// <response code="204">Vertex has no outgoing edges or vertex not found</response>
        [HttpGet("/vertex/{vertexIdentifier}/edges/out")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(List<string>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public List<String> GetAllAvailableOutEdgesOnVertex([FromRoute] Int32 vertexIdentifier)
        {
            VertexModel vertex;
            return _fallen8.TryGetVertex(out vertex, vertexIdentifier)
                       ? vertex.GetOutgoingEdgeIds()
                       : null;
        }

        /// <summary>
        /// Gets all available incoming edge property IDs for a vertex
        /// </summary>
        /// <param name="vertexIdentifier">The ID of the vertex</param>
        /// <returns>A list of edge property IDs for incoming edges</returns>
        /// <response code="200">Returns the list of incoming edge property IDs</response>
        /// <response code="204">Vertex has no incoming edges or vertex not found</response>
        [HttpGet("/vertex/{vertexIdentifier}/edges/in")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(List<string>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public List<String> GetAllAvailableIncEdgesOnVertex([FromRoute] Int32 vertexIdentifier)
        {
            VertexModel vertex;
            return _fallen8.TryGetVertex(out vertex, vertexIdentifier)
                       ? vertex.GetIncomingEdgeIds()
                       : null;
        }

        /// <summary>
        /// Gets outgoing edges of a specific type from a vertex
        /// </summary>
        /// <param name="vertexIdentifier">The ID of the vertex</param>
        /// <param name="edgePropertyIdentifier">The edge property identifier/type to filter by</param>
        /// <returns>A list of edge IDs for matching outgoing edges</returns>
        /// <response code="200">Returns the list of matching outgoing edge IDs</response>
        /// <response code="204">No matching edges found or vertex not found</response>
        [HttpGet("/vertex/{vertexIdentifier}/edges/out/{edgePropertyIdentifier}")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(List<int>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public List<int> GetOutgoingEdges([FromRoute] Int32 vertexIdentifier, [FromRoute] String edgePropertyIdentifier)
        {
            VertexModel vertex;
            if (_fallen8.TryGetVertex(out vertex, vertexIdentifier))
            {
                IReadOnlyList<EdgeModel> edges;
                if (vertex.TryGetOutEdge(out edges, edgePropertyIdentifier))
                {
                    return edges.Select(_ => _.Id).ToList();
                }
            }
            return null;
        }

        /// <summary>
        /// Gets incoming edges of a specific type to a vertex
        /// </summary>
        /// <param name="vertexIdentifier">The ID of the vertex</param>
        /// <param name="edgePropertyIdentifier">The edge property identifier/type to filter by</param>
        /// <returns>A list of edge IDs for matching incoming edges</returns>
        /// <response code="200">Returns the list of matching incoming edge IDs</response>
        /// <response code="204">No matching edges found or vertex not found</response>
        [HttpGet("/vertex/{vertexIdentifier}/edges/in/{edgePropertyIdentifier}")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(List<int>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public List<int> GetIncomingEdges([FromRoute] Int32 vertexIdentifier, [FromRoute] String edgePropertyIdentifier)
        {
            VertexModel vertex;
            if (_fallen8.TryGetVertex(out vertex, vertexIdentifier))
            {
                IReadOnlyList<EdgeModel> edges;
                if (vertex.TryGetInEdge(out edges, edgePropertyIdentifier))
                {
                    return edges.Select(_ => _.Id).ToList();
                }
            }
            return null;
        }

        /// <summary>
        /// Gets the total count of incoming edges for a vertex
        /// </summary>
        /// <param name="vertexIdentifier">The ID of the vertex</param>
        /// <returns>The count of incoming edges</returns>
        /// <response code="200">Returns the count of incoming edges</response>
        /// <response code="404">Vertex with the specified ID was not found</response>
        [HttpGet("/vertex/{vertexIdentifier}/edges/indegree")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(uint), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<uint> GetInDegree([FromRoute] int vertexIdentifier)
        {
            // A missing vertex is a 404, not an ambiguous 0 that also means "zero edges" (feature
            // api-error-contract E7); a non-integer id fails route binding -> 400 (E2).
            VertexModel vertex;
            if (_fallen8.TryGetVertex(out vertex, vertexIdentifier))
            {
                return vertex.GetInDegree();
            }
            return NotFound(String.Format("Could not find vertex with id {0}.", vertexIdentifier));
        }

        /// <summary>
        /// Gets the total count of outgoing edges for a vertex
        /// </summary>
        /// <param name="vertexIdentifier">The ID of the vertex</param>
        /// <returns>The count of outgoing edges</returns>
        /// <response code="200">Returns the count of outgoing edges</response>
        /// <response code="404">Vertex with the specified ID was not found</response>
        [HttpGet("/vertex/{vertexIdentifier}/edges/outdegree")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(uint), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<uint> GetOutDegree([FromRoute] int vertexIdentifier)
        {
            VertexModel vertex;
            if (_fallen8.TryGetVertex(out vertex, vertexIdentifier))
            {
                return vertex.GetOutDegree();
            }
            return NotFound(String.Format("Could not find vertex with id {0}.", vertexIdentifier));
        }

        /// <summary>
        /// Gets the count of incoming edges of a specific type for a vertex
        /// </summary>
        /// <param name="vertexIdentifier">The ID of the vertex</param>
        /// <param name="edgePropertyIdentifier">The edge property identifier/type to count</param>
        /// <returns>The count of incoming edges matching the specified type</returns>
        /// <response code="200">Returns the count of matching incoming edges (0 if the vertex has no such edge group)</response>
        /// <response code="404">Vertex with the specified ID was not found</response>
        [HttpGet("/vertex/{vertexIdentifier}/edges/in/{edgePropertyIdentifier}/degree")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(uint), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<uint> GetInEdgeDegree([FromRoute] int vertexIdentifier, [FromRoute] string edgePropertyIdentifier)
        {
            VertexModel vertex;
            if (!_fallen8.TryGetVertex(out vertex, vertexIdentifier))
            {
                return NotFound(String.Format("Could not find vertex with id {0}.", vertexIdentifier));
            }

            // A live vertex with no such edge group genuinely has degree 0 (200), distinct from a
            // missing vertex (404) - feature api-error-contract E7.
            IReadOnlyList<EdgeModel> edges;
            return vertex.TryGetInEdge(out edges, edgePropertyIdentifier)
                ? Convert.ToUInt32(edges.Count)
                : 0u;
        }

        /// <summary>
        /// Gets the count of outgoing edges of a specific type from a vertex
        /// </summary>
        /// <param name="vertexIdentifier">The ID of the vertex</param>
        /// <param name="edgePropertyIdentifier">The edge property identifier/type to count</param>
        /// <returns>The count of outgoing edges matching the specified type</returns>
        /// <response code="200">Returns the count of matching outgoing edges (0 if the vertex has no such edge group)</response>
        /// <response code="404">Vertex with the specified ID was not found</response>
        [HttpGet("/vertex/{vertexIdentifier}/edges/out/{edgePropertyIdentifier}/degree")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(uint), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<uint> GetOutEdgeDegree([FromRoute] int vertexIdentifier, [FromRoute] string edgePropertyIdentifier)
        {
            VertexModel vertex;
            if (!_fallen8.TryGetVertex(out vertex, vertexIdentifier))
            {
                return NotFound(String.Format("Could not find vertex with id {0}.", vertexIdentifier));
            }

            IReadOnlyList<EdgeModel> edges;
            return vertex.TryGetOutEdge(out edges, edgePropertyIdentifier)
                ? Convert.ToUInt32(edges.Count)
                : 0u;
        }
    }
}
