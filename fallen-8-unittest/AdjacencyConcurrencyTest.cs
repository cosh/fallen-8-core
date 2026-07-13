// MIT License
//
// AdjacencyConcurrencyTest.cs
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
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Guardrail tests for the lock-free-reader / single-writer <b>adjacency</b> contract
    /// (see features/adjacency-flattening). These encode the invariant that must never break:
    /// while the single TransactionManager thread appends/removes edges (which republish a
    /// vertex's out/in edge groups copy-on-write), concurrent readers that touch the adjacency
    /// - <c>OutEdges</c>/<c>InEdges</c>, <c>TryGetOut/InEdge</c>, <c>GetOut/InDegree</c>,
    /// <c>GetAllNeighbors</c>, <c>GetOut/IncomingEdgeIds</c> and a full path traversal - must
    /// NEVER observe a torn or half-published group (a null edge slot inside a returned list, an
    /// edge with a null endpoint), throw an NRE / IndexOutOfRange, nor see a captured view mutate
    /// under them.
    ///
    /// The suite is written against only the method/shape contract that is stable across the
    /// storage change (it uses <c>var</c> for the edge-list type), so it compiles and passes
    /// unchanged both before and after the <c>ImmutableDictionary/ImmutableList</c> -&gt;
    /// copy-on-write <c>Dictionary&lt;string, EdgeModel[]&gt;</c> swap - which is exactly what a
    /// storage-representation guardrail must do.
    ///
    /// Readers run on dedicated background threads (not the thread pool) so they cannot starve
    /// the pool threads the TransactionManager uses to execute transactions.
    /// </summary>
    [TestClass]
    public class AdjacencyConcurrencyTest
    {
        private const string VertexLabel = "person";
        private const string EdgeLabel = "knows";
        private const string EdgePropertyId = "friend";

        private static int ReaderCount => Math.Max(4, Environment.ProcessorCount);

        private ILoggerFactory _loggerFactory;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
        }

        /// <summary>
        /// Many readers hammer the adjacency of a small hot-set of vertices while the single writer
        /// adds and removes edges on that same hot-set one small transaction at a time (maximising
        /// the number of publish boundaries, i.e. race windows). Every edge a reader resolves must
        /// be fully published: non-null, with non-null endpoints, filed under the correct vertex
        /// and direction; every degree/neighbour/id query must agree with a captured view and never
        /// throw.
        /// </summary>
        [TestMethod]
        public void ConcurrentReaders_DuringSingleWriterEdgeChurn_NeverSeeTornAdjacencyOrThrow()
        {
            var fallen8 = new Fallen8(_loggerFactory);

            const int vertexCount = 400;
            // The hot-set the writer keeps churning; kept small so readers and the writer collide
            // on the very same vertices' adjacency (the interesting race).
            const int hotSet = 40;
            const int edgeChurnRounds = 6000;

            var vertexTx = new CreateVerticesTransaction();
            for (var i = 0; i < vertexCount; i++)
            {
                vertexTx.AddVertex(1u, VertexLabel, new Dictionary<string, object> { { "seq", i } });
            }
            fallen8.EnqueueTransaction(vertexTx).WaitUntilFinished();

            // An ISOLATED, frozen line-graph the writer never touches, for the path-traversal readers.
            // Running a full BLS over the CHURNING hot-set would be a red herring: BLS builds per-hop
            // frontier dictionaries across several steps and is not atomic against a graph mutating
            // mid-traversal (it can miss a frontier key), which is a property of the algorithm, not of
            // the adjacency storage (it reproduces on the pre-change ImmutableDictionary too). Traversing
            // a subgraph whose adjacency is frozen exercises the traversal's adjacency READS concurrently
            // with the writer's churn elsewhere, deterministically.
            const int stableCount = 6;
            var stableTx = new CreateVerticesTransaction();
            for (var i = 0; i < stableCount; i++)
            {
                stableTx.AddVertex(1u, "stable");
            }
            fallen8.EnqueueTransaction(stableTx).WaitUntilFinished();
            var stableVertices = stableTx.GetCreatedVertices();
            int stableStart = stableVertices[0].Id;
            int stableEnd = stableVertices[stableCount - 1].Id;
            var stableEdgeTx = new CreateEdgesTransaction();
            for (var i = 0; i < stableCount - 1; i++)
            {
                stableEdgeTx.AddEdge(stableVertices[i].Id, "line", stableVertices[i + 1].Id, 1u, "line");
            }
            fallen8.EnqueueTransaction(stableEdgeTx).WaitUntilFinished();

            var errors = new ConcurrentQueue<Exception>();
            int writerDone = 0;

            var readers = StartReaders(() =>
            {
                var rng = new Random(Thread.CurrentThread.ManagedThreadId * 7919 + Environment.TickCount);
                while (Volatile.Read(ref writerDone) == 0)
                {
                    for (var k = 0; k < 512; k++)
                    {
                        int id = rng.Next(0, vertexCount);

                        VertexModel v;
                        if (!fallen8.TryGetVertex(out v, id))
                        {
                            continue;
                        }

                        // --- Capture the out-edge view once and assert it is a fully-published,
                        //     internally-consistent snapshot. An out-edge's source is THIS vertex. ---
                        var outView = v.OutEdges;
                        if (outView != null)
                        {
                            foreach (var group in outView)
                            {
                                Assert.IsNotNull(group.Value, "Torn read: null out-edge group for key '" + group.Key + "'.");
                                foreach (var edge in group.Value)
                                {
                                    Assert.IsNotNull(edge, "Torn read: null edge slot inside a published out-edge group.");
                                    Assert.IsNotNull(edge.SourceVertex, "Torn read: out-edge with a null source vertex.");
                                    Assert.IsNotNull(edge.TargetVertex, "Torn read: out-edge with a null target vertex.");
                                    Assert.AreSame(v, edge.SourceVertex, "An out-edge must originate from the vertex that lists it.");
                                    Assert.AreEqual(EdgeLabel, edge.Label, "Torn read: unexpected edge label.");
                                }
                            }

                            // A captured view must not change size while we hold it, no matter how
                            // many groups the writer republishes afterwards.
                            int firstPass = CountEdges(outView);
                            int secondPass = CountEdges(outView);
                            Assert.AreEqual(firstPass, secondPass,
                                "A captured out-edge view must be stable for its whole lifetime (copy-on-write snapshot).");
                        }

                        // --- Same for the in-edge view; an in-edge's target is THIS vertex. ---
                        var inView = v.InEdges;
                        if (inView != null)
                        {
                            foreach (var group in inView)
                            {
                                Assert.IsNotNull(group.Value, "Torn read: null in-edge group for key '" + group.Key + "'.");
                                foreach (var edge in group.Value)
                                {
                                    Assert.IsNotNull(edge, "Torn read: null edge slot inside a published in-edge group.");
                                    Assert.IsNotNull(edge.SourceVertex, "Torn read: in-edge with a null source vertex.");
                                    Assert.IsNotNull(edge.TargetVertex, "Torn read: in-edge with a null target vertex.");
                                    Assert.AreSame(v, edge.TargetVertex, "An in-edge must point at the vertex that lists it.");
                                }
                            }
                        }

                        // --- Degree accessors must never throw under churn. Each takes its OWN
                        //     snapshot, so they are NOT compared against a separately-captured view
                        //     (that would be a two-snapshot race, not a torn-read check); the
                        //     single-capture stability check above is what pins snapshot integrity.
                        //     A degree must, however, be a sane non-negative count. ---
                        uint outDegree = v.GetOutDegree();
                        uint inDegree = v.GetInDegree();
                        Assert.IsTrue(outDegree <= (uint)vertexCount * 4u, "GetOutDegree returned an implausible value (torn read?).");
                        Assert.IsTrue(inDegree <= (uint)vertexCount * 4u, "GetInDegree returned an implausible value (torn read?).");

                        foreach (var neighbour in v.GetAllNeighbors())
                        {
                            Assert.IsNotNull(neighbour, "GetAllNeighbors must never yield a null neighbour.");
                        }

                        var outIds = v.GetOutgoingEdgeIds();
                        var inIds = v.GetIncomingEdgeIds();

                        // Resolve each advertised group id; a resolved group must contain no null edge.
                        foreach (var edgePropertyId in outIds)
                        {
                            if (v.TryGetOutEdge(out var edges, edgePropertyId))
                            {
                                for (var e = 0; e < edges.Count; e++)
                                {
                                    Assert.IsNotNull(edges[e], "TryGetOutEdge returned a group with a null edge slot.");
                                }
                            }
                        }
                        foreach (var edgePropertyId in inIds)
                        {
                            if (v.TryGetInEdge(out var edges, edgePropertyId))
                            {
                                for (var e = 0; e < edges.Count; e++)
                                {
                                    Assert.IsNotNull(edges[e], "TryGetInEdge returned a group with a null edge slot.");
                                }
                            }
                        }
                    }

                    // A path traversal over the isolated stable subgraph must resolve cleanly while
                    // the writer churns the hot-set's adjacency - the traversal reads adjacency
                    // snapshots throughout and must never throw or see a torn group.
                    List<Path> paths;
                    var definition = new ShortestPathDefinition
                    {
                        SourceVertexId = stableStart,
                        DestinationVertexId = stableEnd,
                        MaxDepth = stableCount + 1,
                        MaxResults = 1
                    };
                    bool found = fallen8.TryCalculateShortestPath(out paths, "BLS", definition);
                    Assert.IsTrue(found && paths.Count >= 1,
                        "A path traversal over the frozen stable subgraph must resolve under concurrent edge churn.");
                }
            }, errors);

            try
            {
                // Single writer: churn edges over the hot-set, one small transaction at a time. Keep
                // a bounded list of live edge ids so we can also remove them (exercising the
                // copy-on-write detach path concurrently with the reads).
                var live = new Queue<int>();
                var rng = new Random(1234);
                for (var round = 0; round < edgeChurnRounds; round++)
                {
                    int source = rng.Next(0, hotSet);
                    int target = rng.Next(0, hotSet);
                    var addTx = new CreateEdgesTransaction();
                    addTx.AddEdge(source, EdgePropertyId, target, 1u, EdgeLabel);
                    fallen8.EnqueueTransaction(addTx).WaitUntilFinished();

                    var created = addTx.GetCreatedEdges();
                    if (created != null && created.Count > 0)
                    {
                        live.Enqueue(created[0].Id);
                    }

                    // Once the live set grows, remove the oldest edge so add and remove interleave.
                    if (live.Count > 64)
                    {
                        int toRemove = live.Dequeue();
                        fallen8.EnqueueTransaction(new RemoveGraphElementTransaction { GraphElementId = toRemove })
                            .WaitUntilFinished();
                    }
                }
            }
            finally
            {
                Volatile.Write(ref writerDone, 1);
            }

            JoinReaders(readers);
            AssertNoErrors(errors);

            // Post-hoc sanity: the surviving adjacency is coherent - every out-edge is mirrored as an
            // in-edge of its target and vice versa, with no null slots.
            foreach (var vertex in fallen8.GetAllVertices())
            {
                var outView = vertex.OutEdges;
                if (outView == null)
                {
                    continue;
                }
                foreach (var group in outView)
                {
                    foreach (var edge in group.Value)
                    {
                        Assert.IsNotNull(edge, "A surviving out-edge slot must not be null.");
                        Assert.AreSame(vertex, edge.SourceVertex, "A surviving out-edge must originate from its lister.");
                        Assert.IsTrue(edge.TargetVertex.TryGetInEdge(out var mirror, group.Key),
                            "Every out-edge must be mirrored in its target's in-edges under the same key.");
                        Assert.IsTrue(mirror.Any(e => ReferenceEquals(e, edge)),
                            "The mirrored in-edge group must contain the edge.");
                    }
                }
            }
        }

        #region helpers

        private static int CountEdges<TList>(IEnumerable<KeyValuePair<string, TList>> view)
            where TList : IEnumerable<EdgeModel>
        {
            int count = 0;
            foreach (var group in view)
            {
                foreach (var _ in group.Value)
                {
                    count++;
                }
            }
            return count;
        }

        private static Thread[] StartReaders(Action readerBody, ConcurrentQueue<Exception> errors)
        {
            var threads = new Thread[ReaderCount];
            for (var i = 0; i < threads.Length; i++)
            {
                threads[i] = new Thread(() =>
                {
                    try
                    {
                        readerBody();
                    }
                    catch (Exception ex)
                    {
                        errors.Enqueue(ex);
                    }
                })
                {
                    IsBackground = true,
                    Name = "adjacency-reader-" + i
                };
                threads[i].Start();
            }
            return threads;
        }

        private static void JoinReaders(Thread[] readers)
        {
            foreach (var reader in readers)
            {
                Assert.IsTrue(reader.Join(TimeSpan.FromMinutes(2)), "A reader thread did not terminate in time (possible deadlock).");
            }
        }

        private static void AssertNoErrors(ConcurrentQueue<Exception> errors)
        {
            if (!errors.IsEmpty)
            {
                var distinct = errors.Select(e => e.GetType().Name + ": " + e.Message).Distinct().Take(10);
                Assert.Fail("Concurrent adjacency readers observed " + errors.Count + " error(s):\n" + string.Join("\n", distinct));
            }
        }

        #endregion
    }
}
