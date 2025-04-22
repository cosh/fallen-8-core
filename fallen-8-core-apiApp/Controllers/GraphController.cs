// MIT License
//
// GraphController.cs
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
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.App.Interfaces;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.App.Controllers.Cache;
using NoSQL.GraphDB.Core.App.Controllers.Model;
using NoSQL.GraphDB.Core.App.Helper;
using NoSQL.GraphDB.Core.Helper;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Index.Fulltext;
using NoSQL.GraphDB.Core.Index.Spatial;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Serializer;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.App.Controllers
{
    [ApiController]
    [Route("api/v{version:apiVersion}/[controller]")]
    [ApiVersion("0.1")]
    public class GraphController : ControllerBase, IRESTService
    {
        #region Data

        /// <summary>
        ///   The internal Fallen-8 instance
        /// </summary>
        private readonly Fallen8 _fallen8;

        private readonly ILogger<GraphController> _logger;

        private readonly GeneratedCodeCache _cache;

        #endregion

        public GraphController(ILogger<GraphController> logger, Fallen8 fallen8)
        {
            _logger = logger;

            _fallen8 = fallen8;

            _cache = new GeneratedCodeCache();
        }

        #region IDisposable Members

        public void Dispose()
        {
        }

        #endregion

        #region IGraphService implementation

        /// <summary>
        /// Creates a new vertex in the graph
        /// </summary>
        /// <param name="definition">The vertex specification containing label and property information</param>
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
        /// <response code="204">Vertex successfully created</response>
        /// <response code="400">Invalid vertex specification</response>
        [HttpPut("/vertex")]
        [Consumes("application/json")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public void AddVertex([FromBody] VertexSpecification definition)
        {
            #region initial checks

            if (definition == null)
            {
                throw new ArgumentNullException("definition");
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

            _fallen8.EnqueueTransaction(tx);
        }

        /// <summary>
        /// Creates a new edge between two vertices in the graph
        /// </summary>
        /// <param name="definition">The edge specification containing source, target and property information</param>
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
        /// <response code="204">Edge successfully created</response>
        /// <response code="400">Invalid edge specification or referenced vertices do not exist</response>
        [HttpPut("/edge")]
        [Consumes("application/json")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public void AddEdge(EdgeSpecification definition)
        {
            #region initial checks

            if (definition == null)
            {
                throw new ArgumentNullException("definition");
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

            _fallen8.EnqueueTransaction(tx);
        }

        /// <summary>
        /// Retrieves the complete graph data including vertices and edges
        /// </summary>
        /// <param name="maxElements">Maximum number of elements to return (default: 1000)</param>
        /// <returns>A graph object containing lists of vertices and edges</returns>
        /// <response code="200">Returns the graph data with vertices and edges</response>
        [HttpGet("/graph")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(Graph), StatusCodes.Status200OK)]
        public Graph GetGraph([FromQuery] int maxElements = 1000)
        {
            var result = new Graph();

            var edges = _fallen8.GetAllEdges().Take(maxElements);
            result.Edges = edges.Select(_ => new Edge(_)).ToList();

            var vertices = _fallen8.GetAllVertices().Take(maxElements);
            result.Vertices = vertices.Select(_ => new Vertex(_)).ToList();

            return result;
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
        public int GetSourceVertexForEdge([FromRoute] Int32 edgeIdentifier)
        {
            EdgeModel edge;
            if (_fallen8.TryGetEdge(out edge, edgeIdentifier))
            {
                return edge.SourceVertex.Id;
            }

            throw new WebException(String.Format("Could not find edge with id {0}.", edgeIdentifier));
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
        public int GetTargetVertexForEdge([FromRoute] Int32 edgeIdentifier)
        {
            EdgeModel edge;
            if (_fallen8.TryGetEdge(out edge, edgeIdentifier))
            {
                return edge.TargetVertex.Id;
            }

            throw new WebException(String.Format("Could not find edge with id {0}.", edgeIdentifier));
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
                ImmutableList<EdgeModel> edges;
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
                ImmutableList<EdgeModel> edges;
                if (vertex.TryGetInEdge(out edges, edgePropertyIdentifier))
                {
                    return edges.Select(_ => _.Id).ToList();
                }
            }
            return null;
        }

        /// <summary>
        /// Scans the graph for elements with a specific property value
        /// </summary>
        /// <param name="propertyId">The property ID to scan for</param>
        /// <param name="definition">Scan specification with comparison operator and value</param>
        /// <returns>A collection of graph element IDs matching the criteria</returns>
        /// <remarks>
        /// Sample request:
        ///
        ///     POST /scan/graph/property/name
        ///     {
        ///        "operator": "Equal",
        ///        "literal": {
        ///          "value": "John Doe",
        ///          "fullQualifiedTypeName": "System.String"
        ///        },
        ///        "resultType": "Vertices"
        ///     }
        /// </remarks>
        /// <response code="200">Returns the matching element IDs</response>
        /// <response code="400">Invalid scan specification</response>
        [HttpPost("/scan/graph/property/{propertyId}")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(IEnumerable<int>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public IEnumerable<int> GraphScan([FromRoute] String propertyId, [FromBody] ScanSpecification definition)
        {
            #region initial checks

            if (definition == null)
            {
                throw new ArgumentNullException("definition");
            }

            #endregion

            IComparable value = definition.Literal.FullQualifiedTypeName == null
                ? definition.Literal.Value
                : (IComparable)Convert.ChangeType(definition.Literal.Value,
                                                         Type.GetType(definition.Literal.FullQualifiedTypeName, true,
                                                                      true));

            List<AGraphElementModel> graphElements;
            return _fallen8.GraphScan(out graphElements, propertyId, value, definition.Operator)
                       ? CreateResult(graphElements, definition.ResultType)
                       : Enumerable.Empty<Int32>();
        }

        /// <summary>
        /// Performs a scan operation on an index with a specific value and operator
        /// </summary>
        /// <param name="definition">Index scan specification with index ID, operator and value</param>
        /// <returns>A collection of graph element IDs matching the criteria</returns>
        /// <remarks>
        /// Sample request:
        ///
        ///     POST /scan/index/all
        ///     {
        ///        "indexId": "userNameIndex",
        ///        "operator": "Equal",
        ///        "literal": {
        ///          "value": "Jane",
        ///          "fullQualifiedTypeName": "System.String"
        ///        },
        ///        "resultType": "Vertices"
        ///     }
        /// </remarks>
        /// <response code="200">Returns the matching element IDs</response>
        /// <response code="400">Invalid scan specification or index not found</response>
        [HttpPost("/scan/index/all")]
        [Produces("application/json")]
        [Consumes("application/json")]
        [ProducesResponseType(typeof(IEnumerable<int>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public IEnumerable<int> IndexScan([FromBody] IndexScanSpecification definition)
        {
            #region initial checks

            if (definition == null)
            {
                throw new ArgumentNullException("definition");
            }

            #endregion

            IComparable value = definition.Literal.FullQualifiedTypeName == null
                ? definition.Literal.Value
                : (IComparable)Convert.ChangeType(definition.Literal.Value,
                                                         Type.GetType(definition.Literal.FullQualifiedTypeName, true,
                                                                      true));

            ImmutableList<AGraphElementModel> graphElements;
            return _fallen8.IndexScan(out graphElements, definition.IndexId, value, definition.Operator)
                       ? CreateResult(graphElements, definition.ResultType)
                       : Enumerable.Empty<Int32>();
        }

        /// <summary>
        /// Performs a range-based scan on an index between two values
        /// </summary>
        /// <param name="definition">Range scan specification with index ID, limits and include/exclude options</param>
        /// <returns>A collection of graph element IDs within the specified range</returns>
        /// <remarks>
        /// Sample request:
        ///
        ///     POST /scan/index/range
        ///     {
        ///        "indexId": "ageIndex",
        ///        "leftLimit": 18,
        ///        "rightLimit": 30,
        ///        "includeLeft": true,
        ///        "includeRight": false,
        ///        "fullQualifiedTypeName": "System.Int32",
        ///        "resultType": "Vertices"
        ///     }
        /// </remarks>
        /// <response code="200">Returns the matching element IDs within the range</response>
        /// <response code="400">Invalid range specification or index not found</response>
        [HttpPost("/scan/index/range")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(IEnumerable<int>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public IEnumerable<int> RangeIndexScan([FromBody] RangeIndexScanSpecification definition)
        {
            #region initial checks

            if (definition == null)
            {
                throw new ArgumentNullException("definition");
            }

            #endregion

            var left = (IComparable)Convert.ChangeType(definition.LeftLimit,
                                                        Type.GetType(definition.FullQualifiedTypeName, true, true));

            var right = (IComparable)Convert.ChangeType(definition.RightLimit,
                                                         Type.GetType(definition.FullQualifiedTypeName, true, true));

            ImmutableList<AGraphElementModel> graphElements;
            return _fallen8.RangeIndexScan(out graphElements, definition.IndexId, left, right, definition.IncludeLeft,
                                           definition.IncludeRight)
                       ? CreateResult(graphElements, definition.ResultType)
                       : Enumerable.Empty<Int32>();
        }

        /// <summary>
        /// Performs a fulltext search on an indexed property
        /// </summary>
        /// <param name="definition">Fulltext search specification with index ID and search terms</param>
        /// <returns>A result object containing matched elements and highlighting information</returns>
        /// <remarks>
        /// Sample request:
        ///
        ///     POST /scan/index/fulltext
        ///     {
        ///        "indexId": "documentIndex",
        ///        "requestString": "graph database nosql"
        ///     }
        /// </remarks>
        /// <response code="200">Returns the search results with highlighting</response>
        /// <response code="400">Invalid search specification or index not found</response>
        /// <response code="404">Index not found or is not a fulltext index</response>
        [HttpPost("/scan/index/fulltext")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(FulltextSearchResultREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public FulltextSearchResultREST FulltextIndexScan([FromBody] FulltextIndexScanSpecification definition)
        {
            #region initial checks

            if (definition == null)
            {
                throw new ArgumentNullException("definition");
            }

            #endregion

            FulltextSearchResult result;
            return _fallen8.FulltextIndexScan(out result, definition.IndexId, definition.RequestString)
                       ? new FulltextSearchResultREST(result)
                       : null;
        }

        /// <summary>
        /// Performs a spatial distance search using a spatial index
        /// </summary>
        /// <param name="definition">Spatial search specification with index ID, reference element and distance</param>
        /// <returns>A collection of graph element IDs within the specified distance</returns>
        /// <remarks>
        /// Sample request:
        ///
        ///     POST /scan/index/spatial
        ///     {
        ///        "indexId": "locationIndex",
        ///        "graphElementId": 123,
        ///        "distance": 5.0
        ///     }
        /// </remarks>
        /// <response code="200">Returns the element IDs within the specified distance</response>
        /// <response code="400">Invalid search specification</response>
        /// <response code="404">Index not found, is not a spatial index, or reference element not found</response>
        [HttpPost("/scan/index/spatial")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(IEnumerable<int>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IEnumerable<int> SpatialIndexScanSearchDistance([FromBody] SearchDistanceSpecification definition)
        {
            #region initial checks

            if (definition == null)
            {
                throw new ArgumentNullException("definition");
            }

            #endregion

            AGraphElementModel graphElement;
            if (_fallen8.TryGetGraphElement(out graphElement, definition.GraphElementId))
            {
                IIndex idx;
                if (_fallen8.IndexFactory.TryGetIndex(out idx, definition.IndexId))
                {
                    var spatialIndex = idx as ISpatialIndex;
                    if (spatialIndex != null)
                    {
                        ImmutableList<AGraphElementModel> result;
                        return spatialIndex.SearchDistance(out result, definition.Distance, graphElement)
                            ? result.Select(_ => _.Id)
                            : null;
                    }
                    _logger.LogError(String.Format("The index with id {0} is no spatial index.", definition.IndexId));
                    return null;
                }
                _logger.LogError(String.Format("Could not find index {0}.", definition.IndexId));
                return null;
            }
            _logger.LogError(String.Format("Could not find graph element {0}.", definition.GraphElementId));
            return null;
        }

        /// <summary>
        /// Adds or updates a property on a graph element (vertex or edge)
        /// </summary>
        /// <param name="graphElementIdentifier">The ID of the graph element</param>
        /// <param name="propertyIdString">The ID/key of the property</param>
        /// <param name="definition">Property value specification</param>
        /// <remarks>
        /// Sample request:
        ///
        ///     PUT /graphelement/123/age
        ///     {
        ///        "propertyValue": 35,
        ///        "fullQualifiedTypeName": "System.Int32"
        ///     }
        /// </remarks>
        /// <response code="204">Property successfully added</response>
        /// <response code="400">Invalid property specification or graph element not found</response>
        [HttpPut("/graphelement/{graphElementIdentifier}/{propertyIdString}")]
        [Consumes("application/json")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public void AddProperty([FromRoute] string graphElementIdentifier, [FromRoute] string propertyIdString, [FromBody] PropertySpecification definition)
        {
            var graphElementId = Convert.ToInt32(graphElementIdentifier);
            var propertyId = propertyIdString;

            var property = Convert.ChangeType(
                definition.PropertyValue,
                Type.GetType(definition.FullQualifiedTypeName, true, true));

            AddPropertyTransaction tx = new AddPropertyTransaction()
            {
                Definition =
                {
                    GraphElementId = graphElementId,
                    PropertyId = propertyId,
                    Property = property
                }
            };

            _fallen8.EnqueueTransaction(tx);
        }

        /// <summary>
        /// Removes a property from a graph element (vertex or edge)
        /// </summary>
        /// <param name="graphElementIdentifier">The ID of the graph element</param>
        /// <param name="propertyIdString">The ID/key of the property to remove</param>
        /// <remarks>
        /// Sample request:
        ///
        ///     DELETE /graphelement/123/age
        /// </remarks>
        /// <response code="204">Property successfully removed</response>
        /// <response code="400">Graph element or property not found</response>
        [HttpDelete("/graphelement/{graphElementIdentifier}/{propertyIdString}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public void TryRemoveProperty([FromRoute] string graphElementIdentifier, [FromRoute] string propertyIdString)
        {
            var graphElementId = Convert.ToInt32(graphElementIdentifier);
            var propertyId = propertyIdString;

            RemovePropertyTransaction tx = new RemovePropertyTransaction()
            {
                GraphElementId = graphElementId,
                PropertyId = propertyId
            };

            _fallen8.EnqueueTransaction(tx);
        }

        /// <summary>
        /// Removes a graph element (vertex or edge) from the graph
        /// </summary>
        /// <param name="graphElementIdentifier">The ID of the graph element to remove</param>
        /// <remarks>
        /// Sample request:
        ///
        ///     DELETE /graphelement/123
        /// </remarks>
        /// <response code="204">Graph element successfully removed</response>
        /// <response code="400">Graph element not found</response>
        [HttpDelete("/graphelement/{graphElementIdentifier}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public void TryRemoveGraphElement([FromRoute] string graphElementIdentifier)
        {
            var graphElementId = Convert.ToInt32(graphElementIdentifier);

            RemoveGraphElementTransaction tx = new RemoveGraphElementTransaction()
            {
                GraphElementId = graphElementId
            };

            _fallen8.EnqueueTransaction(tx);
        }

        /// <summary>
        /// Gets the total count of incoming edges for a vertex
        /// </summary>
        /// <param name="vertexIdentifier">The ID of the vertex</param>
        /// <returns>The count of incoming edges</returns>
        /// <response code="200">Returns the count of incoming edges</response>
        [HttpGet("/vertex/{vertexIdentifier}/edges/indegree")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(uint), StatusCodes.Status200OK)]
        public uint GetInDegree([FromRoute] string vertexIdentifier)
        {
            VertexModel vertex;
            if (_fallen8.TryGetVertex(out vertex, Convert.ToInt32(vertexIdentifier)))
            {
                return vertex.GetInDegree();
            }
            return 0;
        }

        /// <summary>
        /// Gets the total count of outgoing edges for a vertex
        /// </summary>
        /// <param name="vertexIdentifier">The ID of the vertex</param>
        /// <returns>The count of outgoing edges</returns>
        /// <response code="200">Returns the count of outgoing edges</response>
        [HttpGet("/vertex/{vertexIdentifier}/edges/outdegree")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(uint), StatusCodes.Status200OK)]
        public uint GetOutDegree([FromRoute] string vertexIdentifier)
        {
            VertexModel vertex;
            if (_fallen8.TryGetVertex(out vertex, Convert.ToInt32(vertexIdentifier)))
            {
                return vertex.GetOutDegree();
            }
            return 0;
        }

        /// <summary>
        /// Gets the count of incoming edges of a specific type for a vertex
        /// </summary>
        /// <param name="vertexIdentifier">The ID of the vertex</param>
        /// <param name="edgePropertyIdentifier">The edge property identifier/type to count</param>
        /// <returns>The count of incoming edges matching the specified type</returns>
        /// <response code="200">Returns the count of matching incoming edges</response>
        [HttpGet("/vertex/{vertexIdentifier}/edges/in/{edgePropertyIdentifier}/degree")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(uint), StatusCodes.Status200OK)]
        public uint GetInEdgeDegree([FromRoute] string vertexIdentifier, [FromRoute] string edgePropertyIdentifier)
        {
            VertexModel vertex;
            if (_fallen8.TryGetVertex(out vertex, Convert.ToInt32(vertexIdentifier)))
            {
                ImmutableList<EdgeModel> edges;
                if (vertex.TryGetInEdge(out edges, edgePropertyIdentifier))
                {
                    return Convert.ToUInt32(edges.Count);
                }
            }
            return 0;
        }

        /// <summary>
        /// Gets the count of outgoing edges of a specific type from a vertex
        /// </summary>
        /// <param name="vertexIdentifier">The ID of the vertex</param>
        /// <param name="edgePropertyIdentifier">The edge property identifier/type to count</param>
        /// <returns>The count of outgoing edges matching the specified type</returns>
        /// <response code="200">Returns the count of matching outgoing edges</response>
        [HttpGet("/vertex/{vertexIdentifier}/edges/out/{edgePropertyIdentifier}/degree")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(uint), StatusCodes.Status200OK)]
        public uint GetOutEdgeDegree([FromRoute] string vertexIdentifier, [FromRoute] string edgePropertyIdentifier)
        {
            VertexModel vertex;
            if (_fallen8.TryGetVertex(out vertex, Convert.ToInt32(vertexIdentifier)))
            {
                ImmutableList<EdgeModel> edges;
                if (vertex.TryGetOutEdge(out edges, edgePropertyIdentifier))
                {
                    return Convert.ToUInt32(edges.Count);
                }
            }
            return 0;
        }

        /// <summary>
        /// Finds paths between two vertices in the graph
        /// </summary>
        /// <param name="from">The ID of the source vertex</param>
        /// <param name="to">The ID of the target vertex</param>
        /// <param name="definition">Path specification with algorithm, depth, filters and other constraints</param>
        /// <returns>A list of paths between the vertices</returns>
        /// <remarks>
        /// Sample request:
        ///
        ///     POST /path/1/to/5
        ///     {
        ///        "pathAlgorithmName": "BLS",
        ///        "maxDepth": 5,
        ///        "maxPathWeight": 100.0,
        ///        "maxResults": 10,
        ///        "edgePropertyFilter": "friendship",
        ///        "vertexFilter": "Person"
        ///     }
        /// </remarks>
        /// <response code="200">Returns the found paths between the vertices</response>
        /// <response code="400">Invalid path specification</response>
        /// <response code="404">Source or target vertex not found</response>
        [HttpPost("/path/{from}/to/{to}")]
        [Produces("application/json")]
        [Consumes("application/json")]
        [ProducesResponseType(typeof(List<PathREST>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public List<PathREST> GetPaths([FromRoute] Int32 from, [FromRoute] Int32 to, [FromBody] PathSpecification definition)
        {
            // Always initialize with empty list to avoid returning null
            List<PathREST> result = new List<PathREST>();

            try
            {
                if (definition == null)
                {
                    definition = new PathSpecification();
                }

                // Special case - when MaxDepth is 0, no paths can be found
                if (definition.MaxDepth <= 0)
                {
                    return result;
                }

                IPathTraverser traverser = null;

                Object cachedTraverser;
                if (!_cache.Traverser.TryGetValue(definition, out cachedTraverser))
                {
                    //Traverser was not cached
                    var compilerMessage = CodeGenerationHelper.GeneratePathTraverser(out traverser, definition);

                    if (traverser != null)
                    {
                        _cache.AddTraverser(definition, traverser);
                    }
                    else
                    {
                        _logger?.LogError(compilerMessage);
                        return result; // Return empty list if we can't get a traverser
                    }
                }
                else
                {
                    traverser = (IPathTraverser)cachedTraverser;
                }

                if (traverser != null)
                {
                    List<Core.Algorithms.Path.Path> paths;
                    if (_fallen8.CalculateShortestPath(
                        out paths,
                        definition.PathAlgorithmName ?? "BLS", // Default to BLS if not specified
                        from,
                        to,
                        definition.MaxDepth,
                        definition.MaxPathWeight,
                        definition.MaxResults,
                        traverser.EdgePropertyFilter(),
                        traverser.VertexFilter(),
                        traverser.EdgeFilter(),
                        traverser.EdgeCost(),
                        traverser.VertexCost()))
                    {
                        if (paths != null && paths.Count > 0)
                        {
                            return new List<PathREST>(paths.Select(aPath => new PathREST(aPath)));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Log the exception but don't let it propagate
                _logger?.LogError(ex, "Error calculating path between vertices {0} and {1}", from, to);
            }

            return result; // Always return the initialized list, never null
        }

        /// <summary>
        /// Creates a new index for the graph
        /// </summary>
        /// <param name="definition">Plugin specification with index type and configuration options</param>
        /// <returns>True if the index was successfully created, false otherwise</returns>
        /// <remarks>
        /// Sample request:
        ///
        ///     POST /index
        ///     {
        ///        "uniqueId": "nameIndex",
        ///        "pluginType": "DictionaryIndex",
        ///        "pluginOptions": {
        ///           "propertyId": "name",
        ///           "type": "System.String"
        ///        }
        ///     }
        /// </remarks>
        /// <response code="200">Returns true if the index was created successfully</response>
        /// <response code="400">Invalid index specification</response>
        [HttpPost("/index")]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public bool CreateIndex([FromBody] PluginSpecification definition)
        {
            //TODO: return IIndex object representation
            IIndex result;
            return _fallen8.IndexFactory.TryCreateIndex(out result, definition.UniqueId, definition.PluginType, ServiceHelper.CreatePluginOptions(definition.PluginOptions));
        }

        /// <summary>
        /// Adds a graph element to an existing index
        /// </summary>
        /// <param name="indexId">The ID of the index</param>
        /// <param name="definition">Specification containing graph element ID and key information</param>
        /// <returns>True if the element was successfully added to the index, false otherwise</returns>
        /// <remarks>
        /// Sample request:
        ///
        ///     PUT /index/nameIndex
        ///     {
        ///        "graphElementId": 123,
        ///        "key": {
        ///          "propertyValue": "John Smith",
        ///          "fullQualifiedTypeName": "System.String"
        ///        }
        ///     }
        /// </remarks>
        /// <response code="200">Returns true if the element was successfully added to the index</response>
        /// <response code="400">Invalid specification, index not found, or graph element not found</response>
        [HttpPut("/index/{indexId}")]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public bool AddToIndex([FromRoute] String indexId, [FromBody] IndexAddToSpecification definition)
        {
            IIndex idx;
            if (_fallen8.IndexFactory.TryGetIndex(out idx, indexId))
            {
                AGraphElementModel graphElement;
                if (_fallen8.TryGetGraphElement(out graphElement, definition.GraphElementId))
                {
                    idx.AddOrUpdate(ServiceHelper.CreateObject(definition.Key), graphElement);
                    return true;
                }

                _logger.LogError(String.Format("Could not find graph element {0}.", definition.GraphElementId));
                return false;
            }
            _logger.LogError(String.Format("Could not find index {0}.", indexId));
            return false;
        }

        /// <summary>
        /// Removes a key from an index
        /// </summary>
        /// <param name="indexId">The ID of the index</param>
        /// <param name="property">The property specification representing the key to remove</param>
        /// <returns>True if the key was successfully removed, false otherwise</returns>
        /// <remarks>
        /// Sample request:
        ///
        ///     DELETE /index/nameIndex/propertyValue
        ///     {
        ///        "propertyValue": "John Smith",
        ///        "fullQualifiedTypeName": "System.String"
        ///     }
        /// </remarks>
        /// <response code="200">Returns true if the key was successfully removed</response>
        /// <response code="400">Invalid property specification or index not found</response>
        [HttpDelete("/index/{indexId}/propertyValue")]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public bool RemoveKeyFromIndex([FromRoute] String indexId, [FromBody] PropertySpecification property)
        {
            IIndex idx;
            if (_fallen8.IndexFactory.TryGetIndex(out idx, indexId))
            {
                return idx.TryRemoveKey(ServiceHelper.CreateObject(property));
            }
            _logger.LogError(String.Format("Could not find index {0}.", indexId));
            return false;
        }

        /// <summary>
        /// Removes a graph element from an index
        /// </summary>
        /// <param name="indexId">The ID of the index</param>
        /// <param name="graphElementId">The ID of the graph element to remove</param>
        /// <returns>True if the graph element was successfully removed from the index, false otherwise</returns>
        /// <remarks>
        /// Sample request:
        ///
        ///     DELETE /index/nameIndex/123
        /// </remarks>
        /// <response code="200">Returns true if the element was successfully removed from the index</response>
        /// <response code="404">Index not found or graph element not found</response>
        [HttpDelete("/index/{indexId}/{graphElementId}")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public bool RemoveGraphElementFromIndex([FromRoute] String indexId, [FromRoute] Int32 graphElementId)
        {
            IIndex idx;
            if (_fallen8.IndexFactory.TryGetIndex(out idx, indexId))
            {
                AGraphElementModel graphElement;
                if (_fallen8.TryGetGraphElement(out graphElement, graphElementId))
                {
                    idx.RemoveValue(graphElement);
                    return true;
                }

                _logger.LogError(String.Format("Could not find graph element {0}.", graphElementId));
                return false;
            }
            _logger.LogError(String.Format("Could not find index {0}.", indexId));
            return false;
        }

        /// <summary>
        /// Deletes an index from the system
        /// </summary>
        /// <param name="indexId">The ID of the index to delete</param>
        /// <returns>True if the index was successfully deleted, false otherwise</returns>
        /// <remarks>
        /// Sample request:
        ///
        ///     DELETE /index/nameIndex
        /// </remarks>
        /// <response code="200">Returns true if the index was successfully deleted</response>
        /// <response code="404">Index not found</response>
        [HttpDelete("/index/{indexId}")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public bool DeleteIndex([FromRoute] String indexId)
        {
            return _fallen8.IndexFactory.TryDeleteIndex(indexId);
        }

        #endregion

        #region private helper

        /// <summary>
        ///   Creats the result
        /// </summary>
        /// <param name="graphElements"> The graph elements </param>
        /// <param name="resultTypeSpecification"> The result specification </param>
        /// <returns> </returns>
        private static IEnumerable<int> CreateResult(IEnumerable<AGraphElementModel> graphElements,
                                                    ResultTypeSpecification resultTypeSpecification)
        {
            switch (resultTypeSpecification)
            {
                case ResultTypeSpecification.Vertices:
                    return graphElements.OfType<VertexModel>().Select(_ => _.Id);

                case ResultTypeSpecification.Edges:
                    return graphElements.OfType<EdgeModel>().Select(_ => _.Id);

                case ResultTypeSpecification.Both:
                    return graphElements.Select(_ => _.Id);

                default:
                    throw new ArgumentOutOfRangeException("resultTypeSpecification");
            }
        }

        #endregion

        #region not implemented

        [NonAction]
        public void Save(SerializationWriter writer)
        {
        }

        [NonAction]
        public void Load(SerializationReader reader, Fallen8 fallen8)
        {
        }

        [NonAction]
        public void Shutdown()
        {
        }

        #endregion
    }
}
