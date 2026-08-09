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
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Model;
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
