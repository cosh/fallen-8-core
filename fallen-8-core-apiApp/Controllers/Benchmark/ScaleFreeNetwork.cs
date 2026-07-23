// MIT License
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
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading.Tasks;
using NoSQL.GraphDB.App.Controllers.Model;
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
        private IFallen8 _f8;

        private static string edgeProperty = "A";

        public ScaleFreeNetwork(IFallen8 fallen8)
        {
            _f8 = fallen8;
        }

        /// <summary>
        /// Creates a benchmark graph. Despite the class name, the default edge distribution is
        /// UNIFORM random (no hubs); pass <paramref name="preferentialAttachment"/> for a real
        /// Barabási–Albert-style scale-free network whose analytics show structure at scale
        /// (feature sample-graphs). Both distributions write edge property "A" — the exact
        /// edges <see cref="TryBench"/> traverses.
        /// </summary>
        /// <param name="nodeCount">The number of nodes to create</param>
        /// <param name="edgeCountPerVertex">The number of edges per vertex</param>
        /// <param name="preferentialAttachment">Whether targets are drawn preferentially
        /// (rich-get-richer) instead of uniformly</param>
        public async Task CreateScaleFreeNetworkAsync(int nodeCount, int edgeCountPerVertex, bool preferentialAttachment = false)
        {
            // The shared local-clock stamp (feature code-quality): the clock convention lives
            // in DateHelper alone - see the comment on DateHelper.GetModificationDate.
            var creationDate = DateHelper.GetNowStamp();
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

            await vertexTxTask.Completion;

            var verticesCreates = vertexTx.GetCreatedVertices();

            if (edgeCountPerVertex != 0)
            {
                if (preferentialAttachment)
                {
                    await CreatePreferentialEdgesAsync(verticesCreates, edgeCountPerVertex, creationDate);
                }
                else
                {
                    var partitions = Partitioner.Create(0, verticesCreates.Count);

                    // The partitions build their edge transactions CPU-parallel and enqueue without
                    // blocking; the single await below covers all of them, so no pool thread is
                    // pinned while the writer drains the batch.
                    var edgeCommits = new ConcurrentBag<TransactionInformation>();

                    Parallel.ForEach(partitions, range =>
                    {
                        var verticesInPartition = range.Item2 - range.Item1;
                        edgeCommits.Add(CreateEdges(verticesCreates, verticesCreates.GetRange(range.Item1, verticesInPartition), edgeCountPerVertex, creationDate));
                    });

                    await Task.WhenAll(edgeCommits.Select(commit => commit.Completion));
                }
            }

            TrimTransaction tx = new TrimTransaction();

            _f8.EnqueueTransaction(tx);
        }

        private TransactionInformation CreateEdges(ImmutableList<VertexModel> allVertices, ImmutableList<VertexModel> partition, long edgesPerVertex, UInt32 creationDate)
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

            return _f8.EnqueueTransaction(edgesCreateTx);
        }

        /// <summary>
        /// Barabási–Albert-style attachment: targets are drawn from a pool where every already-
        /// processed vertex appears once (baseline) plus once per time it was chosen — the rich
        /// get richer, so the in-degree distribution is heavy-tailed and PageRank/degree at
        /// scale show real hubs. Sequential by nature (every pick reweights the pool); the picks
        /// are index lookups, and the transactions are batched like the uniform path's
        /// partitions. Vertex i gets min(edgesPerVertex, i) out-edges, all toward earlier
        /// vertices, so the pool always holds enough distinct targets.
        /// </summary>
        private async Task CreatePreferentialEdgesAsync(ImmutableList<VertexModel> vertices, int edgesPerVertex, UInt32 creationDate)
        {
            const int edgesPerTransaction = 50_000;

            var prng = new Random();
            var pool = new List<Int32>(vertices.Count * (1 + edgesPerVertex));
            var commits = new List<TransactionInformation>();
            var edgesCreateTx = new CreateEdgesTransaction();
            var edgesInTransaction = 0;

            for (var i = 0; i < vertices.Count; i++)
            {
                var targetCount = Math.Min(edgesPerVertex, i);
                if (targetCount > 0)
                {
                    var targets = new HashSet<Int32>();
                    while (targets.Count < targetCount)
                    {
                        targets.Add(pool[prng.Next(0, pool.Count)]);
                    }

                    foreach (var target in targets)
                    {
                        edgesCreateTx.AddEdge(vertices[i].Id, edgeProperty, target, creationDate);
                        pool.Add(target);

                        if (++edgesInTransaction >= edgesPerTransaction)
                        {
                            commits.Add(_f8.EnqueueTransaction(edgesCreateTx));
                            edgesCreateTx = new CreateEdgesTransaction();
                            edgesInTransaction = 0;
                        }
                    }
                }

                pool.Add(vertices[i].Id);
            }

            if (edgesInTransaction > 0)
            {
                commits.Add(_f8.EnqueueTransaction(edgesCreateTx));
            }

            await Task.WhenAll(commits.Select(commit => commit.Completion));
        }

        /// <summary>
        /// Runs the edge-traversal benchmark and reports structured statistics.
        /// </summary>
        /// <param name="result">The benchmark statistics, or null on failure</param>
        /// <param name="message">The failure reason, or null on success</param>
        /// <param name="myIterations">Number of timed iterations</param>
        /// <returns>True when the benchmark ran</returns>
        public Boolean TryBench(out BenchmarkResultREST result, out String message, int myIterations = 1000)
        {
            result = null;
            message = null;

            IReadOnlyList<VertexModel> vertices = _f8.GetAllVertices();
            var tps = new List<double>();
            long edgeCount = 0;

            if (vertices == null || vertices.Count == 0)
            {
                message = "No vertices found in the graph.";
                return false;
            }

            if (myIterations <= 0)
            {
                message = "Number of iterations must be greater than 0.";
                return false;
            }

            // Clamp to >=1: with fewer vertices than logical CPUs the naive formula is 0,
            // and Partitioner.Create(..., rangeSize: 0) throws. (A tiny graph on a
            // many-core host, exercised by the tests and the docker smoke test.)
            Int32 range = Math.Max(1, ((vertices.Count / Environment.ProcessorCount) * 3) / 2);

            for (var i = 0; i < myIterations; i++)
            {
                var sw = Stopwatch.StartNew();

                edgeCount = CountAllEdgesParallelPartitioner(vertices, range);

                sw.Stop();

                tps.Add(edgeCount / sw.Elapsed.TotalSeconds);
            }

            result = new BenchmarkResultREST
            {
                Iterations = myIterations,
                EdgesTraversed = edgeCount,
                AverageTps = Statistics.Average(tps),
                MedianTps = Statistics.Median(tps),
                StandardDeviationTps = Statistics.StandardDeviation(tps)
            };
            return true;
        }

        /// <summary>
        /// Counter
        /// </summary>
        /// <param name="vertices"></param>
        /// <param name="vertexRange"></param>
        /// <returns></returns>
        private static long CountAllEdgesParallelPartitioner(IReadOnlyList<VertexModel> vertices, Int32 vertexRange)
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
                        IReadOnlyList<EdgeModel> outEdge;
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
