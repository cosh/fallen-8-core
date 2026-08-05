// MIT License
//
// Measurements.cs
//
// Copyright (c) 2011-2026 Henning Rauch
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
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NoSQL.GraphDB.Core.Algorithms.Traversal;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Bench
{
    /// <summary>
    ///   One scenario shape: a vertex count and an out-degree.
    /// </summary>
    internal readonly struct Shape
    {
        internal Shape(String label, Int32 vertices, Int32 edgesPerVertex)
        {
            Label = label;
            Vertices = vertices;
            EdgesPerVertex = edgesPerVertex;
        }

        internal String Label { get; }

        internal Int32 Vertices { get; }

        internal Int32 EdgesPerVertex { get; }
    }

    /// <summary>
    ///   The four measurements the capacity report publishes. Each is deliberately simple and
    ///   self-contained: the value of this tool is that a reader can see exactly what was timed.
    /// </summary>
    internal static class Measurements
    {
        /// <summary>
        ///   Retained bytes per vertex and per edge for one shape.
        ///
        ///   <para>Two separate deltas: a bare vertex set first (so the per-vertex figure carries no
        ///   adjacency at all), then the edges on top of that fixed set (so the per-edge figure carries
        ///   the edge object AND the adjacency slots on both endpoints). Subtracting a shared baseline
        ///   keeps the engine's own fixed cost out of both.</para>
        /// </summary>
        internal static MemoryMetric Memory(Shape shape)
        {
            // The engine is constructed BEFORE the baseline reading, so its own fixed cost (the
            // transaction manager, the factories, an empty master store) is excluded rather than
            // amortised into the per-vertex figure. Getting this order wrong inflates bytesPerVertex,
            // and inflates it more the smaller the scenario is.
            var engine = GraphBuilder.NewEngine();
            var baseline = GraphBuilder.RetainedBytes();

            var vertices = GraphBuilder.AddVertices(engine, shape.Vertices);
            var afterVertices = GraphBuilder.RetainedBytes();

            var edges = GraphBuilder.AddEdges(engine, vertices, shape.EdgesPerVertex);
            var afterEdges = GraphBuilder.RetainedBytes();

            var metric = new MemoryMetric
            {
                Label = shape.Label,
                Vertices = shape.Vertices,
                Edges = edges,
                AverageDegree = shape.EdgesPerVertex,
                BytesPerVertex = Math.Round((afterVertices - baseline) / (Double)shape.Vertices, 1),
                BytesPerEdge = edges == 0 ? 0d : Math.Round((afterEdges - afterVertices) / (Double)edges, 1),
                RetainedMb = Math.Round((afterEdges - baseline) / 1048576.0, 1)
            };

            // Keep the graph alive until after the last reading, otherwise the collection that
            // precedes it is free to reclaim the very thing being measured.
            GC.KeepAlive(engine);
            engine.Dispose();
            return metric;
        }

        /// <summary>
        ///   Committed single-element writes per second with the WAL on, at a given producer count.
        ///
        ///   <para>Single-element transactions on purpose: that is the worst case, and the gap between
        ///   one producer and many is exactly the group-commit amortisation the page describes. A batch
        ///   transaction would measure something else entirely (and much faster).</para>
        /// </summary>
        internal static WriteThroughputMetric WriteThroughput(String label, Int32 totalWrites, Int32 producers, Double maxSeconds)
        {
            var directory = Path.Combine(Path.GetTempPath(), "f8-bench-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            try
            {
                var engine = GraphBuilder.NewEngineWithWal(Path.Combine(directory, "bench.wal"));
                try
                {
                    var perProducer = totalWrites / producers;
                    var committed = 0L;

                    // A time cap, not just a write count. Serial single-element writes are fsync-bound
                    // at a few hundred per second, so the full profile's write count would otherwise
                    // add many minutes for a number that is already stable after a few seconds. The
                    // rate is computed over what was ACTUALLY committed and the report records that
                    // count, so a capped scenario is still an honest rate, just over less work.
                    var stopwatch = Stopwatch.StartNew();
                    Parallel.For(0, producers, new ParallelOptions { MaxDegreeOfParallelism = producers }, _ =>
                    {
                        var mine = 0;
                        for (var i = 0; i < perProducer; i++)
                        {
                            var tx = new CreateVertexTransaction
                            {
                                Definition = new VertexDefinition { CreationDate = 1u }
                            };
                            engine.EnqueueTransaction(tx).WaitUntilFinished();
                            mine++;

                            // Checked every 64 commits: reading the stopwatch on every one would show
                            // up in an fsync-bound measurement.
                            if ((mine & 63) == 0 && stopwatch.Elapsed.TotalSeconds >= maxSeconds)
                            {
                                break;
                            }
                        }

                        Interlocked.Add(ref committed, mine);
                    });
                    stopwatch.Stop();

                    return new WriteThroughputMetric
                    {
                        Label = label,
                        Producers = producers,
                        Writes = committed,
                        WritesPerSecond = Math.Round(committed / stopwatch.Elapsed.TotalSeconds, 0),
                        WalEnabled = true
                    };
                }
                finally
                {
                    engine.Dispose();
                }
            }
            finally
            {
                TryDelete(directory);
            }
        }

        /// <summary>
        ///   How long a checkpoint holds the single writer thread, which is what any concurrent write
        ///   waits. The first save is discarded as a warm-up (it pays first-touch costs a steady-state
        ///   instance would not), and the second is the reading.
        /// </summary>
        internal static SaveStallMetric SaveStall(Shape shape)
        {
            var directory = Path.Combine(Path.GetTempPath(), "f8-bench-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            try
            {
                var engine = GraphBuilder.NewEngine();
                try
                {
                    var vertices = GraphBuilder.AddVertices(engine, shape.Vertices);
                    var edges = GraphBuilder.AddEdges(engine, vertices, shape.EdgesPerVertex);

                    var path = Path.Combine(directory, "checkpoint");
                    TimeSave(engine, path);
                    var milliseconds = TimeSave(engine, path);

                    return new SaveStallMetric
                    {
                        Elements = engine.VertexCount + engine.EdgeCount,
                        Vertices = engine.VertexCount,
                        Edges = edges,
                        WriterHoldMs = Math.Round(milliseconds, 1)
                    };
                }
                finally
                {
                    engine.Dispose();
                }
            }
            finally
            {
                TryDelete(directory);
            }
        }

        /// <summary>
        ///   Raw out-edge traversal throughput, through the SAME engine primitive
        ///   (<see cref="OutEdgeSweep" />) that <c>GET /benchmark</c> uses, so a number measured here
        ///   and a number measured through the REST endpoint describe one code path.
        ///
        ///   <para>The vertex snapshot is materialised once, outside the timed region: it is an O(V)
        ///   allocation and not traversal work. The reported rate is the BEST pass rather than the
        ///   mean, because a slow pass means the machine was busy with something else, not that the
        ///   traversal is slower. Every pass prints, so an unstable machine is visible instead of
        ///   averaged away.</para>
        /// </summary>
        internal static TraversalMetric Traversal(Shape shape, Int32 iterations)
        {
            var engine = GraphBuilder.NewEngine();
            try
            {
                var vertices = GraphBuilder.AddVertices(engine, shape.Vertices);
                var edges = GraphBuilder.AddEdges(engine, vertices, shape.EdgesPerVertex);

                var snapshot = engine.GetAllVertices();
                var partition = OutEdgeSweep.DefaultPartitionSize(snapshot.Count);

                // One untimed pass: on a heap this size the first touch of every adjacency array is
                // seconds of one-off page-fault cost that has nothing to do with traversal speed.
                OutEdgeSweep.Sweep(snapshot, partition);

                var best = 0d;
                for (var i = 0; i < iterations; i++)
                {
                    var stopwatch = Stopwatch.StartNew();
                    var traversed = OutEdgeSweep.Sweep(snapshot, partition);
                    stopwatch.Stop();

                    var rate = traversed / stopwatch.Elapsed.TotalSeconds;
                    Console.WriteLine(String.Format(CultureInfo.InvariantCulture,
                        "    pass {0}/{1}: {2:N0} edges in {3:N0} ms = {4:N0} edges/s",
                        i + 1, iterations, traversed, stopwatch.Elapsed.TotalMilliseconds, rate));
                    best = Math.Max(best, rate);
                }

                return new TraversalMetric
                {
                    Label = shape.Label,
                    Vertices = shape.Vertices,
                    Edges = edges,
                    Iterations = iterations,
                    EdgesPerSecond = Math.Round(best, 0)
                };
            }
            finally
            {
                engine.Dispose();
            }
        }

        private static Double TimeSave(Core.Fallen8 engine, String path)
        {
            var stopwatch = Stopwatch.StartNew();
            var tx = new SaveTransaction { Path = path, SavePartitions = 0 };
            engine.EnqueueTransaction(tx).WaitUntilFinished();
            stopwatch.Stop();
            return stopwatch.Elapsed.TotalMilliseconds;
        }

        private static void TryDelete(String directory)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a measurement run over.
            }
        }
    }
}
