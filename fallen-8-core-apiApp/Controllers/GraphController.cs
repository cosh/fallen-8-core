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
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.App.Configuration;
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
using NoSQL.GraphDB.Core.Index.Vector;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Serializer;
using NoSQL.GraphDB.Core.Transaction;
using System.Threading.Tasks;

namespace NoSQL.GraphDB.App.Controllers
{
    [ApiController]
    [Route("api/v{version:apiVersion}/[controller]")]
    [ApiVersion("0.1")]
    [UnconditionalSuppressMessage("Trimming", "IL2096:Call to 'System.Type.GetType' can perform case insensitive lookup of the type", Justification = "Type names are provided by API users and need case-insensitive lookup. Trimming is disabled for this application.")]
    public class GraphController : ControllerBase, IRESTService
    {
        #region Data

        /// <summary>
        ///   The internal Fallen-8 instance
        /// </summary>
        private readonly IFallen8 _fallen8;

        private readonly ILogger<GraphController> _logger;

        private readonly GeneratedCodeCache _cache;

        /// <summary>Upper bound on how many elements a single page read (<see cref="GetGraph"/>) returns,
        /// so a request cannot materialize the whole graph (feature api-error-contract E6).</summary>
        private const int MaxPageSize = 100_000;

        #endregion

        /// <summary>
        ///   Resolves a caller-supplied fully-qualified type name for value conversion. A null/empty
        ///   name means "use the raw value" (<paramref name="type"/> is <c>null</c>, returns
        ///   <c>true</c>); an ALLOW-LISTED primitive name returns its <see cref="Type"/>; any other name
        ///   returns <c>false</c> so the caller answers 400 (feature api-error-contract E3). Resolution
        ///   goes through <see cref="AllowedLiteralTypes"/>, NEVER <c>Type.GetType(userString)</c>, so an
        ///   attacker-controlled name cannot force-load an assembly or run a static ctor (feature
        ///   dynamic-code-resource-limits R3).
        /// </summary>
        private static bool TryResolveType(string fullQualifiedTypeName, out Type type)
        {
            if (string.IsNullOrEmpty(fullQualifiedTypeName))
            {
                type = null;
                return true;
            }

            return AllowedLiteralTypes.TryResolve(fullQualifiedTypeName, out type);
        }

        /// <summary>
        ///   The authorization service used for the request-shape-aware dynamic-code capability
        ///   check on <see cref="CalculateShortestPath"/> (feature stored-query-library). Null when
        ///   the controller is constructed directly (unit tests) - the hosted pipeline always
        ///   supplies it, and the pipeline-level matrix tests pin the real gate behaviour.
        /// </summary>
        private readonly IAuthorizationService _authorizationService;

        public GraphController(ILogger<GraphController> logger, IFallen8 fallen8,
            IAuthorizationService authorizationService = null)
        {
            _logger = logger;

            _fallen8 = fallen8;

            _cache = new GeneratedCodeCache();

            _authorizationService = authorizationService;
        }

        /// <summary>
        ///   Whether a path request INTRODUCES code: any non-blank inline filter/cost fragment.
        ///   Only such a request requires the dynamic-code capability (feature
        ///   stored-query-library); a storedQuery reference or a fragment-less request compiles no
        ///   user-supplied code.
        /// </summary>
        private static bool CarriesInlineCode(PathSpecification definition)
        {
            return !String.IsNullOrWhiteSpace(definition.Filter?.Vertex) ||
                   !String.IsNullOrWhiteSpace(definition.Filter?.Edge) ||
                   !String.IsNullOrWhiteSpace(definition.Filter?.EdgeProperty) ||
                   !String.IsNullOrWhiteSpace(definition.Cost?.Vertex) ||
                   !String.IsNullOrWhiteSpace(definition.Cost?.Edge);
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
        private ActionResult DenyUnlessDynamicCodeCapability()
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

            var transactionTask = _fallen8.EnqueueTransaction(tx);

            if (waitForCompletion)
            {
                await transactionTask.Completion;

                // The worker rolls a faulting transaction back (correctness-fixes B6). When the
                // caller waited for the outcome, a rolled-back write must not be reported as success.
                if (transactionTask.TransactionState == TransactionState.RolledBack)
                {
                    return RolledBackResult(transactionTask.FailureReason);
                }
            }

            return Accepted();
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

            var transactionTask = _fallen8.EnqueueTransaction(tx);

            if (waitForCompletion)
            {
                await transactionTask.Completion;

                // The worker rolls a faulting transaction back (correctness-fixes B6). When the
                // caller waited for the outcome, a rolled-back write must not be reported as success.
                if (transactionTask.TransactionState == TransactionState.RolledBack)
                {
                    return RolledBackResult(transactionTask.FailureReason);
                }
            }

            return Accepted();
        }

        /// <summary>
        /// Retrieves a graph element (vertex or edge) by its identifier
        /// </summary>
        /// <param name="graphElementIdentifier">The ID of the graph element to retrieve</param>
        /// <returns>The graph element object if found, null otherwise</returns>
        /// <response code="200">Returns the graph element object</response>
        /// <response code="204">Graph element with the specified ID was not found</response>
        [HttpGet("/graphelement/{graphElementIdentifier}")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(AGraphElement), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public AGraphElement GetGraphElement([FromRoute] Int32 graphElementIdentifier)
        {
            AGraphElementModel ge;
            if (_fallen8.TryGetGraphElement(out ge, graphElementIdentifier))
            {
                if (ge is VertexModel vertex)
                {
                    return new Vertex(vertex);
                }

                if (ge is EdgeModel edge)
                {
                    return new Edge(edge);
                }
            }

            return null;
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
            // Bounded read (feature api-error-contract E6): clamp to [0, MaxPageSize] so a single
            // request cannot materialize the whole graph (the old Take(int.MaxValue) DoS), and a
            // negative maxElements yields an empty page instead of the old silent Take(negative).
            var take = Math.Clamp(maxElements, 0, MaxPageSize);

            var result = new Graph();

            var edges = _fallen8.GetAllEdges().Take(take);
            result.Edges = edges.Select(_ => new Edge(_)).ToList();

            var vertices = _fallen8.GetAllVertices().Take(take);
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
        public ActionResult<IEnumerable<int>> GraphScan([FromRoute] String propertyId, [FromBody] ScanSpecification definition)
        {
            // A malformed scan spec (missing body/literal, unknown type name, or an unconvertible value)
            // is a client error -> 400, not a thrown exception -> 500 (feature api-error-contract E3).
            if (definition == null || definition.Literal == null)
            {
                return BadRequest("A scan specification with a literal is required.");
            }

            if (!TryConvertLiteral(definition.Literal, out var value, out var error))
            {
                return BadRequest(error);
            }

            List<AGraphElementModel> graphElements;
            return _fallen8.GraphScan(out graphElements, propertyId, value, definition.Operator)
                       ? new ActionResult<IEnumerable<int>>(CreateResult(graphElements, definition.ResultType))
                       : new ActionResult<IEnumerable<int>>(Enumerable.Empty<Int32>());
        }

        /// <summary>
        ///   Converts a <see cref="LiteralSpecification"/> to an <see cref="IComparable"/> for a scan,
        ///   returning <c>false</c> with a client-facing <paramref name="error"/> for an unknown type
        ///   name or an unconvertible value (feature api-error-contract E3) instead of throwing.
        /// </summary>
        private static bool TryConvertLiteral(LiteralSpecification literal, out IComparable value, out string error)
        {
            value = null;
            error = null;

            if (!TryResolveType(literal.FullQualifiedTypeName, out var targetType))
            {
                error = String.Format("Unknown type name '{0}'.", literal.FullQualifiedTypeName);
                return false;
            }

            try
            {
                value = targetType == null
                    ? literal.Value
                    : (IComparable)Convert.ChangeType(literal.Value, targetType);
                return true;
            }
            catch (Exception ex) when (ex is InvalidCastException || ex is FormatException || ex is OverflowException || ex is ArgumentNullException)
            {
                error = String.Format("The literal value could not be converted to '{0}': {1}",
                    literal.FullQualifiedTypeName, ex.Message);
                return false;
            }
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
        public ActionResult<IEnumerable<int>> IndexScan([FromBody] IndexScanSpecification definition)
        {
            if (definition == null || definition.Literal == null)
            {
                return BadRequest("An index scan specification with a literal is required.");
            }

            if (!TryConvertLiteral(definition.Literal, out var value, out var error))
            {
                return BadRequest(error);
            }

            IReadOnlyList<AGraphElementModel> graphElements;
            return _fallen8.IndexScan(out graphElements, definition.IndexId, value, definition.Operator)
                       ? new ActionResult<IEnumerable<int>>(CreateResult(graphElements, definition.ResultType))
                       : new ActionResult<IEnumerable<int>>(Enumerable.Empty<Int32>());
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
        public ActionResult<IEnumerable<int>> RangeIndexScan([FromBody] RangeIndexScanSpecification definition)
        {
            if (definition == null)
            {
                return BadRequest("A range scan specification is required.");
            }

            // Guarded type resolution + conversion of both limits (feature api-error-contract E3).
            if (!TryResolveType(definition.FullQualifiedTypeName, out var limitType))
            {
                return BadRequest(String.Format("Unknown type name '{0}'.", definition.FullQualifiedTypeName));
            }

            IComparable left, right;
            try
            {
                left = (IComparable)Convert.ChangeType(definition.LeftLimit, limitType ?? typeof(string));
                right = (IComparable)Convert.ChangeType(definition.RightLimit, limitType ?? typeof(string));
            }
            catch (Exception ex) when (ex is InvalidCastException || ex is FormatException || ex is OverflowException || ex is ArgumentNullException)
            {
                return BadRequest(String.Format("A range limit could not be converted to '{0}': {1}",
                    definition.FullQualifiedTypeName, ex.Message));
            }

            IReadOnlyList<AGraphElementModel> graphElements;
            return _fallen8.RangeIndexScan(out graphElements, definition.IndexId, left, right, definition.IncludeLeft,
                                           definition.IncludeRight)
                       ? new ActionResult<IEnumerable<int>>(CreateResult(graphElements, definition.ResultType))
                       : new ActionResult<IEnumerable<int>>(Enumerable.Empty<Int32>());
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
        /// Adds (or replaces) an element's embedding vector in a vector index
        /// </summary>
        /// <param name="indexId">The ID of the vector index</param>
        /// <param name="definition">The element and its vector - explicit ("vector") or read from a float[] property ("propertyId")</param>
        /// <returns>True when the vector was indexed</returns>
        /// <remarks>
        /// One vector per element: adding again replaces. The generic PUT /index/{indexId} add
        /// path cannot express a float[] key, which is why the vector family has this typed
        /// endpoint (like fulltext and spatial have theirs).
        ///
        /// Sample request (explicit mode):
        ///
        ///     PUT /index/vector/myEmbeddings
        ///     {
        ///        "graphElementId": 42,
        ///        "vector": [0.12, -0.5, 0.33]
        ///     }
        ///
        /// Sample request (property mode - reads the element's float[] property):
        ///
        ///     PUT /index/vector/myEmbeddings
        ///     {
        ///        "graphElementId": 42,
        ///        "propertyId": "embedding"
        ///     }
        /// </remarks>
        /// <response code="200">The vector was indexed (add-again replaced the previous vector)</response>
        /// <response code="400">Not a vector index, neither/both modes supplied, wrong dimension, NaN/Infinity components, zero-norm vector under Cosine, or the named property is missing / not a float[]</response>
        /// <response code="404">The index or the graph element does not exist</response>
        [HttpPut("/index/vector/{indexId}")]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult AddToVectorIndex([FromRoute] String indexId, [FromBody] VectorIndexAddSpecification definition)
        {
            if (definition == null)
            {
                return BadRequest("A vector add specification is required.");
            }

            if (!_fallen8.IndexFactory.TryGetIndex(out var index, indexId))
            {
                return NotFound(String.Format("No index named '{0}'.", indexId));
            }

            if (!(index is IVectorIndex vectorIndex))
            {
                return BadRequest(String.Format("Index '{0}' is not a vector index.", indexId));
            }

            if (!_fallen8.TryGetGraphElement(out var element, definition.GraphElementId))
            {
                return NotFound(String.Format("Could not find graph element with id {0}.", definition.GraphElementId));
            }

            var hasVector = definition.Vector != null;
            var hasProperty = !String.IsNullOrEmpty(definition.PropertyId);
            if (hasVector == hasProperty)
            {
                return BadRequest("Exactly one of 'vector' / 'propertyId' must be supplied.");
            }

            Single[] vector;
            if (hasVector)
            {
                vector = definition.Vector;
            }
            else
            {
                if (!element.TryGetProperty<Object>(out var propertyValue, definition.PropertyId))
                {
                    return BadRequest(String.Format("Element {0} carries no property '{1}'.",
                        definition.GraphElementId, definition.PropertyId));
                }

                vector = propertyValue as Single[];
                if (vector == null)
                {
                    return BadRequest(String.Format("Property '{0}' on element {1} is not a float[].",
                        definition.PropertyId, definition.GraphElementId));
                }
            }

            if (vector.Length != vectorIndex.Dimension)
            {
                return BadRequest(String.Format("The vector has dimension {0}; index '{1}' requires {2}.",
                    vector.Length, indexId, vectorIndex.Dimension));
            }

            if (VectorIndex.HasNonFiniteComponent(vector))
            {
                return BadRequest("The vector contains NaN or Infinity components; only finite values can rank.");
            }

            if (vectorIndex.Metric == VectorDistanceMetric.Cosine && VectorIndex.IsZeroNorm(vector))
            {
                return BadRequest("A zero-norm vector cannot rank under the Cosine metric.");
            }

            vectorIndex.AddOrUpdate(vector, element);
            return Ok(true);
        }

        /// <summary>
        /// Finds the k nearest neighbours of a query vector in a vector index
        /// </summary>
        /// <param name="definition">The kNN query: index, query vector, k, optional kind/label constraints</param>
        /// <returns>The hits best-first with raw scores, plus the metric and its direction</returns>
        /// <remarks>
        /// Exact brute-force kNN (SIMD): deterministic ordering - best score first, ties broken
        /// by ascending element id. Constraints are applied BEFORE scoring, so the returned k are
        /// k MATCHING elements. Removed elements never appear. The GraphRAG recipe: feed the
        /// returned element ids into the existing traversal surface (POST /path, PUT /subgraph,
        /// property reads) - similarity search lands ON the graph.
        ///
        /// Sample request:
        ///
        ///     POST /scan/index/vector
        ///     {
        ///        "indexId": "myEmbeddings",
        ///        "query": [0.1, 0.2, 0.3],
        ///        "k": 10,
        ///        "kind": "vertex",
        ///        "label": "person"
        ///     }
        /// </remarks>
        /// <response code="200">Returns the k best-scoring matching elements (fewer when the corpus is smaller)</response>
        /// <response code="400">Not a vector index, wrong query dimension, NaN/Infinity components, k outside [1, 1024], zero-norm query under Cosine, or an unknown kind value</response>
        /// <response code="404">The index does not exist</response>
        [HttpPost("/scan/index/vector")]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(VectorSearchResultREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult VectorIndexScan([FromBody] VectorIndexScanSpecification definition)
        {
            if (definition == null || definition.Query == null)
            {
                return BadRequest("A vector scan specification with a query vector is required.");
            }

            if (!_fallen8.IndexFactory.TryGetIndex(out var index, definition.IndexId))
            {
                return NotFound(String.Format("No index named '{0}'.", definition.IndexId));
            }

            if (!(index is IVectorIndex vectorIndex))
            {
                return BadRequest(String.Format("Index '{0}' is not a vector index.", definition.IndexId));
            }

            VectorSearchConstraint constraint = null;
            if (!String.IsNullOrEmpty(definition.Kind) || definition.Label != null)
            {
                constraint = new VectorSearchConstraint { Label = definition.Label };
                switch (definition.Kind)
                {
                    case null:
                    case "":
                    case "any":
                        constraint.Kind = VectorSearchElementKind.Any;
                        break;
                    case "vertex":
                        constraint.Kind = VectorSearchElementKind.Vertex;
                        break;
                    case "edge":
                        constraint.Kind = VectorSearchElementKind.Edge;
                        break;
                    default:
                        return BadRequest(String.Format("'{0}' is not a valid kind. Expected vertex, edge or any.", definition.Kind));
                }
            }

            if (!vectorIndex.TryNearestNeighbors(out var result, definition.Query, definition.K, constraint))
            {
                return BadRequest(String.Format(
                    "Invalid kNN query: the query must have dimension {0} with finite components, k must be within [1, {1}], and a Cosine query must not be zero-norm.",
                    vectorIndex.Dimension, VectorIndex.MaxK));
            }

            var results = new List<VectorScoredElementREST>(result.Entries.Count);
            foreach (var entry in result.Entries)
            {
                results.Add(new VectorScoredElementREST { GraphElementId = entry.Element.Id, Score = entry.Score });
            }

            return Ok(new VectorSearchResultREST
            {
                Metric = result.Metric.ToString(),
                HigherIsBetter = result.HigherIsBetter,
                Results = results
            });
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
        /// <param name="waitForCompletion">When true, waits for the transaction to complete before responding</param>
        /// <remarks>
        /// Sample request:
        ///
        ///     PUT /graphelement/123/age
        ///     {
        ///        "propertyValue": 35,
        ///        "fullQualifiedTypeName": "System.Int32"
        ///     }
        /// </remarks>
        /// <response code="202">Property addition accepted (and committed when waitForCompletion is true)</response>
        /// <response code="400">Malformed request body / invalid property specification. (A non-existent graph element is NOT a 400: an out-of-range id rolls back with an internal error → 500, and an in-range/absent id is a no-op → 202.)</response>
        /// <response code="500">The transaction was rolled back with an internal error (only when waitForCompletion is true)</response>
        [HttpPut("/graphelement/{graphElementIdentifier}/{propertyIdString}")]
        [Consumes("application/json")]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> AddProperty([FromRoute] int graphElementIdentifier, [FromRoute] string propertyIdString, [FromBody] PropertySpecification definition, [FromQuery] bool waitForCompletion = false)
        {
            // A non-integer graphElementIdentifier now fails route binding -> 400 ProblemDetails
            // (feature api-error-contract E2), instead of Convert.ToInt32 throwing FormatException -> 500.
            if (definition == null)
            {
                return BadRequest("A property specification body is required.");
            }

            var graphElementId = graphElementIdentifier;
            var propertyId = propertyIdString;

            // Guarded type resolution (E3): an unknown type name is a 400, not a thrown TypeLoadException.
            if (!TryResolveType(definition.FullQualifiedTypeName, out var targetType))
            {
                return BadRequest(String.Format("Unknown type name '{0}'.", definition.FullQualifiedTypeName));
            }

            object property;
            try
            {
                property = targetType == null
                    ? definition.PropertyValue
                    : Convert.ChangeType(definition.PropertyValue, targetType);
            }
            catch (Exception ex) when (ex is InvalidCastException || ex is FormatException || ex is OverflowException || ex is ArgumentNullException)
            {
                return BadRequest(String.Format("The property value could not be converted to '{0}': {1}",
                    definition.FullQualifiedTypeName, ex.Message));
            }

            // Definition must be constructed here: the nested-initializer form assigns into
            // the property's default value, which is null -> NullReferenceException.
            AddPropertyTransaction tx = new AddPropertyTransaction()
            {
                Definition = new PropertyAddDefinition()
                {
                    GraphElementId = graphElementId,
                    PropertyId = propertyId,
                    Property = property
                }
            };

            var transactionTask = _fallen8.EnqueueTransaction(tx);

            if (waitForCompletion)
            {
                await transactionTask.Completion;

                // The worker rolls a faulting transaction back (correctness-fixes B6). When the
                // caller waited for the outcome, a rolled-back write must not be reported as success.
                if (transactionTask.TransactionState == TransactionState.RolledBack)
                {
                    return RolledBackResult(transactionTask.FailureReason);
                }
            }

            return Accepted();

        }

        /// <summary>
        /// Removes a property from a graph element (vertex or edge)
        /// </summary>
        /// <param name="graphElementIdentifier">The ID of the graph element</param>
        /// <param name="propertyIdString">The ID/key of the property to remove</param>
        /// <param name="waitForCompletion">When true, waits for the transaction to complete before responding</param>
        /// <remarks>
        /// Sample request:
        ///
        ///     DELETE /graphelement/123/age
        /// </remarks>
        /// <response code="202">Property removal accepted (and committed when waitForCompletion is true)</response>
        /// <response code="400">Malformed request (e.g. a non-integer element id). (A non-existent element/property is NOT a 400: an out-of-range id → 500, an in-range/absent id is a no-op → 202.)</response>
        /// <response code="500">The transaction was rolled back with an internal error (only when waitForCompletion is true)</response>
        [HttpDelete("/graphelement/{graphElementIdentifier}/{propertyIdString}")]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> TryRemoveProperty([FromRoute] int graphElementIdentifier, [FromRoute] string propertyIdString, [FromQuery] bool waitForCompletion = false)
        {
            // A non-integer id fails route binding -> 400 (feature api-error-contract E2).
            var graphElementId = graphElementIdentifier;
            var propertyId = propertyIdString;

            RemovePropertyTransaction tx = new RemovePropertyTransaction()
            {
                GraphElementId = graphElementId,
                PropertyId = propertyId
            };

            var transactionTask = _fallen8.EnqueueTransaction(tx);

            if (waitForCompletion)
            {
                await transactionTask.Completion;

                // The worker rolls a faulting transaction back (correctness-fixes B6). When the
                // caller waited for the outcome, a rolled-back write must not be reported as success.
                if (transactionTask.TransactionState == TransactionState.RolledBack)
                {
                    return RolledBackResult(transactionTask.FailureReason);
                }
            }

            return Accepted();

        }

        /// <summary>
        /// Removes a graph element (vertex or edge) from the graph
        /// </summary>
        /// <param name="graphElementIdentifier">The ID of the graph element to remove</param>
        /// <param name="waitForCompletion">When true, waits for the transaction to complete before responding</param>
        /// <remarks>
        /// Sample request:
        ///
        ///     DELETE /graphelement/123
        /// </remarks>
        /// <response code="202">Graph element removal accepted (and committed when waitForCompletion is true)</response>
        /// <response code="400">Malformed request (e.g. a non-integer element id). (A non-existent graph element is NOT a 400: an out-of-range id → 500, an in-range/absent id is a no-op → 202.)</response>
        /// <response code="500">The transaction was rolled back with an internal error (only when waitForCompletion is true)</response>
        [HttpDelete("/graphelement/{graphElementIdentifier}")]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> TryRemoveGraphElement([FromRoute] int graphElementIdentifier, [FromQuery] bool waitForCompletion = false)
        {
            // A non-integer id fails route binding -> 400 (feature api-error-contract E2).
            var graphElementId = graphElementIdentifier;

            RemoveGraphElementTransaction tx = new RemoveGraphElementTransaction()
            {
                GraphElementId = graphElementId
            };

            var transactionTask = _fallen8.EnqueueTransaction(tx);

            if (waitForCompletion)
            {
                await transactionTask.Completion;

                // The worker rolls a faulting transaction back (correctness-fixes B6). When the
                // caller waited for the outcome, a rolled-back write must not be reported as success.
                if (transactionTask.TransactionState == TransactionState.RolledBack)
                {
                    return RolledBackResult(transactionTask.FailureReason);
                }
            }

            return Accepted();
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
        public ActionResult<List<PathREST>> CalculateShortestPath([FromRoute] Int32 from, [FromRoute] Int32 to, [FromBody] PathSpecification definition)
        {
            // Always initialize with empty list to avoid returning null
            List<PathREST> result = new List<PathREST>();

            try
            {
                if (definition == null)
                {
                    definition = new PathSpecification();
                }

                // Request-shape-aware dynamic-code gate (feature stored-query-library): only a
                // request that INTRODUCES code - any inline filter/cost fragment - requires the
                // EnableDynamicCodeExecution capability. A storedQuery reference or a filterless
                // request compiles no user-supplied code and passes with the switch off.
                // Authentication itself is unchanged (the fallback policy applies as on every
                // endpoint); this replaces the former endpoint-level DynamicCodePolicy, which
                // gated the whole endpoint regardless of request shape.
                if (CarriesInlineCode(definition))
                {
                    var denied = DenyUnlessDynamicCodeCapability();
                    if (denied != null)
                    {
                        return denied;
                    }
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
                    var pathDefinition = new ShortestPathDefinition
                    {
                        SourceVertexId = from,
                        DestinationVertexId = to,
                        MaxDepth = definition.MaxDepth,
                        MaxPathWeight = definition.MaxPathWeight,
                        MaxResults = definition.MaxResults,
                        EdgePropertyFilter = traverser.EdgePropertyFilter(),
                        VertexFilter = traverser.VertexFilter(),
                        EdgeFilter = traverser.EdgeFilter(),
                        EdgeCost = traverser.EdgeCost(),
                        VertexCost = traverser.VertexCost()
                    };

                    List<Core.Algorithms.Path.Path> paths;
                    if (_fallen8.TryCalculateShortestPath(
                        out paths,
                        definition.PathAlgorithmName ?? "BLS", // Default to BLS if not specified
                        pathDefinition))
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
        ///   Builds the error result returned when a waited-on mutation transaction was rolled back,
        ///   mapping the structured <see cref="TransactionFailureReason"/> to the appropriate HTTP
        ///   status: a client-caused rollback surfaces as a 4xx, an internal fault as a 500.
        /// </summary>
        private IActionResult RolledBackResult(TransactionFailureReason reason)
        {
            switch (reason)
            {
                case TransactionFailureReason.InvalidInput:
                    return StatusCode(StatusCodes.Status400BadRequest,
                        "The transaction was rolled back: the request was invalid.");

                case TransactionFailureReason.NotFound:
                    return StatusCode(StatusCodes.Status404NotFound,
                        "The transaction was rolled back: a referenced graph element does not exist.");

                case TransactionFailureReason.QuotaExceeded:
                case TransactionFailureReason.Conflict:
                    return StatusCode(StatusCodes.Status409Conflict,
                        "The transaction was rolled back: the request conflicts with the current state or a resource quota.");

                default:
                    return StatusCode(StatusCodes.Status500InternalServerError,
                        "The transaction was rolled back; the operation did not complete.");
            }
        }

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
        public void Load(SerializationReader reader, IFallen8 fallen8)
        {
        }

        [NonAction]
        public void Shutdown()
        {
        }

        #endregion
    }
}
