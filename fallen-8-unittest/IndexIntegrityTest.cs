// MIT License
//
// IndexIntegrityTest.cs
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
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.ChangeFeed;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.App.Services;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Index membership integrity (feature platform-integrity-audit W3), the prerequisite for any
    ///   repopulation or rebuild: <see cref="IIndex.AddOrUpdate" /> must be IDEMPOTENT for the same
    ///   (key, element) pair.
    ///
    ///   <para>Why this is a prerequisite and not a nicety: the bucket append was unconditional, so
    ///   re-adding a pair doubled the posting list, re-adding twice tripled it, and the inflated bucket
    ///   was then persisted into the next checkpoint. Any populate-again path - a rebuild, a replayed
    ///   population, a client that re-asserts its keys on every sync - was therefore a silent
    ///   bucket-multiplication machine whose output outlived the process. A scan then returned the same
    ///   element N times, so a caller counting hits or resolving a key to "exactly one element" drew a
    ///   wrong conclusion from a correct-looking answer.</para>
    /// </summary>
    [TestClass]
    public class IndexIntegrityTest
    {
        private ILoggerFactory _loggerFactory;
        private Fallen8 _fallen8;
        private string _tempDir;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
            _fallen8 = new Fallen8(_loggerFactory);
            _tempDir = Path.Combine(Path.GetTempPath(), "f8_w3_" + Guid.NewGuid().ToString("N"));
        }

        [TestCleanup]
        public void TestCleanup()
        {
            _fallen8?.Dispose();
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
        }

        private int NewVertex(string label = "device")
        {
            var tx = new CreateVerticesTransaction();
            tx.AddVertex(new VertexDefinition { Label = label, CreationDate = 0 });
            _fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.GetCreatedVertices().Single().Id;
        }

        private IIndex NewIndex(string name, string type = "DictionaryIndex")
        {
            Assert.IsTrue(_fallen8.IndexFactory.TryCreateIndex(out var index, name, type), "index creation");
            return index;
        }

        private void RunSetProperty(int id, string key, object value)
        {
            var info = _fallen8.EnqueueTransaction(new SetPropertiesTransaction().SetProperty(id, key, value));
            info.WaitUntilFinished();
            Assert.AreEqual(TransactionState.Finished, info.TransactionState, "error: " + info.Error);
        }

        private AGraphElementModel Element(int id)
        {
            Assert.IsTrue(_fallen8.TryGetGraphElement(out var element, id), "the element exists");
            return element;
        }

        [TestMethod]
        public void AddOrUpdate_IsIdempotent_ForTheSameKeyAndElement()
        {
            var index = NewIndex("claims");
            var element = Element(NewVertex());

            index.AddOrUpdate("mac:44d2", element);
            index.AddOrUpdate("mac:44d2", element);
            index.AddOrUpdate("mac:44d2", element);

            Assert.IsTrue(index.TryGetValue(out var bucket, "mac:44d2"));
            Assert.AreEqual(1, bucket.Count,
                "Three identical adds must leave ONE bucket entry; an unconditional append made a " +
                "re-population multiply every posting list and then persisted the result.");
            Assert.AreEqual(1, index.CountOfKeys());
            Assert.AreEqual(1, index.CountOfValues());
        }

        [TestMethod]
        public void AddOrUpdate_StillAddsDistinctElementsUnderOneKey()
        {
            // The idempotency guard must not collapse a genuine multi-value bucket, which is the whole
            // point of a dictionary index.
            var index = NewIndex("claims");
            var first = Element(NewVertex("a"));
            var second = Element(NewVertex("b"));

            index.AddOrUpdate("shared", first);
            index.AddOrUpdate("shared", second);
            index.AddOrUpdate("shared", first); // duplicate of the first only

            Assert.IsTrue(index.TryGetValue(out var bucket, "shared"));
            Assert.AreEqual(2, bucket.Count, "two DISTINCT elements remain two entries");
        }

        [TestMethod]
        public void AddOrUpdate_StillAddsOneElementUnderSeveralKeys()
        {
            // An element carrying several identity claims is indexed under each of them.
            var index = NewIndex("claims");
            var element = Element(NewVertex());

            index.AddOrUpdate("mac:44d2", element);
            index.AddOrUpdate("serial:1234", element);
            index.AddOrUpdate("mac:44d2", element); // duplicate of the first key only

            Assert.AreEqual(2, index.CountOfKeys());
            Assert.IsTrue(index.TryGetValue(out var byMac, "mac:44d2"));
            Assert.AreEqual(1, byMac.Count);
            Assert.IsTrue(index.TryGetValue(out var bySerial, "serial:1234"));
            Assert.AreEqual(1, bySerial.Count);
        }

        [TestMethod]
        public void AddOrUpdate_ThenRemoveValue_LeavesNothingBehind()
        {
            // The guard reads the reverse map, so it must not desynchronise it: after a removal the
            // element is gone from every key AND a re-add works again.
            var index = NewIndex("claims");
            var element = Element(NewVertex());

            index.AddOrUpdate("mac:44d2", element);
            index.AddOrUpdate("mac:44d2", element);
            index.RemoveValue(element);

            Assert.IsFalse(index.TryGetValue(out _, "mac:44d2") && index.CountOfValues() > 0,
                "the element is gone from the index");

            index.AddOrUpdate("mac:44d2", element);
            Assert.IsTrue(index.TryGetValue(out var bucket, "mac:44d2"));
            Assert.AreEqual(1, bucket.Count, "a re-add after a removal works, and once");
        }

        [TestMethod]
        public void AddOrUpdate_IsIdempotent_AcrossACheckpointRoundTrip()
        {
            // The reverse map is rebuilt from the buckets on load, so the guard must still hold on a
            // reloaded index - otherwise the first re-population after a restart doubles everything.
            Directory.CreateDirectory(_tempDir);
            var snapshot = Path.Combine(_tempDir, "snapshot.f8s");

            var index = NewIndex("claims");
            var element = Element(NewVertex());
            index.AddOrUpdate("mac:44d2", element);

            var save = new SaveTransaction { Path = snapshot };
            _fallen8.EnqueueTransaction(save).WaitUntilFinished();

            using var reloaded = new Fallen8(_loggerFactory);
            var load = new LoadTransaction { Path = save.ActualPath };
            var info = reloaded.EnqueueTransaction(load);
            info.WaitUntilFinished();
            Assert.AreEqual(TransactionState.Finished, info.TransactionState, "load failed: " + info.Error);

            Assert.IsTrue(reloaded.IndexFactory.TryGetIndex(out var reloadedIndex, "claims"),
                "the index survived the checkpoint");
            Assert.IsTrue(reloaded.TryGetGraphElement(out var reloadedElement, element.Id));

            reloadedIndex.AddOrUpdate("mac:44d2", reloadedElement);

            Assert.IsTrue(reloadedIndex.TryGetValue(out var bucket, "mac:44d2"));
            Assert.AreEqual(1, bucket.Count,
                "Re-asserting a key after a reload must not duplicate it; the reverse map is rebuilt " +
                "from the loaded buckets, so the guard has to work off that rebuild.");
        }

        [TestMethod]
        public void AfterAWipe_TheSamePairCanBeAddedAgain()
        {
            // REGRESSION GUARD for the interaction between the W3 idempotency guard and the W4 exact
            // rebuild. The guard answers "is this pair already indexed" from the REVERSE map, so if
            // Wipe ever cleared the buckets without clearing that map, the guard would refuse every
            // re-add and an exact rebuild would silently produce an EMPTY index - a repair that
            // destroys instead of repairing. Wipe does clear both today; this pins it, because the
            // failure mode is invisible (no error, just no keys).
            var index = NewIndex("claims");
            var element = Element(NewVertex());
            index.AddOrUpdate("mac:44d2", element);

            index.Wipe();
            index.AddOrUpdate("mac:44d2", element);

            Assert.IsTrue(index.TryGetValue(out var bucket, "mac:44d2"),
                "the identical pair must be re-addable after a wipe");
            Assert.AreEqual(1, bucket.Count);
        }

        [TestMethod]
        public void AfterRemovingAKey_TheSamePairCanBeAddedAgain()
        {
            // The same guard against TryRemoveKey, which maintains the reverse map by a separate code
            // path from RemoveValue and Wipe.
            var index = NewIndex("claims");
            var element = Element(NewVertex());
            index.AddOrUpdate("mac:44d2", element);

            Assert.IsTrue(index.TryRemoveKey("mac:44d2"));
            index.AddOrUpdate("mac:44d2", element);

            Assert.IsTrue(index.TryGetValue(out var bucket, "mac:44d2"));
            Assert.AreEqual(1, bucket.Count);
        }

        #region repair from element state (W4)

        [TestMethod]
        public void Repair_RestoresKeysAfterTheIndexLostThem()
        {
            // The crash / tabula-rasa / save-game-load / dropped-manifest case: the elements are intact
            // and the index is empty. Repair must restore exactly what element state justifies.
            var index = NewIndex("byName");
            var first = NewVertex();
            var second = NewVertex();
            RunSetProperty(first, "name", "alpha");
            RunSetProperty(second, "name", "beta");

            index.AddOrUpdate("alpha", Element(first));
            index.AddOrUpdate("beta", Element(second));
            index.Wipe(); // simulate the loss
            Assert.AreEqual(0, index.CountOfKeys());

            Assert.IsTrue(IndexRepair.TryRepairFromProperty(_fallen8, null, "byName", "name",
                out var result, out var error), "repair failed: " + error);

            Assert.AreEqual(2, result.IndexedElements);
            Assert.AreEqual(0, result.SkippedUnindexableValues);
            Assert.IsFalse(result.Replaced, "the default mode is add-only repair");
            Assert.AreEqual(2, index.CountOfKeys());
            Assert.IsTrue(index.TryGetValue(out var alpha, "alpha"));
            Assert.AreEqual(first, alpha.Single().Id);
        }

        [TestMethod]
        public void Repair_IsIdempotent_SoItIsSafeOnEveryStart()
        {
            var index = NewIndex("byName");
            var id = NewVertex();
            RunSetProperty(id, "name", "alpha");

            for (var run = 0; run < 3; run++)
            {
                Assert.IsTrue(IndexRepair.TryRepairFromProperty(_fallen8, null, "byName", "name",
                    out _, out var error), "repair failed: " + error);
            }

            Assert.IsTrue(index.TryGetValue(out var bucket, "alpha"));
            Assert.AreEqual(1, bucket.Count,
                "Three repairs must leave ONE entry - this is why the W3 idempotency guard is a hard " +
                "prerequisite: without it a repair-on-every-start would multiply every bucket and " +
                "persist the result.");
        }

        [TestMethod]
        public void Repair_AddOnly_LeavesAStaleKey_AndReplaceRemovesIt()
        {
            // The honest difference between the two modes, pinned so nobody assumes repair is exact.
            var index = NewIndex("byName");
            var id = NewVertex();
            RunSetProperty(id, "name", "old");
            index.AddOrUpdate("old", Element(id));

            // The element's value changes; the index still carries the previous key.
            RunSetProperty(id, "name", "new");

            Assert.IsTrue(IndexRepair.TryRepairFromProperty(_fallen8, null, "byName", "name",
                out _, out _), "repair");
            Assert.AreEqual(2, index.CountOfKeys(),
                "add-only repair restores the current key and leaves the stale one - documented, not a bug");

            Assert.IsTrue(IndexRepair.TryRepairFromProperty(_fallen8, null, "byName", "name",
                out var exact, out _, replace: true), "exact rebuild");
            Assert.IsTrue(exact.Replaced);
            Assert.AreEqual(1, index.CountOfKeys(), "an exact rebuild drops what element state no longer justifies");
            Assert.IsTrue(index.TryGetValue(out _, "new"));
        }

        [TestMethod]
        public void Repair_NeverIndexesARemovedElement()
        {
            var index = NewIndex("byName");
            var kept = NewVertex();
            var removed = NewVertex();
            RunSetProperty(kept, "name", "kept");
            RunSetProperty(removed, "name", "gone");
            _fallen8.EnqueueTransaction(new RemoveGraphElementsTransaction
            {
                GraphElementIds = new List<int> { removed }
            }).WaitUntilFinished();

            Assert.IsTrue(IndexRepair.TryRepairFromProperty(_fallen8, null, "byName", "name",
                out var result, out _));

            Assert.AreEqual(1, result.IndexedElements, "only the live element is indexed");
            Assert.IsFalse(index.TryGetValue(out _, "gone"), "a tombstone must never be re-indexed");
        }

        [TestMethod]
        public void Repair_RefusesAnUnknownIndex_AndAMissingProperty()
        {
            Assert.IsFalse(IndexRepair.TryRepairFromProperty(_fallen8, null, "nope", "name", out _, out var noIndex));
            StringAssert.Contains(noIndex, "no index");

            NewIndex("byName");
            Assert.IsFalse(IndexRepair.TryRepairFromProperty(_fallen8, null, "byName", null, out _, out var noProperty));
            StringAssert.Contains(noProperty, "property id is required");
        }

        [TestMethod]
        public void Repair_RefusesAnIndexThatCannotTakeArbitraryKeys()
        {
            // A vector index ranks approximate neighbours rather than answering an exact key, so an
            // arbitrary property value can never be a key in it. It must refuse with a REASON rather
            // than silently indexing nothing. (The spatial index refuses for the same reason - its keys
            // are geometries - and reports the same SupportsPointEqualityLookup == false.)
            Assert.IsTrue(_fallen8.IndexFactory.TryCreateIndex(out _, "vectors", "VectorIndex",
                new Dictionary<string, object> { { "dimension", 3 }, { "metric", "Cosine" } }),
                "vector index creation");

            Assert.IsFalse(IndexRepair.TryRepairFromProperty(_fallen8, null, "vectors", "name", out _, out var error));
            StringAssert.Contains(error, "point-equality");
        }

        [TestMethod]
        public void Repair_CountsAnUnindexableValueRatherThanSkippingItSilently()
        {
            var index = NewIndex("byVector");
            var id = NewVertex();
            // A float[] reaches the store through the engine API; it is not IComparable, so it cannot be
            // a bucket key. The point is that it is COUNTED.
            _fallen8.EnqueueTransaction(new SetPropertiesTransaction()
                .SetProperty(id, "vec", new float[] { 1f, 2f })).WaitUntilFinished();

            Assert.IsTrue(IndexRepair.TryRepairFromProperty(_fallen8, null, "byVector", "vec",
                out var result, out _));

            Assert.AreEqual(0, result.IndexedElements);
            Assert.AreEqual(1, result.SkippedUnindexableValues);
            Assert.AreEqual(0, index.CountOfKeys());
        }

        #endregion

        #region repair in PREFIX mode

        [TestMethod]
        public void Repair_InPrefixMode_IndexesEveryMatchingPropertyOfOneElement()
        {
            // The reason prefix mode exists: a caller's set of values is spread across dense ordinal
            // keys because the property surface accepts scalars and no array, so ONE element carries
            // several claim keys. Restoring only the first leaves it findable by one identity and
            // invisible by the rest, which looks like a successful repair and then duplicates the
            // element on the next lookup-then-create pass.
            var index = NewIndex("identity");
            var id = NewVertex();
            RunSetProperty(id, "$identity:0", "mac:44d244aabbcc");
            RunSetProperty(id, "$identity:1", "serial:AB-1234");

            Assert.IsTrue(IndexRepair.TryRepairFromProperty(_fallen8, null, "identity", "$identity:",
                out var result, out var error, prefix: true), "prefix repair failed: " + error);

            Assert.AreEqual(1, result.ScannedElements, "ScannedElements counts ELEMENTS, not entries");
            Assert.AreEqual(2, result.IndexedElements, "IndexedElements counts the ENTRIES indexed");
            Assert.AreEqual(2, index.CountOfKeys());
            Assert.IsTrue(index.TryGetValue(out var byMac, "mac:44d244aabbcc"), "findable by the first claim");
            Assert.AreEqual(id, byMac.Single().Id);
            Assert.IsTrue(index.TryGetValue(out var bySerial, "serial:AB-1234"), "findable by the second claim");
            Assert.AreEqual(id, bySerial.Single().Id);
        }

        [TestMethod]
        public void Repair_ExactKeyMode_IsUnchangedByThePrefixAddition()
        {
            // The default stays ONE exact key: the prefix string matches no property key at all, and the
            // real key contributes exactly its own value. This is the failure prefix mode was added for,
            // pinned from the other side so the addition cannot quietly change the default.
            var index = NewIndex("identity");
            var id = NewVertex();
            RunSetProperty(id, "$identity:0", "mac:44d244aabbcc");
            RunSetProperty(id, "$identity:1", "serial:AB-1234");

            Assert.IsTrue(IndexRepair.TryRepairFromProperty(_fallen8, null, "identity", "$identity:",
                out var byPrefixString, out _), "an exact-key repair of a prefix is not an error");
            Assert.AreEqual(0, byPrefixString.IndexedElements, "no property is keyed '$identity:' exactly");
            Assert.AreEqual(0, index.CountOfKeys());

            Assert.IsTrue(IndexRepair.TryRepairFromProperty(_fallen8, null, "identity", "$identity:0",
                out var byExactKey, out _), "exact-key repair");
            Assert.AreEqual(1, byExactKey.IndexedElements);
            Assert.AreEqual(1, index.CountOfKeys(), "exactly the one named key, never the sibling ordinal");
            Assert.IsTrue(index.TryGetValue(out _, "mac:44d244aabbcc"));
            Assert.IsFalse(index.TryGetValue(out _, "serial:AB-1234"),
                "the second claim is invisible in exact-key mode - the whole point of the prefix option");
        }

        [TestMethod]
        public void Repair_InPrefixMode_IsAddOnlyAndIdempotent()
        {
            var index = NewIndex("identity");
            var id = NewVertex();
            RunSetProperty(id, "$identity:0", "mac:44d244aabbcc");
            RunSetProperty(id, "$identity:1", "serial:AB-1234");

            for (var run = 0; run < 3; run++)
            {
                Assert.IsTrue(IndexRepair.TryRepairFromProperty(_fallen8, null, "identity", "$identity:",
                    out _, out var error, prefix: true), "prefix repair failed: " + error);
            }

            Assert.AreEqual(2, index.CountOfKeys());
            Assert.IsTrue(index.TryGetValue(out var bucket, "mac:44d244aabbcc"));
            Assert.AreEqual(1, bucket.Count,
                "three prefix repairs must leave ONE entry per key, exactly as the exact-key mode does");

            // Add-only, unchanged by the prefix path: a key element state no longer justifies is left
            // alone, and the current value is added next to it.
            RunSetProperty(id, "$identity:0", "mac:ffffffffffff");
            Assert.IsTrue(IndexRepair.TryRepairFromProperty(_fallen8, null, "identity", "$identity:",
                out var afterChange, out _, prefix: true));
            Assert.IsFalse(afterChange.Replaced, "prefix mode does not imply a rebuild");
            Assert.AreEqual(3, index.CountOfKeys(), "add-only leaves the stale key - documented, not a bug");
            Assert.IsTrue(index.TryGetValue(out _, "mac:ffffffffffff"));
        }

        [TestMethod]
        public void Repair_InPrefixMode_MatchingNothing_IndexesNothing_AndIsNotAnError()
        {
            // "The elements carry nothing under this prefix" and "the prefix is wrong" are the same
            // answer from here, and both are legitimate: on a graph no client has written to yet the
            // repair is a no-op. The numbers are what let a caller tell that apart (scanned many,
            // indexed none), so it must not be a refusal.
            var index = NewIndex("identity");
            var id = NewVertex();
            RunSetProperty(id, "name", "alpha");

            Assert.IsTrue(IndexRepair.TryRepairFromProperty(_fallen8, null, "identity", "$claim:",
                out var result, out var error, prefix: true), "an empty prefix match is not an error: " + error);

            Assert.AreEqual(1, result.ScannedElements);
            Assert.AreEqual(0, result.IndexedElements);
            Assert.AreEqual(0, result.SkippedUnindexableValues);
            Assert.AreEqual(0, index.CountOfKeys());
            Assert.AreEqual(0, index.CountOfValues());
        }

        [TestMethod]
        public void Repair_InPrefixMode_CountsAnUnindexableValueUnderAMatchingKey()
        {
            // The counting rule is per VALUE in this mode: the indexable sibling still lands, and the
            // value that cannot be a key is counted rather than silently dropped.
            var index = NewIndex("identity");
            var id = NewVertex();
            RunSetProperty(id, "$identity:0", "mac:44d244aabbcc");
            _fallen8.EnqueueTransaction(new SetPropertiesTransaction()
                .SetProperty(id, "$identity:1", new float[] { 1f, 2f })).WaitUntilFinished();

            Assert.IsTrue(IndexRepair.TryRepairFromProperty(_fallen8, null, "identity", "$identity:",
                out var result, out _, prefix: true));

            Assert.AreEqual(1, result.IndexedElements);
            Assert.AreEqual(1, result.SkippedUnindexableValues);
            Assert.AreEqual(1, index.CountOfKeys());
        }

        #endregion

        [TestMethod]
        public void FulltextIndex_AddOrUpdate_IsIdempotentToo()
        {
            // The FULLTEXT index was left out of the idempotence fix while reporting
            // SupportsPointEqualityLookup == true, so index repair accepted it and its own "idempotent, safe
            // to run on every start" contract was false here: every POST /index/backfill duplicated every
            // posting, and the inflated buckets went into the next checkpoint, where a fulltext scan returns
            // them all. Same guard as the bucket family, pinned on the one index that did not have it.
            var index = NewIndex("notes", "RegExIndex");
            var element = Element(NewVertex());

            index.AddOrUpdate("the hall printer", element);
            index.AddOrUpdate("the hall printer", element);
            index.AddOrUpdate("the hall printer", element);

            Assert.IsTrue(index.TryGetValue(out var bucket, "the hall printer"));
            Assert.AreEqual(1, bucket.Count,
                "re-adding one (key, element) pair must not grow the posting list, or repair inflates every " +
                "bucket it touches and the next checkpoint persists the inflation");
        }

        [TestMethod]
        public void AddOrUpdate_RefusesARemovedElement_SoRepairCannotPinATombstone()
        {
            // The bucket family's half of the IIndex.AddOrUpdate contract (never index a removed element).
            // Pinned per implementation rather than once, because each index enforces it in its own lock.
            var index = NewIndex("claims-after-removal");
            var id = NewVertex();
            var element = Element(id);

            var removal = _fallen8.EnqueueTransaction(new RemoveGraphElementsTransaction
            {
                GraphElementIds = new List<Int32> { id },
            });
            removal.WaitUntilFinished();
            Assert.AreEqual(TransactionState.Finished, removal.TransactionState, "error: " + removal.Error);

            index.AddOrUpdate("mac:44d2", element);

            Assert.IsFalse(index.TryGetValue(out _, "mac:44d2"),
                "a removed element must never enter an index: the scan would filter it, but the id is pinned " +
                "in the index and survives into the checkpoint");
        }

        [TestMethod]
        public void SingleValueIndex_AddOrUpdate_RefusesARemovedElement_Too()
        {
            // SingleValueIndex is repair-eligible for exactly the same reasons the bucket family is
            // (SupportsPointEqualityLookup == true, so IndexRepair feeds it live elements from the calling
            // thread), and it kept a single value per key rather than a bucket - so a tombstone arriving
            // here does not inflate anything, it OVERWRITES the live element the key resolved to.
            var index = NewIndex("serial", "SingleValueIndex");
            var live = Element(NewVertex());
            index.AddOrUpdate("sn:1", live);

            var doomedId = NewVertex();
            var doomed = Element(doomedId);
            var removal = _fallen8.EnqueueTransaction(new RemoveGraphElementsTransaction
            {
                GraphElementIds = new List<Int32> { doomedId },
            });
            removal.WaitUntilFinished();
            Assert.AreEqual(TransactionState.Finished, removal.TransactionState, "error: " + removal.Error);

            index.AddOrUpdate("sn:1", doomed);
            index.AddOrUpdate("sn:2", doomed);

            Assert.IsTrue(index.TryGetValue(out var bucket, "sn:1"), "the live key must survive");
            Assert.AreSame(live, bucket.Single(),
                "a removed element must never displace the live element a key resolves to");
            Assert.IsFalse(index.TryGetValue(out _, "sn:2"),
                "a removed element must never enter an index: the scan filters it, but the id is pinned in " +
                "the index and survives into the checkpoint");
            Assert.AreEqual(1, index.CountOfKeys(), "the tombstone must not add a key either");
        }

        [TestMethod]
        public void RegExIndex_RefusesARemovedElement_ButKeepsLiveOnes()
        {
            // The FULLTEXT half of the same per-implementation tombstone guard (feature
            // unstructured-ingestion C2/E1: the add-after-remove tombstone leak). It stands up its own
            // engine, with the change feed on as the ingest host has it, rather than the fixture's.
            using var engine = new Fallen8(TestLoggerFactory.Create(), new ChangeFeedOptions());
            Assert.IsTrue(engine.IndexFactory.TryCreateIndex(out var index, "ft", "RegExIndex"));

            var liveTx = new CreateVertexTransaction { Definition = new VertexDefinition { CreationDate = 1u, Label = "Chunk" } };
            engine.EnqueueTransaction(liveTx).WaitUntilFinished();
            var live = liveTx.VertexCreated;

            var doomedTx = new CreateVertexTransaction { Definition = new VertexDefinition { CreationDate = 1u, Label = "Chunk" } };
            engine.EnqueueTransaction(doomedTx).WaitUntilFinished();
            var doomed = doomedTx.VertexCreated;

            // A live element indexes normally (the guard must not break the happy path).
            index.AddOrUpdate("alpha content", live);
            Assert.AreEqual(1, index.CountOfValues());

            // Remove the second vertex, then replay a stale add of it - exactly the
            // add-after-remove race a concurrent DELETE creates during ingest. The guard must
            // skip it so no tombstone is pinned in the index.
            engine.EnqueueTransaction(new RemoveGraphElementsTransaction
            {
                GraphElementIds = new List<Int32> { doomed.Id }
            }).WaitUntilFinished();
            Assert.IsFalse(engine.TryGetVertex(out _, doomed.Id), "precondition: the vertex is removed");

            index.AddOrUpdate("beta content", doomed);
            Assert.AreEqual(1, index.CountOfValues(), "a removed element must not enter the index");
            Assert.IsFalse(index.TryGetValue(out _, "beta content"), "its key must be absent");
        }

        [TestMethod]
        public void RangeIndex_AddOrUpdate_IsIdempotentToo()
        {
            // RangeIndex shares ABucketIndex, so it inherits the guard; pinned so a future split of the
            // family does not lose it on one side.
            var index = NewIndex("ages", "RangeIndex");
            var element = Element(NewVertex());

            index.AddOrUpdate(42, element);
            index.AddOrUpdate(42, element);

            Assert.IsTrue(index.TryGetValue(out var bucket, 42));
            Assert.AreEqual(1, bucket.Count);
        }
    }
}
