// MIT License
//
// BucketIndexRemovalTest.cs
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
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Index.Range;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Covers the bucket-index removal representation (feature cheap-withdrawal): a removal is
    ///   RECORDED in log time and the posting list is compacted only once the recorded removals
    ///   reach half of it, instead of the list being rebuilt for every single removed element.
    ///   That is a performance change with a correctness surface, and this is the correctness half.
    ///
    ///   <para><b>Why these tests drive the index API directly instead of removing vertices from
    ///   the graph.</b> The engine also filters removed elements at the READ end (feature
    ///   index-lifecycle 3.2), so a graph-level removal would be invisible in a bucket even if the
    ///   bucket kept it. Calling <c>RemoveValue</c> on a vertex that is still LIVE in the graph
    ///   removes that safety net, so any element these tests see is one the bucket really
    ///   returned. Every one of them fails if the recorded removal is not honoured on the read
    ///   path.</para>
    /// </summary>
    [TestClass]
    public class BucketIndexRemovalTest
    {
        private ILoggerFactory _loggerFactory;

        [TestInitialize]
        public void Setup()
        {
            _loggerFactory = TestLoggerFactory.Create();
        }

        private static VertexModel[] Vertices(Fallen8 fallen8, Int32 count)
        {
            var tx = new CreateVerticesTransaction();
            for (var i = 0; i < count; i++)
            {
                tx.AddVertex(1u, "v");
            }

            fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.GetCreatedVertices().ToArray();
        }

        private static IIndex NewDictionary(Fallen8 fallen8, String id = "dict")
        {
            Assert.IsTrue(fallen8.IndexFactory.TryCreateIndex(out var index, id, "DictionaryIndex"));
            return index;
        }

        /// <summary>The live elements under one key, or an empty list when the key is gone.</summary>
        private static List<AGraphElementModel> Live(IIndex index, Object key)
            => index.TryGetValue(out var bucket, key)
                ? bucket.ToList()
                : new List<AGraphElementModel>();

        // ---- the read paths ------------------------------------------------------------------

        /// <summary>
        ///   One removal out of ten stays BELOW the compaction threshold, so the element is still
        ///   physically in the posting list. Every read path must nonetheless behave as though it
        ///   is gone. This is the test that fails if <c>Live</c> ever returns the raw list.
        /// </summary>
        [TestMethod]
        public void ARecordedRemoval_IsInvisibleToEveryReadPath()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var v = Vertices(fallen8, 10);
            var index = NewDictionary(fallen8);
            foreach (var vertex in v)
            {
                index.AddOrUpdate("k", vertex);
            }

            index.RemoveValue(v[3]);

            var live = Live(index, "k");
            Assert.AreEqual(9, live.Count, "TryGetValue must not return the removed element.");
            Assert.IsFalse(live.Any(e => ReferenceEquals(e, v[3])));
            Assert.AreEqual(9, index.CountOfValues(), "CountOfValues must count live elements only.");

            var fromKeyValues = index.GetKeyValues().Single().Value;
            Assert.AreEqual(9, fromKeyValues.Count, "GetKeyValues must not yield the removed element.");
            Assert.IsFalse(fromKeyValues.Any(e => ReferenceEquals(e, v[3])));

            // The vertex is deliberately still live in the graph, so nothing but the bucket's own
            // bookkeeping can be what excluded it.
            Assert.IsTrue(fallen8.TryGetVertex(out _, v[3].Id), "the vertex must still exist in the graph");
        }

        /// <summary>
        ///   Order is part of what a posting list gives back, and the representation preserves it
        ///   because a removal is recorded by identity rather than by tombstoning a position.
        /// </summary>
        [TestMethod]
        public void ARecordedRemoval_PreservesTheOrderOfWhatIsLeft()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var v = Vertices(fallen8, 10);
            var index = NewDictionary(fallen8);
            foreach (var vertex in v)
            {
                index.AddOrUpdate("k", vertex);
            }

            index.RemoveValue(v[4]);
            index.RemoveValue(v[0]);

            var expected = new[] { v[1], v[2], v[3], v[5], v[6], v[7], v[8], v[9] };
            var live = Live(index, "k");
            CollectionAssert.AreEqual(expected, live.ToArray(), "insertion order must survive a removal");
        }

        /// <summary>
        ///   Removals either side of the compaction threshold must be indistinguishable from the
        ///   outside. Six of ten crosses it; one of ten does not.
        /// </summary>
        [DataTestMethod]
        [DataRow(1)]
        [DataRow(4)]
        [DataRow(5)]
        [DataRow(6)]
        [DataRow(9)]
        [DataRow(10)]
        public void RemovalsEitherSideOfTheCompactionThreshold_LeaveExactlyTheLiveSet(Int32 removeCount)
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var v = Vertices(fallen8, 10);
            var index = NewDictionary(fallen8);
            foreach (var vertex in v)
            {
                index.AddOrUpdate("k", vertex);
            }

            for (var i = 0; i < removeCount; i++)
            {
                index.RemoveValue(v[i]);
            }

            var expected = v.Skip(removeCount).ToArray();
            CollectionAssert.AreEqual(expected, Live(index, "k").ToArray());
            Assert.AreEqual(expected.Length, index.CountOfValues());
            Assert.AreEqual(removeCount == 10 ? 0 : 1, index.CountOfKeys(),
                "a key must disappear exactly when its last value does");
        }

        // ---- resurrection -------------------------------------------------------------------

        /// <summary>
        ///   Re-adding an element whose removal was recorded but not yet compacted away must
        ///   resurrect the entry the list already holds, NOT append a second one. Without that,
        ///   the element would appear twice and the next compaction would delete both copies.
        /// </summary>
        [TestMethod]
        public void ReAddingAnElementWhoseRemovalIsStillPending_LeavesExactlyOneEntry()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var v = Vertices(fallen8, 10);
            var index = NewDictionary(fallen8);
            foreach (var vertex in v)
            {
                index.AddOrUpdate("k", vertex);
            }

            index.RemoveValue(v[3]);
            index.AddOrUpdate("k", v[3]);

            var live = Live(index, "k");
            Assert.AreEqual(10, live.Count, "the resurrected element must be back exactly once");
            Assert.AreEqual(1, live.Count(e => ReferenceEquals(e, v[3])), "and not twice");
            Assert.AreEqual(10, index.CountOfValues());

            // Force a compaction afterwards: if the resurrection had appended a duplicate while
            // leaving the element recorded as removed, compaction would drop it entirely here.
            for (var i = 4; i < 10; i++)
            {
                index.RemoveValue(v[i]);
            }

            var afterCompaction = Live(index, "k");
            Assert.AreEqual(4, afterCompaction.Count);
            Assert.AreEqual(1, afterCompaction.Count(e => ReferenceEquals(e, v[3])),
                "the resurrected element must survive a later compaction");
        }

        /// <summary>Re-adding after the removal was already compacted away simply appends.</summary>
        [TestMethod]
        public void ReAddingAnElementWhoseRemovalWasAlreadyCompacted_AppendsItOnce()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var v = Vertices(fallen8, 10);
            var index = NewDictionary(fallen8);
            foreach (var vertex in v)
            {
                index.AddOrUpdate("k", vertex);
            }

            for (var i = 0; i < 6; i++)
            {
                index.RemoveValue(v[i]);
            }

            index.AddOrUpdate("k", v[0]);

            var live = Live(index, "k");
            Assert.AreEqual(5, live.Count);
            Assert.AreEqual(1, live.Count(e => ReferenceEquals(e, v[0])));
            Assert.AreSame(v[0], live[live.Count - 1], "an append goes to the end");
        }

        /// <summary>The idempotence guard must still hold on top of the new representation.</summary>
        [TestMethod]
        public void AddingTheSamePairTwice_StillDoesNotDuplicateIt()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var v = Vertices(fallen8, 1);
            var index = NewDictionary(fallen8);

            index.AddOrUpdate("k", v[0]);
            index.AddOrUpdate("k", v[0]);
            index.AddOrUpdate("k", v[0]);

            Assert.AreEqual(1, index.CountOfValues());
        }

        // ---- several keys -------------------------------------------------------------------

        /// <summary>
        ///   One RemoveValue must clear the element from every key it appears under, because the
        ///   reverse map names them all and the element is dropped from the reverse map afterwards.
        /// </summary>
        [TestMethod]
        public void AnElementUnderSeveralKeys_LeavesAllOfThemOnOneRemoval()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var v = Vertices(fallen8, 4);
            var index = NewDictionary(fallen8);
            foreach (var key in new[] { "a", "b", "c" })
            {
                foreach (var vertex in v)
                {
                    index.AddOrUpdate(key, vertex);
                }
            }

            Assert.AreEqual(12, index.CountOfValues());
            index.RemoveValue(v[1]);

            foreach (var key in new[] { "a", "b", "c" })
            {
                var live = Live(index, key);
                Assert.AreEqual(3, live.Count, $"key {key} must have lost the element");
                Assert.IsFalse(live.Any(e => ReferenceEquals(e, v[1])));
            }

            Assert.AreEqual(9, index.CountOfValues());
        }

        /// <summary>
        ///   Dropping a whole key while one of its elements has a recorded removal must not leave
        ///   a dangling reverse entry, which would make a later re-add silently do nothing.
        /// </summary>
        [TestMethod]
        public void DroppingAKeyWhileARemovalIsPending_LeavesNoDanglingReverseEntry()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var v = Vertices(fallen8, 10);
            var index = NewDictionary(fallen8);
            foreach (var vertex in v)
            {
                index.AddOrUpdate("a", vertex);
                index.AddOrUpdate("b", vertex);
            }

            index.RemoveValue(v[2]);
            Assert.IsTrue(index.TryRemoveKey("a"));

            // The element is addable again under the dropped key, which it would not be if a
            // stale reverse entry still claimed the pair was present.
            index.AddOrUpdate("a", v[2]);
            var live = Live(index, "a");
            Assert.AreEqual(1, live.Count);
            Assert.AreSame(v[2], live[0]);

            // And key b still shows nine, having lost the removed element and nothing else.
            Assert.AreEqual(9, Live(index, "b").Count);
        }

        // ---- persistence --------------------------------------------------------------------

        /// <summary>
        ///   A checkpoint taken while a removal is recorded but not compacted must persist the LIVE
        ///   set only. Otherwise the removed element comes back on load, which is a resurrection
        ///   across a restart and the worst failure this representation could have.
        /// </summary>
        [TestMethod]
        public void ACheckpointTakenWithAPendingRemoval_PersistsOnlyTheLiveSet()
        {
            using var temp = new TempDirectory("f8_bucketrm_");

            var source = new Fallen8(_loggerFactory);
            var v = Vertices(source, 10);
            var index = NewDictionary(source);
            foreach (var vertex in v)
            {
                index.AddOrUpdate("k", vertex);
            }

            index.RemoveValue(v[7]);
            var removedId = v[7].Id;

            var savePath = System.IO.Path.Combine(temp.FullName, "idx.f8s");
            var saveTx = new SaveTransaction { Path = savePath, SavePartitions = 1 };
            source.EnqueueTransaction(saveTx).WaitUntilFinished();

            var loaded = new Fallen8(_loggerFactory);
            loaded.EnqueueTransaction(new LoadTransaction { Path = saveTx.ActualPath }).WaitUntilFinished();

            Assert.IsTrue(loaded.IndexFactory.TryGetIndex(out var reloaded, "dict"));
            Assert.AreEqual(9, reloaded.CountOfValues(), "the pending-removed element must not be persisted");
            Assert.IsTrue(reloaded.TryGetValue(out var bucket, "k"));
            Assert.IsFalse(bucket.Any(e => e.Id == removedId), "and must not come back on load");

            // The reverse map was rebuilt from the loaded buckets, so removal still works against it.
            var survivor = bucket[0];
            reloaded.RemoveValue(survivor);
            Assert.AreEqual(8, reloaded.CountOfValues());

            loaded.Dispose();
            source.Dispose();
        }

        // ---- the range index, which reads buckets through its own path ----------------------

        /// <summary>
        ///   RangeIndex gathers buckets itself for its ordered queries, so it needs its own proof
        ///   that it reads the live view and not the raw posting list.
        /// </summary>
        [TestMethod]
        public void RangeQueries_NeverReturnAnElementWhoseRemovalWasRecorded()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var v = Vertices(fallen8, 9);
            Assert.IsTrue(fallen8.IndexFactory.TryCreateIndex(out var index, "range", "RangeIndex"));
            var range = (IRangeIndex)index;

            // Three keys, three elements each, so no single removal empties a key.
            for (var i = 0; i < 9; i++)
            {
                index.AddOrUpdate(10 * (1 + (i / 3)), v[i]);
            }

            index.RemoveValue(v[4]); // under key 20

            Assert.IsTrue(range.Between(out var between, 10, 30, true, true));
            Assert.AreEqual(8, between.Count, "Between must exclude the removed element");
            Assert.IsFalse(between.Any(e => ReferenceEquals(e, v[4])));

            Assert.IsTrue(range.LowerThan(out var lower, 20, true));
            Assert.AreEqual(5, lower.Count, "LowerThan must exclude it too");
            Assert.IsFalse(lower.Any(e => ReferenceEquals(e, v[4])));

            Assert.IsTrue(range.GreaterThan(out var greater, 20, true));
            Assert.AreEqual(5, greater.Count, "and GreaterThan");
            Assert.IsFalse(greater.Any(e => ReferenceEquals(e, v[4])));

            Assert.IsTrue(index.TryGetValue(out var exact, 20));
            Assert.AreEqual(2, exact.Count, "and so must the point lookup");
        }

        // ---- the model test, which is worth more than any single case above -----------------

        /// <summary>
        ///   Drives a long, seeded, interleaved sequence of adds and removes across several keys
        ///   and compares the index against a plain reference model after every operation. This is
        ///   the test that would catch a compaction-boundary bug the hand-picked cases above miss,
        ///   because it crosses the threshold repeatedly, in both directions, with resurrections
        ///   mixed in. Seeded so a failure is reproducible.
        /// </summary>
        [TestMethod]
        public void InterleavedAddsAndRemoves_TrackAReferenceModelExactly()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var v = Vertices(fallen8, 40);
            var index = NewDictionary(fallen8);
            var keys = new[] { "a", "b", "c" };

            // The reference model: key -> the live elements under it, in insertion order. An
            // element may sit under several keys, and RemoveValue clears it from all of them,
            // which the model reproduces by removing it from every list.
            var model = keys.ToDictionary(k => k, _ => new List<VertexModel>());

            var random = new Random(20260901);
            for (var step = 0; step < 4000; step++)
            {
                var vertex = v[random.Next(v.Length)];
                if (random.Next(100) < 55)
                {
                    var key = keys[random.Next(keys.Length)];
                    index.AddOrUpdate(key, vertex);
                    if (!model[key].Contains(vertex))
                    {
                        model[key].Add(vertex);
                    }
                }
                else
                {
                    index.RemoveValue(vertex);
                    foreach (var key in keys)
                    {
                        model[key].Remove(vertex);
                    }
                }

                if (step % 97 != 0)
                {
                    continue;
                }

                foreach (var key in keys)
                {
                    CollectionAssert.AreEqual(model[key].ToArray(), Live(index, key).ToArray(),
                        $"step {step}, key {key}: the live bucket diverged from the model");
                }

                Assert.AreEqual(model.Values.Sum(list => list.Count), index.CountOfValues(),
                    $"step {step}: CountOfValues diverged from the model");
                Assert.AreEqual(model.Count(kv => kv.Value.Count > 0), index.CountOfKeys(),
                    $"step {step}: a key survived with nothing live in it, or was dropped too early");
            }
        }
    }
}
