// MIT License
//
// Fallen8.cs
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
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.Cache;
using NoSQL.GraphDB.Core.Expression;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Index.Fulltext;
using NoSQL.GraphDB.Core.Index.Range;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Persistency;
using NoSQL.GraphDB.Core.Plugin;
using NoSQL.GraphDB.Core.Service;
using NoSQL.GraphDB.Core.SubGraph;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Core
{
    public sealed class Fallen8 : AFallen8
    {
        #region Data

        /// <summary>
        ///   The graph elements storage: a dense array where the index equals the element id.
        ///   Thread safety is provided by the TransactionManager: every mutation runs on its
        ///   single worker thread and publishes a NEW, fully-populated array by an atomic
        ///   (volatile) reference swap of this field, while lock-free readers capture the current
        ///   reference once (O(1) indexer) and get a consistent snapshot. A published array is
        ///   never mutated in place (a soft-remove only flips the element's own <c>_removed</c>
        ///   flag, and Trim builds a fresh array), so a reader that captured a reference keeps a
        ///   stable view. The field is <c>volatile</c> so the publish (release) / capture
        ///   (acquire) pair is correct on weak-memory (for example ARM) hardware, not only x86;
        ///   each read method must read it exactly once.
        ///   (<see cref="ImmutableArray{T}"/> is a struct and cannot be <c>volatile</c>; a
        ///   copy-on-write <c>T[]</c> behind a volatile reference gives the same
        ///   immutable-after-publish guarantee with correct memory ordering.)
        /// </summary>
        private volatile AGraphElementModel[] _graphElements;

        /// <summary>
        /// The delegate to find elements in the big list
        /// </summary>
        /// <param name="objectOfT">The to be analyzed object of T</param>
        /// <returns>True or false</returns>
        public delegate Boolean ElementSeeker(AGraphElementModel objectOfT);

        /// <summary>
        ///   The index factory.
        /// </summary>
        public override IndexFactory IndexFactory
        {
            get; internal set;
        }

        /// <summary>
        ///   The service factory.
        /// </summary>
        public override ServiceFactory ServiceFactory
        {
            get; internal set;
        }

        /// <summary>
        ///   The subgraph factory.
        /// </summary>
        public override SubGraphFactory SubGraphFactory
        {
            get; internal set;
        }

        /// <summary>
        ///   The compiler used to rebuild persisted subgraphs on load. Null unless set by the
        ///   hosting layer (for example the REST API).
        /// </summary>
        public override ISubGraphRecipeCompiler SubGraphRecipeCompiler
        {
            get; set;
        }

        /// <summary>
        /// The count of edges
        /// </summary>
        public override Int32 EdgeCount
        {
            get; protected set;
        }

        /// <summary>
        /// The count of vertices
        /// </summary>
        public override Int32 VertexCount
        {
            get; protected set;
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

        #endregion

        #region Internal Helper Methods

        /// <summary>
        /// Creates a logger for the specified type. Used internally by persistence factory.
        /// </summary>
        internal ILogger<T> CreateLogger<T>()
        {
            return LoggerFactory.CreateLogger<T>();
        }

        #endregion

        #region Constructor

        /// <summary>
        ///   Initializes a new instance of the Fallen-8 class.
        /// </summary>
        public Fallen8(ILoggerFactory loggerfactory)
        {
            LoggerFactory = loggerfactory;
            _logger = loggerfactory.CreateLogger<Fallen8>();

            // Create loggers for factories
            var indexLogger = loggerfactory.CreateLogger<IndexFactory>();
            var serviceLogger = loggerfactory.CreateLogger<ServiceFactory>();
            var subGraphLogger = loggerfactory.CreateLogger<SubGraphFactory>();
            var persistencyLogger = loggerfactory.CreateLogger<PersistencyFactory>();

            IndexFactory = new IndexFactory(this, indexLogger);
            _graphElements = [];
            ServiceFactory = new ServiceFactory(this, serviceLogger);
            SubGraphFactory = new SubGraphFactory(this, subGraphLogger, _pluginCache);
            IndexFactory.Indices.Clear();
            _txManager = new TransactionManager(this);
            _persistencyFactory = new PersistencyFactory(persistencyLogger);
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

        #region master store mutation (single-writer, copy-on-write)

        /// <summary>
        ///   Appends one element to the master store. Runs only on the single TransactionManager
        ///   writer thread. A brand-new array is fully populated first and then published by a
        ///   single volatile write, so a lock-free reader observes either the old array or the
        ///   fully built new one - never a partially written array (id == index preserved).
        /// </summary>
        private void AppendGraphElement(AGraphElementModel element)
        {
            var current = _graphElements;
            var next = new AGraphElementModel[current.Length + 1];
            Array.Copy(current, next, current.Length);
            next[current.Length] = element;
            _graphElements = next; // atomic publish (release); slot written before publication
        }

        /// <summary>
        ///   Appends a batch of elements to the master store in one copy-on-write publication.
        ///   <paramref name="elements"/> is accepted as a covariant read-only list so callers can
        ///   pass a <c>List&lt;VertexModel&gt;</c>/<c>List&lt;EdgeModel&gt;</c> directly.
        /// </summary>
        private void AppendGraphElements(IReadOnlyList<AGraphElementModel> elements)
        {
            if (elements == null || elements.Count == 0)
            {
                return;
            }

            var current = _graphElements;
            var next = new AGraphElementModel[current.Length + elements.Count];
            Array.Copy(current, next, current.Length);
            for (var i = 0; i < elements.Count; i++)
            {
                next[current.Length + i] = elements[i];
            }
            _graphElements = next; // atomic publish (release); all slots written before publication
        }

        /// <summary>
        ///   Resolves an element by id for a single-writer mutation. Preserves the historical
        ///   contract that an out-of-range id throws <see cref="ArgumentOutOfRangeException"/>:
        ///   the previous <c>ImmutableList</c> indexer threw that type, whereas a raw array
        ///   indexer would throw <see cref="IndexOutOfRangeException"/>. The returned element may
        ///   itself be null (a slot left empty by a load).
        /// </summary>
        private AGraphElementModel GetGraphElementForMutation(Int32 graphElementId)
        {
            var snapshot = _graphElements;
            if (graphElementId < 0 || graphElementId >= snapshot.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(graphElementId));
            }
            return snapshot[graphElementId];
        }

        #endregion

        internal VertexModel CreateVertex_internal(UInt32 creationDate, String label, Dictionary<String, Object> properties = null)
        {
            //create the new vertex
            var newVertex = new VertexModel(_currentId, creationDate, label, properties);

            //insert it
            AppendGraphElement(newVertex);

            //increment the id
            Interlocked.Increment(ref _currentId);

            //Increase the vertex count
            VertexCount++;

            return newVertex;
        }

        internal List<VertexModel> CreateVertices_internal(List<VertexDefinition> definitions)
        {
            var newVertices = new List<VertexModel>();

            if (definitions != null && definitions.Count > 0)
            {
                foreach (var aVertexDef in definitions)
                {
                    //create the new vertex
                    var newVertex = new VertexModel(_currentId, aVertexDef.CreationDate, aVertexDef.Label, aVertexDef.Properties);

                    newVertices.Add(newVertex);

                    //increment the id
                    Interlocked.Increment(ref _currentId);

                    //Increase the vertex count
                    VertexCount++;
                }

                //insert all vertices in batch
                AppendGraphElements(newVertices);
            }

            return newVertices;
        }

        public override bool TryGetGraphElement(out AGraphElementModel result, int id)
        {
            // Capture the published snapshot once. The bound check and the indexer then operate
            // on the same collection, so a concurrent single-writer append or Trim cannot make
            // this read observe a count that disagrees with the backing store (no out-of-range).
            var snapshot = _graphElements;

            if (id < 0 || id >= snapshot.Length)
            {
                result = null;
                return false;
            }

            result = snapshot[id];
            return result != null && !result._removed;
        }

        public override bool TryGetEdge(out EdgeModel result, int id)
        {
            var snapshot = _graphElements;

            if (id < 0 || id >= snapshot.Length)
            {
                result = null;
                return false;
            }

            result = snapshot[id] as EdgeModel;
            return result != null && !result._removed;
        }

        public override bool TryGetVertex(out VertexModel result, int id)
        {
            var snapshot = _graphElements;

            if (id < 0 || id >= snapshot.Length)
            {
                result = null;
                return false;
            }

            result = snapshot[id] as VertexModel;
            return result != null && !result._removed;
        }



        public override bool GraphScan(out List<AGraphElementModel> result, String propertyId, IComparable literal,
            BinaryOperator binOp = BinaryOperator.Equals, String interestingLabel = null)
        {
            if (string.IsNullOrWhiteSpace(propertyId))
            {
                throw new ArgumentException("Property ID cannot be null or whitespace.", nameof(propertyId));
            }

            if (literal == null)
            {
                throw new ArgumentNullException(nameof(literal));
            }

            #region binary operation

            switch (binOp)
            {
                case BinaryOperator.Equals:
                    result = FindElements(BinaryEqualsMethod, literal, propertyId, interestingLabel);
                    break;

                case BinaryOperator.Greater:
                    result = FindElements(BinaryGreaterMethod, literal, propertyId, interestingLabel);
                    break;

                case BinaryOperator.GreaterOrEquals:
                    result = FindElements(BinaryGreaterOrEqualMethod, literal, propertyId, interestingLabel);
                    break;

                case BinaryOperator.LowerOrEquals:
                    result = FindElements(BinaryLowerOrEqualMethod, literal, propertyId, interestingLabel);
                    break;

                case BinaryOperator.Lower:
                    result = FindElements(BinaryLowerMethod, literal, propertyId, interestingLabel);
                    break;

                case BinaryOperator.NotEquals:
                    result = FindElements(BinaryNotEqualsMethod, literal, propertyId, interestingLabel);
                    break;

                default:
                    result = new List<AGraphElementModel>();
                    break;
            }

            #endregion

            return result.Count > 0;
        }

        public override bool IndexScan(out ImmutableList<AGraphElementModel> result, string indexId, IComparable literal, BinaryOperator binOp = BinaryOperator.Equals)
        {
            if (string.IsNullOrWhiteSpace(indexId))
            {
                throw new ArgumentException("Index ID cannot be null or whitespace.", nameof(indexId));
            }

            if (literal == null)
            {
                throw new ArgumentNullException(nameof(literal));
            }

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

        public override bool RangeIndexScan(out ImmutableList<AGraphElementModel> result, string indexId, IComparable leftLimit, IComparable rightLimit, bool includeLeft = true, bool includeRight = true)
        {
            if (string.IsNullOrWhiteSpace(indexId))
            {
                throw new ArgumentException("Index ID cannot be null or whitespace.", nameof(indexId));
            }

            if (leftLimit == null)
            {
                throw new ArgumentNullException(nameof(leftLimit));
            }

            if (rightLimit == null)
            {
                throw new ArgumentNullException(nameof(rightLimit));
            }

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

        public override bool FulltextIndexScan(out FulltextSearchResult result, string indexId, string searchQuery)
        {
            if (string.IsNullOrWhiteSpace(indexId))
            {
                throw new ArgumentException("Index ID cannot be null or whitespace.", nameof(indexId));
            }

            if (string.IsNullOrWhiteSpace(searchQuery))
            {
                throw new ArgumentException("Search query cannot be null or whitespace.", nameof(searchQuery));
            }

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

        internal string Save(string path, int savePartitions = 5)
        {
            return _persistencyFactory.Save(this, path, savePartitions);
        }

        public override bool TryCalculateShortestPath(
            out List<Path> result,
            string plugin,
            ShortestPathDefinition definition)
        {
            if (string.IsNullOrWhiteSpace(plugin))
            {
                throw new ArgumentException("Plugin name cannot be null or whitespace.", nameof(plugin));
            }

            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            IShortestPathAlgorithm algo = null;

            Object cachedAlgo;
            if (!_pluginCache.ShortestPath.TryGetValue(plugin, out cachedAlgo))
            {
                //Shortest path plugin was not cached
                if (PluginFactory.TryFindPlugin(out algo, plugin))
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
                return algo.TryCalculateShortestPath(out result, definition);
            }

            result = new List<Path>();
            return false;
        }

        public override bool TryCalculateShortestPath<T>(
            out List<Path> result,
            ShortestPathDefinition definition)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            Type shortestPathType = typeof(T);
            var algo = Activator.CreateInstance(shortestPathType, false) as IShortestPathAlgorithm;

            if (algo != null)
            {
                Object cachedAlgo;
                if (!_pluginCache.ShortestPath.TryGetValue(algo.PluginName, out cachedAlgo))
                {
                    //Shortest path plugin was not cached
                    algo.Initialize(this, null);
                    _pluginCache.AddShortestPath(algo);
                }
                else
                {
                    algo = (IShortestPathAlgorithm)cachedAlgo;
                }

                return algo.TryCalculateShortestPath(out result, definition);
            }

            result = new List<Path>();
            return false;
        }

        internal EdgeModel CreateEdge_internal(Int32 sourceVertexId, String edgePropertyId, Int32 targetVertexId,
            UInt32 creationDate, String label, Dictionary<String, Object> properties)
        {
            EdgeModel outgoingEdge = null;

            var sourceVertex = GetGraphElementForMutation(sourceVertexId) as VertexModel;
            var targetVertex = GetGraphElementForMutation(targetVertexId) as VertexModel;

            //get the related vertices
            if (sourceVertex != null && targetVertex != null)
            {
                outgoingEdge = new EdgeModel(_currentId, creationDate, targetVertex, sourceVertex, label, edgePropertyId, properties);

                //add the edge to the graph elements
                AppendGraphElement(outgoingEdge);

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

            if (definitions != null && definitions.Count > 0)
            {
                foreach (var aEdgeDefinition in definitions)
                {
                    var sourceVertex = GetGraphElementForMutation(aEdgeDefinition.SourceVertexId) as VertexModel;
                    var targetVertex = GetGraphElementForMutation(aEdgeDefinition.TargetVertexId) as VertexModel;

                    //get the related vertices
                    if (sourceVertex != null && targetVertex != null)
                    {
                        var newEdge = new EdgeModel(_currentId, aEdgeDefinition.CreationDate, targetVertex, sourceVertex,
                            aEdgeDefinition.Label, aEdgeDefinition.EdgePropertyId, aEdgeDefinition.Properties);

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

                //add the edges to the graph elements in batch
                if (newEdges.Count > 0)
                {
                    AppendGraphElements(newEdges);
                }
            }

            return newEdges;
        }

        internal void SetProperty_internal(Int32 graphElementId, String propertyId, Object property)
        {
            AGraphElementModel graphElement = GetGraphElementForMutation(graphElementId);
            if (graphElement != null)
            {
                graphElement.SetProperty(propertyId, property);
            }
        }

        internal void RemoveProperty_internal(Int32 graphElementId, String propertyId)
        {
            var graphElement = GetGraphElementForMutation(graphElementId);

            if (graphElement != null)
            {
                graphElement.RemoveProperty(propertyId);
            }
        }

        internal bool TryRemoveGraphElement_private(Int32 graphElementId)
        {
            AGraphElementModel graphElement = GetGraphElementForMutation(graphElementId);

            if (graphElement == null || graphElement._removed)
            {
                return false;
            }

            //used if an edge is removed
            List<String> inEdgeRemovals = null;
            List<String> outEdgeRemovals = null;

            try
            {
                #region remove element

                graphElement.MarkAsRemoved();

                if (graphElement is VertexModel)
                {
                    #region remove vertex

                    var vertex = (VertexModel)graphElement;

                    #region out edges

                    var outgoingEdgeContainer = vertex.OutEdges;
                    if (outgoingEdgeContainer != null)
                    {
                        foreach (var aOutEdgeProperty in outgoingEdgeContainer)
                        {
                            foreach (var aOutEdge in aOutEdgeProperty.Value)
                            {
                                //remove from incoming edges of target vertex
                                aOutEdge.TargetVertex.RemoveIncomingEdge(aOutEdgeProperty.Key, aOutEdge);

                                //remove the edge itself
                                aOutEdge.MarkAsRemoved();
                            }
                        }
                    }

                    #endregion

                    #region in edges

                    var incomingEdgeContainer = vertex.InEdges;
                    if (incomingEdgeContainer != null)
                    {
                        foreach (var aInEdgeProperty in incomingEdgeContainer)
                        {
                            foreach (var aInEdge in aInEdgeProperty.Value)
                            {
                                //remove from outgoing edges of source vertex
                                aInEdge.SourceVertex.RemoveOutGoingEdge(aInEdgeProperty.Key, aInEdge);

                                //remove the edge itself
                                aInEdge.MarkAsRemoved();
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

                // Restoring the graph can itself fault (for example on a half-constructed edge).
                // Guard the restore so that a secondary failure neither skips the counter recompute
                // nor masks the original removal failure, which must remain the observed exception.
                try
                {
                    // Removal is a soft-delete: the element is only flagged via MarkAsRemoved and is
                    // never taken out of _graphElements. The correct rollback is therefore to clear that
                    // flag again. (Re-inserting into _graphElements would duplicate the still-present
                    // element and break the id==index invariant, and would not clear the removed flag.)
                    graphElement.MarkAsNotRemoved();

                    if (graphElement is VertexModel)
                    {
                        #region restore vertex

                        var vertex = (VertexModel)graphElement;

                        #region out edges

                        var outgoingEdgeContainer = vertex.OutEdges;
                        if (outgoingEdgeContainer != null)
                        {
                            foreach (var aOutEdgeProperty in outgoingEdgeContainer)
                            {
                                foreach (var aOutEdge in aOutEdgeProperty.Value)
                                {
                                    //restore into the incoming edges of the target vertex
                                    aOutEdge.TargetVertex.AddIncomingEdge(aOutEdgeProperty.Key, aOutEdge);

                                    //reset the edge
                                    aOutEdge.MarkAsNotRemoved();
                                }
                            }
                        }

                        #endregion

                        #region in edges

                        var incomingEdgeContainer = vertex.InEdges;
                        if (incomingEdgeContainer != null)
                        {
                            foreach (var aInEdgeProperty in incomingEdgeContainer)
                            {
                                foreach (var aInEdge in aInEdgeProperty.Value)
                                {
                                    //restore into the outgoing edges of the source vertex
                                    //(removal detached it via RemoveOutGoingEdge, so the inverse is AddOutEdge)
                                    aInEdge.SourceVertex.AddOutEdge(aInEdgeProperty.Key, aInEdge);

                                    //reset the edge
                                    aInEdge.MarkAsNotRemoved();
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
                }
                catch (Exception restoreException)
                {
                    // Swallow (but log) the restore failure so it does not replace the original
                    // removal failure that is rethrown below.
                    _logger.LogError(restoreException,
                        "Failed to fully restore graph element {GraphElementId} after a faulted removal; rolling back with the original failure.",
                        graphElementId);
                }
                finally
                {
                    //recalculate the counter (must run even when the restore above faulted)
                    RecalculateGraphElementCounter();
                }

                #endregion

                throw;
            }

            return true;
        }

        internal void TabulaRasa_internal()
        {
            _currentId = 0;
            _graphElements = [];
            IndexFactory.DeleteAllIndices();
            VertexCount = 0;
            EdgeCount = 0;
        }

        /// <summary>
        ///   Publishes the flat graph-element array produced by a load into the master store.
        ///   Invoked by the persistency factory once every element (and its edge fix-up) is
        ///   built, but BEFORE indices and services are rehydrated, because they resolve element
        ///   ids through <see cref="TryGetGraphElement" /> against the published store. The array
        ///   is dense and id-ordered (index == id), matching the on-disk format.
        /// </summary>
        internal void PublishLoadedGraphElements(AGraphElementModel[] graphElements)
        {
            // The loaded array is already dense and id-ordered (and may contain null slots for
            // ids that were null/removed at save time, which readers filter). It is fully built
            // and not mutated afterwards, so it becomes the master store directly (no copy).
            _graphElements = graphElements;
        }

        internal void Load_internal(String path, Boolean startServices = false)
        {
            if (String.IsNullOrWhiteSpace(path))
            {
                _logger.LogInformation("There is no path given, so nothing will be loaded.");
                return;
            }

            _logger.LogInformation("Fallen-8 now loads a savegame from path \"{Path}\"", path);

            var oldIndexFactory = IndexFactory;
            var oldServiceFactory = ServiceFactory;
            var oldSubGraphFactory = SubGraphFactory;
            oldServiceFactory.ShutdownAllServices();
            var oldGraphElements = _graphElements;

            _graphElements = [];

            // Load publishes the graph elements into _graphElements itself (via
            // PublishLoadedGraphElements) before it rehydrates indices, because index
            // rehydration resolves element ids through TryGetGraphElement against the
            // published master store. Passing the volatile field by ref would drop its
            // volatile semantics (CS0420), so publication goes through that method instead.
            var success = _persistencyFactory.Load(this, path, ref _currentId, startServices);

            if (success)
            {
                oldIndexFactory.DeleteAllIndices();
                oldSubGraphFactory.DeleteAllSubGraphs();

                // Rebuild persisted subgraphs against the freshly loaded graph. Requires a
                // registered recipe compiler; without one, persisted subgraphs are skipped.
                var recipes = _persistencyFactory.LoadSubGraphRecipes(path);
                if (recipes.Count > 0)
                {
                    SubGraphFactory.RehydrateFromRecipes(recipes, SubGraphRecipeCompiler);
                }
            }
            else
            {
                _graphElements = oldGraphElements;
                IndexFactory = oldIndexFactory;
                ServiceFactory = oldServiceFactory;
                SubGraphFactory = oldSubGraphFactory;
                ServiceFactory.StartAllServices();
            }

            Trim_internal();
        }

        public override TransactionInformation EnqueueTransaction(ATransaction tx)
        {
            return _txManager.AddTransaction(tx);
        }

        public override TransactionState GetTransactionState(String txId)
        {
            return _txManager.GetState(txId);
        }

        public override void Dispose()
        {
            TabulaRasa_internal();

            _graphElements = null;

            IndexFactory.DeleteAllIndices();
            IndexFactory = null;

            ServiceFactory.ShutdownAllServices();
            ServiceFactory = null;

            SubGraphFactory.DeleteAllSubGraphs();
            SubGraphFactory = null;
        }

        #region private helper methods

        /// <summary>
        ///   Finds the elements.
        /// </summary>
        /// <returns> The elements. </returns>
        /// <param name='finder'> Finder. </param>
        /// <param name='literal'> Literal. </param>
        /// <param name='propertyId'> Property identifier. </param>
        /// <param name='interestingLabel'> The interesting label. </param>
        private List<AGraphElementModel> FindElements(BinaryOperatorDelegate finder, IComparable literal, String propertyId,
            String interestingLabel = null)
        {
            return FindElements(
                aGraphElement =>
                {
                    Object property;
                    return aGraphElement.TryGetProperty(out property, propertyId) &&
                           finder(property as IComparable, literal);
                });
        }

        /// <summary>
        /// Find elements by scanning the list
        /// </summary>
        /// <param name="seeker">A delegate to search for the right element</param>
        /// <param name='interestingLabel'> The interesting label. </param>
        /// <returns>A list of matching graph elements</returns>
        private List<AGraphElementModel> FindElements(ElementSeeker seeker, String interestingLabel = null)
        {
            var snapshot = _graphElements;
            return snapshot.AsParallel()
                .Where(_ => _ != null && !_._removed)
                .Where(CheckLabel(interestingLabel))
                .Where(_ => seeker(_))
                .ToList();
        }

        private static Func<AGraphElementModel, Boolean> CheckLabel(String interestingLabel = null)
        {
            return _ => interestingLabel == null || interestingLabel != null && _.Label != null && _.Label.Equals(interestingLabel);
        }

        /// <summary>
        ///   Finds elements via an index.
        /// </summary>
        /// <returns> The elements. </returns>
        /// <param name='finder'> Finder delegate. </param>
        /// <param name='literal'> Literal. </param>
        /// <param name='index'> Index. </param>
        private static ImmutableList<AGraphElementModel> FindElementsIndex(BinaryOperatorDelegate finder,
                                                                           IComparable literal, IIndex index)
        {
            return ImmutableList.CreateRange<AGraphElementModel>(index.GetKeyValues()
                                                             .AsParallel()
                                                             .Select(aIndexElement => new KeyValuePair<IComparable, ImmutableList<AGraphElementModel>>((IComparable)aIndexElement.Key, aIndexElement.Value))
                                                             .Where(aTypedIndexElement => finder(aTypedIndexElement.Key, literal))
                                                             .Select(_ => _.Value)
                                                             .SelectMany(__ => __)
                                                             .Distinct());
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
        ///   Trims the Fallen-8 by removing null entries and compacting IDs.
        /// </summary>
        internal void Trim_internal()
        {
            // Runs on the single writer thread. Capture the current array once.
            var current = _graphElements;

            // Trim individual graph elements
            for (var i = 0; i < _currentId && i < current.Length; i++)
            {
                AGraphElementModel graphElement = current[i];
                graphElement?.Trim();
            }

            // Create new dense array excluding nulls and removed elements
            var newGraphElementList = current
                .Where(element => element != null && !element._removed)
                .ToArray();

            // Reassign IDs sequentially (index == id)
            for (int i = 0; i < newGraphElementList.Length; i++)
            {
                newGraphElementList[i].SetId(i);
            }

            // Update current ID
            _currentId = newGraphElementList.Length;

            // Publish the compacted array with a single atomic write. In-flight readers holding
            // the previous array keep a fully consistent (old-id-space) view; they never index it
            // out of range because their bound check used the same captured array's length.
            _graphElements = newGraphElementList;

            // Trim transaction manager
            _txManager.Trim();

            // Recalculate counters
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
            var snapshot = _graphElements;
            return snapshot.AsParallel()
                .Where(_ => _ != null && !_._removed && _ is TInteresting)
                .Count();
        }

        public override ImmutableList<VertexModel> GetAllVertices(String interestingLabel = null)
        {
            var snapshot = _graphElements;
            return ImmutableList.CreateRange<VertexModel>(
                 snapshot
                 .AsParallel()
                 .Where(_ => _ != null && !_._removed && _ is VertexModel)
                 .Where(CheckLabel(interestingLabel))
                 .Select(_ => (VertexModel)_));
        }

        public override ImmutableList<EdgeModel> GetAllEdges(String interestingLabel = null)
        {
            var snapshot = _graphElements;
            return ImmutableList.CreateRange<EdgeModel>(
                        snapshot
                        .AsParallel()
                        .Where(_ => _ != null && !_._removed && _ is EdgeModel)
                        .Where(CheckLabel(interestingLabel))
                        .Select(_ => (EdgeModel)_));
        }

        public override ImmutableList<AGraphElementModel> GetAllGraphElements(String interestingLabel = null)
        {
            var snapshot = _graphElements;
            return ImmutableList.CreateRange<AGraphElementModel>(
                        snapshot
                        .AsParallel()
                        .Where(_ => _ != null && !_._removed)
                        .Where(CheckLabel(interestingLabel)));
        }

        #endregion
    }
}
