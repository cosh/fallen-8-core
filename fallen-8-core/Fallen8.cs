// MIT License
//
// Fallen8.cs
//
// Copyright (c) 2021 Henning Rauch
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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.Cache;
using NoSQL.GraphDB.Core.Error;
using NoSQL.GraphDB.Core.Expression;
using NoSQL.GraphDB.Core.Helper;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Index.Fulltext;
using NoSQL.GraphDB.Core.Index.Range;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Persistency;
using NoSQL.GraphDB.Core.Plugin;
using NoSQL.GraphDB.Core.Service;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Core
{
    public sealed class Fallen8 : AThreadSafeElement, IRead, IWrite, IDisposable
    {
        #region Data

        /// <summary>
        ///   The graph elements
        /// </summary>
        private  ImmutableList<AGraphElement> _graphElements;

        /// <summary>
        /// The delegate to find elements in the big list
        /// </summary>
        /// <param name="objectOfT">The to be analyzed object of T</param>
        /// <returns>True or false</returns>
        public delegate Boolean ElementSeeker(AGraphElement objectOfT);

        /// <summary>
        ///   The index factory.
        /// </summary>
        public IndexFactory IndexFactory
        {
            get; internal set;
        }

        /// <summary>
        ///   The index factory.
        /// </summary>
        public ServiceFactory ServiceFactory
        {
            get; internal set;
        }

        /// <summary>
        /// The count of edges
        /// </summary>
        public Int32 EdgeCount
        {
            get; private set;
        }

        /// <summary>
        /// The count of vertices
        /// </summary>
        public Int32 VertexCount
        {
            get; private set;
        }

        /// <summary>
        ///   The current identifier.
        /// </summary>
        private Int32 _currentId = 0;

        /// <summary>
        ///   Binary operator delegate.
        /// </summary>
        private delegate Boolean BinaryOperatorDelegate(IComparable property, IComparable literal);

        /// <summary>
        /// Cache for all kinds of plugins
        /// </summary>
        private readonly PluginCache _pluginCache = new PluginCache();

        /// <summary>
        /// Transaction manager
        /// </summary>
        private readonly TransactionManager _txManager;

        /// <summary>
        ///   The persisitency factory.
        /// </summary>
        private readonly PersistencyFactory _persistencyFactory;

        /// <summary>
        /// The logger
        /// </summary>
        private readonly ILogger<Fallen8> _logger;

        /// <summary>
        /// The logger factory
        /// </summary>
        internal readonly ILoggerFactory _loggerFactory;

        #endregion

        #region Constructor

        /// <summary>
        ///   Initializes a new instance of the Fallen-8 class.
        /// </summary>
        public Fallen8(ILoggerFactory loggerfactory)
        {
            _loggerFactory = loggerfactory;

            IndexFactory = new IndexFactory(loggerfactory);
            _graphElements = ImmutableList.Create<AGraphElement>();
            ServiceFactory = new ServiceFactory(this);
            IndexFactory.Indices.Clear();
            _txManager = new TransactionManager(this);
            _logger = loggerfactory.CreateLogger<Fallen8>();
            _persistencyFactory = new PersistencyFactory(loggerfactory);
        }

        /// <summary>
        ///   Initializes a new instance of the Fallen-8 class and loads the vertices from a save point.
        /// </summary>
        /// <param name='path'> Path to the save point. </param>
        public Fallen8(String path, ILoggerFactory loggerfactory)
            : this(loggerfactory)
        {
            Load_internal(path, true);
        }

        #endregion

        internal VertexModel CreateVertex_internal(UInt32 creationDate, PropertyContainer[] properties = null)
        {
            //create the new vertex
            var newVertex = new VertexModel(_currentId, creationDate, properties);

            //insert it
            _graphElements = _graphElements.Add(newVertex);

            //increment the id
            Interlocked.Increment(ref _currentId);

            //Increase the vertex count
            VertexCount++;
            return newVertex;
        }

        internal List<VertexModel> CreateVertices_internal(List<VertexDefinition> definitions)
        {
            var newVertices = new List<VertexModel>();

            if (definitions != null)
            {
                foreach (var aVertexDef in definitions)
                {
                    //create the new vertex
                    var newVertex = new VertexModel(_currentId, aVertexDef.CreationDate, aVertexDef.Properties);

                    newVertices.Add(newVertex);

                    //increment the id
                    Interlocked.Increment(ref _currentId);

                    //Increase the vertex count
                    VertexCount++;
                }
            }

            //insert it
            _graphElements = _graphElements.AddRange(newVertices);

            return newVertices;
        }

        public bool TryGetGraphElement(out AGraphElement result, int id)
        {
            if (ReadResource())
            {
                try
                {

                    result = _graphElements[id];
                    return result != null;
                }
                catch (ArgumentOutOfRangeException)
                {
                    result = null;
                    return false;
                }
                finally
                {
                    FinishReadResource();
                }
            }

            throw new CollisionException();
        }

        public bool TryGetEdge(out EdgeModel result, int id)
        {
            if (ReadResource())
            {
                try
                {
                    result = _graphElements[id] as EdgeModel;
                    return result != null;
                }
                catch (ArgumentOutOfRangeException)
                {
                    result = null;
                    return false;
                }
                finally
                {
                    FinishReadResource();
                }
            }

            throw new CollisionException();
        }

        public bool TryGetVertex(out VertexModel result, int id)
        {
            if (ReadResource())
            {
                try
                {
                    result = _graphElements[id] as VertexModel;
                    return result != null;
                }
                catch (ArgumentOutOfRangeException)
                {
                    result = null;
                    return false;
                }
                finally
                {
                    FinishReadResource();
                }
            }

            throw new CollisionException();
        }



        public bool GraphScan(out List<AGraphElement> result, ushort propertyId, IComparable literal, BinaryOperator binOp = BinaryOperator.Equals)
        {
            #region binary operation

            switch (binOp)
            {
                case BinaryOperator.Equals:
                    result = FindElements(BinaryEqualsMethod, literal, propertyId);
                    break;

                case BinaryOperator.Greater:
                    result = FindElements(BinaryGreaterMethod, literal, propertyId);
                    break;

                case BinaryOperator.GreaterOrEquals:
                    result = FindElements(BinaryGreaterOrEqualMethod, literal, propertyId);
                    break;

                case BinaryOperator.LowerOrEquals:
                    result = FindElements(BinaryLowerOrEqualMethod, literal, propertyId);
                    break;

                case BinaryOperator.Lower:
                    result = FindElements(BinaryLowerMethod, literal, propertyId);
                    break;

                case BinaryOperator.NotEquals:
                    result = FindElements(BinaryNotEqualsMethod, literal, propertyId);
                    break;

                default:
                    result = new List<AGraphElement>();

                    break;
            }

            #endregion

            return result.Count > 0;
        }

        public bool IndexScan(out ReadOnlyCollection<AGraphElement> result, string indexId, IComparable literal, BinaryOperator binOp = BinaryOperator.Equals)
        {
            IIndex index;
            if (!IndexFactory.TryGetIndex(out index, indexId))
            {
                result = null;
                return false;
            }

            #region binary operation

            switch (binOp)
            {
                case BinaryOperator.Equals:
                    if (!index.TryGetValue(out result, literal))
                    {
                        result = null;
                        return false;
                    }
                    break;

                case BinaryOperator.Greater:
                    result = FindElementsIndex(BinaryGreaterMethod, literal, index);
                    break;

                case BinaryOperator.GreaterOrEquals:
                    result = FindElementsIndex(BinaryGreaterOrEqualMethod, literal, index);
                    break;

                case BinaryOperator.LowerOrEquals:
                    result = FindElementsIndex(BinaryLowerOrEqualMethod, literal, index);
                    break;

                case BinaryOperator.Lower:
                    result = FindElementsIndex(BinaryLowerMethod, literal, index);
                    break;

                case BinaryOperator.NotEquals:
                    result = FindElementsIndex(BinaryNotEqualsMethod, literal, index);
                    break;

                default:
                    result = null;
                    return false;
            }

            #endregion

            return result.Count > 0;
        }

        public bool RangeIndexScan(out ReadOnlyCollection<AGraphElement> result, string indexId, IComparable leftLimit, IComparable rightLimit, bool includeLeft = true, bool includeRight = true)
        {
            IIndex index;
            if (!IndexFactory.TryGetIndex(out index, indexId))
            {
                result = null;
                return false;
            }

            var rangeIndex = index as IRangeIndex;
            if (rangeIndex != null)
            {
                return rangeIndex.Between(out result, leftLimit, rightLimit, includeLeft, includeRight);
            }

            result = null;
            return false;
        }

        public bool FulltextIndexScan(out FulltextSearchResult result, string indexId, string searchQuery)
        {
            IIndex index;
            if (!IndexFactory.TryGetIndex(out index, indexId))
            {
                result = null;
                return false;
            }

            var fulltextIndex = index as IFulltextIndex;
            if (fulltextIndex != null)
            {
                return fulltextIndex.TryQuery(out result, searchQuery);
            }

            result = null;
            return false;
        }

        public string Save(string path, uint savePartitions = 5)
        {
            if (ReadResource())
            {
                string saveGamePath = null;

                try
                {
                    saveGamePath = _persistencyFactory.Save(this, _graphElements, path, savePartitions, _currentId);
                }
                finally
                {
                    FinishReadResource();
                }

                return saveGamePath;
            }

            throw new CollisionException();
        }

        public bool CalculateShortestPath(
            out List<Path> result,
            string algorithmname,
            Int32 sourceVertexId,
            Int32 destinationVertexId,
            Int32 maxDepth = 1,
            Double maxPathWeight = Double.MaxValue,
            Int32 maxResults = 1,
            PathDelegates.EdgePropertyFilter edgePropertyFilter = null,
            PathDelegates.VertexFilter vertexFilter = null,
            PathDelegates.EdgeFilter edgeFilter = null,
            PathDelegates.EdgeCost edgeCost = null,
            PathDelegates.VertexCost vertexCost = null)
        {
            IShortestPathAlgorithm algo = null;

            Object cachedAlgo;
            if (!_pluginCache.ShortestPath.TryGetValue(algorithmname, out cachedAlgo))
            {
                //Shortest path plugin was not cached
                if (PluginFactory.TryFindPlugin(out algo, algorithmname))
                {
                    algo.Initialize(this, null);

                    _pluginCache.AddShortestPath(algo);
                }
            }
            else
            {
                algo = (IShortestPathAlgorithm)cachedAlgo;
            }

            if (algo != null)
            {
                algo.Initialize(this, null);

                if (ReadResource())
                {
                    try
                    {
                        result = algo.Calculate(sourceVertexId, destinationVertexId, maxDepth, maxPathWeight, maxResults,
                                                edgePropertyFilter,
                                                vertexFilter, edgeFilter, edgeCost, vertexCost);

                        return result != null && result.Count > 0;
                    }
                    finally
                    {
                        FinishReadResource();
                    }
                }

                throw new CollisionException();
            }

            result = null;
            return false;
        }

        internal EdgeModel CreateEdge_internal(Int32 sourceVertexId, UInt16 edgePropertyId, Int32 targetVertexId, UInt32 creationDate, PropertyContainer[] properties)
        {
            EdgeModel outgoingEdge = null;

            var sourceVertex = _graphElements[sourceVertexId] as VertexModel;
            var targetVertex = _graphElements[targetVertexId] as VertexModel;

            //get the related vertices
            if (sourceVertex != null && targetVertex != null)
            {
                outgoingEdge = new EdgeModel(_currentId, creationDate, targetVertex, sourceVertex, properties);

                //add the edge to the graph elements
                _graphElements = _graphElements.Add(outgoingEdge);

                //increment the ids
                Interlocked.Increment(ref _currentId);

                //add the edge to the source vertex
                sourceVertex.AddOutEdge(edgePropertyId, outgoingEdge);

                //link the vertices
                targetVertex.AddIncomingEdge(edgePropertyId, outgoingEdge);

                //increase the edgeCount
                EdgeCount++;
            }

            return outgoingEdge;
        }

        internal List<EdgeModel> CreateEdges_internal(List<EdgeDefinition> definitions)
        {
            var newEdges = new List<EdgeModel>();

            if (definitions != null)
            {
                foreach (var aEdgeDefinition in definitions)
                {
                    var sourceVertex = _graphElements[aEdgeDefinition.SourceVertexId] as VertexModel;
                    var targetVertex = _graphElements[aEdgeDefinition.TargetVertexId] as VertexModel;

                    //get the related vertices
                    if (sourceVertex != null && targetVertex != null)
                    {
                        var newEdge = new EdgeModel(_currentId, aEdgeDefinition.CreationDate, targetVertex, sourceVertex, aEdgeDefinition.Properties);

                        newEdges.Add(newEdge);
                        
                        //increment the ids
                        Interlocked.Increment(ref _currentId);

                        //add the edge to the source vertex
                        sourceVertex.AddOutEdge(aEdgeDefinition.EdgePropertyId, newEdge);

                        //link the vertices
                        targetVertex.AddIncomingEdge(aEdgeDefinition.EdgePropertyId, newEdge);

                        //increase the edgeCount
                        EdgeCount++;
                    }
                }

                //add the edge to the graph elements
                _graphElements = _graphElements.AddRange(newEdges);
            }

            return newEdges;
        }

        internal Boolean TryAddProperty_internal(Int32 graphElementId, UInt16 propertyId, Object property)
        {
            var success = false;
            AGraphElement graphElement = _graphElements[graphElementId];
            if (graphElement != null)
            {
                success = graphElement != null && graphElement.TryAddProperty(propertyId, property);
            }

            return success;
        }

        internal bool TryRemoveProperty_internal(Int32 graphElementId, UInt16 propertyId)
        {
            var graphElement = _graphElements[graphElementId];

            var success = graphElement != null && graphElement.TryRemoveProperty(propertyId);

            return success;
        }

        internal bool TryRemoveGraphElement_private(Int32 graphElementId)
        {
                    AGraphElement graphElement = _graphElements[graphElementId];

                    if (graphElement == null || graphElement._removed)
                    {
                        return false;
                    }

                    //used if an edge is removed
                    List<UInt16> inEdgeRemovals = null;
                    List<UInt16> outEdgeRemovals = null;

                    try
                    {
                        #region remove element

                        _graphElements[graphElementId].MarkAsRemoved();

                        if (graphElement is VertexModel)
                        {
                            #region remove vertex

                            var vertex = (VertexModel)graphElement;

                            #region out edges

                            var outgoingEdgeConatiner = vertex.GetOutgoingEdges();
                            if (outgoingEdgeConatiner != null)
                            {
                                for (var i = 0; i < outgoingEdgeConatiner.Count; i++)
                                {
                                    var aOutEdgeContainer = outgoingEdgeConatiner[i];
                                    for (var j = 0; j < aOutEdgeContainer.Edges.Count; j++)
                                    {
                                        var aOutEdge = aOutEdgeContainer.Edges[j];

                                        //remove from incoming edges of target vertex
                                        aOutEdge.TargetVertex.RemoveIncomingEdge(aOutEdgeContainer.EdgePropertyId, aOutEdge);

                                        //remove the edge itself
                                        _graphElements[aOutEdge.Id].MarkAsRemoved();
                                    }
                                }
                            }

                            #endregion

                            #region in edges

                            var incomingEdgeContainer = vertex.GetIncomingEdges();
                            if (incomingEdgeContainer != null)
                            {
                                for (var i = 0; i < incomingEdgeContainer.Count; i++)
                                {
                                    var aInEdgeContainer = incomingEdgeContainer[i];
                                    for (var j = 0; j < aInEdgeContainer.Edges.Count; j++)
                                    {
                                        var aInEdge = aInEdgeContainer.Edges[j];

                                        //remove from outgoing edges of source vertex
                                        aInEdge.SourceVertex.RemoveOutGoingEdge(aInEdgeContainer.EdgePropertyId, aInEdge);

                                        //remove the edge itself
                                        _graphElements[aInEdge.Id].MarkAsRemoved();
                                    }
                                }
                            }

                            #endregion

                            //update the EdgeCount --> hard way
                            RecalculateGraphElementCounter();

                            #endregion
                        }
                        else
                        {
                            #region remove edge

                            var edge = (EdgeModel)graphElement;

                            //remove from incoming edges of target vertex
                            inEdgeRemovals = edge.TargetVertex.RemoveIncomingEdge(edge);

                            //remove from outgoing edges of source vertex
                            outEdgeRemovals = edge.SourceVertex.RemoveOutGoingEdge(edge);

                            //update the EdgeCount --> easy way
                            EdgeCount--;

                            #endregion
                        }

                        #endregion
                    }
                    catch (Exception)
                    {
                        #region restore

                        _graphElements.Insert(graphElementId, graphElement);

                        if (graphElement is VertexModel)
                        {
                            #region restore vertex

                            var vertex = (VertexModel)graphElement;

                            #region out edges

                            var outgoingEdgeConatiner = vertex.GetOutgoingEdges();
                            if (outgoingEdgeConatiner != null)
                            {
                                for (var i = 0; i < outgoingEdgeConatiner.Count; i++)
                                {
                                    var aOutEdgeContainer = outgoingEdgeConatiner[i];
                                    for (var j = 0; j < aOutEdgeContainer.Edges.Count; j++)
                                    {
                                        var aOutEdge = aOutEdgeContainer.Edges[j];

                                        //remove from incoming edges of target vertex
                                        aOutEdge.TargetVertex.AddIncomingEdge(aOutEdgeContainer.EdgePropertyId, aOutEdge);

                                        //reset the edge
                                        _graphElements.Insert(aOutEdge.Id, aOutEdge);
                                    }
                                }
                            }

                            #endregion

                            #region in edges

                            var incomingEdgeContainer = vertex.GetIncomingEdges();
                            if (incomingEdgeContainer != null)
                            {
                                for (var i = 0; i < incomingEdgeContainer.Count; i++)
                                {
                                    var aInEdgeContainer = incomingEdgeContainer[i];
                                    for (var j = 0; j < aInEdgeContainer.Edges.Count; j++)
                                    {
                                        var aInEdge = aInEdgeContainer.Edges[j];

                                        //remove from outgoing edges of source vertex
                                        aInEdge.SourceVertex.AddOutEdge(aInEdgeContainer.EdgePropertyId, aInEdge);

                                        //reset the edge
                                        _graphElements.Insert(aInEdge.Id, aInEdge);
                                    }
                                }
                            }

                            #endregion

                            #endregion
                        }
                        else
                        {
                            #region restore edge

                            var edge = (EdgeModel)graphElement;

                            if (inEdgeRemovals != null)
                            {
                                for (var i = 0; i < inEdgeRemovals.Count; i++)
                                {
                                    edge.TargetVertex.AddIncomingEdge(inEdgeRemovals[i], edge);
                                }
                            }

                            if (outEdgeRemovals != null)
                            {
                                for (var i = 0; i < outEdgeRemovals.Count; i++)
                                {
                                    edge.SourceVertex.AddOutEdge(outEdgeRemovals[i], edge);
                                }
                            }

                            #endregion
                        }

                        //recalculate the counter
                        RecalculateGraphElementCounter();

                        #endregion

                        throw;
                    }

                return true;
        }

        internal void TabulaRasa_internal()
        {
            _currentId = 0;
            _graphElements = ImmutableList.Create<AGraphElement>();
            IndexFactory.DeleteAllIndices();
            VertexCount = 0;
            EdgeCount = 0;
        }

        internal void Load_internal(String path, Boolean startServices = false)
        {
            if (String.IsNullOrWhiteSpace(path))
            {
                _logger.LogInformation(String.Format("There is no path given, so nothing will be loaded."));
                return;
            }

            _logger.LogInformation(String.Format("Fallen-8 now loads a savegame from path \"{0}\"", path));

            var oldIndexFactory = IndexFactory;
            var oldServiceFactory = ServiceFactory;
            oldServiceFactory.ShutdownAllServices();
            var oldGraphElements = _graphElements;
            GC.Collect();
            GC.Collect();
            GC.WaitForFullGCComplete(-1);
            GC.WaitForPendingFinalizers();

            _graphElements = ImmutableList.Create<AGraphElement>();

            var success = _persistencyFactory.Load(this, ref _graphElements, path, ref _currentId, startServices);

            if (success)
            {
                oldIndexFactory.DeleteAllIndices();
            }
            else
            {
                _graphElements = oldGraphElements;
                IndexFactory = oldIndexFactory;
                ServiceFactory = oldServiceFactory;
                ServiceFactory.StartAllServices();
            }

            Trim_internal();
        }

        public TransactionInformation EnqueueTransaction(ATransaction tx)
        {
            return _txManager.AddTransaction(tx);
        }

        public TransactionState GetTransactionState(String txId)
        {
            return _txManager.GetState(txId);
        }

        public void Dispose()
        {
            TabulaRasa_internal();

            _graphElements = null;

            IndexFactory.DeleteAllIndices();
            IndexFactory = null;

            ServiceFactory.ShutdownAllServices();
            ServiceFactory = null;
        }

        #region private helper methods

        /// <summary>
        ///   Finds the elements.
        /// </summary>
        /// <returns> The elements. </returns>
        /// <param name='finder'> Finder. </param>
        /// <param name='literal'> Literal. </param>
        /// <param name='propertyId'> Property identifier. </param>
        private List<AGraphElement> FindElements(BinaryOperatorDelegate finder, IComparable literal, UInt16 propertyId)
        {
            if (ReadResource())
            {
                try
                {
                    var result = FindElements(
                        aGraphElement =>
                        {
                            Object property;
                            return aGraphElement.TryGetProperty(out property, propertyId) &&
                                   finder(property as IComparable, literal);
                        });

                    return result;
                }
                finally
                {
                    FinishReadResource();
                }
            }

            throw new CollisionException();
        }

        /// <summary>
        /// Find elements by scanning the list
        /// </summary>
        /// <param name="seeker">A delegate to search for the right element</param>
        /// <returns>A list of matching graph elements</returns>
        private List<AGraphElement> FindElements(ElementSeeker seeker)
        {
            return _graphElements.AsParallel()
                .Where(_ => _ != null && !_._removed && seeker(_))
                .ToList();
        }

        /// <summary>
        ///   Finds elements via an index.
        /// </summary>
        /// <returns> The elements. </returns>
        /// <param name='finder'> Finder delegate. </param>
        /// <param name='literal'> Literal. </param>
        /// <param name='index'> Index. </param>
        private static ReadOnlyCollection<AGraphElement> FindElementsIndex(BinaryOperatorDelegate finder,
                                                                           IComparable literal, IIndex index)
        {
            return new ReadOnlyCollection<AGraphElement>(index.GetKeyValues()
                                                             .AsParallel()
                                                             .Select(aIndexElement => new KeyValuePair<IComparable, ReadOnlyCollection<AGraphElement>>((IComparable)aIndexElement.Key, aIndexElement.Value))
                                                             .Where(aTypedIndexElement => finder(aTypedIndexElement.Key, literal))
                                                             .Select(_ => _.Value)
                                                             .SelectMany(__ => __)
                                                             .Distinct()
                                                             .ToList());
        }

        /// <summary>
        ///   Method for binary comparism
        /// </summary>
        /// <returns> <c>true</c> for equality; otherwise, <c>false</c> . </returns>
        /// <param name='property'> Property. </param>
        /// <param name='literal'> Literal. </param>
        private static Boolean BinaryEqualsMethod(IComparable property, IComparable literal)
        {
            return property.Equals(literal);
        }

        /// <summary>
        ///   Method for binary comparism
        /// </summary>
        /// <returns> <c>true</c> for inequality; otherwise, <c>false</c> . </returns>
        /// <param name='property'> Property. </param>
        /// <param name='literal'> Literal. </param>
        private static Boolean BinaryNotEqualsMethod(IComparable property, IComparable literal)
        {
            return !property.Equals(literal);
        }

        /// <summary>
        ///   Method for binary comparism
        /// </summary>
        /// <returns> <c>true</c> for greater property; otherwise, <c>false</c> . </returns>
        /// <param name='property'> Property. </param>
        /// <param name='literal'> Literal. </param>
        private static Boolean BinaryGreaterMethod(IComparable property, IComparable literal)
        {
            return property.CompareTo(literal) > 0;
        }

        /// <summary>
        ///   Method for binary comparism
        /// </summary>
        /// <returns> <c>true</c> for lower property; otherwise, <c>false</c> . </returns>
        /// <param name='property'> Property. </param>
        /// <param name='literal'> Literal. </param>
        private static Boolean BinaryLowerMethod(IComparable property, IComparable literal)
        {
            return property.CompareTo(literal) < 0;
        }

        /// <summary>
        ///   Method for binary comparism
        /// </summary>
        /// <returns> <c>true</c> for lower or equal property; otherwise, <c>false</c> . </returns>
        /// <param name='property'> Property. </param>
        /// <param name='literal'> Literal. </param>
        private static Boolean BinaryLowerOrEqualMethod(IComparable property, IComparable literal)
        {
            return property.CompareTo(literal) <= 0;
        }

        /// <summary>
        ///   Method for binary comparism
        /// </summary>
        /// <returns> <c>true</c> for greater or equal property; otherwise, <c>false</c> . </returns>
        /// <param name='property'> Property. </param>
        /// <param name='literal'> Literal. </param>
        private static Boolean BinaryGreaterOrEqualMethod(IComparable property, IComparable literal)
        {
            return property.CompareTo(literal) >= 0;
        }

        /// <summary>
        ///   Trims the Fallen-8.
        /// </summary>
        internal void Trim_internal()
        {
            for (var i = 0; i < _currentId; i++)
            {
                AGraphElement graphElement = _graphElements[i];
                if (graphElement != null)
                {
                    graphElement.Trim();
                }
            }

            List<AGraphElement> newGraphElementList = new List<AGraphElement>();

            //copy the list and exclude nulls
            foreach (var aGraphElement in _graphElements)
            {
                if (aGraphElement != null)
                {
                    newGraphElementList.Add(aGraphElement);
                }
            }

            //check the IDs
            for (int i = 0; i < newGraphElementList.Count; i++)
            {
                newGraphElementList[i].SetId(i);
            }

            //set the correct currentID
            _currentId = newGraphElementList.Count;

            //cleanup and switch
            _graphElements.Clear();
            _graphElements = newGraphElementList.ToImmutableList();

            _txManager.Trim();

            GC.Collect();
            GC.Collect();
            GC.WaitForFullGCComplete(-1);
            GC.WaitForPendingFinalizers();

            RecalculateGraphElementCounter();
        }

        /// <summary>
        /// Recalculates the count of the graph elements
        /// </summary>
        private void RecalculateGraphElementCounter()
        {
            EdgeCount = GetCountOf<EdgeModel>();
            VertexCount = GetCountOf<VertexModel>();
        }

        public int GetCountOf<TInteresting>()
        {
            return _graphElements.AsParallel()
                .Where(_ => _ != null && _ is TInteresting).Count();
        }

        public ReadOnlyCollection<VertexModel> GetAllVertices()
        {
            ReadOnlyCollection<VertexModel> result;

            if (ReadResource())
            {
                try
                {
                    result = new ReadOnlyCollection<VertexModel>(
                        _graphElements
                        .AsParallel()
                        .Where(_ => _ != null && _ is VertexModel)
                        .Select(_ => (VertexModel)_).ToList());

                    return result;
                }
                finally
                {
                    FinishReadResource();
                }
            }

            throw new CollisionException();
        }

        public ReadOnlyCollection<EdgeModel> GetAllEdges()
        {
            ReadOnlyCollection<EdgeModel> result;

            if (ReadResource())
            {
                try
                {
                    result = new ReadOnlyCollection<EdgeModel>(
                        _graphElements
                        .AsParallel()
                        .Where(_ => _ != null && _ is EdgeModel)
                        .Select(_ => (EdgeModel)_).ToList());

                    return result;
                }
                finally
                {
                    FinishReadResource();
                }
            }

            throw new CollisionException();
        }

        #endregion
    }
}
