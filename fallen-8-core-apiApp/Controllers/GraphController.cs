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

        [HttpPut("/vertex")]
        [Consumes("application/json")]
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

        [HttpPut("/edge")]
        [Consumes("application/json")]
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

        [HttpGet("/graph")]
        [Produces("application/json")]
        public Graph GetGraph([FromQuery] int maxElements = 1000)
        {
            var result = new Graph();

            var edges = _fallen8.GetAllEdges().Take(maxElements);
            result.Edges = edges.Select(_ => new Edge(_)).ToList();

            var vertices = _fallen8.GetAllVertices().Take(maxElements);
            result.Vertices = vertices.Select(_ => new Vertex(_)).ToList();

            return result;
        }

        [HttpGet("/edge/{edgeIdentifier}/source")]
        public int GetSourceVertexForEdge([FromRoute] Int32 edgeIdentifier)
        {
            EdgeModel edge;
            if (_fallen8.TryGetEdge(out edge, edgeIdentifier))
            {
                return edge.SourceVertex.Id;
            }

            throw new WebException(String.Format("Could not find edge with id {0}.", edgeIdentifier));
        }

        [HttpGet("/edge/{edgeIdentifier}/target")]
        public int GetTargetVertexForEdge([FromRoute] Int32 edgeIdentifier)
        {
            EdgeModel edge;
            if (_fallen8.TryGetEdge(out edge, edgeIdentifier))
            {
                return edge.TargetVertex.Id;
            }

            throw new WebException(String.Format("Could not find edge with id {0}.", edgeIdentifier));
        }


        [HttpGet("/vertex/{vertexIdentifier}/edges/out")]
        [Produces("application/json")]
        public List<String> GetAllAvailableOutEdgesOnVertex([FromRoute] Int32 vertexIdentifier)
        {
            VertexModel vertex;
            return _fallen8.TryGetVertex(out vertex, vertexIdentifier)
                       ? vertex.GetOutgoingEdgeIds()
                       : null;
        }


        [HttpGet("/vertex/{vertexIdentifier}/edges/in")]
        [Produces("application/json")]
        public List<String> GetAllAvailableIncEdgesOnVertex([FromRoute] Int32 vertexIdentifier)
        {
            VertexModel vertex;
            return _fallen8.TryGetVertex(out vertex, vertexIdentifier)
                       ? vertex.GetIncomingEdgeIds()
                       : null;
        }

        [HttpGet("/vertex/{vertexIdentifier}/edges/out/{edgePropertyIdentifier}")]
        [Produces("application/json")]
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


        [HttpGet("/vertex/{vertexIdentifier}/edges/in/{edgePropertyIdentifier}")]
        [Produces("application/json")]
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


        [HttpPost("/scan/graph/property/{propertyId}")]
        [Produces("application/json")]
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

        [HttpPost("/scan/index/all")]
        [Produces("application/json")]
        [Consumes("application/json")]
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

        [HttpPost("/scan/index/range")]
        [Produces("application/json")]
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

        [HttpPost("/scan/index/fulltext")]
        [Produces("application/json")]
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

        [HttpPost("/scan/index/spatial")]
        [Produces("application/json")]
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

        [HttpPut("/graphelement/{graphElementIdentifier}/{propertyIdString}")]
        [Consumes("application/json")]
        public void AddProperty([FromRoute] string graphElementIdString, [FromRoute] string propertyIdString, [FromBody] PropertySpecification definition)
        {
            var graphElementId = Convert.ToInt32(graphElementIdString);
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

        [HttpDelete("/graphelement/{graphElementIdentifier}/{propertyIdString}")]
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

        [HttpDelete("/graphelement/{graphElementIdentifier}")]
        public void TryRemoveGraphElement([FromRoute] string graphElementIdentifier)
        {
            var graphElementId = Convert.ToInt32(graphElementIdentifier);

            RemoveGraphElementTransaction tx = new RemoveGraphElementTransaction()
            {
                GraphElementId = graphElementId
            };

            _fallen8.EnqueueTransaction(tx);
        }

        [HttpGet("/vertex/{vertexIdentifier}/edges/indegree")]
        [Produces("application/json")]
        public uint GetInDegree([FromRoute] string vertexIdentifier)
        {
            VertexModel vertex;
            if (_fallen8.TryGetVertex(out vertex, Convert.ToInt32(vertexIdentifier)))
            {
                return vertex.GetInDegree();
            }
            return 0;
        }

        [HttpGet("/vertex/{vertexIdentifier}/edges/outdegree")]
        [Produces("application/json")]
        public uint GetOutDegree([FromRoute] string vertexIdentifier)
        {
            VertexModel vertex;
            if (_fallen8.TryGetVertex(out vertex, Convert.ToInt32(vertexIdentifier)))
            {
                return vertex.GetOutDegree();
            }
            return 0;
        }

        [HttpGet("/vertex/{vertexIdentifier}/edges/in/{edgePropertyIdentifier}/degree")]
        [Produces("application/json")]
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

        [HttpGet("/vertex/{vertexIdentifier}/edges/out/{edgePropertyIdentifier}/degree")]
        [Produces("application/json")]
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

        [HttpPost("/path/{from}/to/{to}")]
        [Produces("application/json")]
        [Consumes("application/json")]
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

        [HttpPost("/index")]
        [Consumes("application/json")]
        [Produces("application/json")]
        public bool CreateIndex([FromBody] PluginSpecification definition)
        {
            //TODO: return IIndex object representation
            IIndex result;
            return _fallen8.IndexFactory.TryCreateIndex(out result, definition.UniqueId, definition.PluginType, ServiceHelper.CreatePluginOptions(definition.PluginOptions));
        }

        [HttpPut("/index/{indexId}")]
        [Consumes("application/json")]
        [Produces("application/json")]
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

        [HttpDelete("/index/{indexId}/propertyValue")]
        [Consumes("application/json")]
        [Produces("application/json")]
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

        [HttpDelete("/index/{indexId}/{graphElementId}")]
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

        [HttpDelete("/index/{indexId}")]
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
