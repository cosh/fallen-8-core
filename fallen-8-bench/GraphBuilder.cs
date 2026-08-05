// MIT License
//
// GraphBuilder.cs
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
using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Bench
{
    /// <summary>
    ///   Deterministic graph construction shared by every measurement, so two numbers in one report
    ///   always describe the same kind of graph. Determinism matters more than realism here: a
    ///   measurement that cannot be repeated on another machine is not comparable, and comparing is
    ///   the whole purpose of the report.
    /// </summary>
    internal static class GraphBuilder
    {
        /// <summary>The edge type every generated edge carries.</summary>
        internal const String EdgeType = "bench";

        /// <summary>A fixed creation stamp: a wall-clock read would make two runs differ for no reason.</summary>
        private const UInt32 CreationDate = 1u;

        /// <summary>A fixed seed, so the same request produces the same graph everywhere.</summary>
        internal const Int32 Seed = 20260805;

        /// <summary>An engine with no logging and nothing on disk.</summary>
        internal static Fallen8 NewEngine() => new Fallen8(NullLoggerFactory.Instance);

        /// <summary>An engine whose commits are appended to a write-ahead log and fsync'd.</summary>
        internal static Fallen8 NewEngineWithWal(String walPath)
            => new Fallen8(NullLoggerFactory.Instance, new WriteAheadLogOptions(walPath));

        /// <summary>
        ///   Vertices per construction transaction. Batched rather than one transaction for the whole
        ///   graph because the transaction holds a definition object per element until it commits: at
        ///   ten million vertices a single transaction's definition list is itself hundreds of
        ///   megabytes, on top of the graph it is building. Batching bounds that to one batch's worth.
        /// </summary>
        private const Int32 VertexBatch = 1_000_000;

        /// <summary>Edges per construction transaction, bounded for the same reason.</summary>
        private const Int32 EdgeBatch = 2_000_000;

        /// <summary>
        ///   Adds <paramref name="count" /> bare vertices and returns them in creation order. Bare on
        ///   purpose: properties are the caller's variable, so the structural cost is measured without
        ///   them and the page says to add yours on top.
        /// </summary>
        internal static IReadOnlyList<VertexModel> AddVertices(Fallen8 engine, Int32 count)
        {
            // Presized: growing a ten-million-element list by doubling would leave a trail of
            // abandoned arrays right where the next reading measures retained bytes.
            var created = new List<VertexModel>(count);

            for (var start = 0; start < count; start += VertexBatch)
            {
                var batch = Math.Min(VertexBatch, count - start);
                var tx = new CreateVerticesTransaction();
                for (var i = 0; i < batch; i++)
                {
                    tx.AddVertex(CreationDate, null);
                }

                engine.EnqueueTransaction(tx).WaitUntilFinished();
                created.AddRange(tx.GetCreatedVertices());
            }

            return created;
        }

        /// <summary>
        ///   Adds <paramref name="edgesPerVertex" /> out-edges to every vertex, targets drawn from the
        ///   same vertex set with a seeded generator. Returns the edge count added.
        ///
        ///   <para>Targets are uniform over the whole vertex set, which is the pessimistic case for a
        ///   traversal: every followed edge is a random access into the vertex heap, so at a size that
        ///   outgrows cache the sweep measures memory latency rather than the adjacency walk. That is
        ///   deliberate, because it is what a real graph traversal does.</para>
        /// </summary>
        internal static Int64 AddEdges(Fallen8 engine, IReadOnlyList<VertexModel> vertices, Int32 edgesPerVertex)
        {
            var random = new Random(Seed);
            var added = 0L;
            var tx = new CreateEdgesTransaction();
            var pending = 0;

            for (var v = 0; v < vertices.Count; v++)
            {
                for (var e = 0; e < edgesPerVertex; e++)
                {
                    tx.AddEdge(vertices[v].Id, EdgeType, vertices[random.Next(vertices.Count)].Id, CreationDate);
                    pending++;
                    added++;

                    if (pending == EdgeBatch)
                    {
                        engine.EnqueueTransaction(tx).WaitUntilFinished();
                        tx = new CreateEdgesTransaction();
                        pending = 0;
                    }
                }
            }

            if (pending > 0)
            {
                engine.EnqueueTransaction(tx).WaitUntilFinished();
            }

            return added;
        }

        /// <summary>
        ///   Retained managed bytes, after a forced blocking compacting collection. Two collections
        ///   with a finaliser drain between them, because a single pass leaves objects that were only
        ///   reachable from a finalisation queue still counted, which would inflate every reading.
        /// </summary>
        internal static Int64 RetainedBytes()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            return GC.GetTotalMemory(true);
        }
    }
}
