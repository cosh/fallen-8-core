// MIT License
//
// WriteAheadLogTest.cs
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
using NoSQL.GraphDB.Core.Expression;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Covers Stage D of the persistence-hardening theme: the opt-in write-ahead log (spec P4 /
    /// plan Phase 5). Exercises the WAL-off default (unchanged behaviour), crash recovery between
    /// snapshots (elements/ids/edges/properties/removals), corrupt/torn-tail safety, and the
    /// snapshot-truncate composition (no double-apply).
    /// </summary>
    [TestClass]
    public class WriteAheadLogTest
    {
        private ILoggerFactory _loggerFactory;
        private TempDirectory _temp;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
            _temp = new TempDirectory("f8_wal_");
        }

        [TestCleanup]
        public void TestCleanup()
        {
            _temp?.Dispose();
        }

        #region helpers

        private string SavePath => Path.Combine(_temp.FullName, "savegame.f8s");
        private string WalPath => Path.Combine(_temp.FullName, "savegame.f8s.wal");

        private Fallen8 NewEngineWithWal()
        {
            return new Fallen8(_loggerFactory, new WriteAheadLogOptions(WalPath));
        }

        private static VertexModel[] AddVertices(Fallen8 fallen8, params (string Label, string Name)[] specs)
        {
            return AddVertices(fallen8, out _, specs);
        }

        private static VertexModel[] AddVertices(Fallen8 fallen8, out TransactionInformation info,
            params (string Label, string Name)[] specs)
        {
            var tx = new CreateVerticesTransaction();
            foreach (var spec in specs)
            {
                tx.AddVertex(1u, spec.Label, new Dictionary<string, object> { { "name", spec.Name } });
            }
            info = fallen8.EnqueueTransaction(tx);
            info.WaitUntilFinished();
            return tx.GetCreatedVertices().ToArray();
        }

        /// <summary>The log's current length. Read after each commit, it gives that commit's entry
        /// boundaries without the test knowing anything about the frame format.</summary>
        private long WalLength => new FileInfo(WalPath).Length;

        /// <summary>Cuts the log to <paramref name="length" /> bytes, as a crash mid-append leaves it.</summary>
        private void CutWalTo(long length)
        {
            using var fs = new FileStream(WalPath, FileMode.Open, FileAccess.Write);
            fs.SetLength(length);
        }

        /// <summary>Inverts one byte of the log: the frame holding it no longer passes its CRC.</summary>
        private void FlipWalByteAt(long offset)
        {
            using var fs = new FileStream(WalPath, FileMode.Open, FileAccess.ReadWrite);
            fs.Seek(offset, SeekOrigin.Begin);
            var original = fs.ReadByte();
            fs.Seek(offset, SeekOrigin.Begin);
            fs.WriteByte((byte)(original ^ 0xFF));
        }

        private static EdgeModel AddEdge(Fallen8 fallen8, int sourceId, string edgePropertyId, int targetId, string label)
        {
            var tx = new CreateEdgesTransaction();
            tx.AddEdge(sourceId, edgePropertyId, targetId, 1u, label);
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.GetCreatedEdges().Single();
        }

        private static string Save(Fallen8 fallen8, string path, int partitions = 1)
        {
            var tx = new SaveTransaction { Path = path, SavePartitions = partitions };
            var info = fallen8.EnqueueTransaction(tx);
            info.WaitUntilFinished();
            Assert.AreEqual(TransactionState.Finished, info.TransactionState, "The save should finish.");
            return tx.ActualPath;
        }

        private static (TransactionState State, Exception Error) Load(Fallen8 fallen8, string path)
        {
            var tx = new LoadTransaction { Path = path };
            var info = fallen8.EnqueueTransaction(tx);
            info.WaitUntilFinished();
            return (info.TransactionState, info.Error);
        }

        private static int CountWithName(Fallen8 fallen8, string name)
        {
            List<AGraphElementModel> hits;
            fallen8.GraphScan(out hits, "name", name, BinaryOperator.Equals);
            return hits.Count;
        }

        private static string NameOf(Fallen8 fallen8, int id)
        {
            Assert.IsTrue(fallen8.TryGetVertex(out var v, id), "Vertex " + id + " should exist.");
            v.TryGetProperty(out string name, "name");
            return name;
        }



        #endregion

        #region WAL off by default

        [TestMethod]
        public void WalDisabledByDefault_WritesNoLog_AndSaveLoadIsUnchanged()
        {
            // The ordinary constructor carries no WAL: no log file appears and behaviour is exactly
            // the pre-WAL save/load round-trip.
            var source = new Fallen8(_loggerFactory);
            AddVertices(source, ("person", "Alice"), ("person", "Bob"));
            var actualPath = Save(source, SavePath);

            Assert.IsFalse(File.Exists(WalPath), "A WAL-disabled engine must not create a log file.");

            var loaded = new Fallen8(_loggerFactory);
            var (state, error) = Load(loaded, actualPath);

            Assert.AreEqual(TransactionState.Finished, state, "Load should succeed; instead: " + error);
            Assert.AreEqual(2, loaded.VertexCount);
            Assert.IsFalse(File.Exists(WalPath), "Loading in a WAL-disabled engine must not create a log file.");
        }

        #endregion

        #region crash recovery between snapshots

        [TestMethod]
        public void CrashBetweenSnapshots_RecoversPostSnapshotVerticesEdgesAndProperties()
        {
            // Phase 1: create, snapshot, then create MORE (which live only in the WAL), then "crash".
            var source = NewEngineWithWal();
            var pre = AddVertices(source, ("person", "Alice"), ("person", "Bob")); // ids 0,1
            Assert.AreEqual(0, pre[0].Id);
            Assert.AreEqual(1, pre[1].Id);

            var actualPath = Save(source, SavePath);
            Assert.AreEqual(SavePath, actualPath, "The first save uses the base path.");

            // Post-snapshot work: recorded only in the WAL (no further snapshot).
            var carol = AddVertices(source, ("person", "Carol"))[0]; // id 2
            Assert.AreEqual(2, carol.Id);
            var edge = AddEdge(source, pre[0].Id, "knows", carol.Id, "knows"); // id 3, Alice -> Carol
            Assert.AreEqual(3, edge.Id);

            // Simulate a crash: drop the in-memory instance WITHOUT another snapshot. Dispose releases
            // the file handles but does NOT reset the log, so the fsync'd post-snapshot entries remain.
            source.Dispose();

            // Phase 2: recover from the same paths.
            var recovered = NewEngineWithWal();
            var (state, error) = Load(recovered, actualPath);

            Assert.AreEqual(TransactionState.Finished, state, "Recovery load should succeed; instead: " + error);
            Assert.AreEqual(3, recovered.VertexCount, "All three vertices (2 snapshot + 1 replayed) recover.");
            Assert.AreEqual(1, recovered.EdgeCount, "The post-snapshot edge is replayed.");

            // Ids are reconstructed identically.
            Assert.AreEqual("Alice", NameOf(recovered, 0));
            Assert.AreEqual("Bob", NameOf(recovered, 1));
            Assert.AreEqual("Carol", NameOf(recovered, 2));

            // Adjacency is reconstructed: Alice --knows--> Carol.
            Assert.IsTrue(recovered.TryGetVertex(out var alice, 0));
            Assert.IsTrue(alice.TryGetOutEdge(out var outEdges, "knows"));
            Assert.AreEqual(1, outEdges.Count);
            Assert.AreEqual(3, outEdges[0].Id);
            Assert.AreEqual(2, outEdges[0].TargetVertex.Id, "The replayed edge points at Carol.");
            Assert.IsTrue(recovered.TryGetVertex(out var carolReloaded, 2));
            Assert.AreEqual(1u, carolReloaded.GetInDegree(), "Carol has the replayed incoming edge.");

            recovered.Dispose();
        }

        [TestMethod]
        public void CrashBetweenSnapshots_RecoversRemovalsAndPropertyAdditions()
        {
            var source = NewEngineWithWal();
            var v = AddVertices(source, ("person", "Alice"), ("person", "Bob"), ("person", "Carol")); // 0,1,2
            var actualPath = Save(source, SavePath);

            // Post-snapshot: remove Bob, add a property to Alice, create Dave.
            source.EnqueueTransaction(new RemoveGraphElementTransaction { GraphElementId = v[1].Id }).WaitUntilFinished();
            source.EnqueueTransaction(new AddPropertyTransaction
            {
                Definition = new PropertyAddDefinition { GraphElementId = v[0].Id, PropertyId = "age", Property = 30 }
            }).WaitUntilFinished();
            var dave = AddVertices(source, ("person", "Dave"))[0]; // id 3
            Assert.AreEqual(3, dave.Id);

            source.Dispose();

            var recovered = NewEngineWithWal();
            var (state, error) = Load(recovered, actualPath);

            Assert.AreEqual(TransactionState.Finished, state, "Recovery load should succeed; instead: " + error);
            Assert.AreEqual(3, recovered.VertexCount, "Bob removed, Dave added: 3 live vertices.");
            Assert.AreEqual(0, CountWithName(recovered, "Bob"), "The post-snapshot removal is replayed.");
            Assert.AreEqual(1, CountWithName(recovered, "Dave"), "The post-snapshot creation is replayed.");
            Assert.IsFalse(recovered.TryGetVertex(out _, 1), "Bob's id is no longer live.");

            Assert.IsTrue(recovered.TryGetVertex(out var alice, 0));
            Assert.IsTrue(alice.TryGetProperty(out int age, "age"), "Alice's replayed property is present.");
            Assert.AreEqual(30, age);

            recovered.Dispose();
        }

        [TestMethod]
        public void CrashBetweenSnapshots_RecoversMixedPropertyValueTypes()
        {
            var source = NewEngineWithWal();
            AddVertices(source, ("person", "Alice")); // id 0
            var actualPath = Save(source, SavePath);

            // A vertex created only in the WAL, carrying several primitive property value types.
            var tx = new CreateVerticesTransaction();
            tx.AddVertex(1u, "typed", new Dictionary<string, object>
            {
                { "i", 42 },
                { "s", "hello" },
                { "b", true },
                { "d", 3.5d }
            });
            source.EnqueueTransaction(tx).WaitUntilFinished();

            source.Dispose();

            var recovered = NewEngineWithWal();
            Load(recovered, actualPath);

            Assert.IsTrue(recovered.TryGetVertex(out var typed, 1), "The typed vertex recovers at its id.");
            Assert.IsTrue(typed.TryGetProperty(out int i, "i"));
            Assert.AreEqual(42, i);
            Assert.IsTrue(typed.TryGetProperty(out string s, "s"));
            Assert.AreEqual("hello", s);
            Assert.IsTrue(typed.TryGetProperty(out bool b, "b"));
            Assert.IsTrue(b);
            Assert.IsTrue(typed.TryGetProperty(out double d, "d"));
            Assert.AreEqual(3.5d, d);

            recovered.Dispose();
        }

        [TestMethod]
        public void RemoveHighestIdThenSnapshot_ThenCreate_RecoversWithIdSpacePreserved()
        {
            // Exercises the baseline/padding path: soft-removing the HIGHEST id before the snapshot
            // makes the snapshot's id-space size (max live id + 1) SMALLER than the writer's
            // _currentId. Recovery must still re-assign the post-snapshot id correctly.
            var source = NewEngineWithWal();
            var v = AddVertices(source, ("person", "Alice"), ("person", "Bob"), ("person", "Carol")); // 0,1,2
            // Remove the highest id (Carol, 2) WITHOUT trimming: _currentId stays 3, but max live id is 1.
            source.EnqueueTransaction(new RemoveGraphElementTransaction { GraphElementId = v[2].Id }).WaitUntilFinished();

            var actualPath = Save(source, SavePath); // snapshot id-space size = 2, but true _currentId = 3

            var dave = AddVertices(source, ("person", "Dave"))[0]; // must get id 3, not 2
            Assert.AreEqual(3, dave.Id);

            source.Dispose();

            var recovered = NewEngineWithWal();
            var (state, error) = Load(recovered, actualPath);

            Assert.AreEqual(TransactionState.Finished, state, "Recovery load should succeed; instead: " + error);
            Assert.AreEqual(3, recovered.VertexCount, "Alice, Bob and the replayed Dave.");
            Assert.AreEqual("Alice", NameOf(recovered, 0));
            Assert.AreEqual("Bob", NameOf(recovered, 1));
            Assert.IsFalse(recovered.TryGetVertex(out _, 2), "The removed Carol's id stays a gap.");
            Assert.AreEqual("Dave", NameOf(recovered, 3), "Dave recovers at its original id (3), not the snapshot's next free id (2).");

            recovered.Dispose();
        }

        #endregion

        #region compose with snapshot truncation (no double-apply)

        [TestMethod]
        public void SecondSnapshotTruncatesLog_RecoveryDoesNotDoubleApplyPreSnapshotWork()
        {
            var source = NewEngineWithWal();
            AddVertices(source, ("person", "Alice"), ("person", "Bob")); // 0,1
            Save(source, SavePath); // snapshot #1 (A,B); log reset

            AddVertices(source, ("person", "Carol")); // id 2, in the log
            var secondPath = Save(source, SavePath); // snapshot #2 (A,B,C); log reset again -> Carol superseded
            Assert.AreNotEqual(SavePath, secondPath, "A second save must not overwrite the first.");

            AddVertices(source, ("person", "Dave")); // id 3, in the log (post snapshot #2)

            source.Dispose();

            // Recover from the NEWEST snapshot: Carol comes from the snapshot (NOT re-applied from a
            // stale log entry), Dave is replayed from the log.
            var recovered = NewEngineWithWal();
            var (state, error) = Load(recovered, secondPath);

            Assert.AreEqual(TransactionState.Finished, state, "Recovery load should succeed; instead: " + error);
            Assert.AreEqual(4, recovered.VertexCount, "Exactly A,B,C,D - no duplicate Carol from a stale log entry.");
            Assert.AreEqual(1, CountWithName(recovered, "Carol"), "Carol must not be double-applied.");
            Assert.AreEqual(1, CountWithName(recovered, "Dave"));
            Assert.AreEqual("Carol", NameOf(recovered, 2));
            Assert.AreEqual("Dave", NameOf(recovered, 3));

            recovered.Dispose();
        }

        [TestMethod]
        public void CleanReloadWithWalEnabled_AfterSaveWithNoPostSnapshotWork_RoundTrips()
        {
            // The snapshot reset leaves an empty (entry-less) log paired with the snapshot; a reload
            // then replays nothing and yields exactly the snapshot state.
            var source = NewEngineWithWal();
            AddVertices(source, ("person", "Alice"), ("person", "Bob"), ("person", "Carol"));
            var actualPath = Save(source, SavePath);
            source.Dispose();

            var recovered = NewEngineWithWal();
            var (state, _) = Load(recovered, actualPath);

            Assert.AreEqual(TransactionState.Finished, state);
            Assert.AreEqual(3, recovered.VertexCount);
            recovered.Dispose();
        }

        [TestMethod]
        public void LoadSnapshotTheLogDoesNotPairWith_ReAnchorsLog_AndSubsequentWorkRecovers()
        {
            // Bootstrap flow: a snapshot produced elsewhere is loaded into a WAL-enabled engine whose
            // (fresh) log does not pair with it. The load must re-anchor the log to that snapshot, and
            // work done afterwards must then be recoverable by reloading the same snapshot.
            var producer = new Fallen8(_loggerFactory); // WAL-disabled producer
            AddVertices(producer, ("person", "Alice"), ("person", "Bob")); // 0,1
            var snapshotPath = Save(producer, SavePath);
            producer.Dispose();

            var engine = NewEngineWithWal(); // fresh, unanchored log
            var (state, error) = Load(engine, snapshotPath);
            Assert.AreEqual(TransactionState.Finished, state, "Bootstrap load should succeed; instead: " + error);
            Assert.AreEqual(2, engine.VertexCount);

            var carol = AddVertices(engine, ("person", "Carol"))[0]; // id 2, logged against the re-anchored snapshot
            Assert.AreEqual(2, carol.Id);
            engine.Dispose();

            var recovered = NewEngineWithWal();
            var (state2, error2) = Load(recovered, snapshotPath);
            Assert.AreEqual(TransactionState.Finished, state2, "Recovery load should succeed; instead: " + error2);
            Assert.AreEqual(3, recovered.VertexCount, "Alice, Bob (snapshot) and the replayed Carol.");
            Assert.AreEqual("Carol", NameOf(recovered, 2));
            recovered.Dispose();
        }

        #endregion

        #region unanchored log (recovery with no snapshot at all)

        [TestMethod]
        public void UnanchoredLog_RecoversCommittedTransactions_WithoutAnySnapshot()
        {
            var source = NewEngineWithWal();
            AddVertices(source, ("person", "Alice"), ("person", "Bob")); // never saved
            var edge = AddEdge(source, 0, "knows", 1, "knows"); // id 2
            Assert.AreEqual(2, edge.Id);
            source.Dispose();

            // A fresh engine on the same log replays the unanchored entries on open (no Load needed).
            var recovered = NewEngineWithWal();
            Assert.AreEqual(2, recovered.VertexCount, "Both vertices recover from the unanchored log.");
            Assert.AreEqual(1, recovered.EdgeCount, "The edge recovers from the unanchored log.");
            Assert.AreEqual("Alice", NameOf(recovered, 0));
            Assert.AreEqual("Bob", NameOf(recovered, 1));
            Assert.IsTrue(recovered.TryGetVertex(out var alice, 0));
            Assert.AreEqual(1u, alice.GetOutDegree());
            recovered.Dispose();
        }

        #endregion

        #region corrupt / torn tail

        [TestMethod]
        public void TornTail_TruncatedMidLastEntry_RecoversAllCompleteEntries()
        {
            var source = NewEngineWithWal();
            AddVertices(source, ("person", "Alice")); // entry 1
            AddVertices(source, ("person", "Bob"));   // entry 2
            AddVertices(source, ("person", "Carol")); // entry 3 (to be torn)
            source.Dispose();

            // Truncate a few bytes off the end: the last entry's frame is now incomplete.
            using (var fs = new FileStream(WalPath, FileMode.Open, FileAccess.Write))
            {
                fs.SetLength(fs.Length - 5);
            }

            var recovered = NewEngineWithWal();
            Assert.AreEqual(2, recovered.VertexCount, "The two complete entries recover; the torn tail is ignored.");
            Assert.AreEqual("Alice", NameOf(recovered, 0));
            Assert.AreEqual("Bob", NameOf(recovered, 1));
            Assert.AreEqual(0, CountWithName(recovered, "Carol"), "The torn last entry is not applied.");
            recovered.Dispose();
        }

        [TestMethod]
        public void TornTail_CorruptLastEntryCrc_RecoversAllCompleteEntries()
        {
            var source = NewEngineWithWal();
            AddVertices(source, ("person", "Alice"));
            AddVertices(source, ("person", "Bob"));
            AddVertices(source, ("person", "Carol")); // last entry - its trailing CRC byte gets flipped
            source.Dispose();

            FlipWalByteAt(WalLength - 1);

            var recovered = NewEngineWithWal();
            Assert.AreEqual(2, recovered.VertexCount, "The CRC-failing last entry is ignored; the rest recover.");
            Assert.AreEqual(0, CountWithName(recovered, "Carol"));
            recovered.Dispose();
        }

        [TestMethod]
        public void TornTail_BogusHugeLengthPrefix_StopsWithoutHugeAllocationOrThrow()
        {
            var source = NewEngineWithWal();
            AddVertices(source, ("person", "Alice"));
            AddVertices(source, ("person", "Bob"));
            source.Dispose();

            // Append a lone, un-followed length prefix claiming ~2 GB. Replay must reject it against
            // the bytes actually remaining (there are none) rather than attempting a 2 GB allocation.
            File.AppendAllText(WalPath, string.Empty); // ensure it exists
            using (var fs = new FileStream(WalPath, FileMode.Append, FileAccess.Write))
            {
                var bogus = BitConverter.GetBytes(int.MaxValue);
                fs.Write(bogus, 0, bogus.Length);
            }

            var recovered = NewEngineWithWal();
            Assert.AreEqual(2, recovered.VertexCount, "The two complete entries recover; the bogus prefix is ignored.");
            recovered.Dispose();
        }

        #endregion

        #region commits after a recovery (feature wal-torn-tail)

        // Reading a torn tail correctly is half of recovery. The other half is what happens to the
        // NEXT commit: appended behind bytes the reader stops at, it is acknowledged as durable and
        // then never replayed. These tests restart twice, because the defect is invisible after one.

        [TestMethod]
        public void TornTail_CommitsAfterRecovery_SurviveTheNextRestart()
        {
            long beforeTornEntry, afterTornEntry;
            using (var source = NewEngineWithWal())
            {
                AddVertices(source, ("person", "Alice"));
                AddVertices(source, ("person", "Bob"));
                AddVertices(source, ("person", "Carol"));
                beforeTornEntry = WalLength;
                AddVertices(source, ("person", "Dave")); // the entry a crash will cut through
                afterTornEntry = WalLength;
            }
            CutWalTo(beforeTornEntry + (afterTornEntry - beforeTornEntry) / 2);

            using var sink = new TestLogSink();
            using (var recovered = new Fallen8(sink.CreateFactory(), new WriteAheadLogOptions(WalPath)))
            {
                Assert.AreEqual(3, recovered.VertexCount, "The complete entries recover; the torn one is not applied.");
                Assert.AreEqual(beforeTornEntry, WalLength, "The torn bytes are cut, back to exactly the last complete entry.");
                Assert.IsFalse(recovered.Durability.Degraded, "A torn tail is the normal crash artifact, not a degraded log.");
                Assert.IsFalse(recovered.Durability.LastRecoveryTruncated,
                    "The torn entry was never acknowledged, so the recovered history is complete.");
                Assert.IsTrue(sink.Contains(LogLevel.Warning, "incomplete final entry"),
                    "The cut is logged: " + String.Join(" | ", sink.Entries.Select(e => e.Message)));

                AddVertices(recovered, out var eve, ("person", "Eve"));
                AddVertices(recovered, out var frank, ("person", "Frank"));
                Assert.IsTrue(eve.Durable && frank.Durable, "Both commits are acknowledged as durable.");
            }

            using var restarted = NewEngineWithWal();
            Assert.AreEqual(5, restarted.VertexCount, "Every commit acknowledged as durable is there after the next restart.");
            Assert.AreEqual(1, CountWithName(restarted, "Eve"));
            Assert.AreEqual(1, CountWithName(restarted, "Frank"));
            Assert.AreEqual(0, CountWithName(restarted, "Dave"), "The torn entry stays gone.");
        }

        [TestMethod]
        public void TornTail_FragmentShorterThanAnEntryHeader_IsCutBeforeTheNextCommit()
        {
            long afterAlice;
            using (var source = NewEngineWithWal())
            {
                AddVertices(source, ("person", "Alice"));
                afterAlice = WalLength;
                AddVertices(source, ("person", "Bob"));
            }
            CutWalTo(afterAlice + 3); // three bytes of Bob's entry: not even its length prefix

            using (var recovered = NewEngineWithWal())
            {
                Assert.AreEqual(1, recovered.VertexCount);
                Assert.AreEqual(afterAlice, WalLength, "The fragment is cut.");
                AddVertices(recovered, out var carol, ("person", "Carol"));
                Assert.IsTrue(carol.Durable);
            }

            using var restarted = NewEngineWithWal();
            Assert.AreEqual(2, restarted.VertexCount);
            Assert.AreEqual(1, CountWithName(restarted, "Carol"), "The commit after the cut survives.");
        }

        [TestMethod]
        public void TornTail_LastEntryOfFullLengthWithABadCrc_IsCutAsAnInterruptedWrite()
        {
            // A cut-off write can leave a frame of full length whose bytes did not all reach the disk
            // (sectors land out of order on a power loss). At the very end of the log that is a torn
            // write, not corruption of acknowledged history.
            long afterBob;
            using (var source = NewEngineWithWal())
            {
                AddVertices(source, ("person", "Alice"));
                AddVertices(source, ("person", "Bob"));
                afterBob = WalLength;
                AddVertices(source, ("person", "Carol"));
            }
            FlipWalByteAt(afterBob + (WalLength - afterBob) / 2);

            using (var recovered = NewEngineWithWal())
            {
                Assert.AreEqual(2, recovered.VertexCount);
                Assert.AreEqual(afterBob, WalLength, "The bad last entry is cut.");
                Assert.IsFalse(recovered.Durability.Degraded);
                Assert.IsFalse(recovered.Durability.LastRecoveryTruncated);
                AddVertices(recovered, out var dave, ("person", "Dave"));
                Assert.IsTrue(dave.Durable);
            }

            using var restarted = NewEngineWithWal();
            Assert.AreEqual(3, restarted.VertexCount);
            Assert.AreEqual(1, CountWithName(restarted, "Dave"));
            Assert.AreEqual(0, CountWithName(restarted, "Carol"));
        }

        [TestMethod]
        public void TornTail_AfterAPairedLoad_CommitsSurviveTheNextRestart()
        {
            // The snapshot-paired path replays at Load, not at construction; the cut must happen there too.
            string snapshot;
            long afterBob;
            using (var source = NewEngineWithWal())
            {
                AddVertices(source, ("person", "Alice"));
                snapshot = Save(source, SavePath);
                AddVertices(source, ("person", "Bob"));
                afterBob = WalLength;
                AddVertices(source, ("person", "Carol"));
            }
            CutWalTo(afterBob + (WalLength - afterBob) / 2);

            using (var recovered = NewEngineWithWal())
            {
                Assert.AreEqual(TransactionState.Finished, Load(recovered, snapshot).State);
                Assert.AreEqual(2, recovered.VertexCount, "Alice from the snapshot, Bob from the log.");
                Assert.AreEqual(afterBob, WalLength, "The torn entry is cut at the paired load.");
                AddVertices(recovered, out var dave, ("person", "Dave"));
                Assert.IsTrue(dave.Durable);
            }

            using var restarted = NewEngineWithWal();
            Assert.AreEqual(TransactionState.Finished, Load(restarted, snapshot).State);
            Assert.AreEqual(3, restarted.VertexCount);
            Assert.AreEqual(1, CountWithName(restarted, "Dave"), "The commit after the paired recovery survives.");
            Assert.AreEqual(0, CountWithName(restarted, "Carol"));
        }

        [TestMethod]
        public void UnreadableEntryInsideTheLog_IsLeftInPlace_FencesTheLog_AndReportsTruncatedHistory()
        {
            // An entry that fails its CRC with more entries after it is not a torn write: acknowledged
            // history after it cannot be read. Cutting would destroy it; appending behind it would
            // repeat the defect. So the bytes stay, the log is fenced until a save, and the recovery
            // says that the graph is a prefix of what was committed.
            string snapshot = null;
            long afterAlice, afterBob;
            using (var source = NewEngineWithWal())
            {
                AddVertices(source, ("person", "Alice"));
                afterAlice = WalLength;
                AddVertices(source, ("person", "Bob"));
                afterBob = WalLength;
                AddVertices(source, ("person", "Carol"));
            }
            FlipWalByteAt(afterAlice + (afterBob - afterAlice) / 2);
            var damaged = File.ReadAllBytes(WalPath);

            using (var recovered = NewEngineWithWal())
            {
                Assert.AreEqual(1, recovered.VertexCount, "Replay stops at the unreadable entry.");
                CollectionAssert.AreEqual(damaged, File.ReadAllBytes(WalPath), "Nothing is cut or rewritten.");
                Assert.IsTrue(recovered.Durability.LastRecoveryTruncated, "Acknowledged history was not replayed, and the flag says so.");
                Assert.IsTrue(recovered.Durability.Degraded, "The log is fenced.");

                AddVertices(recovered, out var dave, ("person", "Dave"));
                Assert.IsFalse(dave.Durable, "A commit behind bytes replay cannot pass is not claimed durable.");

                snapshot = Save(recovered, SavePath);
                Assert.IsFalse(recovered.Durability.Degraded, "A save re-baselines the log and lifts the fence.");
                AddVertices(recovered, out var eve, ("person", "Eve"));
                Assert.IsTrue(eve.Durable, "After the save, commits are durable again.");
            }

            using var restarted = NewEngineWithWal();
            Assert.AreEqual(TransactionState.Finished, Load(restarted, snapshot).State);
            Assert.AreEqual(3, restarted.VertexCount, "Alice and Dave from the snapshot, Eve from the log.");
            Assert.AreEqual(1, CountWithName(restarted, "Dave"));
            Assert.AreEqual(1, CountWithName(restarted, "Eve"));
        }

        [TestMethod]
        public void NegativeLengthInsideTheLog_IsLeftInPlace_AndFencesTheLog()
        {
            // An entry starts with its little-endian Int32 length, so its fourth byte is the high byte:
            // inverting it makes the length negative, which no writer produces. That is damage, not a
            // tear, even though nothing after it can be framed.
            long afterAlice;
            using (var source = NewEngineWithWal())
            {
                AddVertices(source, ("person", "Alice"));
                afterAlice = WalLength;
                AddVertices(source, ("person", "Bob"));
                AddVertices(source, ("person", "Carol"));
            }
            FlipWalByteAt(afterAlice + 3);
            var damaged = File.ReadAllBytes(WalPath);

            using var recovered = NewEngineWithWal();
            Assert.AreEqual(1, recovered.VertexCount);
            CollectionAssert.AreEqual(damaged, File.ReadAllBytes(WalPath), "Nothing is cut.");
            Assert.IsTrue(recovered.Durability.LastRecoveryTruncated);
            Assert.IsTrue(recovered.Durability.Degraded);
        }

        [TestMethod]
        public void ReplayThatStopsAtAnEntryItCannotApply_FencesTheLog()
        {
            // A CRC-valid entry that fails on replay (here: an edge to a vertex that does not exist,
            // spliced in from another log) stops recovery. Every entry after it is unreplayable, and
            // so is any commit appended behind it.
            var otherWal = Path.Combine(_temp.FullName, "other.f8s.wal");
            byte[] edgeEntry;
            using (var other = new Fallen8(_loggerFactory, new WriteAheadLogOptions(otherWal)))
            {
                AddVertices(other, ("person", "Ann"), ("person", "Ben")); // ids 0 and 1
                var beforeEdge = new FileInfo(otherWal).Length;
                AddEdge(other, 0, "knows", 1, "friendship");
                edgeEntry = File.ReadAllBytes(otherWal).Skip((int)beforeEdge).ToArray();
            }
            using (var source = NewEngineWithWal())
            {
                AddVertices(source, ("person", "Cid")); // id 0, and no id 1
            }
            using (var fs = new FileStream(WalPath, FileMode.Append, FileAccess.Write))
            {
                fs.Write(edgeEntry, 0, edgeEntry.Length);
            }

            using (var recovered = NewEngineWithWal())
            {
                Assert.AreEqual(1, recovered.VertexCount);
                Assert.AreEqual(0, recovered.EdgeCount, "The edge cannot be applied.");
                Assert.IsTrue(recovered.Durability.LastRecoveryTruncated);
                Assert.IsTrue(recovered.Durability.Degraded, "The log is fenced after a replay that stopped early.");

                AddVertices(recovered, out var dot, ("person", "Dot"));
                Assert.IsFalse(dot.Durable, "A commit behind the entry replay stops at is not claimed durable.");
            }

            using var restarted = NewEngineWithWal();
            Assert.AreEqual(0, CountWithName(restarted, "Dot"), "Reported non-durable, and indeed not recovered: honest, not lost.");
        }

        [TestMethod]
        public void TornTail_WhenTheCutFails_TheLogIsFencedInsteadOfAppendedBehindTheTear()
        {
            long afterAlice;
            using (var source = NewEngineWithWal())
            {
                AddVertices(source, ("person", "Alice"));
                afterAlice = WalLength;
                AddVertices(source, ("person", "Bob"));
            }
            CutWalTo(afterAlice + 5);

            File.SetAttributes(WalPath, FileAttributes.ReadOnly); // the cut cannot be written
            try
            {
                using var recovered = NewEngineWithWal();
                Assert.AreEqual(1, recovered.VertexCount);
                Assert.IsTrue(recovered.Durability.Degraded, "A tear that could not be cut fences the log.");
                Assert.IsFalse(recovered.Durability.LastRecoveryTruncated, "Nothing acknowledged was lost.");

                File.SetAttributes(WalPath, FileAttributes.Normal); // the disk 'recovers'
                AddVertices(recovered, out var carol, ("person", "Carol"));
                Assert.IsFalse(carol.Durable, "Without the cut, an append would land behind the tear: not durable.");
            }
            finally
            {
                File.SetAttributes(WalPath, FileAttributes.Normal);
            }
        }

        #endregion

        #region WAL <-> snapshot pairing: path normalization + non-pairing discard warning

        [TestMethod]
        public void LoadSameSnapshotViaRelativePathSegmentVariant_StillReplaysPostSnapshotEntries()
        {
            // The WAL pairs with its snapshot by a CANONICALIZED path token, not a raw ordinal match.
            // A non-verbatim reload of the SAME snapshot (here: an inserted "./" segment) must still
            // pair, so the post-snapshot entries REPLAY - a raw match would treat it as "non-pairing"
            // and silently discard committed-since-snapshot work.
            var source = NewEngineWithWal();
            AddVertices(source, ("person", "Alice"), ("person", "Bob")); // 0,1
            var actualPath = Save(source, SavePath);
            Assert.AreEqual(SavePath, actualPath, "The first save uses the base path.");

            var carol = AddVertices(source, ("person", "Carol"))[0]; // id 2, in the WAL only
            Assert.AreEqual(2, carol.Id);
            source.Dispose();

            // Same snapshot, addressed via a file-system-equivalent variant: an extra "." segment.
            // Path.GetFullPath canonicalizes this back to SavePath regardless of the current directory
            // (the path is absolute), so the test is deterministic and cross-platform.
            var variantPath = Path.Combine(_temp.FullName, ".", "savegame.f8s");
            Assert.AreNotEqual(SavePath, variantPath, "Sanity: the variant is not byte-identical to the save path.");

            var recovered = NewEngineWithWal();
            var (state, error) = Load(recovered, variantPath);

            Assert.AreEqual(TransactionState.Finished, state, "Recovery via a path variant should succeed; instead: " + error);
            Assert.AreEqual(3, recovered.VertexCount,
                "Alice, Bob (snapshot) and the replayed Carol - the post-snapshot entry is NOT lost to a path-variant mismatch.");
            Assert.AreEqual("Carol", NameOf(recovered, 2), "Carol recovers at her original id.");
            recovered.Dispose();
        }

        [TestMethod]
        public void LoadSameSnapshotViaCaseVariant_OnWindows_StillReplaysPostSnapshotEntries()
        {
            // Windows/macOS resolve paths case-insensitively, so the pairing comparison is
            // OrdinalIgnoreCase there. This pins that a case variant of the same snapshot path still
            // pairs (and replays) rather than being discarded as non-pairing. Skipped where the file
            // system is case-sensitive (the variant would not even resolve to the same file).
            if (!OperatingSystem.IsWindows())
            {
                Assert.Inconclusive("Case-insensitive path pairing is only exercised on a case-insensitive file system (Windows).");
                return;
            }

            var source = NewEngineWithWal();
            AddVertices(source, ("person", "Alice"), ("person", "Bob"));
            var actualPath = Save(source, SavePath);
            var carol = AddVertices(source, ("person", "Carol"))[0]; // id 2, in the WAL only
            Assert.AreEqual(2, carol.Id);
            source.Dispose();

            var caseVariant = actualPath.ToUpperInvariant();
            Assert.AreNotEqual(actualPath, caseVariant, "Sanity: the case variant differs from the save path.");

            var recovered = NewEngineWithWal();
            var (state, error) = Load(recovered, caseVariant);

            Assert.AreEqual(TransactionState.Finished, state, "Recovery via a case variant should succeed; instead: " + error);
            Assert.AreEqual(3, recovered.VertexCount, "The post-snapshot entry is not lost to a case-variant mismatch.");
            Assert.AreEqual("Carol", NameOf(recovered, 2));
            recovered.Dispose();
        }

        [TestMethod]
        public void LoadGenuinelyDifferentSnapshot_DiscardsUnpairedLogEntries_WithWarning()
        {
            // A log paired with snapshot A and STILL HOLDING committed entries, loaded against a
            // genuinely DIFFERENT snapshot B: those entries belong to A's id space and legitimately
            // cannot replay onto B, so they are discarded - but never silently. A warning must fire.
            var source = NewEngineWithWal();
            AddVertices(source, ("person", "Alice"), ("person", "Bob")); // 0,1
            Save(source, SavePath);                                       // snapshot A; log paired with A
            AddVertices(source, ("person", "Carol"));                     // id 2, in the WAL (paired with A)
            source.Dispose();

            // A different snapshot B, produced independently (different content + engine id).
            var otherPath = Path.Combine(_temp.FullName, "other.f8s");
            var producer = new Fallen8(_loggerFactory);
            AddVertices(producer, ("robot", "Zed"));                      // id 0 in B
            var actualOther = Save(producer, otherPath);
            producer.Dispose();

            // Recover with a capturing logger so we can assert the warning fires.
            var capture = new TestLogSink();
            var recovered = new Fallen8(capture.CreateFactory(), new WriteAheadLogOptions(WalPath));
            var (state, error) = Load(recovered, actualOther);

            Assert.AreEqual(TransactionState.Finished, state, "Loading a different snapshot should still succeed; instead: " + error);
            Assert.AreEqual(1, recovered.VertexCount,
                "Only snapshot B's content - the A-paired log entries are discarded, not replayed.");
            Assert.AreEqual(0, CountWithName(recovered, "Carol"), "Carol (an A-paired log entry) must NOT be applied onto snapshot B.");
            Assert.AreEqual(1, CountWithName(recovered, "Zed"), "Snapshot B's own content loads.");

            Assert.IsTrue(capture.Contains(LogLevel.Warning, "does not pair"),
                "Discarding a non-pairing log that still holds committed entries must be signalled with a warning, never silent.");
            recovered.Dispose();
        }

        #endregion
    }
}
