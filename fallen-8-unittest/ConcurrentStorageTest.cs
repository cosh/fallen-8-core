// MIT License
//
// ConcurrentStorageTest.cs
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
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Guardrail tests for the lock-free-reader / single-writer storage contract
    /// (see features/core-storage-representation). These encode the invariant that must never
    /// break: while the single TransactionManager thread appends/removes/trims graph elements,
    /// concurrent readers that capture a snapshot must NEVER observe a torn or half-published
    /// element, a null slot within the published range, an id != index element, nor throw an
    /// out-of-range/null-reference exception. They must pass unchanged both before and after any
    /// change to the underlying storage representation.
    ///
    /// Readers run on dedicated background threads (not the thread pool) so they cannot starve
    /// the pool threads the TransactionManager uses to execute transactions.
    /// </summary>
    [TestClass]
    public class ConcurrentStorageTest
    {
        private const string VertexLabel = "person";
        private const string EdgeLabel = "knows";
        private const string EdgePropertyId = "friend";

        // Per outer reader pass, number of tight O(1) frontier probes. Large so readers watch the
        // newest slot near-continuously and reliably observe a torn append; the pass is still cheap
        // (each probe is three field reads + an array index) and the test stays writer-bound.
        private const int FrontierWatchBurst = 200_000;

        private static int ReaderCount => Math.Max(4, Environment.ProcessorCount);

        // Low-latency accessors into the engine's private segmented master store, built once. The
        // engine keeps the store behind a private volatile Snapshot holder and declares no
        // InternalsVisibleTo, so this suite reaches it by reflection rather than widening visibility
        // (the same convention as SpatialIndexTest / JsonSourceGenParityTest). Plain
        // FieldInfo.GetValue proved too slow for the strict no-null-slot check: the reflective read
        // sitting between capturing Count and reading the frontier slot let the single writer always
        // fill the torn slot first (the window is only a few ns), so the check could not observe it.
        // Skip-visibility DynamicMethod getters read a field in a few ns, keeping the Count->slot gap
        // down to a plain array index so the race is winnable. See AssertPublishedRangeHasNoNullSlot.
        private static readonly Func<Fallen8, object> ReadSnapshot;
        private static readonly Func<object, int> ReadSnapshotCount;
        private static readonly Func<object, AGraphElementModel[][]> ReadSnapshotSegments;

        static ConcurrentStorageTest()
        {
            var snapshotField = typeof(Fallen8).GetField("_snapshot", BindingFlags.NonPublic | BindingFlags.Instance);
            var snapshotType = snapshotField.FieldType;
            var countField = snapshotType.GetField("Count", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            var segmentsField = snapshotType.GetField("Segments", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

            ReadSnapshot = BuildFieldGetter<Fallen8, object>(snapshotField);
            ReadSnapshotCount = BuildFieldGetter<object, int>(countField);
            ReadSnapshotSegments = BuildFieldGetter<object, AGraphElementModel[][]>(segmentsField);
        }

        private static Func<TInstance, TField> BuildFieldGetter<TInstance, TField>(FieldInfo field)
        {
            var dm = new DynamicMethod(
                "read_" + field.Name,
                typeof(TField),
                new[] { typeof(TInstance) },
                typeof(Fallen8).Module,
                skipVisibility: true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            if (typeof(TInstance) != field.DeclaringType)
            {
                il.Emit(OpCodes.Castclass, field.DeclaringType);
            }
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ret);
            return (Func<TInstance, TField>)dm.CreateDelegate(typeof(Func<TInstance, TField>));
        }

        private ILoggerFactory _loggerFactory;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
        }

        /// <summary>
        /// Many readers hammer TryGet*/scans while the single writer appends thousands of
        /// vertices and edges one small transaction at a time (maximising the number of
        /// publish boundaries, i.e. race windows). Any resolved element must be fully
        /// published and satisfy id == index.
        /// </summary>
        [TestMethod]
        public void ConcurrentReaders_DuringSingleWriterAppends_NeverSeeTornNullOrOutOfRange()
        {
            var fallen8 = new Fallen8(_loggerFactory);

            const int vertexTarget = 4000;
            const int edgeTarget = 4000;

            var errors = new ConcurrentQueue<Exception>();
            int writerDone = 0;

            var readers = StartReaders(() =>
            {
                var rng = new Random(Thread.CurrentThread.ManagedThreadId * 7919 + Environment.TickCount);
                while (Volatile.Read(ref writerDone) == 0)
                {
                    // Probe across the moving frontier so ids just below, at, and beyond the
                    // published count are all exercised.
                    int upper = Math.Max(1, fallen8.VertexCount + fallen8.EdgeCount + 2);
                    for (var k = 0; k < 256; k++)
                    {
                        int id = rng.Next(0, upper);

                        // TryGetGraphElement on any id must never throw and, when it resolves,
                        // must return the element whose Id equals the requested index.
                        AGraphElementModel ge;
                        if (fallen8.TryGetGraphElement(out ge, id))
                        {
                            Assert.IsNotNull(ge, "TryGetGraphElement returned true with a null element (null slot).");
                            Assert.AreEqual(id, ge.Id, "id == index invariant violated (master store).");
                        }

                        VertexModel v;
                        if (fallen8.TryGetVertex(out v, id))
                        {
                            Assert.IsNotNull(v, "TryGetVertex returned true with a null vertex (null slot).");
                            Assert.AreEqual(id, v.Id, "id == index invariant violated for a vertex.");
                            Assert.AreEqual(VertexLabel, v.Label, "Torn read: unexpected vertex label.");
                            int seq;
                            Assert.IsTrue(v.TryGetProperty(out seq, "seq"),
                                "Torn read: published vertex is missing its 'seq' property.");
                            Assert.AreEqual(id, seq, "Torn read: 'seq' property does not match the id.");
                        }

                        EdgeModel e;
                        if (fallen8.TryGetEdge(out e, id))
                        {
                            Assert.IsNotNull(e, "TryGetEdge returned true with a null edge (null slot).");
                            Assert.AreEqual(id, e.Id, "id == index invariant violated for an edge.");
                            Assert.AreEqual(EdgeLabel, e.Label, "Torn read: unexpected edge label.");
                            Assert.IsNotNull(e.SourceVertex, "Torn read: edge has a null source vertex.");
                            Assert.IsNotNull(e.TargetVertex, "Torn read: edge has a null target vertex.");
                        }
                    }

                    // A captured snapshot must be internally consistent for its whole duration.
                    // (GetAllVertices pre-filters nulls, so the meaningful signal is the label, not
                    // a null check the scan can never surface.)
                    var vertexSnapshot = fallen8.GetAllVertices(VertexLabel);
                    foreach (var vertex in vertexSnapshot)
                    {
                        Assert.AreEqual(VertexLabel, vertex.Label, "GetAllVertices snapshot contained a wrong-label element.");
                    }

                    // The strict invariant TryGet*/GetAll* cannot surface: no null slot within the
                    // published range [0, Count). A torn append (Count bumped before the slot write)
                    // would leave a live index transiently null here.
                    AssertPublishedRangeHasNoNullSlot(fallen8);

                    // Watch the append frontier tightly. A torn publish leaves the NEWEST slot
                    // transiently null (until the slot write becomes visible on this core); the
                    // heavier probes above only sample it rarely, so keep readers reading the
                    // frontier near-continuously. This is what makes a reintroduced Count-before-slot
                    // inversion (or a dropped `volatile`, on weak-memory hardware) reliably FAIL here
                    // rather than slip through on a lucky interleaving.
                    for (var h = 0; h < FrontierWatchBurst && Volatile.Read(ref writerDone) == 0; h++)
                    {
                        AssertFrontierSlotNotNull(fallen8);
                    }
                }
            }, errors);

            try
            {
                // Single writer: append vertices, then edges, one small transaction each. The
                // transactions queue up and are drained back-to-back by the single worker thread.
                TransactionInformation last = null;
                for (var i = 0; i < vertexTarget; i++)
                {
                    var tx = new CreateVerticesTransaction();
                    tx.AddVertex(1u, VertexLabel, new Dictionary<string, object> { { "seq", i } });
                    last = fallen8.EnqueueTransaction(tx);
                }
                last.WaitUntilFinished();

                var edgeRng = new Random(1234);
                for (var j = 0; j < edgeTarget; j++)
                {
                    int source = edgeRng.Next(0, vertexTarget);
                    int target = edgeRng.Next(0, vertexTarget);
                    var tx = new CreateEdgesTransaction();
                    tx.AddEdge(source, EdgePropertyId, target, 1u, EdgeLabel);
                    last = fallen8.EnqueueTransaction(tx);
                }
                last.WaitUntilFinished();
            }
            finally
            {
                Volatile.Write(ref writerDone, 1);
            }

            JoinReaders(readers);
            AssertNoErrors(errors);

            Assert.AreEqual(vertexTarget, fallen8.VertexCount, "All vertices must be present at the end.");
            Assert.AreEqual(edgeTarget, fallen8.EdgeCount, "All edges must be present at the end.");

            // Post-hoc (no longer racy): every id in the dense range resolves and satisfies id == index,
            // proving no slot was permanently left null by the concurrent appends.
            for (var id = 0; id < vertexTarget + edgeTarget; id++)
            {
                AGraphElementModel ge;
                Assert.IsTrue(fallen8.TryGetGraphElement(out ge, id), "Every appended id must resolve.");
                Assert.AreEqual(id, ge.Id, "id == index must hold for every element.");
            }
        }

        /// <summary>
        /// Readers scan and look up ids while the single writer soft-removes elements. Removed
        /// elements must disappear cleanly (TryGet returns false, scans exclude them) and no read
        /// may throw or observe a null.
        /// </summary>
        [TestMethod]
        public void ConcurrentReaders_DuringSingleWriterRemovals_NeverThrowOrSeeNull()
        {
            var fallen8 = new Fallen8(_loggerFactory);

            const int vertexTarget = 2000;
            const int edgeTarget = 1500;
            int totalIds = vertexTarget + edgeTarget;

            // Seed the graph first.
            var vertexTx = new CreateVerticesTransaction();
            for (var i = 0; i < vertexTarget; i++)
            {
                vertexTx.AddVertex(1u, VertexLabel, new Dictionary<string, object> { { "seq", i } });
            }
            fallen8.EnqueueTransaction(vertexTx).WaitUntilFinished();

            var seedEdgeRng = new Random(99);
            var edgeTx = new CreateEdgesTransaction();
            for (var j = 0; j < edgeTarget; j++)
            {
                edgeTx.AddEdge(seedEdgeRng.Next(0, vertexTarget), EdgePropertyId, seedEdgeRng.Next(0, vertexTarget), 1u, EdgeLabel);
            }
            fallen8.EnqueueTransaction(edgeTx).WaitUntilFinished();

            var errors = new ConcurrentQueue<Exception>();
            int writerDone = 0;

            var readers = StartReaders(() =>
            {
                var rng = new Random(Thread.CurrentThread.ManagedThreadId * 104729 + Environment.TickCount);
                while (Volatile.Read(ref writerDone) == 0)
                {
                    for (var k = 0; k < 256; k++)
                    {
                        int id = rng.Next(0, totalIds);
                        AGraphElementModel ge;
                        if (fallen8.TryGetGraphElement(out ge, id))
                        {
                            Assert.IsNotNull(ge, "Resolved element must not be null.");
                            Assert.AreEqual(id, ge.Id, "id == index must hold even while others are being removed.");
                        }
                    }

                    foreach (var vertex in fallen8.GetAllVertices())
                    {
                        Assert.AreEqual(VertexLabel, vertex.Label, "GetAllVertices returned a wrong-label vertex during removals.");
                    }
                    foreach (var edge in fallen8.GetAllEdges())
                    {
                        Assert.IsNotNull(edge.SourceVertex, "Snapshot edge has a null source during removals.");
                        Assert.IsNotNull(edge.TargetVertex, "Snapshot edge has a null target during removals.");
                    }

                    // Soft-removal only flips the element's removed flag; it never clears a slot, so
                    // [0, Count) stays fully populated. Assert the strict no-null-slot invariant.
                    AssertPublishedRangeHasNoNullSlot(fallen8);
                }
            }, errors);

            try
            {
                // Single writer removes every 3rd vertex (which cascades to its edges), one tx at a time.
                for (var id = 0; id < vertexTarget; id += 3)
                {
                    var tx = new RemoveGraphElementTransaction { GraphElementId = id };
                    fallen8.EnqueueTransaction(tx).WaitUntilFinished();
                }
            }
            finally
            {
                Volatile.Write(ref writerDone, 1);
            }

            JoinReaders(readers);
            AssertNoErrors(errors);

            // Every removed vertex is gone; a surviving vertex still satisfies id == index.
            for (var id = 0; id < vertexTarget; id += 3)
            {
                VertexModel removed;
                Assert.IsFalse(fallen8.TryGetVertex(out removed, id), "A removed vertex must not resolve.");
            }
        }

        /// <summary>
        /// The specific Trim hazard: Trim renumbers ids and republishes a smaller store. Readers
        /// that probe ids up to the ORIGINAL (pre-trim) upper bound must, after a trim shrinks the
        /// store, get a clean "not found" for now-out-of-range ids — never an out-of-range throw —
        /// because the bound check and the indexer must read the same captured snapshot.
        /// </summary>
        [TestMethod]
        public void ConcurrentReaders_DuringTrimRenumbering_NeverGoOutOfRange()
        {
            var fallen8 = new Fallen8(_loggerFactory);

            const int vertexTarget = 2000;
            const int edgeTarget = 1000;
            int originalUpperBound = vertexTarget + edgeTarget;

            var vertexTx = new CreateVerticesTransaction();
            for (var i = 0; i < vertexTarget; i++)
            {
                vertexTx.AddVertex(1u, VertexLabel, new Dictionary<string, object> { { "seq", i } });
            }
            fallen8.EnqueueTransaction(vertexTx).WaitUntilFinished();

            var seedEdgeRng = new Random(7);
            var edgeTx = new CreateEdgesTransaction();
            for (var j = 0; j < edgeTarget; j++)
            {
                edgeTx.AddEdge(seedEdgeRng.Next(0, vertexTarget), EdgePropertyId, seedEdgeRng.Next(0, vertexTarget), 1u, EdgeLabel);
            }
            fallen8.EnqueueTransaction(edgeTx).WaitUntilFinished();

            var errors = new ConcurrentQueue<Exception>();
            int writerDone = 0;

            var readers = StartReaders(() =>
            {
                var rng = new Random(Thread.CurrentThread.ManagedThreadId * 1299709 + Environment.TickCount);
                while (Volatile.Read(ref writerDone) == 0)
                {
                    // Deliberately probe up to the ORIGINAL bound; after a trim the store is smaller,
                    // so most of these are out of range and must resolve to a clean "false".
                    for (var k = 0; k < 256; k++)
                    {
                        int id = rng.Next(0, originalUpperBound);
                        AGraphElementModel ge;
                        if (fallen8.TryGetGraphElement(out ge, id))
                        {
                            Assert.IsNotNull(ge, "Resolved element must not be null across a trim.");
                        }
                        VertexModel v;
                        fallen8.TryGetVertex(out v, id);
                        EdgeModel e;
                        fallen8.TryGetEdge(out e, id);
                    }

                    foreach (var vertex in fallen8.GetAllVertices())
                    {
                        Assert.AreEqual(VertexLabel, vertex.Label, "GetAllVertices returned a wrong-label vertex across a trim.");
                    }

                    // Trim republishes a fully dense, compacted array, so [0, Count) must have no
                    // null slot at any instant across the renumbering.
                    AssertPublishedRangeHasNoNullSlot(fallen8);
                }
            }, errors);

            try
            {
                // The writer interleaves removals and trims: each trim compacts ids and republishes
                // a smaller store while readers are mid-flight.
                for (var round = 0; round < 6; round++)
                {
                    // Remove a slice of the (current) vertices.
                    var removeRng = new Random(round + 1);
                    for (var n = 0; n < 100; n++)
                    {
                        int currentVertexCount = fallen8.VertexCount;
                        if (currentVertexCount <= 1)
                        {
                            break;
                        }
                        var tx = new RemoveGraphElementTransaction { GraphElementId = removeRng.Next(0, currentVertexCount) };
                        fallen8.EnqueueTransaction(tx).WaitUntilFinished();
                    }

                    fallen8.EnqueueTransaction(new TrimTransaction()).WaitUntilFinished();
                }
            }
            finally
            {
                Volatile.Write(ref writerDone, 1);
            }

            JoinReaders(readers);
            AssertNoErrors(errors);

            // After all trims, the store is dense again: every id in [0, VertexCount+EdgeCount)
            // resolves with id == index.
            int finalCount = fallen8.VertexCount + fallen8.EdgeCount;
            for (var id = 0; id < finalCount; id++)
            {
                AGraphElementModel ge;
                Assert.IsTrue(fallen8.TryGetGraphElement(out ge, id), "Post-trim store must be dense (id == index).");
                Assert.AreEqual(id, ge.Id, "Post-trim id == index must hold.");
            }
        }

        /// <summary>
        /// A snapshot captured by a reader must remain stable and consistent for its whole
        /// lifetime, regardless of how many mutations the writer publishes afterwards.
        /// </summary>
        [TestMethod]
        public void CapturedSnapshot_RemainsStable_WhileWriterKeepsMutating()
        {
            var fallen8 = new Fallen8(_loggerFactory);

            const int initial = 1000;
            var vertexTx = new CreateVerticesTransaction();
            for (var i = 0; i < initial; i++)
            {
                vertexTx.AddVertex(1u, VertexLabel, new Dictionary<string, object> { { "seq", i } });
            }
            fallen8.EnqueueTransaction(vertexTx).WaitUntilFinished();

            // Capture a snapshot up front.
            ImmutableList<VertexModel> snapshot = fallen8.GetAllVertices(VertexLabel);
            int snapshotCount = snapshot.Count;
            Assert.AreEqual(initial, snapshotCount);

            var errors = new ConcurrentQueue<Exception>();
            int writerDone = 0;

            // A reader re-iterates the SAME captured snapshot repeatedly while the writer mutates.
            var readers = StartReaders(() =>
            {
                while (Volatile.Read(ref writerDone) == 0)
                {
                    Assert.AreEqual(snapshotCount, snapshot.Count, "A captured snapshot must not change size.");
                    foreach (var v in snapshot)
                    {
                        Assert.IsNotNull(v, "A captured snapshot must not develop null entries.");
                    }
                }
            }, errors);

            try
            {
                for (var i = 0; i < 2000; i++)
                {
                    var tx = new CreateVerticesTransaction();
                    tx.AddVertex(1u, VertexLabel, new Dictionary<string, object> { { "seq", initial + i } });
                    fallen8.EnqueueTransaction(tx);
                }
                var flush = new CreateVerticesTransaction();
                flush.AddVertex(1u, VertexLabel);
                fallen8.EnqueueTransaction(flush).WaitUntilFinished();
            }
            finally
            {
                Volatile.Write(ref writerDone, 1);
            }

            JoinReaders(readers);
            AssertNoErrors(errors);

            // The captured snapshot is unchanged; the live graph has grown.
            Assert.AreEqual(snapshotCount, snapshot.Count);
            Assert.IsTrue(fallen8.VertexCount > initial, "The live graph must have grown past the snapshot.");
        }

        #region helpers

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
                    Name = "storage-reader-" + i
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
                Assert.Fail("Concurrent readers observed " + errors.Count + " error(s):\n" + string.Join("\n", distinct));
            }
        }

        /// <summary>
        /// The strict storage invariant that the public readers CANNOT surface: every slot in the
        /// published live range <c>[0, Count)</c> is non-null. <c>TryGet*</c> folds an in-range null
        /// slot into a clean <c>false</c> (<c>result != null &amp;&amp; !result._removed</c>) and the
        /// <c>GetAll*</c> scans pre-filter nulls, so a torn / half-published append — <c>Count</c>
        /// bumped before the slot write, or a lost release from dropping <c>volatile</c> on the
        /// holder — is invisible to every public read path (a standalone probe recorded &gt;1000 such
        /// corruption events while the existing assertions tripped on none of them).
        ///
        /// This capture reads the private volatile <c>_snapshot</c> exactly once (an atomic reference
        /// read); <c>Count</c> and <c>Segments</c> are then taken from that single immutable holder,
        /// so the bound and the slots are mutually consistent and the check is race-free with respect
        /// to concurrent single-writer appends/Trim — it can only fire on a genuine in-range null.
        /// In these append / soft-remove / Trim test graphs no id below <c>Count</c> is ever null
        /// (removal only flags the element, Trim rebuilds a dense array, and nothing is loaded from a
        /// gapped save file), so a null slot here can only mean corruption.
        ///
        /// The scan walks the range frontier-first (highest id down): a torn single-element append
        /// leaves the NEWEST slot (id == <c>Count - 1</c>) transiently null, so probing the top ids
        /// first maximises the chance of observing the tear before the writer fills the slot.
        /// Reflection is used deliberately (the engine declares no <c>InternalsVisibleTo</c>) so the
        /// public surface stays unchanged.
        /// </summary>
        private static void AssertPublishedRangeHasNoNullSlot(Fallen8 fallen8)
        {
            var snapshot = ReadSnapshot(fallen8);
            Assert.IsNotNull(snapshot, "The storage snapshot holder must never be null while the graph is live.");

            // Read Segments FIRST and Count LAST. The torn slot is null for only a few nanoseconds
            // (from publishing Count until the writer fills the slot); the ONLY work allowed between
            // capturing Count and reading the frontier slot is plain array indexing. Any slower read
            // sitting between them would let the writer always win the race, so the check could never
            // observe the tear (an earlier Count-first, GetValue-based ordering indeed missed it).
            var segments = ReadSnapshotSegments(snapshot);
            int count = ReadSnapshotCount(snapshot);
            if (count == 0)
            {
                return;
            }

            // Segments are uniform, full-size blocks (the writer always allocates a whole segment),
            // so the segment size is simply the length of an allocated segment — no need to mirror
            // the engine's private SegmentSize constant here. Walk frontier-first (id == Count-1
            // down) so the newest, most-recently-torn slot is read with minimal latency after Count.
            int segmentSize = segments[0].Length;
            for (int id = count - 1; id >= 0; id--)
            {
                AGraphElementModel element = segments[id / segmentSize][id % segmentSize];
                Assert.IsNotNull(element,
                    "Torn / half-published append: null slot at live id " + id + " within [0, Count=" + count +
                    "). TryGet*/GetAll* mask this; a Count-before-slot publication inversion, or dropping " +
                    "volatile on the snapshot holder, would reintroduce exactly this corruption.");
            }
        }

        /// <summary>
        /// O(1) sibling of <see cref="AssertPublishedRangeHasNoNullSlot"/> that checks ONLY the newest
        /// live slot (id == <c>Count - 1</c>) — where a torn single-element append always leaves its
        /// transient null. Kept allocation-free and reflection-free (via the pre-built getters) so it
        /// can run in a tight burst, keeping a reader on the frontier almost continuously. Count is
        /// read LAST so the only work between capturing it and reading the slot is an array index.
        /// </summary>
        private static void AssertFrontierSlotNotNull(Fallen8 fallen8)
        {
            var snapshot = ReadSnapshot(fallen8);
            if (snapshot == null)
            {
                return;
            }

            var segments = ReadSnapshotSegments(snapshot);
            int count = ReadSnapshotCount(snapshot);
            if (count == 0)
            {
                return;
            }

            int frontier = count - 1;
            int segmentSize = segments[0].Length;
            AGraphElementModel element = segments[frontier / segmentSize][frontier % segmentSize];
            Assert.IsNotNull(element,
                "Torn / half-published append: null slot at frontier id " + frontier + " (Count=" + count +
                "). TryGet*/GetAll* mask this; a Count-before-slot publication inversion, or dropping " +
                "volatile on the snapshot holder, would reintroduce exactly this corruption.");
        }

        #endregion
    }
}
