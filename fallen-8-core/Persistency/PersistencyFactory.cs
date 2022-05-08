﻿// MIT License
//
// PersistencyFactory.cs
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
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Core.Helper;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Serializer;
using NoSQL.GraphDB.Core.Service;

namespace NoSQL.GraphDB.Core.Persistency
{
    /// <summary>
    ///   Persistency factory.
    /// </summary>
    internal class PersistencyFactory
    {
        #region Data

        /// <summary>
        /// The logger
        /// </summary>
        private readonly ILogger<PersistencyFactory> _logger;

        #endregion

        public PersistencyFactory(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<PersistencyFactory>();
        }

        #region internal methods

        /// <summary>
        ///   Load Fallen-8 from a save point
        /// </summary>
        /// <param name="fallen8">Fallen-8</param>
        /// <param name="graphElements">The graph elements </param>
        /// <param name="pathToSavePoint">The path to the save point.</param>
        /// <param name="currentId">The maximum graph element id</param>
        /// <param name="startServices">Start the services</param>
        internal Boolean Load(Fallen8 fallen8, ref ImmutableList<AGraphElement> graphElements, string pathToSavePoint, ref Int32 currentId, Boolean startServices)
        {
            //if there is no savepoint file... do nothing
            if (!File.Exists(pathToSavePoint))
            {
                _logger.LogError(String.Format("Fallen-8 could not be loaded because the path \"{0}\" does not exist.", pathToSavePoint));

                return false;
            }

            var pathName = Path.GetDirectoryName(pathToSavePoint);
            var fileName = Path.GetFileName(pathToSavePoint);

            _logger.LogInformation(String.Format("Now loading file \"{0}\" from path \"{1}\"", fileName, pathName));

            using (var file = File.Open(pathToSavePoint, FileMode.Open, FileAccess.Read))
            {
                var reader = new SerializationReader(file);
                currentId = reader.ReadInt32();

                AGraphElement[] graphElementArray = new AGraphElement[currentId];

                #region graph elements

                //initialize the list of graph elements
                var graphElementStreams = new List<String>();
                var numberOfGraphElemementStreams = reader.ReadInt32();
                for (var i = 0; i < numberOfGraphElemementStreams; i++)
                {
                    var graphElementBunchFilename = Path.Combine(pathName, reader.ReadString());
                    _logger.LogInformation(String.Format("Found graph element bunch {0} here: \"{1}\"", i, graphElementBunchFilename));

                    graphElementStreams.Add(graphElementBunchFilename);
                }

                LoadGraphElements(graphElementArray, graphElementStreams);

                graphElements = graphElementArray.ToImmutableList();

                #endregion

                #region indexe

                var indexStreams = new List<String>();
                var numberOfIndexStreams = reader.ReadInt32();
                for (var i = 0; i < numberOfIndexStreams; i++)
                {
                    var indexFilename = Path.Combine(pathName, reader.ReadString());
                    _logger.LogInformation(String.Format("Found index number {0} here: \"{1}\"", i, indexFilename));

                    indexStreams.Add(indexFilename);
                }
                var newIndexFactory = new IndexFactory(fallen8._loggerFactory);
                LoadIndices(fallen8, newIndexFactory, indexStreams);
                fallen8.IndexFactory = newIndexFactory;

                #endregion

                #region services

                var serviceStreams = new List<String>();
                var numberOfServiceStreams = reader.ReadInt32();
                for (var i = 0; i < numberOfServiceStreams; i++)
                {
                    var serviceFilename = Path.Combine(pathName, reader.ReadString());
                    _logger.LogInformation(String.Format("Found service number {0} here: \"{1}\"", i, serviceFilename));

                    serviceStreams.Add(serviceFilename);
                }
                var newServiceFactory = new ServiceFactory(fallen8);
                fallen8.ServiceFactory = newServiceFactory;
                LoadServices(fallen8, newServiceFactory, serviceStreams, startServices);

                #endregion

                return true;
            }
        }

        /// <summary>
        ///   Save the specified graphElements, indices and pathToSavePoint.
        /// </summary>
        /// <param name='fallen8'> Fallen-8. </param>
        /// <param name='graphElements'> Graph elements. </param>
        /// <param name='path'> Path. </param>
        /// <param name='savePartitions'> The number of save partitions for the graph elements. </param>
        /// <param name="currentId">The current graph elemement identifier.</param>
        /// <returns>The path of the savegame</returns>
        internal string Save(Fallen8 fallen8, ImmutableList<AGraphElement> graphElements, String path, UInt32 savePartitions, Int32 currentId)
        {
            // Create the new, empty data file.
            if (File.Exists(path))
            {
                //the newer save gets an timestamp
                path = path + Constants.VersionSeparator + DateTime.Now.ToBinary().ToString(CultureInfo.InvariantCulture);
            }

            using (var file = File.Create(path, Constants.BufferSize, FileOptions.SequentialScan))
            {
                var writer = new SerializationWriter(file, true);
                writer.Write(currentId);

                //create some futures to save as much as possible in parallel
                const TaskCreationOptions options = TaskCreationOptions.LongRunning;
                var f = new TaskFactory(CancellationToken.None, options, TaskContinuationOptions.None,
                                        TaskScheduler.Default);
                #region graph elements

                var graphElementCount = Convert.ToUInt32(currentId);
                Task<string>[] graphElementSaver;

                if (graphElementCount > 0)
                {
                    var graphElementPartitions = CreatePartitions(graphElementCount, savePartitions);
                    graphElementSaver = new Task<string>[graphElementPartitions.Count];

                    for (var i = 0; i < graphElementPartitions.Count; i++)
                    {
                        var partition = graphElementPartitions[i];
                        graphElementSaver[i] = f.StartNew(() => SaveBunch(partition, graphElements, path));
                    }
                }
                else
                {
                    graphElementSaver = new Task<string>[0];
                }

                #endregion

                #region indices

                var indexSaver = new Task<string>[fallen8.IndexFactory.Indices.Count];

                var counter = 0;
                foreach (var aIndex in fallen8.IndexFactory.Indices)
                {
                    var indexName = aIndex.Key;
                    var index = aIndex.Value;

                    indexSaver[counter] = f.StartNew(() => SaveIndex(indexName, index, path));
                    counter++;
                }

                #endregion

                #region services

                var serviceSaver = new Task<string>[fallen8.ServiceFactory.Services.Count];

                counter = 0;
                foreach (var aService in fallen8.ServiceFactory.Services)
                {
                    var serviceName = aService.Key;
                    var service = aService.Value;

                    serviceSaver[counter] = f.StartNew(() => SaveService(serviceName, service, path));
                    counter++;
                }

                #endregion

                writer.Write(graphElementSaver.Length);
                foreach (var aFileStreamName in graphElementSaver)
                {
                    writer.Write(aFileStreamName.Result);
                }

                writer.Write(indexSaver.Length);
                foreach (var aIndexFileName in indexSaver)
                {
                    writer.Write(aIndexFileName.Result);
                }

                writer.Write(serviceSaver.Length);
                foreach (var aServiceFileName in serviceSaver)
                {
                    writer.Write(aServiceFileName.Result);
                }

                writer.UpdateHeader();
                writer.Flush();
                file.Flush();
            }

            return path;
        }

        #endregion

        #region private helper

        /// <summary>
        ///   The serialized edge.
        /// </summary>
        private const byte SerializedEdge = 0;

        /// <summary>
        ///   The serialized vertex.
        /// </summary>
        private const byte SerializedVertex = 1;

        /// <summary>
        ///   The serialized null.
        /// </summary>
        private const byte SerializedNull = 2;

        /// <summary>
        ///   Saves the index.
        /// </summary>
        /// <returns> The filename of the persisted index. </returns>
        /// <param name='indexName'> Index name. </param>
        /// <param name='index'> Index. </param>
        /// <param name='path'> Path. </param>
        private String SaveIndex(string indexName, IIndex index, string path)
        {
            var indexFileName = path + Constants.IndexSaveString + indexName;

            using (var indexFile = File.Create(indexFileName, Constants.BufferSize, FileOptions.SequentialScan))
            {
                var indexWriter = new SerializationWriter(indexFile);

                indexWriter.Write(indexName);
                indexWriter.Write(index.PluginName);
                index.Save(indexWriter);

                indexWriter.UpdateHeader();
                indexWriter.Flush();
                indexFile.Flush();
            }

            return Path.GetFileName(indexFileName);
        }

        /// <summary>
        /// Saves the service
        /// </summary>
        /// <param name="serviceName">Service name.</param>
        /// <param name="service">Service.</param>
        /// <param name="path">Path.</param>
        /// <returns>The filename of the persisted service.</returns>
        private String SaveService(string serviceName, IService service, string path)
        {
            var serviceFileName = path + Constants.ServiceSaveString + serviceName;

            using (var serviceFile = File.Create(serviceFileName, Constants.BufferSize, FileOptions.SequentialScan))
            {
                var serviceWriter = new SerializationWriter(serviceFile);

                serviceWriter.Write(serviceName);
                serviceWriter.Write(service.PluginName);
                service.Save(serviceWriter);

                serviceWriter.UpdateHeader();
                serviceWriter.Flush();
                serviceFile.Flush();
            }

            return Path.GetFileName(serviceFileName);
        }

        /// <summary>
        ///   Loads a graph element bunch.
        /// </summary>
        /// <returns> The edges that point to vertices that are not within this bunch. </returns>
        /// <param name='graphElementBunchPath'> Graph element bunch path. </param>
        /// <param name='graphElementsOfFallen8'> Graph elements of Fallen-8. </param>
        /// <param name="edgeTodoOnVertex"> The edges that have to be added to this vertex </param>
        private List<EdgeSneakPeak> LoadAGraphElementBunch(
            string graphElementBunchPath,
            AGraphElement[] graphElementsOfFallen8,
            Dictionary<Int32, List<EdgeOnVertexToDo>> edgeTodoOnVertex)
        {
            //if there is no savepoint file... do nothing
            if (!File.Exists(graphElementBunchPath))
            {
                return null;
            }

            var result = new List<EdgeSneakPeak>();

            using (var file = File.Open(graphElementBunchPath, FileMode.Open, FileAccess.Read))
            {
                var reader = new SerializationReader(file);
                var minimumId = reader.ReadInt32();
                var maximumId = reader.ReadInt32();
                var countOfElements = maximumId - minimumId;

                for (var i = 0; i < countOfElements; i++)
                {
                    var kind = reader.ReadByte();
                    switch (kind)
                    {
                        case SerializedEdge:
                            //edge
                            LoadEdge(reader, graphElementsOfFallen8, ref result);
                            break;

                        case SerializedVertex:
                            //vertex
                            LoadVertex(reader, graphElementsOfFallen8, edgeTodoOnVertex);
                            break;

                        case SerializedNull:
                            //null --> do nothing
                            break;
                    }
                }
            }

            return result;
        }

        private void LoadIndices(Fallen8 fallen8, IndexFactory indexFactory, List<String> indexStreams)
        {
            //load the indices
            for (var i = 0; i < indexStreams.Count; i++)
            {
                LoadAnIndex(indexStreams[i], fallen8, indexFactory);
            }
        }

        private void LoadServices(Fallen8 fallen8, ServiceFactory newServiceFactory, List<string> serviceStreams, Boolean startServices)
        {
            //load the indices
            for (var i = 0; i < serviceStreams.Count; i++)
            {
                LoadAService(serviceStreams[i], fallen8, newServiceFactory, startServices);
            }
        }

        private void LoadAService(string serviceLocaion, Fallen8 fallen8, ServiceFactory serviceFactory, Boolean startService)
        {
            //if there is no savepoint file... do nothing
            if (!File.Exists(serviceLocaion))
            {
                return;
            }

            using (var file = File.Open(serviceLocaion, FileMode.Open, FileAccess.Read))
            {
                var reader = new SerializationReader(file);

                var indexName = reader.ReadString();
                var indexPluginName = reader.ReadString();

                serviceFactory.OpenService(indexName, indexPluginName, reader, fallen8, startService);
            }
        }

        private void LoadAnIndex(string indexLocaion, Fallen8 fallen8, IndexFactory indexFactory)
        {
            //if there is no savepoint file... do nothing
            if (!File.Exists(indexLocaion))
            {
                return;
            }

            using (var file = File.Open(indexLocaion, FileMode.Open, FileAccess.Read))
            {
                var reader = new SerializationReader(file);

                var indexName = reader.ReadString();
                var indexPluginName = reader.ReadString();

                indexFactory.OpenIndex(indexName, indexPluginName, reader, fallen8);
            }
        }

        /// <summary>
        ///   Saves the graph element bunch.
        /// </summary>
        /// <returns> The path to the graph element bunch </returns>
        /// <param name='range'> Range. </param>
        /// <param name='graphElements'> Graph elements. </param>
        /// <param name='pathToSavePoint'> Path to save point basis. </param>
        private String SaveBunch(Tuple<Int32, Int32> range, ImmutableList<AGraphElement> graphElements,
                                        String pathToSavePoint)
        {
            var partitionFileName = pathToSavePoint + Constants.GraphElementsSaveString + range.Item1 + "_to_" + range.Item2;

            using (var partitionFile = File.Create(partitionFileName, Constants.BufferSize, FileOptions.SequentialScan))
            {
                var partitionWriter = new SerializationWriter(partitionFile);

                partitionWriter.Write(range.Item1);
                partitionWriter.Write(range.Item2);

                for (var i = range.Item1; i < range.Item2; i++)
                {
                    AGraphElement aGraphElement = graphElements[i];
                    //there can be nulls
                    if (aGraphElement == null || aGraphElement._removed)
                    {
                        partitionWriter.Write(SerializedNull); // 2 for null
                        continue;
                    }

                    //code if it is an vertex or an edge
                    if (aGraphElement is VertexModel)
                    {
                        WriteVertex((VertexModel)aGraphElement, partitionWriter);
                    }
                    else
                    {
                        WriteEdge((EdgeModel)aGraphElement, partitionWriter);
                    }
                }

                partitionWriter.UpdateHeader();
                partitionWriter.Flush();
                partitionFile.Flush();
            }

            return Path.GetFileName(partitionFileName);
        }

        /// <summary>
        ///   Loads the graph elements.
        /// </summary>
        /// <param name='graphElements'> Graph elements of Fallen-8. </param>
        /// <param name='graphElementStreams'> Graph element streams. </param>
        private void LoadGraphElements(AGraphElement[] graphElements, List<String> graphElementStreams)
        {
            var edgeTodo = new Dictionary<Int32, List<EdgeOnVertexToDo>>();
            var result = new List<List<EdgeSneakPeak>>(graphElementStreams.Count);

            //create the major part of the graph elements
            for (var i = 0; i < graphElementStreams.Count; i++)
            {
                result.Add(LoadAGraphElementBunch(graphElementStreams[i], graphElements, edgeTodo));
            }

            foreach (var aEdgeSneakPeakList in result)
            {
                foreach (var aSneakPeak in aEdgeSneakPeakList)
                {
                    VertexModel sourceVertex = graphElements[aSneakPeak.SourceVertexId] as VertexModel;
                    VertexModel targetVertex = graphElements[aSneakPeak.TargetVertexId] as VertexModel;
                    if (sourceVertex != null && targetVertex != null)
                    {
                        graphElements[aSneakPeak.Id] =
                            new EdgeModel(
                                aSneakPeak.Id,
                                aSneakPeak.CreationDate,
                                aSneakPeak.ModificationDate,
                                targetVertex,
                                sourceVertex,
                                aSneakPeak.Label,
                                aSneakPeak.Properties);
                    }
                    else
                    {
                        throw new Exception(String.Format("Corrupt savegame... could not create the edge {0}", aSneakPeak.Id));
                    }
                }
            }

            foreach (var aKV in edgeTodo)
            {
                EdgeModel edge = graphElements[aKV.Key] as EdgeModel;
                if (edge != null)
                {
                    foreach (var aTodo in aKV.Value)
                    {
                        VertexModel interestingVertex = graphElements[aTodo.VertexId] as VertexModel;
                        if (interestingVertex != null)
                        {
                            if (aTodo.IsIncomingEdge)
                            {
                                interestingVertex.AddIncomingEdge(aTodo.EdgePropertyId, edge);
                            }
                            else
                            {
                                interestingVertex.AddOutEdge(aTodo.EdgePropertyId, edge);
                            }
                        }
                        else
                        {
                            _logger.LogError(String.Format("Corrupt savegame... could not get the vertex {0}", aTodo.VertexId));
                        }
                    }
                }
                else
                {
                    _logger.LogError(String.Format("Corrupt savegame... could not get the edge {0}", aKV.Key));
                }
            }
        }

        /// <summary>
        ///   Creates the partitions.
        /// </summary>
        /// <returns> The partitions. </returns>
        /// <param name='totalCount'> Total count. </param>
        /// <param name='savePartitions'> Save partitions. </param>
        private List<Tuple<Int32, Int32>> CreatePartitions(UInt32 totalCount, UInt32 savePartitions)
        {
            var result = new List<Tuple<Int32, Int32>>();

            if (totalCount < savePartitions)
            {
                for (var i = 0; i < totalCount; i++)
                {
                    result.Add(new Tuple<Int32, Int32>(i, i + 1));
                }

                return result;
            }

            UInt32 size = totalCount / savePartitions;

            for (var i = 0; i < savePartitions; i++)
            {
                var lowerLimit = 0 + i * size;
                var upperLimit = 0 + (i * size) + size;
                result.Add(new Tuple<Int32, Int32>(Convert.ToInt32(lowerLimit), Convert.ToInt32(upperLimit)));
            }

            //trim the last partition
            var lastPartition = Convert.ToInt32(savePartitions - 1);
            var lastElement = Convert.ToInt32(0 + totalCount);
            result[lastPartition] = new Tuple<Int32, Int32>(result[lastPartition].Item1, lastElement);

            return result;
        }

        /// <summary>
        ///   Writes A graph element.
        /// </summary>
        /// <param name='graphElement'> Graph element. </param>
        /// <param name='writer'> Writer. </param>
        private void WriteAGraphElement(AGraphElement graphElement, SerializationWriter writer)
        {
            writer.WriteOptimized(graphElement.Id);
            writer.WriteOptimized(graphElement.CreationDate);
            writer.WriteOptimized(graphElement.ModificationDate);
            writer.WriteOptimized(graphElement.Label);

            var properties = graphElement.GetAllProperties();
            writer.WriteOptimized(properties.Count);
            foreach (var aProperty in properties)
            {
                writer.WriteOptimized(aProperty.PropertyId);
                writer.WriteObject(aProperty.Value);
            }
        }

        /// <summary>
        ///   Loads the vertex.
        /// </summary>
        /// <param name='reader'> Reader. </param>
        /// <param name='graphElements'> Graph elements. </param>
        /// <param name='edgeTodo'> Edge todo. </param>
        private void LoadVertex(SerializationReader reader, AGraphElement[] graphElements,
                                       Dictionary<Int32, List<EdgeOnVertexToDo>> edgeTodo)
        {
            var id = reader.ReadOptimizedInt32();
            var creationDate = reader.ReadOptimizedUInt32();
            var modificationDate = reader.ReadOptimizedUInt32();
            var label = reader.ReadOptimizedString();

            #region properties

            var propertyCount = reader.ReadOptimizedInt32();
            PropertyContainer[] properties = null;

            if (propertyCount > 0)
            {
                properties = new PropertyContainer[propertyCount];
                for (var i = 0; i < propertyCount; i++)
                {
                    var propertyIdentifier = reader.ReadOptimizedUInt16();
                    var propertyValue = reader.ReadObject();

                    properties[i] = new PropertyContainer { PropertyId = propertyIdentifier, Value = propertyValue };
                }
            }

            #endregion

            #region edges

            #region outgoing edges

            List<EdgeContainer> outEdgeProperties = null;
            var outEdgeCount = reader.ReadInt32();

            if (outEdgeCount > 0)
            {
                outEdgeProperties = new List<EdgeContainer>(outEdgeCount);
                for (var i = 0; i < outEdgeCount; i++)
                {
                    var outEdgePropertyId = reader.ReadUInt16();
                    var outEdgePropertyCount = reader.ReadInt32();
                    var outEdges = new List<EdgeModel>(outEdgePropertyCount);
                    for (var j = 0; j < outEdgePropertyCount; j++)
                    {
                        var edgeId = reader.ReadInt32();


                        EdgeModel edge = graphElements[edgeId] as EdgeModel;
                        if (edge != null)
                        {
                            outEdges.Add(edge);
                        }
                        else
                        {
                            var aEdgeTodo = new EdgeOnVertexToDo
                            {
                                VertexId = id,
                                EdgePropertyId = outEdgePropertyId,
                                IsIncomingEdge = false
                            };

                            List<EdgeOnVertexToDo> todo;
                            if (edgeTodo.TryGetValue(edgeId, out todo))
                            {
                                todo.Add(aEdgeTodo);
                            }
                            else
                            {
                                edgeTodo.Add(edgeId, new List<EdgeOnVertexToDo> { aEdgeTodo });
                            }
                        }
                    }
                    outEdgeProperties.Add(new EdgeContainer(outEdgePropertyId, outEdges));
                }
            }

            #endregion

            #region incoming edges

            List<EdgeContainer> incEdgeProperties = null;
            var incEdgeCount = reader.ReadInt32();

            if (incEdgeCount > 0)
            {
                incEdgeProperties = new List<EdgeContainer>(incEdgeCount);
                for (var i = 0; i < incEdgeCount; i++)
                {
                    var incEdgePropertyId = reader.ReadUInt16();
                    var incEdgePropertyCount = reader.ReadInt32();
                    var incEdges = new List<EdgeModel>(incEdgePropertyCount);
                    for (var j = 0; j < incEdgePropertyCount; j++)
                    {
                        var edgeId = reader.ReadInt32();


                        EdgeModel edge = graphElements[edgeId] as EdgeModel;
                        if (edge != null)
                        {
                            incEdges.Add(edge);
                        }
                        else
                        {
                            var aEdgeTodo = new EdgeOnVertexToDo
                            {
                                VertexId = id,
                                EdgePropertyId = incEdgePropertyId,
                                IsIncomingEdge = true
                            };

                            List<EdgeOnVertexToDo> todo;
                            if (edgeTodo.TryGetValue(edgeId, out todo))
                            {
                                todo.Add(aEdgeTodo);
                            }
                            else
                            {
                                edgeTodo.Add(edgeId, new List<EdgeOnVertexToDo> { aEdgeTodo });
                            }
                        }
                    }
                    incEdgeProperties.Add(new EdgeContainer(incEdgePropertyId, incEdges));
                }
            }

            #endregion

            #endregion

            graphElements[id] = new VertexModel(id, creationDate, modificationDate, label, properties, outEdgeProperties,
                                                     incEdgeProperties);
        }

        /// <summary>
        ///   Writes the vertex.
        /// </summary>
        /// <param name='vertex'> Vertex. </param>
        /// <param name='writer'> Writer. </param>
        private void WriteVertex(VertexModel vertex, SerializationWriter writer)
        {
            writer.Write(SerializedVertex);
            WriteAGraphElement(vertex, writer);

            #region edges

            var outgoingEdges = vertex._outEdges;
            if (outgoingEdges == null)
            {
                writer.Write(0);
            }
            else
            {
                writer.Write(outgoingEdges.Count);
                foreach (var aOutEdgeProperty in outgoingEdges)
                {
                    writer.Write(aOutEdgeProperty.EdgePropertyId);
                    writer.Write(aOutEdgeProperty.Edges.Count);
                    foreach (var aOutEdge in aOutEdgeProperty.Edges)
                    {
                        writer.Write(aOutEdge.Id);
                    }
                }
            }

            var incomingEdges = vertex._inEdges;
            if (incomingEdges == null)
            {
                writer.Write(0);
            }
            else
            {
                writer.Write(incomingEdges.Count);
                foreach (var aIncEdgeProperty in incomingEdges)
                {
                    writer.Write(aIncEdgeProperty.EdgePropertyId);
                    writer.Write(aIncEdgeProperty.Edges.Count);
                    foreach (var aIncEdge in aIncEdgeProperty.Edges)
                    {
                        writer.Write(aIncEdge.Id);
                    }
                }
            }

            #endregion
        }

        /// <summary>
        ///   Loads the edge.
        /// </summary>
        /// <param name='reader'> Reader. </param>
        /// <param name='graphElements'> Graph elements. </param>
        /// <param name='sneakPeaks'> Sneak peaks. </param>
        private void LoadEdge(SerializationReader reader, AGraphElement[] graphElements,
                                     ref List<EdgeSneakPeak> sneakPeaks)
        {
            var id = reader.ReadOptimizedInt32();
            var creationDate = reader.ReadOptimizedUInt32();
            var modificationDate = reader.ReadOptimizedUInt32();
            var label = reader.ReadOptimizedString();

            #region properties

            PropertyContainer[] properties = null;
            var propertyCount = reader.ReadOptimizedInt32();

            if (propertyCount > 0)
            {
                properties = new PropertyContainer[propertyCount];
                for (var i = 0; i < propertyCount; i++)
                {
                    var propertyIdentifier = reader.ReadOptimizedUInt16();
                    var propertyValue = reader.ReadObject();

                    properties[i] = new PropertyContainer { PropertyId = propertyIdentifier, Value = propertyValue };
                }
            }

            #endregion

            var sourceVertexId = reader.ReadOptimizedInt32();
            var targetVertexId = reader.ReadOptimizedInt32();

            VertexModel sourceVertex = graphElements[sourceVertexId] as VertexModel;
            VertexModel targetVertex = graphElements[targetVertexId] as VertexModel;

            if (sourceVertex != null && targetVertex != null)
            {
                graphElements[id] = new EdgeModel(id, creationDate, modificationDate, targetVertex, sourceVertex, label, properties);
            }
            else
            {
                sneakPeaks.Add(new EdgeSneakPeak
                {
                    CreationDate = creationDate,
                    Id = id,
                    ModificationDate = modificationDate,
                    Properties = properties,
                    SourceVertexId = sourceVertexId,
                    TargetVertexId = targetVertexId,
                    Label = label
                });
            }
        }

        /// <summary>
        ///   Writes the edge.
        /// </summary>
        /// <param name='edge'> Edge. </param>
        /// <param name='writer'> Writer. </param>
        private void WriteEdge(EdgeModel edge, SerializationWriter writer)
        {
            writer.Write(SerializedEdge);
            WriteAGraphElement(edge, writer);
            writer.WriteOptimized(edge.SourceVertex.Id);
            writer.WriteOptimized(edge.TargetVertex.Id);
        }

        #endregion
    }
}
