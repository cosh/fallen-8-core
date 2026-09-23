// MIT License
//
// IndexLockContainmentTest.cs
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
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Helper;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Serializer;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   A throw inside a guarded region must not leak the lock (platform-integrity audit, W8).
    ///
    ///   <para>
    ///     What a leak costs is stated once, on <c>AThreadSafeElement._usingResource</c>: acquisition
    ///     never returns again, so the symptom is a pegged core and a wedged index with no exception
    ///     naming either. Two of the callers that can trigger it swallow the throw as well
    ///     (<c>PurgeValueFromIndex</c> and <c>SaveIndex</c>), so there is no error surface at all.
    ///     That is why this file exists: nothing else would notice a regression here.
    ///   </para>
    ///   <para>
    ///     The exhaustive test is the FIRST one, and it is exhaustive on purpose. The behavioural
    ///     test below it demonstrates the user-visible symptom once; repeating that shape per member
    ///     would leave a spinning thread per member on a red run.
    ///   </para>
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class IndexLockContainmentTest
    {
        private Fallen8 _fallen8;

        [TestInitialize]
        public void TestInitialize()
        {
            _fallen8 = new Fallen8(TestLoggerFactory.Create());
        }

        [TestCleanup]
        public void TestCleanup()
        {
            _fallen8.Dispose();
        }

        /// <summary>
        ///   Every guarded section of <see cref="SingleValueIndex" />, driven to throw, leaves the
        ///   lock free. Deterministic and thread-free, which is what lets it cover all thirteen
        ///   rather than one.
        ///   <para>
        ///     The trigger for twelve of them is uniform: <c>Dispose</c> nulls the dictionary WITHOUT
        ///     taking the lock, and every one of those members dereferences it as its first act, so
        ///     a disposed index throws <see cref="NullReferenceException" /> from inside the lock.
        ///     <c>Load</c> needs its own, because it assigns the dictionary rather than reading it.
        ///   </para>
        ///   <para>
        ///     Both halves of each row are required. Asserting that the call THREW is what stops
        ///     this test rotting into a vacuous pass: if a later change adds a null guard, the
        ///     trigger stops throwing, the lock is trivially free, and every row would pass while
        ///     testing nothing. The assertion forces a future author to pick a new trigger instead.
        ///   </para>
        /// </summary>
        [TestMethod]
        public void EveryGuardedSectionOfSingleValueIndex_ReleasesTheLockWhenItThrows()
        {
            var rows = new (String What, Action<SingleValueIndex> Act)[]
            {
                ("CountOfKeys", i => i.CountOfKeys()),
                ("CountOfValues", i => i.CountOfValues()),
                ("AddOrUpdate", i => i.AddOrUpdate("k", null)),
                ("TryRemoveKey", i => i.TryRemoveKey("k")),
                ("RemoveValue", i => i.RemoveValue(null)),
                ("Wipe", i => i.Wipe()),
                ("GetKeys", i => i.GetKeys()),
                // An iterator: nothing runs until it is enumerated, so the enumeration is the call.
                // This is the one row that was already guarded, and it is here as the control.
                ("GetKeyValues", i => i.GetKeyValues().ToList()),
                ("TryGetValue(object)", i => i.TryGetValue(out ImmutableList<AGraphElementModel> _, "k")),
                ("TryGetValue(IComparable)", i => i.TryGetValue(out AGraphElementModel _, "k")),
                ("Values", i => i.Values()),
                ("Save", i => i.Save(new SerializationWriter(new MemoryStream(), true))),
                ("Load", Load),
            };

            foreach (var row in rows)
            {
                var index = new SingleValueIndex();
                index.Initialize(_fallen8, null);
                if (!String.Equals(row.What, "Load", StringComparison.Ordinal))
                {
                    index.Dispose();
                }

                var threw = false;
                try
                {
                    row.Act(index);
                }
                catch (Exception)
                {
                    threw = true;
                }

                Assert.IsTrue(threw,
                    row.What + " no longer throws on this trigger, so this row proves nothing. "
                    + "Pick a trigger that still reaches the guarded region.");
                AssertLockIsFree(index, row.What);
            }
        }

        /// <summary>
        ///   The user-visible symptom, once: a throw in one writer used to leave every later writer
        ///   spinning at full CPU forever, with nothing raised to say so.
        ///   <para>
        ///     The second writer runs on a dedicated background thread at the lowest priority, not on
        ///     the thread pool: a pool thread blocked forever drags the pool's injection rate down and
        ///     slows every later async test in a sequential suite, and a background thread lets the
        ///     process exit at the end of the run without joining a runaway. Priority is a mitigation
        ///     and not a guarantee, because Windows boosts starved threads.
        ///   </para>
        ///   <para>
        ///     When this test FAILS, that thread spins for the remainder of the process and pegs one
        ///     logical core. That is unavoidable: it is the defect. It is also why the test above,
        ///     and not this one, is the exhaustive half.
        ///   </para>
        ///   <para>
        ///     The wait is deliberately generous. On a fixed build the second write completes in
        ///     microseconds, so ten seconds is six orders of magnitude of headroom and cannot flake
        ///     under suite load, and the cost is paid only on a red run. The probe must NOT run on
        ///     the test thread: a leaked write lock blocks readers too, so a check from here would
        ///     hang the whole suite with no failure message and no test name.
        ///   </para>
        /// </summary>
        [TestMethod]
        public void AThrowInOneWriter_DoesNotLeaveTheNextWriterSpinning()
        {
            var index = new SingleValueIndex();
            index.Initialize(_fallen8, null);

            // A key whose hash throws. IndexHelper.CheckObject is only an "as" cast, so it is the
            // DICTIONARY that first asks for the hash, which happens inside the write lock.
            Assert.ThrowsException<InvalidOperationException>(
                () => index.AddOrUpdate(new ThrowsOnHash(), null),
                "the fixture key has to reach the dictionary for this test to mean anything");

            using var done = new ManualResetEventSlim(false);
            var second = new Thread(() =>
            {
                index.AddOrUpdate("plain", null);
                done.Set();
            })
            {
                IsBackground = true,
                Priority = ThreadPriority.Lowest,
                Name = "W8 second writer",
            };
            second.Start();

            Assert.IsTrue(done.Wait(TimeSpan.FromSeconds(10)),
                "the write lock was never released, so this writer is spinning on Thread.Yield and "
                + "will go on doing so for the life of the process: that is what a missing finally "
                + "costs, and no exception anywhere names it");
        }

        private static void Load(SingleValueIndex index)
        {
            // A negative key count: the Dictionary constructor refuses it, from inside the write
            // lock, without the dictionary ever having been dereferenced.
            using var stream = new MemoryStream();
            var writer = new SerializationWriter(stream, true);
            writer.Write(0);    // parameter
            writer.Write(-1);   // keyCount
            writer.UpdateHeader();
            writer.Flush();
            stream.Position = 0;

            index.Load(new SerializationReader(stream), null);
        }

        /// <summary>
        ///   Reads the lock word by reflection. It is an implementation detail, and it is still the
        ///   right assertion here: it is the only one that scales to thirteen sections without
        ///   thirteen permanently spinning threads, and it fails instantly and precisely.
        ///   <para>
        ///     The field is private on the BASE type, and reflection does not surface a private base
        ///     field through a derived one, so the lookup must target
        ///     <see cref="AThreadSafeElement" /> itself. Targeting the derived type returns a null
        ///     <see cref="FieldInfo" /> and the failure then reads as a broken test rather than a
        ///     renamed field, which is why the field is asserted present first.
        ///   </para>
        /// </summary>
        private static void AssertLockIsFree(AThreadSafeElement element, String what)
        {
            var field = typeof(AThreadSafeElement).GetField(
                "_usingResource", BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.IsNotNull(field,
                "AThreadSafeElement._usingResource is gone or renamed, so this gate can no longer "
                + "see whether a lock leaked. Re-point it at whatever holds the lock state now.");

            var held = (Int32)field.GetValue(element);
            Assert.AreEqual(0, held,
                what + " threw and left the lock held (word = 0x" + held.ToString("x8")
                + "). The low 20 bits count readers and the high bits writers, so a non-zero word "
                + "here means every later acquisition of this index spins forever.");
        }

        /// <summary>
        ///   A key the dictionary cannot hash. <see cref="IComparable" /> is what
        ///   <c>IndexHelper.CheckObject</c> casts to, so it is required to get past the cast;
        ///   <c>CompareTo</c> itself is never called, because the index is a
        ///   <see cref="Dictionary{TKey,TValue}" /> and not a sorted one. Equals is overridden
        ///   alongside GetHashCode because omitting it is a warning, and warnings are errors here.
        /// </summary>
        private sealed class ThrowsOnHash : IComparable
        {
            public Int32 CompareTo(Object obj) => 0;

            public override Int32 GetHashCode() =>
                throw new InvalidOperationException("this key's hash throws");

            public override Boolean Equals(Object obj) => ReferenceEquals(this, obj);
        }
    }
}
