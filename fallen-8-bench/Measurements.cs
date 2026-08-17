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
    ///   The five measurements the capacity report publishes. Each is deliberately simple and
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
        ///   How long it takes to bring one namespace up: construct an engine and restore the
        ///   checkpoint it boots from. This is what a start pays per LOADED namespace, and therefore
        ///   the latency half of what excluding a namespace from the startup load saves (feature
        ///   namespace-startup-load; the heap half is <see cref="Memory" />). It is measured on the
        ///   same graph shapes as <see cref="SaveStall" />, so a save and its restore can be read
        ///   off one row pair.
        ///
        ///   <para>Deliberately NOT warmed up, which is the opposite choice from
        ///   <see cref="SaveStall" /> and for a stated reason: a save repeats for the life of a
        ///   process, so its steady state is what a caller waits on, while a startup load happens
        ///   exactly once in a COLD process - the JIT and first-touch costs a warm-up would remove
        ///   are part of the thing being measured. The scenarios run smallest first, so that one-off
        ///   cost lands on the row where it is visible as a worse per-element rate rather than being
        ///   hidden inside a large one.</para>
        ///
        ///   <para>The write-ahead-log tail is not in the number. A boot replays it on top of the
        ///   checkpoint, and its cost is proportional to what was committed since the last save, not
        ///   to the size of the graph.</para>
        /// </summary>
        internal static LoadMetric Load(Shape shape)
        {
            var directory = Path.Combine(Path.GetTempPath(), "f8-bench-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            try
            {
                String checkpoint;
                Int32 expectedVertices;
                Int64 expectedEdges;

                var writer = GraphBuilder.NewEngine();
                try
                {
                    var vertices = GraphBuilder.AddVertices(writer, shape.Vertices);
                    expectedEdges = GraphBuilder.AddEdges(writer, vertices, shape.EdgesPerVertex);
                    expectedVertices = writer.VertexCount;

                    var save = new SaveTransaction { Path = Path.Combine(directory, "checkpoint"), SavePartitions = 0 };
                    writer.EnqueueTransaction(save).WaitUntilFinished();
                    checkpoint = save.ActualPath;
                }
                finally
                {
                    writer.Dispose();
                }

                // The graph that produced the checkpoint has to be gone before the restore is timed:
                // a boot restores into a heap that does not already hold a copy of the same graph,
                // and leaving one there would have the restore allocating against a collector that
                // is working twice as hard as it would on a real start.
                GraphBuilder.RetainedBytes();

                var stopwatch = Stopwatch.StartNew();
                // Engine construction is inside the timed region on purpose: a boot pays it per
                // namespace too (it is also where an unanchored write-ahead log replays), and "do
                // not load this namespace" is precisely a decision not to run these two steps.
                var restored = GraphBuilder.NewEngine();
                try
                {
                    var load = new LoadTransaction { Path = checkpoint, StartServices = false };
                    restored.EnqueueTransaction(load).WaitUntilFinished();
                    stopwatch.Stop();

                    // A load that quietly restored nothing would report a spectacular rate, so the
                    // counts are checked rather than assumed: the engine treats a missing file as a
                    // no-op, and a rolled-back load leaves the graph empty.
                    if (restored.VertexCount != expectedVertices || restored.EdgeCount != expectedEdges)
                    {
                        throw new InvalidOperationException(String.Format(CultureInfo.InvariantCulture,
                            "The restore of \"{0}\" produced {1} vertices and {2} edges, but the checkpoint was " +
                            "written from {3} vertices and {4} edges; the measurement would be meaningless.",
                            checkpoint, restored.VertexCount, restored.EdgeCount, expectedVertices, expectedEdges));
                    }

                    var elements = (Int64)restored.VertexCount + restored.EdgeCount;
                    return new LoadMetric
                    {
                        Label = shape.Label,
                        Elements = elements,
                        Vertices = restored.VertexCount,
                        Edges = expectedEdges,
                        LoadMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1),
                        ElementsPerSecond = Math.Round(elements / stopwatch.Elapsed.TotalSeconds, 0)
                    };
                }
                finally
                {
                    restored.Dispose();
                }
            }
            finally
            {
                TryDelete(directory);
            }
        }

        /// <summary>
        ///   Raw out-edge traversal throughput, through the SAME engine primitive
        ///   (<see cref="OutEdgeSweep" />) that <c>GET /ns/{ns}/benchmark</c> uses, so a number measured here
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
            // Traversal is heap-layout sensitive: inside a full run this scenario's graph lands in a
            // heap shaped by every scenario before it, which measured 312M against 380M edges/s for
            // the same code and shape (feature traversal-sweep-partitioning, finding 3). Compact
            // everything first, so the published number describes the engine rather than the
            // allocator's history.
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GraphBuilder.RetainedBytes();

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
