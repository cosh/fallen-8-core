// MIT License
//
// IndexRemovalBenchmark.cs
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
using System.Diagnostics;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Opt-in benchmark for bucket-index removal cost. It exists because an integration run that
    ///   withdraws elements was observed to be orders of magnitude slower than the same run creating
    ///   them, and the suspected cause is that a bucket index removes a value by rebuilding the whole
    ///   bucket while it adds one in log time. An integration's claim index puts EVERY element an
    ///   identity claims under a SINGLE key, so that bucket is the whole graph.
    ///
    ///   <para>The control case is the point of this file, not the headline: the same number of
    ///   elements removed from MANY small buckets isolates bucket size as the cause. Without it a
    ///   slow number only says "removal is slow", which does not identify anything.</para>
    ///
    ///   <para>Follows the repo convention (Benchmark category + [Ignore]) so it is NOT part of the
    ///   default run; run the methods explicitly to capture numbers. Output is prefixed "[IDXBENCH]".</para>
    /// </summary>
    [TestClass]
    public class IndexRemovalBenchmark
    {
        private static void Emit(String line)
        {
            Console.WriteLine("[IDXBENCH] " + line);
        }

        private static Int32 EnvInt(String name, Int32 fallback)
        {
            var raw = Environment.GetEnvironmentVariable(name);
            return Int32.TryParse(raw, out var parsed) && parsed > 0 ? parsed : fallback;
        }

        /// <summary>Vertices with no properties, in batches, so the definition list stays bounded.</summary>
        private static List<VertexModel> AddVertices(Fallen8 fallen8, Int32 count)
        {
            var created = new List<VertexModel>(count);
            const Int32 batch = 50_000;
            for (var start = 0; start < count; start += batch)
            {
                var take = Math.Min(batch, count - start);
                var tx = new CreateVerticesTransaction();
                for (var i = 0; i < take; i++)
                {
                    tx.AddVertex(1u, null);
                }

                fallen8.EnqueueTransaction(tx).WaitUntilFinished();
                created.AddRange(tx.GetCreatedVertices());
            }

            return created;
        }

        private static Double Rate(Int32 count, Double seconds)
            => seconds <= 0 ? Double.PositiveInfinity : count / seconds;

        /// <summary>
        ///   Times AddOrUpdate then RemoveValue directly on the index, with every element under ONE
        ///   key (the claim-index shape) and then spread over many keys (the control). Direct on the
        ///   index rather than through a transaction so the number is the index's cost and nothing
        ///   else.
        /// </summary>
        [TestMethod]
        [TestCategory("Benchmark")]
        [Ignore("Opt-in benchmark: run explicitly to capture numbers.")]
        public void BucketIndexRemoval_OneKeyVersusManyKeys()
        {
            var count = EnvInt("IDXBENCH_ELEMENTS", 40_000);
            var spread = EnvInt("IDXBENCH_KEYS", 4_000);
            var fallen8 = new Fallen8(TestLoggerFactory.Create());
            var vertices = AddVertices(fallen8, count);
            Emit($"elements={count} controlKeys={spread}");

            foreach (var oneKey in new[] { true, false })
            {
                var label = oneKey ? "ONE key (claim-index shape)" : $"{spread} keys (control)";
                Assert.IsTrue(fallen8.IndexFactory.TryCreateIndex(
                    out var index, "bench-" + (oneKey ? "one" : "many"), "DictionaryIndex"));

                var sw = Stopwatch.StartNew();
                for (var i = 0; i < count; i++)
                {
                    index.AddOrUpdate(oneKey ? "k" : "k" + (i % spread), vertices[i]);
                }

                var addSeconds = sw.Elapsed.TotalSeconds;

                sw.Restart();
                for (var i = 0; i < count; i++)
                {
                    index.RemoveValue(vertices[i]);
                }

                var removeSeconds = sw.Elapsed.TotalSeconds;

                Emit($"{label}: add {addSeconds:F2}s ({Rate(count, addSeconds):F0}/s), " +
                     $"remove {removeSeconds:F2}s ({Rate(count, removeSeconds):F0}/s), " +
                     $"remove is {(addSeconds <= 0 ? 0 : removeSeconds / addSeconds):F1}x the add");
                Assert.AreEqual(0, index.CountOfKeys(), "every value was removed, so no key should survive");
            }
        }

        /// <summary>
        ///   The same shape through the real removal path, on a graph WITH EDGES: a transaction
        ///   removes the vertices in batches of 500, which is the batch size the integrations REST
        ///   seam uses, so the number is comparable to a withdrawal observed end to end.
        ///
        ///   <para>Edges are the point of this one. An integration indexes its claim on every
        ///   element it creates, vertices AND edges, so the bucket is the whole graph rather than
        ///   its vertices; and removing a vertex cascades to its incident edges, which purges each
        ///   of them from every index and detaches each from the OTHER endpoint's adjacency. The
        ///   default of two edges per vertex keeps the bucket a realistic multiple of the vertex
        ///   count rather than equal to it, which is what makes the removal cost visible.</para>
        /// </summary>
        [TestMethod]
        [TestCategory("Benchmark")]
        [Ignore("Opt-in benchmark: run explicitly to capture numbers.")]
        public void EngineRemoval_WithAClaimShapedIndexPresent()
        {
            var count = EnvInt("IDXBENCH_ELEMENTS", 40_000);
            var batchSize = EnvInt("IDXBENCH_BATCH", 500);
            var edgesPerVertex = EnvInt("IDXBENCH_EDGES", 2);
            var fallen8 = new Fallen8(TestLoggerFactory.Create());
            var vertices = AddVertices(fallen8, count);

            var random = new Random(20260901);
            var edgeTx = new CreateEdgesTransaction();
            for (var v = 0; v < vertices.Count; v++)
            {
                for (var e = 0; e < edgesPerVertex; e++)
                {
                    edgeTx.AddEdge(vertices[v].Id, "bench", vertices[random.Next(vertices.Count)].Id, 1u);
                }
            }

            fallen8.EnqueueTransaction(edgeTx).WaitUntilFinished();

            Assert.IsTrue(fallen8.IndexFactory.TryCreateIndex(out var index, "claims", "DictionaryIndex"));
            var indexed = 0;
            foreach (var v in vertices)
            {
                index.AddOrUpdate("one-identity", v);
                indexed++;
            }

            foreach (var e in fallen8.GetAllEdges())
            {
                index.AddOrUpdate("one-identity", e);
                indexed++;
            }

            Emit($"one bucket holds {indexed} entries ({count} vertices + {indexed - count} edges)");

            var ids = vertices.Select(v => v.Id).ToList();
            var sw = Stopwatch.StartNew();
            for (var start = 0; start < ids.Count; start += batchSize)
            {
                var take = Math.Min(batchSize, ids.Count - start);
                fallen8.EnqueueTransaction(new RemoveGraphElementsTransaction
                {
                    GraphElementIds = ids.GetRange(start, take),
                }).WaitUntilFinished();
            }

            var seconds = sw.Elapsed.TotalSeconds;
            Emit($"engine removal, {count} vertices (cascading to {indexed - count} edges) in batches " +
                 $"of {batchSize}: {seconds:F2}s ({Rate(count, seconds):F0} vertices/s, " +
                 $"{Rate(indexed, seconds):F0} elements/s), " +
                 $"{seconds / Math.Max(1, ids.Count / batchSize):F3}s per batch");
            Assert.AreEqual(0, index.CountOfKeys(), "the index should be empty after every element is gone");
        }
    }
}
