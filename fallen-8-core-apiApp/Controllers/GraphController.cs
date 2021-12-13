using Microsoft.AspNetCore.Mvc;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CSharp;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.App.Interfaces;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.Helper;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Index.Fulltext;
using NoSQL.GraphDB.Core.Index.Spatial;
using NoSQL.GraphDB.Core.Log;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Serializer;
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace NoSQL.GraphDB.App.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class GraphController : ControllerBase, IRESTService
    {
        #region Data

        /// <summary>
        ///   The internal Fallen-8 instance
        /// </summary>
        private readonly Fallen8 _fallen8;

        private readonly ILogger<GraphController> _logger;


        #region Code generation stuff

        private const String PathDelegateClassName = "NoSQL.GraphDB.Algorithms.Path.PathDelegateProvider";

        private readonly String _pathDelegateClassPrefix = CodeGeneration.Prefix;
        private readonly String _pathDelegateClassSuffix = CodeGeneration.Suffix;

        private const String VertexFilterMethodName = "VertexFilter";
        private const String EdgeFilterMethodName = "EdgeFilter";
        private const String EdgePropertyFilterMethodName = "EdgePropertyFilter";
        private const String VertexCostMethodName = "VertexCost";
        private const String EdgeCostMethodName = "EdgeCost";

        #endregion

        #endregion

        public GraphController(ILogger<GraphController> logger, Fallen8 fallen8)
        {
            _logger = logger;

            _fallen8 = fallen8;

            //todo: add compiler stuff later
        }

        #region IDisposable Members

        public void Dispose()
        {
        }

        #endregion

        #region IGraphService implementation

        [HttpPut("/vertex")]
        [Consumes("application/json")]
        public int AddVertex([FromBody] VertexSpecification definition)
        {
            #region initial checks

            if (definition == null)
            {
                throw new ArgumentNullException("definition");
            }

            #endregion

            return _fallen8.CreateVertex(definition.CreationDate, ServiceHelper.GenerateProperties(definition.Properties)).Id;
        }

        [HttpPut("/edge")]
        [Consumes("application/json")]
        public int AddEdge(EdgeSpecification definition)
        {
            #region initial checks

            if (definition == null)
            {
                throw new ArgumentNullException("definition");
            }

            #endregion

            return
                _fallen8.CreateEdge(definition.SourceVertex, definition.EdgePropertyId, definition.TargetVertex,
                                    definition.CreationDate, ServiceHelper.GenerateProperties(definition.Properties)).Id;
        }

        /// <summary>
        ///   Gets the graph element properties.
        /// </summary>
        /// <returns> The graph element properties. </returns>
        /// <param name='graphElementIdentifier'> Vertex identifier. </param>
        [HttpGet("/graphelement/{graphElementIdentifier}")]
        [Produces("application/json")]
        public GraphElementProperties GetAllGraphelementProperties([FromQuery] Int32 graphElementIdentifier)
        {
            AGraphElement vertex;
            if (_fallen8.TryGetGraphElement(out vertex, graphElementIdentifier))
            {
                return new GraphElementProperties
                {
                    Id = vertex.Id,
                    CreationDate = DateHelper.GetDateTimeFromUnixTimeStamp(vertex.CreationDate),
                    ModificationDate = DateHelper.GetDateTimeFromUnixTimeStamp(vertex.CreationDate + vertex.ModificationDate),
                    Properties = vertex.GetAllProperties().Select(_ => new Property { PropertyId = _.PropertyId, PropertyValue = _.Value.ToString() }).ToList()
                };
            }

            return null;
        }


        [HttpGet("/edge/{edgeIdentifier}/source")]
        public int GetSourceVertexForEdge([FromQuery] Int32 edgeIdentifier)
        {
            EdgeModel edge;
            if (_fallen8.TryGetEdge(out edge, edgeIdentifier))
            {
                return edge.SourceVertex.Id;
            }

            throw new WebException(String.Format("Could not find edge with id {0}.", edgeIdentifier));
        }

        [HttpGet("/edge/{edgeIdentifier}/target")]
        public int GetTargetVertexForEdge([FromQuery] Int32 edgeIdentifier)
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
        public List<ushort> GetAllAvailableOutEdgesOnVertex([FromQuery] Int32 vertexIdentifier)
        {
            VertexModel vertex;
            return _fallen8.TryGetVertex(out vertex, vertexIdentifier)
                       ? vertex.GetOutgoingEdgeIds()
                       : null;
        }


        [HttpGet("/vertex/{vertexIdentifier}/edges/in")]
        [Produces("application/json")]
        public List<ushort> GetAllAvailableIncEdgesOnVertex([FromQuery] Int32 vertexIdentifier)
        {
            VertexModel vertex;
            return _fallen8.TryGetVertex(out vertex, vertexIdentifier)
                       ? vertex.GetIncomingEdgeIds()
                       : null;
        }

        [HttpGet("/vertex/{vertexIdentifier}/edges/out/{edgePropertyIdentifier}")]
        [Produces("application/json")]
        public List<int> GetOutgoingEdges([FromQuery] Int32 vertexIdentifier, [FromQuery] UInt16 edgePropertyIdentifier)
        {
            VertexModel vertex;
            if (_fallen8.TryGetVertex(out vertex, vertexIdentifier))
            {
                ReadOnlyCollection<EdgeModel> edges;
                if (vertex.TryGetOutEdge(out edges, edgePropertyIdentifier))
                {
                    return edges.Select(_ => _.Id).ToList();
                }
            }
            return null;
        }


        [HttpGet("/vertex/{vertexIdentifier}/edges/in/{edgePropertyIdentifier}")]
        [Produces("application/json")]
        public List<int> GetIncomingEdges([FromQuery] Int32 vertexIdentifier, [FromQuery] UInt16 edgePropertyIdentifier)
        {
            VertexModel vertex;
            if (_fallen8.TryGetVertex(out vertex, vertexIdentifier))
            {
                ReadOnlyCollection<EdgeModel> edges;
                if (vertex.TryGetInEdge(out edges, edgePropertyIdentifier))
                {
                    return edges.Select(_ => _.Id).ToList();
                }
            }
            return null;
        }


        [HttpPost("/scan/graph/property/{propertyId}")]
        [Produces("application/json")]
        public IEnumerable<int> GraphScan([FromQuery] String propertyIdString, [FromBody] ScanSpecification definition)
        {
            #region initial checks

            if (definition == null)
            {
                throw new ArgumentNullException("definition");
            }

            #endregion

            var propertyId = Convert.ToUInt16(propertyIdString);

            IComparable value = definition.Literal.FullQualifiedTypeName == null
                ? definition.Literal.Value
                : (IComparable)Convert.ChangeType(definition.Literal.Value,
                                                         Type.GetType(definition.Literal.FullQualifiedTypeName, true,
                                                                      true));

            List<AGraphElement> graphElements;
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

            ReadOnlyCollection<AGraphElement> graphElements;
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

            ReadOnlyCollection<AGraphElement> graphElements;
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

            AGraphElement graphElement;
            if (_fallen8.TryGetGraphElement(out graphElement, definition.GraphElementId))
            {
                IIndex idx;
                if (_fallen8.IndexFactory.TryGetIndex(out idx, definition.IndexId))
                {
                    var spatialIndex = idx as ISpatialIndex;
                    if (spatialIndex != null)
                    {
                        ReadOnlyCollection<AGraphElement> result;
                        return spatialIndex.SearchDistance(out result, definition.Distance, graphElement)
                            ? result.Select(_ => _.Id)
                            : null;
                    }
                    Logger.LogError(String.Format("The index with id {0} is no spatial index.", definition.IndexId));
                    return null;
                }
                Logger.LogError(String.Format("Could not find index {0}.", definition.IndexId));
                return null;
            }
            Logger.LogError(String.Format("Could not find graph element {0}.", definition.GraphElementId));
            return null;
        }

        [HttpPut("/graphelement/{graphElementIdentifier}/{propertyIdString}")]
        [Consumes("application/json")]
        public bool TryAddProperty([FromQuery] string graphElementIdString, [FromQuery] string propertyIdString, [FromBody] PropertySpecification definition)
        {
            var graphElementId = Convert.ToInt32(graphElementIdString);
            var propertyId = Convert.ToUInt16(propertyIdString);

            var property = Convert.ChangeType(
                definition.PropertyValue,
                Type.GetType(definition.FullQualifiedTypeName, true, true));

            return _fallen8.TryAddProperty(graphElementId, propertyId, property);
        }

        [HttpDelete("/graphelement/{graphElementIdentifier}/{propertyIdString}")]
        public bool TryRemoveProperty([FromQuery] string graphElementIdentifier, [FromQuery] string propertyIdString)
        {
            var graphElementId = Convert.ToInt32(graphElementIdentifier);
            var propertyId = Convert.ToUInt16(propertyIdString);

            return _fallen8.TryRemoveProperty(graphElementId, propertyId);
        }

        [HttpDelete("/graphelement/{graphElementIdentifier}")]
        public bool TryRemoveGraphElement([FromQuery] string graphElementIdentifier)
        {
            var graphElementId = Convert.ToInt32(graphElementIdentifier);

            return _fallen8.TryRemoveGraphElement(graphElementId);
        }

        [HttpGet("/vertex/{vertexIdentifier}/edges/indegree")]
        [Produces("application/json")]
        public uint GetInDegree([FromQuery] string vertexIdentifier)
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
        public uint GetOutDegree([FromQuery] string vertexIdentifier)
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
        public uint GetInEdgeDegree([FromQuery] string vertexIdentifier, [FromQuery] string edgePropertyIdentifier)
        {
            VertexModel vertex;
            if (_fallen8.TryGetVertex(out vertex, Convert.ToInt32(vertexIdentifier)))
            {
                ReadOnlyCollection<EdgeModel> edges;
                if (vertex.TryGetInEdge(out edges, Convert.ToUInt16(edgePropertyIdentifier)))
                {
                    return Convert.ToUInt32(edges.Count);
                }
            }
            return 0;
        }

        [HttpGet("/vertex/{vertexIdentifier}/edges/out/{edgePropertyIdentifier}/degree")]
        [Produces("application/json")]
        public uint GetOutEdgeDegree([FromQuery] string vertexIdentifier, [FromQuery] string edgePropertyIdentifier)
        {
            VertexModel vertex;
            if (_fallen8.TryGetVertex(out vertex, Convert.ToInt32(vertexIdentifier)))
            {
                ReadOnlyCollection<EdgeModel> edges;
                if (vertex.TryGetOutEdge(out edges, Convert.ToUInt16(edgePropertyIdentifier)))
                {
                    return Convert.ToUInt32(edges.Count);
                }
            }
            return 0;
        }

        [HttpPost("/path/{from}/to/{to}")]
        [Produces("application/json")]
        [Consumes("application/json")]
        public List<PathREST> GetPaths([FromQuery] string from, [FromQuery] string to, [FromBody] PathSpecification definition)
        {
            if (definition != null)
            {
                var fromId = Convert.ToInt32(from);
                var toId = Convert.ToInt32(to);

                var sourceCode = CreateSource(definition);

                var options = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);

                var parsedSyntaxTree = SyntaxFactory.ParseSyntaxTree(sourceCode, options);

                var references = new MetadataReference[]
                {
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(System.Runtime.AssemblyTargetedPatchBandAttribute).Assembly.Location),
                    MetadataReference.CreateFromFile(typeof(Microsoft.CSharp.RuntimeBinder.CSharpArgumentInfo).Assembly.Location),
                };

                var compilation =CSharpCompilation.Create("customPath.dll",
                new[] { parsedSyntaxTree },
                references: references,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release,
                    assemblyIdentityComparer: DesktopAssemblyIdentityComparer.Default));

                using (var peStream = new MemoryStream())
                {
                    var result = compilation.Emit(peStream);

                    compilation.

                    if (!result.Success)
                    {
                        Console.WriteLine("Compilation done with error.");

                        var failures = result.Diagnostics.Where(diagnostic => diagnostic.IsWarningAsError || diagnostic.Severity == DiagnosticSeverity.Error);

                        foreach (var diagnostic in failures)
                        {
                            Console.Error.WriteLine("{0}: {1}", diagnostic.Id, diagnostic.GetMessage());
                        }

                        return null;
                    }

                }


                Type type = null; //get correct type

                var edgeCostDelegate = CreateEdgeCostDelegate(definition.Cost, type);
                var vertexCostDelegate = CreateVertexCostDelegate(definition.Cost, type);

                var edgePropertyFilterDelegate = CreateEdgePropertyFilterDelegate(definition.Filter, type);
                var vertexFilterDelegate = CreateVertexFilterDelegate(definition.Filter, type);
                var edgeFilterDelegate = CreateEdgeFilterDelegate(definition.Filter, type);


                List<Core.Algorithms.Path.Path> paths;
                if (_fallen8.CalculateShortestPath(
                    out paths,
                    definition.PathAlgorithmName,
                    fromId,
                    toId,
                    definition.MaxDepth,
                    definition.MaxPathWeight,
                    definition.MaxResults,
                    edgePropertyFilterDelegate,
                    vertexFilterDelegate,
                    edgeFilterDelegate,
                    edgeCostDelegate,
                    vertexCostDelegate))
                {
                    if (paths != null)
                    {
                        return new List<PathREST>(paths.Select(aPath => new PathREST(aPath)));
                    }
                }
            }
            return null;
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
        public bool AddToIndex([FromQuery] String indexId, [FromBody] IndexAddToSpecification definition)
        {
            IIndex idx;
            if (_fallen8.IndexFactory.TryGetIndex(out idx, indexId))
            {
                AGraphElement graphElement;
                if (_fallen8.TryGetGraphElement(out graphElement, definition.GraphElementId))
                {
                    idx.AddOrUpdate(ServiceHelper.CreateObject(definition.Key), graphElement);
                    return true;
                }

                Logger.LogError(String.Format("Could not find graph element {0}.", definition.GraphElementId));
                return false;
            }
            Logger.LogError(String.Format("Could not find index {0}.", indexId));
            return false;
        }

        [HttpDelete("/index/{indexId}/propertyValue")]
        [Consumes("application/json")]
        [Produces("application/json")]
        public bool RemoveKeyFromIndex([FromQuery] String indexId, [FromBody] PropertySpecification property)
        {
            IIndex idx;
            if (_fallen8.IndexFactory.TryGetIndex(out idx, indexId))
            {
                return idx.TryRemoveKey(ServiceHelper.CreateObject(property));
            }
            Logger.LogError(String.Format("Could not find index {0}.", indexId));
            return false;
        }

        [HttpDelete("/index/{indexId}/{graphElementId}")]
        public bool RemoveGraphElementFromIndex([FromQuery] String indexId, [FromQuery] Int32 graphElementId)
        {
            IIndex idx;
            if (_fallen8.IndexFactory.TryGetIndex(out idx, indexId))
            {
                AGraphElement graphElement;
                if (_fallen8.TryGetGraphElement(out graphElement, graphElementId))
                {
                    idx.RemoveValue(graphElement);
                    return true;
                }

                Logger.LogError(String.Format("Could not find graph element {0}.", graphElementId));
                return false;
            }
            Logger.LogError(String.Format("Could not find index {0}.", indexId));
            return false;
        }

        [HttpDelete("/index/{indexId}")]
        public bool DeleteIndex([FromQuery] String indexId)
        {
            return _fallen8.IndexFactory.TryDeleteIndex(indexId);
        }

        #endregion

        #region private helper

        /// <summary>
        /// Create the source for the code generation
        /// </summary>
        /// <param name="definition">The path specification</param>
        /// <returns>The source code</returns>
        private string CreateSource(PathSpecification definition)
        {
            //use https://carlos.mendible.com/2017/03/02/create-a-class-with-net-core-and-roslyn/ to generate the code


            if (definition == null) return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine(_pathDelegateClassPrefix);

            if (definition.Cost != null)
            {
                if (definition.Cost.Edge != null)
                {
                    sb.AppendLine(definition.Cost.Edge);
                }

                if (definition.Cost.Vertex != null)
                {
                    sb.AppendLine(definition.Cost.Vertex);
                }
            }

            if (definition.Filter != null)
            {
                if (definition.Filter.Edge != null)
                {
                    sb.AppendLine(definition.Filter.Edge);
                }

                if (definition.Filter.EdgeProperty != null)
                {
                    sb.AppendLine(definition.Filter.EdgeProperty);
                }

                if (definition.Filter.Vertex != null)
                {
                    sb.AppendLine(definition.Filter.Vertex);
                }
            }

            sb.AppendLine(_pathDelegateClassSuffix);

            return sb.ToString();
        }

        /// <summary>
        /// Creates an edge filter delegate
        /// </summary>
        /// <param name="pathFilterSpecification">Filter specification.</param>
        /// <param name="pathDelegateType">The path delegate type  </param>
        /// <returns>The delegate</returns>
        private static PathDelegates.EdgeFilter CreateEdgeFilterDelegate(PathFilterSpecification pathFilterSpecification, Type pathDelegateType)
        {
            if (pathFilterSpecification != null && !String.IsNullOrEmpty(pathFilterSpecification.Edge))
            {
                var method = pathDelegateType.GetMethod(EdgeFilterMethodName);

                return (PathDelegates.EdgeFilter)Delegate.CreateDelegate(typeof(PathDelegates.EdgeFilter), method);
            }
            return null;
        }

        /// <summary>
        /// Creates a vertex filter delegate
        /// </summary>
        /// <param name="pathFilterSpecification">Filter specification.</param>
        /// <param name="pathDelegateType">The path delegate type </param>
        /// <returns>The delegate</returns>
        private static PathDelegates.VertexFilter CreateVertexFilterDelegate(PathFilterSpecification pathFilterSpecification, Type pathDelegateType)
        {
            if (pathFilterSpecification != null && !String.IsNullOrEmpty(pathFilterSpecification.Vertex))
            {
                var method = pathDelegateType.GetMethod(VertexFilterMethodName);

                return (PathDelegates.VertexFilter)Delegate.CreateDelegate(typeof(PathDelegates.VertexFilter), method);
            }
            return null;
        }

        /// <summary>
        /// Creates an edge property filter delegate
        /// </summary>
        /// <param name="pathFilterSpecification">Filter specification.</param>
        /// <param name="pathDelegateType">The path delegate type </param>
        /// <returns>The delegate</returns>
        private static PathDelegates.EdgePropertyFilter CreateEdgePropertyFilterDelegate(PathFilterSpecification pathFilterSpecification, Type pathDelegateType)
        {
            if (pathFilterSpecification != null && !String.IsNullOrEmpty(pathFilterSpecification.EdgeProperty))
            {
                var method = pathDelegateType.GetMethod(EdgePropertyFilterMethodName);

                return (PathDelegates.EdgePropertyFilter)Delegate.CreateDelegate(typeof(PathDelegates.EdgePropertyFilter), method);
            }
            return null;
        }

        /// <summary>
        /// Creates a vertex cost delegate
        /// </summary>
        /// <param name="pathCostSpecification">Cost specificateion</param>
        /// <param name="pathDelegateType">The path delegate type </param>
        /// <returns>The delegate</returns>
        private static PathDelegates.VertexCost CreateVertexCostDelegate(PathCostSpecification pathCostSpecification, Type pathDelegateType)
        {
            if (pathCostSpecification != null && !String.IsNullOrEmpty(pathCostSpecification.Edge))
            {
                var method = pathDelegateType.GetMethod(VertexCostMethodName);

                return (PathDelegates.VertexCost)Delegate.CreateDelegate(typeof(PathDelegates.VertexCost), method);
            }
            return null;
        }

        /// <summary>
        /// Creates an edge cost delegate
        /// </summary>
        /// <param name="pathCostSpecification">Cost specificateion</param>
        /// <param name="pathDelegateType">The path delegate type</param>
        /// <returns>The delegate</returns>
        private static PathDelegates.EdgeCost CreateEdgeCostDelegate(PathCostSpecification pathCostSpecification, Type pathDelegateType)
        {
            if (pathCostSpecification != null && !String.IsNullOrEmpty(pathCostSpecification.Edge))
            {
                var method = pathDelegateType.GetMethod(EdgeCostMethodName);

                return (PathDelegates.EdgeCost)Delegate.CreateDelegate(typeof(PathDelegates.EdgeCost), method);
            }
            return null;
        }

        /// <summary>
        ///   Creats the result
        /// </summary>
        /// <param name="graphElements"> The graph elements </param>
        /// <param name="resultTypeSpecification"> The result specification </param>
        /// <returns> </returns>
        private static IEnumerable<int> CreateResult(IEnumerable<AGraphElement> graphElements,
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
