// MIT License
//
// ChangeFeedEngineTest.cs
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
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Engine-side tests for the change feed (feature change-feed, Phases 0+1): descriptor
    /// capture on the write path (post-commit only, rolled-back = zero events, the transaction
    /// mapping incl. cascades and resync reasons, the completeness contract for future
    /// transaction types), and the dispatcher semantics (ordering, filters, catch-up, ring
    /// wraparound, backpressure, lifecycle) - all without HTTP.
    /// </summary>
    [TestClass]
    public class ChangeFeedEngineTest
    {
        private ILoggerFactory _loggerFactory;
        private Fallen8 _fallen8;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
            _fallen8 = new Fallen8(_loggerFactory, new ChangeFeedOptions());
        }

        [TestCleanup]
        public void TestCleanup()
        {
            _fallen8.Dispose();
        }

        #region helpers

        private ChangeFeedSubscription Subscribe(ChangeFeedFilter filter = null, Guid? sinceEpoch = null, long? sinceSeq = null)
        {
            Assert.IsTrue(_fallen8.ChangeFeed.TrySubscribe(filter ?? ChangeFeedFilter.MatchAll, sinceEpoch, sinceSeq, out var subscription),
                "subscribe should succeed");
            return subscription;
        }

        /// <summary>
        ///   Waits until the dispatcher has processed sequence <paramref name="seq"/>. Dispatch is
        ///   asynchronous, so a subscription without <c>since</c> starts at the DISPATCHER's head;
        ///   tests that subscribe after mutating first align the head to the commits.
        /// </summary>
        private static void WaitForSeq(Fallen8 engine, long seq)
        {
            Assert.IsTrue(SpinWait.SpinUntil(() => engine.ChangeFeed.LastSeq >= seq, 5000),
                $"the dispatcher should reach seq {seq} (is at {engine.ChangeFeed.LastSeq})");
        }

        /// <summary>Reads the next event, failing the test after a timeout.</summary>
        private static ChangeEvent Read(ChangeFeedSubscription subscription, int timeoutMs = 5000)
        {
            var read = subscription.Reader.ReadAsync().AsTask();
            Assert.IsTrue(read.Wait(timeoutMs), "expected an event within " + timeoutMs + " ms");
            return read.Result;
        }

        /// <summary>Reads exactly <paramref name="count"/> events.</summary>
        private static List<ChangeEvent> ReadMany(ChangeFeedSubscription subscription, int count, int timeoutMs = 5000)
        {
            var events = new List<ChangeEvent>(count);
            for (var i = 0; i < count; i++)
            {
                events.Add(Read(subscription, timeoutMs));
            }
            return events;
        }

        /// <summary>Asserts no further event arrives within a short grace period.</summary>
        private static void AssertQuiet(ChangeFeedSubscription subscription, int graceMs = 200)
        {
            var read = subscription.Reader.WaitToReadAsync().AsTask();
            Assert.IsFalse(read.Wait(graceMs) && read.Result, "expected no further events");
        }

        private (int a, int b) TwoVertices(string labelA = "person", string labelB = "person")
        {
            var tx = new CreateVerticesTransaction();
            tx.AddVertex(1u, labelA);
            tx.AddVertex(1u, labelB);
            _fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            var v = tx.GetCreatedVertices();
            return (v[0].Id, v[1].Id);
        }

        private int Edge(int source, int target, string edgePropertyId = "knows", string label = "knows")
        {
            var tx = new CreateEdgesTransaction();
            tx.AddEdge(source, edgePropertyId, target, 1u, label);
            _fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.GetCreatedEdges()[0].Id;
        }

        #endregion

        #region post-commit only / zero events on rollback

        [TestMethod]
        public void CommittedWrite_ProducesItsEvents_InCommitOrder()
        {
            using var subscription = Subscribe();

            var (a, b) = TwoVertices();
            var edgeId = Edge(a, b);

            var events = ReadMany(subscription, 3);

            Assert.AreEqual(ChangeEventKind.VertexCreated, events[0].Kind);
            Assert.AreEqual(a, events[0].Id);
            Assert.AreEqual("person", events[0].Label);
            Assert.AreEqual(ChangeElementType.Vertex, events[0].Element);

            Assert.AreEqual(ChangeEventKind.VertexCreated, events[1].Kind);
            Assert.AreEqual(b, events[1].Id);

            Assert.AreEqual(ChangeEventKind.EdgeCreated, events[2].Kind);
            Assert.AreEqual(edgeId, events[2].Id);
            Assert.AreEqual("knows", events[2].Label);
            Assert.AreEqual(a, events[2].SourceId);
            Assert.AreEqual(b, events[2].TargetId);

            // Ascending, gap-free sequence; UTC timestamps; batch events share their commit ts.
            Assert.AreEqual(events[0].Seq + 1, events[1].Seq);
            Assert.AreEqual(events[1].Seq + 1, events[2].Seq);
            Assert.AreEqual(events[0].Ts, events[1].Ts, "a batch's events share one commit timestamp");
            Assert.AreEqual(DateTimeKind.Utc, events[0].Ts.Kind);
        }

        [TestMethod]
        public void RolledBackTransaction_EmitsZeroEvents()
        {
            using var subscription = Subscribe();

            // Clean rollback: an edge to a missing vertex (NotFound).
            var cleanRollback = new CreateEdgeTransaction
            {
                Definition = new NoSQL.GraphDB.Core.Model.EdgeDefinition
                {
                    SourceVertexId = 4242,
                    TargetVertexId = 4243,
                    EdgePropertyId = "knows",
                    CreationDate = 1u
                }
            };
            var info = _fallen8.EnqueueTransaction(cleanRollback);
            info.WaitUntilFinished();
            Assert.AreEqual(TransactionState.RolledBack, info.TransactionState);

            // Thrown-then-rolled-back: a delegate body that faults after doing nothing.
            var thrownRollback = new DelegateTransaction(_ => throw new InvalidOperationException("boom"));
            var thrownInfo = _fallen8.EnqueueTransaction(thrownRollback);
            thrownInfo.WaitUntilFinished();
            Assert.AreEqual(TransactionState.RolledBack, thrownInfo.TransactionState);

            AssertQuiet(subscription);

            // The feed still works afterwards: a committed write's event arrives.
            TwoVertices();
            Assert.AreEqual(ChangeEventKind.VertexCreated, Read(subscription).Kind);
        }

        [TestMethod]
        public void FeedOffEngine_CarriesNoFeed_AndWritesWorkUnchanged()
        {
            using var plain = new Fallen8(_loggerFactory);
            Assert.IsNull(plain.ChangeFeed);

            var tx = new CreateVertexTransaction
            {
                Definition = new NoSQL.GraphDB.Core.Model.VertexDefinition { CreationDate = 1u, Label = "person" }
            };
            var info = plain.EnqueueTransaction(tx);
            info.WaitUntilFinished();
            Assert.AreEqual(TransactionState.Finished, info.TransactionState);
        }

        #endregion

        #region event mapping

        [TestMethod]
        public void PropertySetAndRemove_MapToPropertyEvents_WithKeysAndElementType()
        {
            var (a, _) = TwoVertices();
            WaitForSeq(_fallen8, 2);
            using var subscription = Subscribe();

            _fallen8.EnqueueTransaction(new AddPropertyTransaction
            {
                Definition = new NoSQL.GraphDB.Core.Model.PropertyAddDefinition
                {
                    GraphElementId = a,
                    PropertyId = "name",
                    Property = "Alice"
                }
            }).WaitUntilFinished();

            _fallen8.EnqueueTransaction(new RemovePropertyTransaction { GraphElementId = a, PropertyId = "name" })
                .WaitUntilFinished();

            var set = Read(subscription);
            Assert.AreEqual(ChangeEventKind.PropertySet, set.Kind);
            Assert.AreEqual(a, set.Id);
            Assert.AreEqual("name", set.Key);
            Assert.AreEqual(ChangeElementType.Vertex, set.Element);
            Assert.AreEqual("person", set.Label);

            var removed = Read(subscription);
            Assert.AreEqual(ChangeEventKind.PropertyRemoved, removed.Kind);
            Assert.AreEqual("name", removed.Key);
        }

        [TestMethod]
        public void NoOpMutations_EmitNothing()
        {
            var (a, _) = TwoVertices();
            WaitForSeq(_fallen8, 2);
            using var subscription = Subscribe();

            // Removing a property the element does not carry, and removing a missing element.
            _fallen8.EnqueueTransaction(new RemovePropertyTransaction { GraphElementId = a, PropertyId = "no-such-key" })
                .WaitUntilFinished();
            _fallen8.EnqueueTransaction(new RemoveGraphElementTransaction { GraphElementId = 4242 })
                .WaitUntilFinished();

            AssertQuiet(subscription);
        }

        [TestMethod]
        public void VertexRemoval_EmitsCascadedEdgeRemovals_Deduplicated()
        {
            var (a, b) = TwoVertices();
            var e1 = Edge(a, b, "knows", "knows");
            var e2 = Edge(b, a, "trusts", "trusts");

            WaitForSeq(_fallen8, 4);
            using var subscription = Subscribe();

            _fallen8.EnqueueTransaction(new RemoveGraphElementTransaction { GraphElementId = a })
                .WaitUntilFinished();

            var events = ReadMany(subscription, 3);
            Assert.AreEqual(ChangeEventKind.VertexRemoved, events[0].Kind);
            Assert.AreEqual(a, events[0].Id);

            var removedEdgeIds = events.Skip(1).Select(e => e.Id).ToList();
            CollectionAssert.AreEquivalent(new List<int> { e1, e2 }, removedEdgeIds,
                "both cascaded edges are reported exactly once");
            Assert.IsTrue(events.Skip(1).All(e => e.Kind == ChangeEventKind.EdgeRemoved));

            AssertQuiet(subscription);
        }

        [TestMethod]
        public void EdgeRemoval_ThenVertexRemoval_NeverDuplicatesTheEdgeEvent()
        {
            var (a, b) = TwoVertices();
            var e1 = Edge(a, b);

            WaitForSeq(_fallen8, 3);
            using var subscription = Subscribe();

            var batch = new RemoveGraphElementsTransaction
            {
                GraphElementIds = new List<int> { e1, a }
            };
            _fallen8.EnqueueTransaction(batch).WaitUntilFinished();

            var events = ReadMany(subscription, 2);
            Assert.AreEqual(ChangeEventKind.EdgeRemoved, events[0].Kind);
            Assert.AreEqual(e1, events[0].Id);
            Assert.AreEqual(ChangeEventKind.VertexRemoved, events[1].Kind);
            Assert.AreEqual(a, events[1].Id);

            AssertQuiet(subscription);
        }

        [TestMethod]
        public void SelfLoopCascade_IsReportedOnce()
        {
            var (a, _) = TwoVertices();
            var loop = Edge(a, a, "self", "self");

            WaitForSeq(_fallen8, 3);
            using var subscription = Subscribe();

            _fallen8.EnqueueTransaction(new RemoveGraphElementTransaction { GraphElementId = a })
                .WaitUntilFinished();

            var events = ReadMany(subscription, 2);
            Assert.AreEqual(ChangeEventKind.VertexRemoved, events[0].Kind);
            Assert.AreEqual(ChangeEventKind.EdgeRemoved, events[1].Kind);
            Assert.AreEqual(loop, events[1].Id);
            AssertQuiet(subscription);
        }

        [TestMethod]
        public void CoarseOperations_MapToResyncReasons()
        {
            TwoVertices();
            WaitForSeq(_fallen8, 2);
            using var subscription = Subscribe();

            _fallen8.EnqueueTransaction(new DelegateTransaction(ctx => { })).WaitUntilFinished();
            var delegateResync = Read(subscription);
            Assert.AreEqual(ChangeEventKind.Resync, delegateResync.Kind);
            Assert.AreEqual("delegateWrite", delegateResync.ResyncReason);

            _fallen8.EnqueueTransaction(new TrimTransaction()).WaitUntilFinished();
            Assert.AreEqual("trim", Read(subscription).ResyncReason);

            _fallen8.EnqueueTransaction(new TabulaRasaTransaction()).WaitUntilFinished();
            Assert.AreEqual("tabulaRasa", Read(subscription).ResyncReason);
        }

        [TestMethod]
        public void SaveEmitsNothing_LoadEmitsResync()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "f8_cf_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                TwoVertices();
                WaitForSeq(_fallen8, 2);
                using var subscription = Subscribe();

                var save = new SaveTransaction { Path = Path.Combine(tempDir, "cf.f8s"), SavePartitions = 1 };
                _fallen8.EnqueueTransaction(save).WaitUntilFinished();
                AssertQuiet(subscription);

                var load = new LoadTransaction { Path = save.ActualPath };
                var info = _fallen8.EnqueueTransaction(load);
                info.WaitUntilFinished();
                Assert.AreEqual(TransactionState.Finished, info.TransactionState);

                var resync = Read(subscription);
                Assert.AreEqual(ChangeEventKind.Resync, resync.Kind);
                Assert.AreEqual("load", resync.ResyncReason);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
            }
        }

        /// <summary>
        /// The semantic-drift guard (spec §5): every concrete ATransaction in the engine assembly
        /// must either override DescribeChanges (mapped) or be explicitly exempted here. A new
        /// mutating transaction added later fails this test until it is classified.
        /// </summary>
        [TestMethod]
        public void EveryTransactionType_IsMappedOrExplicitlyExempted()
        {
            // Exemptions: transactions that mutate NO graph element the feed reports.
            // - SaveTransaction: no graph mutation.
            // - CreateSubGraphTransaction / RemoveSubGraphTransaction: subgraphs are derived state
            //   materialized in their OWN standalone graph instance; the main graph is untouched.
            // - Register/RemoveStoredQueryTransaction: library state, not graph state.
            var exempt = new HashSet<string>
            {
                nameof(SaveTransaction),
                nameof(CreateSubGraphTransaction),
                nameof(RemoveSubGraphTransaction),
                nameof(RegisterStoredQueryTransaction),
                nameof(RemoveStoredQueryTransaction)
            };

            var transactionTypes = typeof(ATransaction).Assembly.GetTypes()
                .Where(t => typeof(ATransaction).IsAssignableFrom(t) && !t.IsAbstract)
                .ToList();
            Assert.IsTrue(transactionTypes.Count >= 15, "the enumeration must actually find the transaction types");

            var unclassified = new List<string>();
            foreach (var type in transactionTypes)
            {
                var describeChanges = type.GetMethod("DescribeChanges",
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null,
                    types: new[] { typeof(Fallen8), typeof(ChangeDescriptor.Builder) },
                    modifiers: null);
                var overridesMapping = describeChanges != null && describeChanges.DeclaringType != typeof(ATransaction);

                if (!overridesMapping && !exempt.Contains(type.Name))
                {
                    unclassified.Add(type.Name);
                }
            }

            Assert.AreEqual(0, unclassified.Count,
                "Every ATransaction must map its changes for the feed or be explicitly exempted: " +
                string.Join(", ", unclassified));
        }

        #endregion

        #region filters

        [TestMethod]
        public void Filters_ApplyAndAcrossDimensions_OrWithinOne()
        {
            using var all = Subscribe();
            using var onlyPersonVertices = Subscribe(ChangeFeedFilter.Create(
                kinds: new[] { ChangeEventKind.VertexCreated },
                labels: new[] { "person" }));
            using var byKey = Subscribe(ChangeFeedFilter.Create(keys: new[] { "name" }));

            var (a, b) = TwoVertices("person", "robot");
            Edge(a, b);
            _fallen8.EnqueueTransaction(new AddPropertyTransaction
            {
                Definition = new NoSQL.GraphDB.Core.Model.PropertyAddDefinition
                {
                    GraphElementId = a,
                    PropertyId = "name",
                    Property = "Alice"
                }
            }).WaitUntilFinished();
            _fallen8.EnqueueTransaction(new AddPropertyTransaction
            {
                Definition = new NoSQL.GraphDB.Core.Model.PropertyAddDefinition
                {
                    GraphElementId = a,
                    PropertyId = "age",
                    Property = 30
                }
            }).WaitUntilFinished();

            // Unfiltered: all five events.
            var allEvents = ReadMany(all, 5);
            Assert.IsTrue(allEvents.Select(e => e.Seq).SequenceEqual(allEvents.Select(e => e.Seq).OrderBy(s => s)));

            // kinds=vertexCreated AND labels=person: exactly the person vertex.
            var filtered = Read(onlyPersonVertices);
            Assert.AreEqual(ChangeEventKind.VertexCreated, filtered.Kind);
            Assert.AreEqual(a, filtered.Id);
            AssertQuiet(onlyPersonVertices);

            // keys=name: property events only (setting keys excludes creates), key must match.
            var keyEvent = Read(byKey);
            Assert.AreEqual(ChangeEventKind.PropertySet, keyEvent.Kind);
            Assert.AreEqual("name", keyEvent.Key);
            AssertQuiet(byKey);

            // Consistency: the filtered views are subsets of the same total order.
            Assert.IsTrue(allEvents.Any(e => e.Seq == filtered.Seq && e.Kind == filtered.Kind));
        }

        [TestMethod]
        public void UnlabeledElements_NeverMatchALabelsFilter()
        {
            using var labeled = Subscribe(ChangeFeedFilter.Create(labels: new[] { "person" }));

            var tx = new CreateVerticesTransaction();
            tx.AddVertex(1u); // no label
            _fallen8.EnqueueTransaction(tx).WaitUntilFinished();

            AssertQuiet(labeled);
        }

        [TestMethod]
        public void Resync_BypassesEveryFilter()
        {
            using var narrow = Subscribe(ChangeFeedFilter.Create(
                kinds: new[] { ChangeEventKind.PropertySet },
                labels: new[] { "nothing-matches-this" }));

            _fallen8.EnqueueTransaction(new TabulaRasaTransaction()).WaitUntilFinished();

            var resync = Read(narrow);
            Assert.AreEqual(ChangeEventKind.Resync, resync.Kind);
        }

        #endregion

        #region catch-up + ring

        [TestMethod]
        public void Since_InsideTheRing_ReplaysExactlyTheMissedEvents_ThenContinuesLive()
        {
            var (a, b) = TwoVertices();
            Edge(a, b); // "missed" by the late subscriber
            WaitForSeq(_fallen8, 3);

            var epoch = _fallen8.ChangeFeed.Epoch;
            using var late = Subscribe(sinceEpoch: epoch, sinceSeq: 2);

            var replayed = Read(late);
            Assert.AreEqual(ChangeEventKind.EdgeCreated, replayed.Kind);
            Assert.AreEqual(3, replayed.Seq, "exactly the missed event replays");

            // ...and live continues gap-free.
            TwoVertices();
            var live = ReadMany(late, 2);
            Assert.AreEqual(4, live[0].Seq);
            Assert.AreEqual(5, live[1].Seq);
        }

        [TestMethod]
        public void Since_OlderThanTheRing_StartsWithSeekOutOfRangeResync()
        {
            using var small = new Fallen8(_loggerFactory, new ChangeFeedOptions { BufferSize = 4 });

            var tx = new CreateVerticesTransaction();
            for (var i = 0; i < 10; i++)
            {
                tx.AddVertex(1u, "person");
            }
            small.EnqueueTransaction(tx).WaitUntilFinished();
            WaitForSeq(small, 10);

            // seq 1 fell out of the 4-slot ring (window is 7..10): continuity cannot be served.
            Assert.IsTrue(small.ChangeFeed.TrySubscribe(ChangeFeedFilter.MatchAll, small.ChangeFeed.Epoch, 1, out var seek));
            using (seek)
            {
                var first = Read(seek);
                Assert.AreEqual(ChangeEventKind.Resync, first.Kind);
                Assert.AreEqual("seekOutOfRange", first.ResyncReason);
            }
        }

        [TestMethod]
        public void EpochMismatch_StartsWithResync()
        {
            TwoVertices();
            WaitForSeq(_fallen8, 2);

            using var mismatched = Subscribe(sinceEpoch: Guid.NewGuid(), sinceSeq: 1);

            var first = Read(mismatched);
            Assert.AreEqual(ChangeEventKind.Resync, first.Kind);
            Assert.AreEqual("seekOutOfRange", first.ResyncReason);
        }

        [TestMethod]
        public void RingWraparound_ServesOnlyTheBufferedWindow()
        {
            using var small = new Fallen8(_loggerFactory, new ChangeFeedOptions { BufferSize = 4 });

            for (var batch = 0; batch < 3; batch++)
            {
                var tx = new CreateVerticesTransaction();
                tx.AddVertex(1u, "person");
                tx.AddVertex(1u, "person");
                small.EnqueueTransaction(tx).WaitUntilFinished();
            }
            WaitForSeq(small, 6);

            // 6 events total, the 4-slot ring keeps seqs 3..6: since=4 replays 5 and 6.
            Assert.IsTrue(small.ChangeFeed.TrySubscribe(ChangeFeedFilter.MatchAll, small.ChangeFeed.Epoch, 4, out var inWindow));
            using (inWindow)
            {
                var replayed = ReadMany(inWindow, 2);
                Assert.AreEqual(5, replayed[0].Seq);
                Assert.AreEqual(6, replayed[1].Seq);
                Assert.IsTrue(replayed.All(e => e.Kind == ChangeEventKind.VertexCreated));
            }

            // since=2 is the boundary: everything AFTER 2 (3..6) is still buffered, so the replay
            // is gap-free even though event 2 itself was evicted.
            Assert.IsTrue(small.ChangeFeed.TrySubscribe(ChangeFeedFilter.MatchAll, small.ChangeFeed.Epoch, 2, out var boundary));
            using (boundary)
            {
                Assert.AreEqual(3, Read(boundary).Seq);
            }

            // since=1 needs the evicted event 2: continuity cannot be served gap-free.
            Assert.IsTrue(small.ChangeFeed.TrySubscribe(ChangeFeedFilter.MatchAll, small.ChangeFeed.Epoch, 1, out var evicted));
            using (evicted)
            {
                Assert.AreEqual("seekOutOfRange", Read(evicted).ResyncReason);
            }
        }

        #endregion

        #region backpressure + lifecycle

        [TestMethod]
        public void StalledSubscriber_GetsExactlyOneOverflowResync_FastSubscriberUnaffected()
        {
            using var small = new Fallen8(_loggerFactory, new ChangeFeedOptions { SubscriberQueueSize = 2 });

            Assert.IsTrue(small.ChangeFeed.TrySubscribe(ChangeFeedFilter.MatchAll, null, null, out var stalled));
            Assert.IsTrue(small.ChangeFeed.TrySubscribe(ChangeFeedFilter.MatchAll, null, null, out var fast));

            // The fast subscriber is drained in LOCKSTEP with the producer (one read per commit),
            // so its 2-slot queue can never overflow; the stalled one is never read during the
            // burst and must overflow.
            const int total = 40;
            var fastEvents = new List<ChangeEvent>();
            for (var i = 0; i < total; i++)
            {
                var tx = new CreateVertexTransaction
                {
                    Definition = new NoSQL.GraphDB.Core.Model.VertexDefinition { CreationDate = 1u, Label = "person" }
                };
                small.EnqueueTransaction(tx).WaitUntilFinished();
                fastEvents.Add(Read(fast));
            }

            // The fast subscriber missed nothing: ascending seq, no resync, all forty.
            Assert.AreEqual(total, fastEvents.Count);
            Assert.IsTrue(fastEvents.All(e => e.Kind == ChangeEventKind.VertexCreated),
                "a slow sibling must cost the fast subscriber nothing");
            Assert.IsTrue(fastEvents.Select(e => e.Seq).SequenceEqual(fastEvents.Select(e => e.Seq).OrderBy(s => s)));

            // The stalled subscriber holds only its 2-slot buffered head; draining it frees space
            // and the owed resync(overflow) must arrive WITHOUT any further commit - an idle tail
            // after a burst must never leave continuity loss unsignaled. Drain with TryRead (a
            // timed-out async read would stay pending and steal the next event).
            WaitForSeq(small, total);
            var drained = new List<ChangeEvent>();
            while (stalled.Reader.TryRead(out var buffered) && buffered.Kind == ChangeEventKind.VertexCreated)
            {
                drained.Add(buffered);
            }
            Assert.AreEqual(2, drained.Count, "the stalled queue held exactly its capacity");

            var resync = Read(stalled);
            Assert.AreEqual(ChangeEventKind.Resync, resync.Kind, "the owed resync lands as soon as space frees");
            Assert.AreEqual("overflow", resync.ResyncReason);
            Assert.AreEqual(total, resync.Seq, "the resync reports the last DROPPED position");
            AssertQuiet(stalled); // exactly one resync, no duplicate storm

            // Live delivery resumes with strictly ascending seq after the resync.
            var resumeTx = new CreateVertexTransaction
            {
                Definition = new NoSQL.GraphDB.Core.Model.VertexDefinition { CreationDate = 1u, Label = "person" }
            };
            small.EnqueueTransaction(resumeTx).WaitUntilFinished();

            var resumed = Read(stalled);
            Assert.AreEqual(ChangeEventKind.VertexCreated, resumed.Kind);
            Assert.IsTrue(resumed.Seq > resync.Seq, "per-connection ids stay strictly ascending");
            AssertQuiet(stalled);

            stalled.Dispose();
            fast.Dispose();
        }

        [TestMethod]
        public void ConcurrentProducers_YieldStrictlyAscendingSeqs_WithContiguousBatches()
        {
            using var subscription = Subscribe();

            // Four concurrent enqueuers, each committing 25 two-vertex batches. The single
            // writer serializes commits; the feed must reproduce that order: strictly ascending,
            // gap-free seqs with every batch's two events contiguous (same commit timestamp).
            const int producers = 4;
            const int batchesPerProducer = 25;
            var tasks = new List<Task>();
            for (var p = 0; p < producers; p++)
            {
                tasks.Add(Task.Run(() =>
                {
                    for (var i = 0; i < batchesPerProducer; i++)
                    {
                        var tx = new CreateVerticesTransaction();
                        tx.AddVertex(1u, "person");
                        tx.AddVertex(1u, "person");
                        _fallen8.EnqueueTransaction(tx).WaitUntilFinished();
                    }
                }));
            }
            Task.WaitAll(tasks.ToArray(), 30_000);

            const int totalEvents = producers * batchesPerProducer * 2;
            var events = ReadMany(subscription, totalEvents, timeoutMs: 30_000);

            for (var i = 0; i < totalEvents; i++)
            {
                Assert.AreEqual(i + 1, events[i].Seq, "seqs are gap-free and strictly ascending under concurrent producers");
            }

            // Batch contiguity: events pair up per transaction (shared commit timestamp).
            for (var i = 0; i < totalEvents; i += 2)
            {
                Assert.AreEqual(events[i].Ts, events[i + 1].Ts,
                    "a batch transaction's events are contiguous (no interleaving across commits)");
            }
        }

        [TestMethod]
        public void InboxOverflow_BecomesAResync_ForRingAndSubscribers()
        {
            // Deterministic inbox overflow via the engine's INTERNAL test seams (reached by
            // reflection - the engine declares no InternalsVisibleTo): a 1-slot inbox and a
            // paused dispatcher. While paused, the second descriptor fills the inbox and the
            // third is DROPPED by the writer (lost-events flag); on resume the dispatcher must
            // turn that into a resync(overflow) for the ring and every subscriber.
            using var feed = CreateFeedWithInboxCapacity(1);

            Assert.IsTrue(feed.TrySubscribe(ChangeFeedFilter.MatchAll, null, null, out var subscription));
            using (subscription)
            {
                // Let the dispatcher deliver one descriptor first, then pause it and saturate
                // the inbox.
                PublishTestDescriptor(feed);
                Assert.IsTrue(SpinWait.SpinUntil(() => feed.LastSeq >= 1, 5000));

                using (PauseDispatch(feed))
                {
                    PublishTestDescriptor(feed); // parks in the 1-slot inbox
                    PublishTestDescriptor(feed); // inbox full -> dropped, lost-events flag set
                }

                var first = Read(subscription);
                Assert.AreEqual(ChangeEventKind.VertexCreated, first.Kind);

                var second = Read(subscription);
                Assert.AreEqual(ChangeEventKind.VertexCreated, second.Kind);

                var resync = Read(subscription);
                Assert.AreEqual(ChangeEventKind.Resync, resync.Kind,
                    "a writer-side inbox drop must surface as a resync - continuity loss is never silent");
                Assert.AreEqual("overflow", resync.ResyncReason);

                // The resync entered the ring like any other event: a catch-up replay reproduces it.
                Assert.IsTrue(feed.TrySubscribe(ChangeFeedFilter.MatchAll, feed.Epoch, 2, out var replayer));
                using (replayer)
                {
                    var replayed = Read(replayer);
                    Assert.AreEqual(ChangeEventKind.Resync, replayed.Kind);
                    Assert.AreEqual(resync.Seq, replayed.Seq);
                }
            }
        }

        #region reflection access to the engine's internal test seams

        private static ChangeFeedDispatcher CreateFeedWithInboxCapacity(int inboxCapacity)
        {
            var logger = TestLoggerFactory.Create().CreateLogger<ChangeFeedDispatcher>();
            return (ChangeFeedDispatcher)Activator.CreateInstance(
                typeof(ChangeFeedDispatcher),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                binder: null,
                args: new object[] { new ChangeFeedOptions(), logger, inboxCapacity },
                culture: null);
        }

        private static void PublishTestDescriptor(ChangeFeedDispatcher feed)
        {
            var builder = new NoSQL.GraphDB.Core.ChangeFeed.ChangeDescriptor.Builder();
            builder.VertexCreated(0, "person");
            var buildOrNull = typeof(NoSQL.GraphDB.Core.ChangeFeed.ChangeDescriptor.Builder)
                .GetMethod("BuildOrNull", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var descriptor = buildOrNull.Invoke(builder, null);

            var publish = typeof(ChangeFeedDispatcher)
                .GetMethod("Publish", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            publish.Invoke(feed, new[] { descriptor });
        }

        private static IDisposable PauseDispatch(ChangeFeedDispatcher feed)
        {
            var pause = typeof(ChangeFeedDispatcher)
                .GetMethod("PauseDispatchForTest", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return (IDisposable)pause.Invoke(feed, null);
        }

        #endregion

        [TestMethod]
        public void WalEnabledEngine_DeliversEvents_AfterDurableCommit()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "f8_cfwal_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                using var walEngine = new Fallen8(_loggerFactory,
                    new WriteAheadLogOptions(Path.Combine(tempDir, "cf.wal")), null, null, new ChangeFeedOptions());

                Assert.IsTrue(walEngine.ChangeFeed.TrySubscribe(ChangeFeedFilter.MatchAll, null, null, out var subscription));
                using (subscription)
                {
                    var tx = new CreateVertexTransaction
                    {
                        Definition = new NoSQL.GraphDB.Core.Model.VertexDefinition { CreationDate = 1u, Label = "person" }
                    };
                    var info = walEngine.EnqueueTransaction(tx);
                    info.WaitUntilFinished();
                    Assert.IsTrue(info.Durable, "the WAL-enabled commit is durable");

                    var evt = Read(subscription);
                    Assert.AreEqual(ChangeEventKind.VertexCreated, evt.Kind);
                    Assert.AreEqual(1, evt.Seq);
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
            }
        }

        [TestMethod]
        public void Filters_ElementsDimension_AndOrWithinADimension()
        {
            using var edgesOnly = Subscribe(ChangeFeedFilter.Create(elements: new[] { ChangeElementType.Edge }));
            using var twoLabels = Subscribe(ChangeFeedFilter.Create(labels: new[] { "person", "robot" }));
            using var twoKinds = Subscribe(ChangeFeedFilter.Create(
                kinds: new[] { ChangeEventKind.VertexCreated, ChangeEventKind.EdgeCreated }));

            var (a, b) = TwoVertices("person", "robot");
            var tx = new CreateVerticesTransaction();
            tx.AddVertex(1u, "company");
            _fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            var edgeId = Edge(a, b);

            // elements=edge: exactly the edge event.
            var edgeEvent = Read(edgesOnly);
            Assert.AreEqual(edgeId, edgeEvent.Id);
            Assert.AreEqual(ChangeElementType.Edge, edgeEvent.Element);
            AssertQuiet(edgesOnly);

            // labels=person,robot (OR within the dimension): both labeled vertices; neither the
            // company vertex nor the knows-labeled edge matches.
            var labeled = ReadMany(twoLabels, 2);
            CollectionAssert.AreEquivalent(new[] { "person", "robot" },
                labeled.Select(e => e.Label).ToArray());
            AssertQuiet(twoLabels);

            // kinds=vertexCreated,edgeCreated (OR): all four creates, nothing else would pass.
            var kinds = ReadMany(twoKinds, 4);
            Assert.IsTrue(kinds.All(e =>
                e.Kind == ChangeEventKind.VertexCreated || e.Kind == ChangeEventKind.EdgeCreated));
            AssertQuiet(twoKinds);
        }

        [TestMethod]
        public void MaxSubscribers_IsEnforced_AndUnsubscribeFreesTheSlot()
        {
            using var limited = new Fallen8(_loggerFactory, new ChangeFeedOptions { MaxSubscribers = 2 });

            Assert.IsTrue(limited.ChangeFeed.TrySubscribe(ChangeFeedFilter.MatchAll, null, null, out var first));
            Assert.IsTrue(limited.ChangeFeed.TrySubscribe(ChangeFeedFilter.MatchAll, null, null, out var second));
            Assert.IsFalse(limited.ChangeFeed.TrySubscribe(ChangeFeedFilter.MatchAll, null, null, out _),
                "the third subscriber exceeds MaxSubscribers");
            Assert.AreEqual(2, limited.ChangeFeed.SubscriberCount);

            first.Dispose();
            Assert.AreEqual(1, limited.ChangeFeed.SubscriberCount);
            Assert.IsTrue(limited.ChangeFeed.TrySubscribe(ChangeFeedFilter.MatchAll, null, null, out var third));

            second.Dispose();
            third.Dispose();
            Assert.AreEqual(0, limited.ChangeFeed.SubscriberCount);
        }

        [TestMethod]
        public void Dispose_CompletesSubscriberStreams()
        {
            var engine = new Fallen8(_loggerFactory, new ChangeFeedOptions());
            Assert.IsTrue(engine.ChangeFeed.TrySubscribe(ChangeFeedFilter.MatchAll, null, null, out var subscription));

            engine.Dispose();

            var completion = subscription.Reader.Completion;
            Assert.IsTrue(completion.Wait(5000), "the subscriber stream completes on engine dispose");
        }

        [TestMethod]
        public void EventPayloads_NeverContainPropertyValues()
        {
            var (a, _) = TwoVertices();
            WaitForSeq(_fallen8, 2);
            using var subscription = Subscribe();

            _fallen8.EnqueueTransaction(new AddPropertyTransaction
            {
                Definition = new NoSQL.GraphDB.Core.Model.PropertyAddDefinition
                {
                    GraphElementId = a,
                    PropertyId = "secret",
                    Property = "the-secret-value"
                }
            }).WaitUntilFinished();

            var setEvent = Read(subscription);
            Assert.AreEqual("secret", setEvent.Key);

            // The event type carries no value slot at all; assert the metadata is all there is.
            var properties = typeof(ChangeEvent).GetProperties().Select(p => p.Name).ToList();
            CollectionAssert.AreEquivalent(
                new[] { "Seq", "Ts", "Kind", "Element", "Id", "Label", "Key", "SourceId", "TargetId", "ResyncReason" },
                properties,
                "ChangeEvent must stay metadata-only (no property-value field)");
        }

        #endregion
    }
}
