// MIT License
//
// InlineTransactionExecutionTest.cs
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
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.ChangeFeed;
using NoSQL.GraphDB.Core.Persistency;
using NoSQL.GraphDB.Core.Transaction;
using PathAlgorithms = NoSQL.GraphDB.Core.Algorithms.Path;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Tests for <see cref="TransactionExecutionMode.Inline" />: the engine can be constructed and
    ///   written to on a host that cannot start a thread at all (a single-threaded browser WebAssembly
    ///   runtime), by applying each transaction on the CALLING thread instead of handing it to a writer
    ///   thread.
    ///
    ///   <para>What is pinned here: construction starts no thread and allocates no queue; a write is
    ///   already terminal when <c>EnqueueTransaction</c> returns, so waiting on it cannot block; the
    ///   graph model, traversal, rollback (clean and faulted), <c>GetTransactionState</c>, the terminal
    ///   FIFO, enqueue ordering, the write-ahead log and the change feed all behave as they do threaded;
    ///   and concurrent callers are serialized, so the single-writer invariant survives even a
    ///   forced-inline host that does have threads. The complementary claim - that the THREADED path is
    ///   unchanged - is covered by the rest of the suite (which constructs engines the default way) plus
    ///   the <c>Automatic</c> resolution test below.</para>
    /// </summary>
    [TestClass]
    public class InlineTransactionExecutionTest
    {
        private ILoggerFactory _loggerFactory;
        private string _tempDir;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
            _tempDir = Path.Combine(Path.GetTempPath(), "f8_inline_" + Guid.NewGuid().ToString("N"));
        }

        [TestCleanup]
        public void TestCleanup()
        {
            try
            {
                if (_tempDir != null && Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }

        #region helpers

        private Fallen8 NewInlineEngine()
            => new Fallen8(_loggerFactory, transactionExecutionMode: TransactionExecutionMode.Inline);

        private string WalPath
        {
            get
            {
                Directory.CreateDirectory(_tempDir);
                return Path.Combine(_tempDir, "inline.f8s.wal");
            }
        }

        /// <summary>The engine declares no InternalsVisibleTo, so the writer-thread field is read by
        /// reflection - the same approach <see cref="TransactionRetentionTest" /> uses.</summary>
        private static object TxManagerField(Fallen8 fallen8, string field)
        {
            var txManager = typeof(Fallen8)
                .GetField("_txManager", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(fallen8);
            return txManager.GetType()
                .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(txManager);
        }

        private static void SetMaxRetained(Fallen8 fallen8, int value)
        {
            var txManager = typeof(Fallen8)
                .GetField("_txManager", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(fallen8);
            txManager.GetType()
                .GetProperty("MaxRetainedTerminalTransactions", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                .SetValue(txManager, value);
        }

        private static TransactionInformation CreateVertex(Fallen8 fallen8, string label,
            Dictionary<string, object> properties = null)
        {
            var tx = new CreateVerticesTransaction();
            tx.AddVertex(1u, label, properties);
            return fallen8.EnqueueTransaction(tx);
        }

        /// <summary>Creates a 0 -> 1 -> ... -> n-1 chain of vertices and edges, all inline.</summary>
        private static void CreateChain(Fallen8 fallen8, int length)
        {
            var vertices = new CreateVerticesTransaction();
            for (var i = 0; i < length; i++)
            {
                vertices.AddVertex(1u, "hop", new Dictionary<string, object> { { "index", i } });
            }
            fallen8.EnqueueTransaction(vertices).WaitUntilFinished();

            var created = vertices.GetCreatedVertices();
            var edges = new CreateEdgesTransaction();
            for (var i = 0; i < length - 1; i++)
            {
                edges.AddEdge(created[i].Id, "next", created[i + 1].Id, 1u, "link");
            }
            fallen8.EnqueueTransaction(edges).WaitUntilFinished();
        }

        #endregion

        #region no writer thread

        [TestMethod]
        public void InlineMode_ConstructsWithoutAWriterThreadOrAQueue()
        {
            using var fallen8 = NewInlineEngine();

            Assert.AreEqual(TransactionExecutionMode.Inline, fallen8.TransactionExecution,
                "An explicitly inline engine must report the inline mode.");
            Assert.IsNull(TxManagerField(fallen8, "_worker"),
                "Inline mode must start no writer thread - that Thread.Start is exactly what a single-threaded host cannot do.");
            Assert.IsNull(TxManagerField(fallen8, "_transactions"),
                "Inline mode has no consumer, so it must allocate no queue (and no wait handles) either.");
        }

        [TestMethod]
        public void AutomaticMode_OnAHostThatCanStartThreads_ResolvesToTheThreadedWriter()
        {
            // The server path, i.e. the default every other test in this suite constructs: Automatic
            // must resolve to the dedicated writer thread wherever one can be started, so the runtime
            // fallback cannot silently downgrade a server.
            using var automatic = new Fallen8(_loggerFactory);

            Assert.AreEqual(TransactionExecutionMode.Threaded, automatic.TransactionExecution,
                "On a host that can start a thread, Automatic must pick the threaded writer.");

            var worker = (Thread)TxManagerField(automatic, "_worker");
            Assert.IsNotNull(worker, "The threaded path must still own a writer thread.");
            Assert.AreEqual("Fallen8-Transaction-Writer", worker.Name);
            Assert.IsNotNull(TxManagerField(automatic, "_transactions"),
                "The threaded path must still own the blocking queue that gives it group commit.");
        }

        [TestMethod]
        public void ThreadedMode_RequestedExplicitly_BehavesLikeTheDefault()
        {
            using var threaded = new Fallen8(_loggerFactory, transactionExecutionMode: TransactionExecutionMode.Threaded);

            Assert.AreEqual(TransactionExecutionMode.Threaded, threaded.TransactionExecution);
            CreateVertex(threaded, "v").WaitUntilFinished();
            Assert.AreEqual(1, threaded.VertexCount);
        }

        [TestMethod]
        public void InlineMode_Dispose_TearsDownWithoutAThreadJoinAndStaysIdempotent()
        {
            var fallen8 = NewInlineEngine();
            CreateVertex(fallen8, "v");

            var txManager = typeof(Fallen8)
                .GetField("_txManager", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(fallen8);

            // There is no writer thread to join and no queue to complete, so teardown must simply
            // return - and stay idempotent, exactly as the threaded path's guard is.
            fallen8.Dispose();
            ((IDisposable)txManager).Dispose();

            Assert.AreEqual(0, fallen8.VertexCount, "A disposed engine is reset to the empty snapshot.");
        }

        #endregion

        #region the enqueued transaction is already complete

        [TestMethod]
        public void InlineMode_EnqueueTransaction_ReturnsAnAlreadyCompletedTransaction()
        {
            using var fallen8 = NewInlineEngine();

            var info = CreateVertex(fallen8, "already-done");

            // Observed BEFORE any wait: inline execution applied, flushed and completed the
            // transaction inside the EnqueueTransaction call.
            Assert.IsTrue(info.Completion.IsCompleted,
                "Inline mode must return a transaction whose completion task is already complete.");
            Assert.IsTrue(info.Completion.IsCompletedSuccessfully,
                "The completion task must complete successfully, not fault or cancel.");
            Assert.AreEqual(TransactionState.Finished, info.TransactionState,
                "The returned TransactionInformation must already carry the terminal state.");
            Assert.IsTrue(info.Durable, "With no write-ahead log there is nothing for the log to miss.");
            Assert.AreEqual(1, fallen8.VertexCount, "The write must be visible to a reader immediately.");
        }

        [TestMethod]
        public void InlineMode_WaitingOnATransaction_DoesNotBlock()
        {
            using var fallen8 = NewInlineEngine();

            var info = CreateVertex(fallen8, "no-wait");

            // A ZERO budget can only be met by a transaction that is already finished: this is the
            // assertion that there is no queue latency left to wait for.
            Assert.IsTrue(info.WaitUntilFinished(TimeSpan.Zero),
                "Waiting with a zero timeout must succeed, proving the wait has nothing left to wait for.");

            // The blocking and awaiting forms must both return immediately, and stay repeatable.
            info.WaitUntilFinished();
            Assert.IsTrue(info.WaitUntilFinished(TimeSpan.Zero));
            Assert.AreEqual(TransactionState.Finished, info.TransactionState);
        }

        [TestMethod]
        public async Task InlineMode_AwaitingCompletion_ObservesTheTerminalState()
        {
            using var fallen8 = NewInlineEngine();

            var info = CreateVertex(fallen8, "awaited");
            await info.Completion;

            Assert.AreEqual(TransactionState.Finished, info.TransactionState);
            Assert.AreEqual(1, fallen8.VertexCount);
        }

        [TestMethod]
        public void InlineMode_QueueDepth_IsAlwaysZero()
        {
            using var fallen8 = NewInlineEngine();

            for (var i = 0; i < 5; i++)
            {
                CreateVertex(fallen8, "v");
            }

            // The queue-depth gauge (feature observability) must stay readable and honest: inline mode
            // never has a transaction waiting for a writer.
            var depth = (int)typeof(Fallen8)
                .GetProperty("TransactionQueueDepthForMetrics", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(fallen8);
            Assert.AreEqual(0, depth);
        }

        #endregion

        #region graph model + traversal

        [TestMethod]
        public void InlineMode_CreatesVerticesAndEdges_AndReadsThemBack()
        {
            using var fallen8 = NewInlineEngine();

            var vertices = new CreateVerticesTransaction();
            vertices.AddVertex(1u, "person", new Dictionary<string, object> { { "name", "Alice" } });
            vertices.AddVertex(1u, "person", new Dictionary<string, object> { { "name", "Bob" } });
            var verticesInfo = fallen8.EnqueueTransaction(vertices);
            Assert.AreEqual(TransactionState.Finished, verticesInfo.TransactionState);

            var created = vertices.GetCreatedVertices();
            Assert.AreEqual(2, created.Count);

            var edges = new CreateEdgesTransaction();
            edges.AddEdge(created[0].Id, "knows", created[1].Id, 1u, "friendship");
            var edgesInfo = fallen8.EnqueueTransaction(edges);
            Assert.AreEqual(TransactionState.Finished, edgesInfo.TransactionState);

            // Read the graph back through the ordinary read surface.
            Assert.AreEqual(2, fallen8.VertexCount);
            Assert.AreEqual(1, fallen8.EdgeCount);
            Assert.AreEqual(2, fallen8.GetAllVertices().Count);
            Assert.AreEqual(1, fallen8.GetAllEdges().Count);

            Assert.IsTrue(fallen8.TryGetVertex(out var alice, created[0].Id));
            Assert.IsTrue(alice.TryGetProperty(out string name, "name"));
            Assert.AreEqual("Alice", name);

            var edge = edges.GetCreatedEdges().Single();
            Assert.IsTrue(fallen8.TryGetGraphElement(out var fetchedEdge, edge.Id));
            Assert.AreEqual("friendship", fetchedEdge.Label);

            // Adjacency was wired, not just the elements appended.
            Assert.AreEqual(1, alice.OutEdges["knows"].Count);
            Assert.IsTrue(fallen8.TryGetVertex(out var bob, created[1].Id));
            Assert.AreEqual(1, bob.InEdges["knows"].Count);
        }

        [TestMethod]
        public void InlineMode_Traversal_FindsThePathAcrossAnInlineWrittenGraph()
        {
            using var fallen8 = NewInlineEngine();
            CreateChain(fallen8, 4);

            var definition = new PathAlgorithms.ShortestPathDefinition
            {
                SourceVertexId = 0,
                DestinationVertexId = 3,
                MaxDepth = 5,
                MaxResults = 2
            };

            Assert.IsTrue(fallen8.TryCalculateShortestPath(out var paths, "BLS", definition),
                "A traversal over an inline-written graph must succeed.");
            Assert.AreEqual(1, paths.Count);
            Assert.AreEqual(3, paths[0].GetLength(), "0 -> 1 -> 2 -> 3 is three hops.");
        }

        [TestMethod]
        public void InlineMode_PreservesEnqueueOrder()
        {
            using var fallen8 = NewInlineEngine();

            const int count = 50;
            for (var i = 0; i < count; i++)
            {
                var info = CreateVertex(fallen8, "ordered", new Dictionary<string, object> { { "index", i } });
                Assert.AreEqual(TransactionState.Finished, info.TransactionState);
            }

            // Ids are handed out in commit order, so enqueue order N must own id N.
            for (var i = 0; i < count; i++)
            {
                Assert.IsTrue(fallen8.TryGetVertex(out var vertex, i));
                Assert.IsTrue(vertex.TryGetProperty(out int index, "index"));
                Assert.AreEqual(i, index, "Inline execution must apply transactions in enqueue order.");
            }
        }

        #endregion

        #region rollback, failure reason, transaction state

        [TestMethod]
        public void InlineMode_CleanRollback_ReportsTheFailureReasonAndChangesNothing()
        {
            using var fallen8 = NewInlineEngine();
            CreateVertex(fallen8, "lonely").WaitUntilFinished();

            // A referenced endpoint that does not exist: a clean false -> NotFound, no exception.
            var edges = new CreateEdgesTransaction();
            edges.AddEdge(0, "knows", 999, 1u, "friendship");
            var info = fallen8.EnqueueTransaction(edges);

            Assert.AreEqual(TransactionState.RolledBack, info.TransactionState);
            Assert.AreEqual(TransactionFailureReason.NotFound, info.FailureReason);
            Assert.IsNull(info.Error, "A clean rollback carries no exception.");
            Assert.AreEqual(0, fallen8.EdgeCount, "A rolled-back batch must leave nothing behind.");
            Assert.AreEqual(1, fallen8.VertexCount);
        }

        [TestMethod]
        public void InlineMode_AFaultingTransaction_IsContainedAndTheEngineKeepsWorking()
        {
            using var fallen8 = NewInlineEngine();

            var boom = new InvalidOperationException("inline boom");
            var faulting = new DelegateTransaction(ctx =>
            {
                ctx.CreateVertex(1u, "half-done");
                throw boom;
            }, "faulting");

            // The exception must NOT escape onto the calling thread: inline mode contains a faulting
            // transaction exactly as the writer thread does (B6).
            var info = fallen8.EnqueueTransaction(faulting);

            Assert.AreEqual(TransactionState.RolledBack, info.TransactionState);
            Assert.AreSame(boom, info.Error, "The fault must be recorded on the transaction, not thrown at the caller.");
            Assert.AreEqual(TransactionFailureReason.InternalError, info.FailureReason);
            Assert.AreEqual(0, fallen8.VertexCount, "The rollback must undo the vertex the body created.");

            // The engine survives the fault and keeps accepting writes.
            var next = CreateVertex(fallen8, "after-the-fault");
            Assert.AreEqual(TransactionState.Finished, next.TransactionState);
            Assert.AreEqual(1, fallen8.VertexCount);
        }

        [TestMethod]
        public void InlineMode_GetTransactionState_ResolvesTerminalAndUnknownIds()
        {
            using var fallen8 = NewInlineEngine();

            var tx = new CreateVerticesTransaction();
            tx.AddVertex(1u, "v");
            fallen8.EnqueueTransaction(tx);

            Assert.AreEqual(TransactionState.Finished, fallen8.GetTransactionState(tx.TransactionId),
                "A finished inline transaction must resolve by id.");
            Assert.AreEqual(TransactionState.NotExist, fallen8.GetTransactionState(Guid.NewGuid().ToString()));
            Assert.AreEqual(TransactionState.NotExist, fallen8.GetTransactionState("not-a-guid"));

            // A trim reclaims the terminal bookkeeping in inline mode too.
            fallen8.EnqueueTransaction(new TrimTransaction());
            Assert.AreEqual(TransactionState.NotExist, fallen8.GetTransactionState(tx.TransactionId));
        }

        [TestMethod]
        public void InlineMode_BoundsTerminalRetention_ThroughTheSameFifo()
        {
            using var fallen8 = NewInlineEngine();
            SetMaxRetained(fallen8, 2);

            var ids = new List<string>();
            for (var i = 0; i < 5; i++)
            {
                var tx = new CreateVerticesTransaction();
                tx.AddVertex(1u, "v");
                fallen8.EnqueueTransaction(tx);
                ids.Add(tx.TransactionId);
            }

            Assert.AreEqual(TransactionState.NotExist, fallen8.GetTransactionState(ids[0]),
                "The terminal FIFO must evict the oldest ids in inline mode as well (feature transaction-retention R1).");
            Assert.AreEqual(TransactionState.Finished, fallen8.GetTransactionState(ids[4]),
                "The most recent transaction must still resolve for a caller polling its state.");
            Assert.AreEqual(5, fallen8.VertexCount, "Eviction is bookkeeping only - the writes stay.");
        }

        #endregion

        #region single-writer invariant

        [TestMethod]
        public void InlineMode_ConcurrentCallers_AreSerialized()
        {
            // Inline mode exists for a host with ONE thread, but it must not corrupt the graph if a
            // forced-inline host turns out to have more: the inline gate makes every body run alone.
            using var fallen8 = NewInlineEngine();

            const int writers = 64;
            Parallel.For(0, writers, i =>
            {
                var info = CreateVertex(fallen8, "concurrent", new Dictionary<string, object> { { "writer", i } });
                Assert.AreEqual(TransactionState.Finished, info.TransactionState);
            });

            Assert.AreEqual(writers, fallen8.VertexCount,
                "Every concurrent inline write must be applied exactly once.");
            var writerIds = fallen8.GetAllVertices()
                .Select(v =>
                {
                    Assert.IsTrue(v.TryGetProperty(out int writer, "writer"));
                    return writer;
                })
                .OrderBy(_ => _)
                .ToList();
            CollectionAssert.AreEqual(Enumerable.Range(0, writers).ToList(), writerIds,
                "No write may be lost or duplicated by concurrent inline callers.");
        }

        [TestMethod]
        public void InlineMode_ReentrantEnqueueFromInsideABody_RunsAfterTheOuterTransaction()
        {
            using var fallen8 = NewInlineEngine();

            TransactionInformation nested = null;
            var completedInsideTheBody = true;
            var outer = new DelegateTransaction(ctx =>
            {
                ctx.CreateVertex(1u, "outer");

                // A body CAN reach the engine through a captured reference. The reentrant enqueue must
                // be deferred, not nested inside this body's commit.
                var tx = new CreateVerticesTransaction();
                tx.AddVertex(1u, "nested");
                nested = fallen8.EnqueueTransaction(tx);
                completedInsideTheBody = nested.Completion.IsCompleted;
            }, "outer");

            var outerInfo = fallen8.EnqueueTransaction(outer);

            Assert.AreEqual(TransactionState.Finished, outerInfo.TransactionState);
            Assert.IsFalse(completedInsideTheBody,
                "A reentrant transaction must not be executed nested inside the running body.");
            Assert.IsTrue(nested.Completion.IsCompleted,
                "The deferred transaction must be drained before the outer EnqueueTransaction call returns.");
            Assert.AreEqual(TransactionState.Finished, nested.TransactionState);

            // Enqueue order: the outer body's vertex was created first, the reentrant one after it.
            Assert.AreEqual(2, fallen8.VertexCount);
            Assert.IsTrue(fallen8.TryGetVertex(out var first, 0));
            Assert.AreEqual("outer", first.Label);
            Assert.IsTrue(fallen8.TryGetVertex(out var second, 1));
            Assert.AreEqual("nested", second.Label);
        }

        #endregion

        #region durability + change feed

        [TestMethod]
        public void InlineMode_WithAWriteAheadLog_IsDurableAndReplays()
        {
            var walPath = WalPath;

            using (var fallen8 = new Fallen8(_loggerFactory, new WriteAheadLogOptions(walPath),
                transactionExecutionMode: TransactionExecutionMode.Inline))
            {
                Assert.AreEqual(TransactionExecutionMode.Inline, fallen8.TransactionExecution);

                for (var i = 0; i < 3; i++)
                {
                    var info = CreateVertex(fallen8, "durable", new Dictionary<string, object> { { "index", i } });
                    Assert.AreEqual(TransactionState.Finished, info.TransactionState);
                    Assert.IsTrue(info.Durable,
                        "An inline commit group of one fsyncs before it completes, so the write is durable when the call returns.");
                }

                Assert.AreEqual(3, fallen8.VertexCount);
                // Dropped WITHOUT a Save (a simulated crash): only the log holds these writes.
            }

            using (var recovered = new Fallen8(_loggerFactory, new WriteAheadLogOptions(walPath),
                transactionExecutionMode: TransactionExecutionMode.Inline))
            {
                Assert.AreEqual(3, recovered.VertexCount,
                    "Every inline write whose Enqueue call returned must be recovered from the log - durable-before-ack holds inline.");
            }
        }

        [TestMethod]
        public void InlineMode_ChangeFeed_PublishesCommittedChangesInCommitOrder()
        {
            using var fallen8 = new Fallen8(_loggerFactory, writeAheadLogOptions: null,
                changeFeedOptions: new ChangeFeedOptions(),
                transactionExecutionMode: TransactionExecutionMode.Inline);

            Assert.IsTrue(fallen8.ChangeFeed.TrySubscribe(ChangeFeedFilter.MatchAll, null, null, out var subscription));
            using (subscription)
            {
                var vertices = new CreateVerticesTransaction();
                vertices.AddVertex(1u, "person");
                vertices.AddVertex(1u, "person");
                fallen8.EnqueueTransaction(vertices);
                var created = vertices.GetCreatedVertices();

                var edges = new CreateEdgesTransaction();
                edges.AddEdge(created[0].Id, "knows", created[1].Id, 1u, "friendship");
                fallen8.EnqueueTransaction(edges);

                // Publication happens on the calling thread; DELIVERY is the dispatcher's asynchronous
                // job either way, so the read is bounded rather than immediate.
                var events = new List<ChangeEvent>();
                for (var i = 0; i < 3; i++)
                {
                    var read = subscription.Reader.ReadAsync().AsTask();
                    Assert.IsTrue(read.Wait(5000), "expected an event within 5000 ms");
                    events.Add(read.Result);
                }

                Assert.AreEqual(ChangeEventKind.VertexCreated, events[0].Kind);
                Assert.AreEqual(created[0].Id, events[0].Id);
                Assert.AreEqual(ChangeEventKind.VertexCreated, events[1].Kind);
                Assert.AreEqual(created[1].Id, events[1].Id);
                Assert.AreEqual(ChangeEventKind.EdgeCreated, events[2].Kind);
                Assert.AreEqual(events[0].Seq + 1, events[1].Seq);
                Assert.AreEqual(events[1].Seq + 1, events[2].Seq);
            }
        }

        [TestMethod]
        public void InlineMode_SaveAndLoad_RoundTripTheGraph()
        {
            Directory.CreateDirectory(_tempDir);
            var savePath = Path.Combine(_tempDir, "inline.f8s");

            using (var fallen8 = NewInlineEngine())
            {
                CreateChain(fallen8, 3);
                var save = fallen8.EnqueueTransaction(new SaveTransaction { Path = savePath });
                Assert.AreEqual(TransactionState.Finished, save.TransactionState,
                    "A Save is a commit-group boundary; inline mode already commits one transaction per group.");
            }

            using (var loaded = NewInlineEngine())
            {
                var load = loaded.EnqueueTransaction(new LoadTransaction { Path = savePath, StartServices = false });
                Assert.AreEqual(TransactionState.Finished, load.TransactionState);
                Assert.AreEqual(3, loaded.VertexCount);
                Assert.AreEqual(2, loaded.EdgeCount);
            }
        }

        #endregion
    }
}
