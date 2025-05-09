﻿// MIT License
//
// ScaleFreeNetwork.cs
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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading.Tasks;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Helper;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;
using static NoSQL.GraphDB.Core.Algorithms.Delegates;

namespace NoSQL.GraphDB.App.Controllers.Benchmark
{
    public class ScaleFreeNetwork
    {
        private int _numberOfToBeTestedVertices = 10000000;
        private Fallen8 _f8;

        private static string edgeProperty = "A";

        public ScaleFreeNetwork(Fallen8 fallen8)
        {
            _f8 = fallen8;
        }

        /// <summary>
        /// Creates a scale free network
        /// </summary>
        /// <param name="nodeCount"></param>
        /// <param name="edgeCountPerVertex"></param>
        /// <param name="fallen8"></param>
        public void CreateScaleFreeNetwork(int nodeCount, int edgeCountPerVertex)
        {
            var creationDate = DateHelper.ConvertDateTime(DateTime.Now);
            var prng = new Random();
            if (nodeCount < _numberOfToBeTestedVertices)
            {
                _numberOfToBeTestedVertices = nodeCount;
            }

            CreateVerticesTransaction vertexTx = new CreateVerticesTransaction();

            for (var i = 0; i < nodeCount; i++)
            {
                //                vertexIDs.Add(
                //                    fallen8.CreateVertex(creationDate, new PropertyContainer[4]
                //                                                           {
                //                                                               new PropertyContainer { PropertyId = 23, Value = 4344 },
                //                                                               new PropertyContainer { PropertyId = 24, Value = "Ein gaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaanz langes Property" },
                //                                                               new PropertyContainer { PropertyId = 25, Value = "Ein kurzes Property" },
                //                                                               new PropertyContainer { PropertyId = 26, Value = "Ein gaaaaaaaanz langes Property" },
                //                                                           }).Id);
                vertexTx.AddVertex(creationDate);
            }

            var vertexTxTask = _f8.EnqueueTransaction(vertexTx);

            vertexTxTask.WaitUntilFinished();

            var verticesCreates = vertexTx.GetCreatedVertices();

            if (edgeCountPerVertex != 0)
            {
                var partitions = Partitioner.Create(0, verticesCreates.Count);

                Parallel.ForEach(partitions, range =>
                {
                    var verticesInPartition = range.Item2 - range.Item1;
                    CreateEdges(verticesCreates, verticesCreates.GetRange(range.Item1, verticesInPartition), edgeCountPerVertex, creationDate);
                });
            }

            TrimTransaction tx = new TrimTransaction();

            _f8.EnqueueTransaction(tx);
        }

        private void CreateEdges(ImmutableList<VertexModel> allVertices, ImmutableList<VertexModel> partition, long edgesPerVertex, UInt32 creationDate)
        {
            CreateEdgesTransaction edgesCreateTx = new CreateEdgesTransaction();

            var prng = new Random();

            foreach (var aVertex in partition)
            {
                var targetVertices = new HashSet<Int32>();

                do
                {
                    targetVertices.Add(allVertices[prng.Next(0, allVertices.Count)].Id);
                } while (targetVertices.Count < edgesPerVertex);

                foreach (var aTargetVertex in targetVertices)
                {
                    //                    fallen8.CreateEdge(aVertexId, 0, aTargetVertex, creationDate, new PropertyContainer[2]
                    //                                                           {
                    //                                                               new PropertyContainer { PropertyId = 29, Value = 23.4 },
                    //                                                               new PropertyContainer { PropertyId = 1, Value = 2 },
                    //                                                           });
                    //
                    edgesCreateTx.AddEdge(aVertex.Id, edgeProperty, aTargetVertex, creationDate);
                }
            }

            _f8.EnqueueTransaction(edgesCreateTx).WaitUntilFinished();
        }

        /// <summary>
        /// Benchmark
        /// </summary>
        /// <param name="fallen8"></param>
        /// <param name="myIterations"></param>
        /// <returns></returns>
        public String Bench(int myIterations = 1000)
        {
            ImmutableList<VertexModel> vertices = _f8.GetAllVertices();
            var tps = new List<double>();
            long edgeCount = 0;
            var sb = new StringBuilder();

            Int32 range = ((vertices.Count / Environment.ProcessorCount) * 3) / 2;

            for (var i = 0; i < myIterations; i++)
            {
                var sw = Stopwatch.StartNew();

                edgeCount = CountAllEdgesParallelPartitioner(vertices, range);

                sw.Stop();

                tps.Add(edgeCount / sw.Elapsed.TotalSeconds);
            }

            sb.AppendLine(String.Format("Traversed {0} edges. Average: {1}TPS Median: {2}TPS StandardDeviation {3}TPS ", edgeCount, Statistics.Average(tps), Statistics.Median(tps), Statistics.StandardDeviation(tps)));

            return sb.ToString();
        }

        /// <summary>
        /// Counter
        /// </summary>
        /// <param name="vertices"></param>
        /// <param name="vertexRange"></param>
        /// <returns></returns>
        private static long CountAllEdgesParallelPartitioner(ImmutableList<VertexModel> vertices, Int32 vertexRange)
        {
            var lockObject = new object();
            var edgeCount = 0L;
            var rangePartitioner = Partitioner.Create(0, vertices.Count, vertexRange);

            Parallel.ForEach(
                rangePartitioner,
                () => 0L,
                delegate (Tuple<int, int> range, ParallelLoopState loopstate, long initialValue) {
                    var localCount = initialValue;

                    for (var i = range.Item1; i < range.Item2; i++)
                    {
                        ImmutableList<EdgeModel> outEdge;
                        if (vertices[i].TryGetOutEdge(out outEdge, edgeProperty))
                        {
                            for (int j = 0; j < outEdge.Count; j++)
                            {
                                var vertex = outEdge[j].TargetVertex;
                                localCount++;
                            }
                        }
                    }

                    return localCount;
                },
                delegate (long localSum) {
                    lock (lockObject)
                    {
                        edgeCount += localSum;
                    }
                });

            return edgeCount;
        }
    }
}
