// MIT License
//
// PropertyReplaceTest.cs
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
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.ChangeFeed;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Persistency;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The property UPDATE path (feature platform-integrity-audit W2). Before
    ///   <see cref="SetPropertiesTransaction" /> there was none: <see cref="AddPropertyTransaction" />
    ///   and <see cref="AddPropertiesTransaction" /> are "insert, or verify the existing value is
    ///   equal" and reject a CHANGE with <see cref="TransactionFailureReason.Conflict" />, so changing
    ///   a value took a remove transaction followed by a set transaction - two transactions, not
    ///   atomic, with a window in which the property read as absent.
    ///
    ///   <para>Also pins the two properties the identity model in the integration features depends on:
    ///   a semantically empty write is a TRUE no-op (no change event, no modification-date bump), and a
    ///   removal of an absent property succeeds, which together make a replayed reconciliation
    ///   idempotent.</para>
    ///
    ///   <para>The add transactions' conflict behaviour is deliberately unchanged and is
    ///   regression-guarded here, because transaction-atomicity specified that a conflict be cleanly
    ///   CLASSIFIED rather than thrown mid-batch - it never specified that add-or-must-equal is the
    ///   desired update semantic.</para>
    /// </summary>
    [TestClass]
    public class PropertyReplaceTest
    {
        private ILoggerFactory _loggerFactory;
        private Fallen8 _fallen8;
        private TempDirectory _temp;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
            _fallen8 = new Fallen8(_loggerFactory, new ChangeFeedOptions());
            _temp = new TempDirectory("f8_w2_");
        }

        [TestCleanup]
        public void TestCleanup()
        {
            _fallen8?.Dispose();
            _temp?.Dispose();
        }

        #region helpers

        private int NewVertex(params (string Key, object Value)[] properties)
        {
            var tx = new CreateVerticesTransaction();
            var definition = new VertexDefinition { Label = "device", CreationDate = 0 };
            if (properties.Length > 0)
            {
                definition.Properties = properties.ToDictionary(p => p.Key, p => p.Value);
            }
            tx.AddVertex(definition);
            _fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.GetCreatedVertices().Single().Id;
        }

        private TransactionInformation Run(ATransaction tx)
        {
            var info = _fallen8.EnqueueTransaction(tx);
            info.WaitUntilFinished();
            return info;
        }

        private T Property<T>(int id, string key)
        {
            Assert.IsTrue(_fallen8.TryGetGraphElement(out var element, id), "the element exists");
            Assert.IsTrue(element.TryGetProperty<T>(out var value, key), "the property " + key + " exists");
            return value;
        }

        private bool HasProperty(int id, string key)
        {
            Assert.IsTrue(_fallen8.TryGetGraphElement(out var element, id), "the element exists");
            return element.TryGetProperty<object>(out _, key);
        }

        private uint ModificationDateOf(int id)
        {
            Assert.IsTrue(_fallen8.TryGetGraphElement(out var element, id), "the element exists");
            return element.ModificationDate;
        }

        private ChangeFeedSubscription Subscribe()
        {
            Assert.IsTrue(_fallen8.ChangeFeed.TrySubscribe(ChangeFeedFilter.MatchAll, null, null, out var subscription),
                "subscribe should succeed");
            return subscription;
        }

        /// <summary>Drains whatever the dispatcher has, up to a short quiet period. Used to assert an
        /// ABSENCE of events, so it must not block forever waiting for one that should not exist.</summary>
        private static List<ChangeEvent> DrainFor(ChangeFeedSubscription subscription, int quietMs = 400)
        {
            var events = new List<ChangeEvent>();
            var deadline = Environment.TickCount64 + quietMs;
            while (Environment.TickCount64 < deadline)
            {
                if (subscription.Reader.TryRead(out var change))
                {
                    events.Add(change);
                    deadline = Environment.TickCount64 + quietMs;
                    continue;
                }
                Thread.Sleep(20);
            }
            return events;
        }

        #endregion

        #region the gap: there was no update path

        [TestMethod]
        public void SetProperties_UpdatesAnExistingValue()
        {
            // THE defect this transaction exists for. The same batch through
            // AddPropertiesTransaction is a Conflict (pinned below), and the shipped singular REST
            // route built on it silently discarded the write.
            var id = NewVertex(("ip", "10.0.0.5"));

            var info = Run(new SetPropertiesTransaction().SetProperty(id, "ip", "10.0.0.9"));

            Assert.AreEqual(TransactionState.Finished, info.TransactionState, "error: " + info.Error);
            Assert.AreEqual(TransactionFailureReason.None, info.FailureReason);
            Assert.AreEqual("10.0.0.9", Property<string>(id, "ip"));
        }

        [TestMethod]
        public void SetProperties_AddsANewValue_Too()
        {
            var id = NewVertex();

            Run(new SetPropertiesTransaction().SetProperty(id, "ip", "10.0.0.5"));

            Assert.AreEqual("10.0.0.5", Property<string>(id, "ip"));
        }

        [TestMethod]
        public void SetProperties_SetsAndRemoves_InOneAtomicBatch()
        {
            // The shape a reconciliation against an external source needs: some keys now hold new
            // values, others are gone, and the element is never observable in a half-applied state.
            var id = NewVertex(("ip", "10.0.0.5"), ("stale", "yes"));

            var info = Run(new SetPropertiesTransaction()
                .SetProperty(id, "ip", "10.0.0.9")     // changed
                .SetProperty(id, "mac", "44d2")        // new
                .RemoveProperty(id, "stale"));         // gone

            Assert.AreEqual(TransactionState.Finished, info.TransactionState, "error: " + info.Error);
            Assert.AreEqual("10.0.0.9", Property<string>(id, "ip"));
            Assert.AreEqual("44d2", Property<string>(id, "mac"));
            Assert.IsFalse(HasProperty(id, "stale"));
        }

        #endregion

        #region idempotence: a semantically empty write changes nothing observable

        [TestMethod]
        public void SetProperties_EqualValue_DoesNotBumpTheModificationDate()
        {
            var id = NewVertex(("ip", "10.0.0.5"));
            var before = ModificationDateOf(id);

            // A modification date has second-or-better resolution; sleep so a bump would be visible.
            Thread.Sleep(1100);
            var info = Run(new SetPropertiesTransaction().SetProperty(id, "ip", "10.0.0.5"));

            Assert.AreEqual(TransactionState.Finished, info.TransactionState);
            Assert.AreEqual(before, ModificationDateOf(id),
                "Re-asserting the value an element already holds must not look like a modification, or " +
                "'an unchanged source produces no mutations' is unobservable.");
        }

        [TestMethod]
        public void SetProperties_EqualValue_PublishesNoChangeEvent()
        {
            var id = NewVertex(("ip", "10.0.0.5"));
            var subscription = Subscribe();
            DrainFor(subscription, 300); // discard the creation events

            Run(new SetPropertiesTransaction().SetProperty(id, "ip", "10.0.0.5"));

            var events = DrainFor(subscription);
            Assert.AreEqual(0, events.Count,
                "An equal-value write must publish nothing; it would otherwise churn every subscriber on " +
                "every poll of an unchanged source. Saw: " +
                string.Join(", ", events.Select(e => e.Kind + " " + e.Key)));
        }

        [TestMethod]
        public void SetProperties_RemovingAnAbsentProperty_IsANoOp()
        {
            // Makes a withdrawal replay-safe: replaying it must succeed and change nothing.
            var id = NewVertex(("ip", "10.0.0.5"));
            var before = ModificationDateOf(id);
            Thread.Sleep(1100);

            var info = Run(new SetPropertiesTransaction().RemoveProperty(id, "neverThere"));

            Assert.AreEqual(TransactionState.Finished, info.TransactionState);
            Assert.AreEqual(before, ModificationDateOf(id));
            Assert.AreEqual("10.0.0.5", Property<string>(id, "ip"), "The untouched property is untouched.");
        }

        [TestMethod]
        public void SetProperties_ReplayingTheWholeBatch_IsIdempotent()
        {
            var id = NewVertex(("ip", "10.0.0.5"), ("stale", "yes"));
            SetPropertiesTransaction Batch() => new SetPropertiesTransaction()
                .SetProperty(id, "ip", "10.0.0.9")
                .SetProperty(id, "mac", "44d2")
                .RemoveProperty(id, "stale");

            Run(Batch());
            var after = ModificationDateOf(id);
            Thread.Sleep(1100);

            var subscription = Subscribe();
            DrainFor(subscription, 300);
            var info = Run(Batch()); // the identical batch again

            Assert.AreEqual(TransactionState.Finished, info.TransactionState);
            Assert.AreEqual(after, ModificationDateOf(id), "The second application changes nothing.");
            Assert.AreEqual(0, DrainFor(subscription).Count, "...and publishes nothing.");
        }

        #endregion

        #region change-feed fidelity

        [TestMethod]
        public void SetProperties_ReportsASetAsSetAndARemovalAsRemoved()
        {
            var id = NewVertex(("stale", "yes"));
            var subscription = Subscribe();
            DrainFor(subscription, 300);

            Run(new SetPropertiesTransaction()
                .SetProperty(id, "ip", "10.0.0.9")
                .RemoveProperty(id, "stale"));

            var events = DrainFor(subscription);
            Assert.AreEqual(2, events.Count, "one event per APPLIED write");
            Assert.IsTrue(events.Any(e => e.Kind == ChangeEventKind.PropertySet && e.Key == "ip"),
                "the set is reported as propertySet");
            Assert.IsTrue(events.Any(e => e.Kind == ChangeEventKind.PropertyRemoved && e.Key == "stale"),
                "the removal is reported as propertyRemoved, not as a set - the sibling embedding batch " +
                "reports every write as a set, which is an infidelity worth not copying");
        }

        #endregion

        #region atomicity and validation

        [TestMethod]
        public void SetProperties_IntraBatch_LastWriteWins()
        {
            // Replace semantics: no conflict pass, so a key written twice in one batch simply ends at
            // the last value (the same rule SetEmbeddings_internal already applies).
            var id = NewVertex();

            var info = Run(new SetPropertiesTransaction()
                .SetProperty(id, "ip", "10.0.0.1")
                .SetProperty(id, "ip", "10.0.0.2"));

            Assert.AreEqual(TransactionState.Finished, info.TransactionState, "error: " + info.Error);
            Assert.AreEqual("10.0.0.2", Property<string>(id, "ip"));
        }

        [TestMethod]
        public void SetProperties_IntraBatchSetThenRemove_LeavesItRemoved()
        {
            var id = NewVertex(("ip", "10.0.0.5"));

            Run(new SetPropertiesTransaction()
                .SetProperty(id, "ip", "10.0.0.9")
                .RemoveProperty(id, "ip"));

            Assert.IsFalse(HasProperty(id, "ip"));
        }

        [TestMethod]
        public void SetProperties_IntraBatchDuplicateEqualValue_EmitsOneEvent()
        {
            // The no-op check consults the batch's own pending state, not only the store, so the
            // second identical write is recognised as a no-op rather than re-applied.
            var id = NewVertex();
            var subscription = Subscribe();
            DrainFor(subscription, 300);

            Run(new SetPropertiesTransaction()
                .SetProperty(id, "ip", "10.0.0.1")
                .SetProperty(id, "ip", "10.0.0.1"));

            Assert.AreEqual(1, DrainFor(subscription).Count, "the duplicate is a no-op");
        }

        [TestMethod]
        public void SetProperties_NullDefinition_IsInvalidInput_AndAppliesNothing()
        {
            var id = NewVertex(("ip", "10.0.0.5"));
            var tx = new SetPropertiesTransaction().SetProperty(id, "mac", "44d2");
            tx.Properties.Add(null);

            var info = Run(tx);

            Assert.AreEqual(TransactionState.RolledBack, info.TransactionState);
            Assert.AreEqual(TransactionFailureReason.InvalidInput, info.FailureReason);
            Assert.IsNull(info.Error, "a pre-validated reject is a clean rollback, not a thrown fault");
            Assert.IsFalse(HasProperty(id, "mac"), "the earlier write of a rejected batch must not stay applied");
        }

        [TestMethod]
        public void SetProperties_NullKey_IsInvalidInput()
        {
            var id = NewVertex();

            var info = Run(new SetPropertiesTransaction().SetProperty(id, null, "x"));

            Assert.AreEqual(TransactionState.RolledBack, info.TransactionState);
            Assert.AreEqual(TransactionFailureReason.InvalidInput, info.FailureReason);
        }

        [TestMethod]
        public void SetProperties_OutOfRangeId_AppliesNothing()
        {
            // Validated before any apply, so the batch stays atomic even though replace needs no
            // conflict pass. The out-of-range throw is the historical boundary (InternalError/500).
            var id = NewVertex(("ip", "10.0.0.5"));

            var info = Run(new SetPropertiesTransaction()
                .SetProperty(id, "mac", "44d2")
                .SetProperty(999_999, "ip", "nope"));

            Assert.AreEqual(TransactionState.RolledBack, info.TransactionState);
            Assert.IsFalse(HasProperty(id, "mac"), "nothing of the batch applied");
            Assert.AreEqual("10.0.0.5", Property<string>(id, "ip"));
        }

        [TestMethod]
        public void SetProperties_EmptyBatch_Commits()
        {
            var info = Run(new SetPropertiesTransaction());

            Assert.AreEqual(TransactionState.Finished, info.TransactionState);
        }

        #endregion

        #region the add transactions are unchanged (regression guard)

        [TestMethod]
        public void AddProperties_StillRejectsAChange_AsConflict()
        {
            // transaction-atomicity specified that a conflicting update be cleanly CLASSIFIED, and
            // that behaviour is intentionally preserved: "insert or verify" remains a useful
            // primitive, and W2 added a sibling rather than redefining it.
            var id = NewVertex(("ip", "10.0.0.5"));

            var tx = new AddPropertiesTransaction();
            tx.AddProperty(id, "ip", "10.0.0.9");
            var info = Run(tx);

            Assert.AreEqual(TransactionState.RolledBack, info.TransactionState);
            Assert.AreEqual(TransactionFailureReason.Conflict, info.FailureReason);
            Assert.AreEqual("10.0.0.5", Property<string>(id, "ip"));
        }

        #endregion

        #region WAL: ordinal 19 round-trips

        [TestMethod]
        public void SetProperties_ReplaysFromTheWal_AfterACrash()
        {
            // A new on-disk ordinal is only real once a replay reconstructs it. The producer commits a
            // set-or-remove batch with the WAL on and is disposed WITHOUT a save, so recovery has only
            // the log to work from.
            var walPath = Path.Combine(_temp.FullName, "wal.f8log");
            var snapshotPath = Path.Combine(_temp.FullName, "snapshot.f8s");

            int id;
            using (var producer = new Fallen8(_loggerFactory, new WriteAheadLogOptions(walPath)))
            {
                var create = new CreateVerticesTransaction();
                create.AddVertex(new VertexDefinition
                {
                    Label = "device",
                    CreationDate = 0,
                    Properties = new Dictionary<string, object> { { "ip", "10.0.0.5" }, { "stale", "yes" } }
                });
                producer.EnqueueTransaction(create).WaitUntilFinished();
                id = create.GetCreatedVertices().Single().Id;

                var save = new SaveTransaction { Path = snapshotPath };
                producer.EnqueueTransaction(save).WaitUntilFinished();

                // Post-snapshot, WAL-only: the update path plus a removal.
                producer.EnqueueTransaction(new SetPropertiesTransaction()
                    .SetProperty(id, "ip", "10.0.0.9")
                    .SetProperty(id, "mac", "44d2")
                    .RemoveProperty(id, "stale")).WaitUntilFinished();
            }

            using var recovered = new Fallen8(_loggerFactory, new WriteAheadLogOptions(walPath));
            var load = new LoadTransaction { Path = snapshotPath };
            var info = recovered.EnqueueTransaction(load);
            info.WaitUntilFinished();

            Assert.AreEqual(TransactionState.Finished, info.TransactionState, "recovery failed: " + info.Error);
            Assert.IsTrue(recovered.TryGetGraphElement(out var element, id), "the element recovered");
            Assert.IsTrue(element.TryGetProperty<string>(out var ip, "ip"));
            Assert.AreEqual("10.0.0.9", ip, "the replayed UPDATE won, not the snapshot's value");
            Assert.IsTrue(element.TryGetProperty<string>(out var mac, "mac"));
            Assert.AreEqual("44d2", mac, "the replayed new key is present");
            Assert.IsFalse(element.TryGetProperty<object>(out _, "stale"), "the replayed removal removed");
        }

        #endregion
    }
}
