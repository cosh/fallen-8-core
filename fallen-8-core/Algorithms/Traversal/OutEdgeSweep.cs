// MIT License
//
// OutEdgeSweep.cs
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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NoSQL.GraphDB.Core.Model;

namespace NoSQL.GraphDB.Core.Algorithms.Traversal
{
    /// <summary>
    ///   Follows every outgoing edge of a vertex set, in parallel where the host has threads, and
    ///   returns how many it followed.
    ///   This is the raw traversal-throughput primitive: the fastest honest full sweep of the
    ///   adjacency, doing the pointer-following work a real traversal does and nothing else.
    ///
    ///   <para><b>The ONE home for the sweep.</b> <c>GET /benchmark</c> and the offline
    ///   <c>fallen-8-bench</c> capacity tool both call this, so the throughput a user measures with
    ///   either is the same code path. It lives in the engine because that is the only place the
    ///   allocation-free adjacency enumerator is reachable: an out-of-assembly caller has to go
    ///   through <see cref="VertexModel.GetOutgoingEdgeIds" />, which allocates a key list PER VERTEX
    ///   (ten million allocations per pass on a ten-million-vertex graph) and dominates the
    ///   measurement it is supposed to be taking.</para>
    ///
    ///   <para>Schema-agnostic on purpose: it walks every out-edge group whatever its
    ///   edge-property-id, so it measures the graph that is loaded rather than one generated shape.</para>
    /// </summary>
    public static class OutEdgeSweep
    {
        /// <summary>
        ///   A sink for the dereferenced target ids. Without it the JIT is free to notice that the
        ///   loaded <see cref="EdgeModel.TargetVertex" /> is never used and skip the load, which is
        ///   the one memory access the measurement exists to perform: the count alone can be served
        ///   from the adjacency arrays without ever touching a target vertex.
        /// </summary>
        private static Int64 _sink;

        /// <summary>
        ///   The default partition size: sixteen ranges per logical processor, with a 256-vertex
        ///   floor.
        ///
        ///   <para>Sixteen per core sits on the measured throughput plateau (feature
        ///   traversal-sweep-partitioning: at 100M edges the rate is flat from eight ranges per core
        ///   upward, and 11% above a range count below the core count), and a surplus of ranges gives
        ///   the dynamic partitioner room to balance degree skew, so a supernode-heavy range does not
        ///   serialize a whole core's share. The floor bounds per-range dispatch overhead on a graph
        ///   so small the sweep is microseconds anyway, and keeps the size at least one, which
        ///   <see cref="Partitioner" /> requires.</para>
        /// </summary>
        /// <param name="vertexCount">Number of vertices to be partitioned.</param>
        public static Int32 DefaultPartitionSize(Int32 vertexCount)
            => Math.Max(256, vertexCount / (Environment.ProcessorCount * 16));

        /// <summary>
        ///   Follows every out-edge of every vertex in <paramref name="vertices" /> and returns the
        ///   number traversed. Each edge's <see cref="EdgeModel.TargetVertex" /> is dereferenced, so
        ///   the pass does the real pointer-chasing of a traversal instead of reading cached degrees.
        ///
        ///   <para>The caller supplies the vertex snapshot and is expected to reuse it across
        ///   repeated passes: materialising it is an O(V) allocation that has nothing to do with
        ///   traversal speed and would otherwise be timed as if it did.</para>
        /// </summary>
        /// <param name="vertices">The vertex snapshot to sweep, typically <c>GetAllVertices()</c>.</param>
        /// <param name="partitionSize">
        ///   Vertices per parallel range, or <c>null</c> for <see cref="DefaultPartitionSize" />.
        /// </param>
        /// <returns>The total number of outgoing edges followed.</returns>
        public static Int64 Sweep(IReadOnlyList<VertexModel> vertices, Int32? partitionSize = null)
        {
            ArgumentNullException.ThrowIfNull(vertices);

            if (vertices.Count == 0)
            {
                return 0L;
            }

            var range = partitionSize ?? DefaultPartitionSize(vertices.Count);
            if (range < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(partitionSize), "The partition size must be at least one vertex.");
            }

            var edgeCount = 0L;
            var targetSink = 0L;

            // In PARALLEL where the host can run work off the calling thread, as ONE sequential range
            // where it cannot. Why that question is asked once per process, and never as a compile-time
            // or operating-system switch: see HostCapabilities, its single home.
            //
            // Both arms must return the same number: the ranges are disjoint and cover [0, Count), and
            // their counts and sinks are summed, so the answer never depends on which arm ran.
            if (HostCapabilities.SupportsBackgroundWork)
            {
                Parallel.ForEach(
                    Partitioner.Create(0, vertices.Count, range),
                    () => new Accumulator(),
                    (bounds, _, accumulator) =>
                    {
                        accumulator.Edges += SweepRange(vertices, bounds.Item1, bounds.Item2, ref accumulator.Sink);
                        return accumulator;
                    },
                    accumulator =>
                    {
                        Interlocked.Add(ref edgeCount, accumulator.Edges);
                        Interlocked.Add(ref targetSink, accumulator.Sink);
                    });
            }
            else
            {
                edgeCount = SweepRange(vertices, 0, vertices.Count, ref targetSink);
            }

            // Publish once, after the sweep, so the dereferences cannot be elided but the hot loop
            // never touches shared state.
            Volatile.Write(ref _sink, targetSink);
            return edgeCount;
        }

        /// <summary>
        ///   Follows every out-edge of <c>vertices[from..to)</c>, adding the dereferenced target ids to
        ///   <paramref name="sinkAccumulator" /> and returning the edge count. The hot loops run on
        ///   LOCALS and write back once, which is what keeps the per-range state out of the inner loop.
        /// </summary>
        private static Int64 SweepRange(IReadOnlyList<VertexModel> vertices, Int32 from, Int32 to,
                                        ref Int64 sinkAccumulator)
        {
            var edges = 0L;
            var sink = 0L;

            for (var i = from; i < to; i++)
            {
                // The raw adjacency is immutable after publication, so a snapshot read needs no
                // lock; null means the vertex has no outgoing edges at all.
                var adjacency = vertices[i].GetRawOutEdges();
                if (adjacency == null)
                {
                    continue;
                }

                // Struct enumerator over every group: no key list, no wrapper, no allocation.
                foreach (var group in adjacency)
                {
                    var segment = group.Value;
                    var array = segment.Array;
                    var count = segment.Count;
                    var offset = segment.Offset;

                    // Read the backing array directly rather than through the ArraySegment
                    // indexer: the segment is count-bounded, so this is the same elements with
                    // one less bounds-check layer. Bounds and offset are hoisted, so the inner
                    // loop carries no field loads at all.
                    for (var j = 0; j < count; j++)
                    {
                        var target = array[offset + j].TargetVertex;
                        if (target != null)
                        {
                            sink += target.Id;
                        }

                        edges++;
                    }
                }
            }

            sinkAccumulator += sink;
            return edges;
        }

        /// <summary>
        ///   Per-range thread-local state. A class rather than a tuple so the hot loop mutates locals
        ///   and writes back once, instead of copying a struct on every range.
        /// </summary>
        private sealed class Accumulator
        {
            internal Int64 Edges;

            internal Int64 Sink;
        }
    }
}
