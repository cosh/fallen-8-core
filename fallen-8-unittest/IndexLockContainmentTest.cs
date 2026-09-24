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
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Helper;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Serializer;
using NoSQL.GraphDB.Core.Service;

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

        /// <summary>
        ///   The same rule on the OTHER W8 site, <c>ServiceFactory.TryAddService</c>, and the reason
        ///   this test exists rather than a fourth paragraph of reasoning: what can reach that
        ///   member's <c>catch</c> WITHOUT the lock has now been asserted three times and been wrong
        ///   twice. It was first written up as an unresolved plugin (false: resolution returns false
        ///   and takes the else branch), then as unreachable (false: see below), and the review that
        ///   found the second error named a shape the name map filters out. So it is measured here.
        ///
        ///   <para>
        ///     The reachable shape is a plugin whose construction fails LATE. <c>BuildNameMap</c>
        ///     activates every candidate once to read its name and skips one that throws, so a
        ///     constructor that always throws never enters the map and resolution simply answers
        ///     "no such plugin". But the map is memoized and stores the TYPE, and
        ///     <c>TryFindPlugin</c> then activates a FRESH instance per call with no catch around
        ///     it, so a constructor that succeeded once and fails afterwards propagates out of
        ///     resolution and into the catch, before <c>WriteResource</c> was ever called. Any
        ///     plugin whose constructor touches a file, a socket or configuration can do that.
        ///   </para>
        ///   <para>
        ///     The old code released the lock in that catch, which is the release-never-held half of
        ///     W8: it drives the writer counter negative, which reads as permanently held. So this
        ///     asserts the lock is FREE afterwards, which is the assertion that would have failed.
        ///   </para>
        /// </summary>
        [TestMethod]
        public void AServicePluginWhoseConstructionFailsLate_DoesNotLeakTheFactoryLock()
        {
            var sink = new TestLogSink();
            var factory = new ServiceFactory(
                _fallen8, sink.CreateFactory().CreateLogger<ServiceFactory>());

            // Warms the memoized name map while the plugin still constructs, which is what puts its
            // type in the map at all. Also the happy path, so the arrangement is not assumed.
            Assert.IsTrue(
                factory.TryAddService(out _, LateFailingService.TestPluginName, "first", null),
                "the fixture plugin has to resolve while it is willing to be constructed, or the "
                + "path below is never reached and this test proves nothing");
            AssertLockIsFree(factory, "TryAddService, having added a service");

            LateFailingService.RefuseConstruction = true;
            try
            {
                Assert.IsFalse(
                    factory.TryAddService(out _, LateFailingService.TestPluginName, "second", null),
                    "a plugin that cannot be constructed is not a service that was added");
            }
            finally
            {
                LateFailingService.RefuseConstruction = false;
            }

            // The load-bearing half. Without it this test passes just as well when resolution
            // answers a quiet "no such plugin", which is the OTHER way to return false and reaches
            // no catch at all: the whole point is that the constructor's exception propagated INTO
            // the catch, and this is the only observable that says so.
            // Matched on the CATCH's own sentence, not on the constructor's: Activator wraps a
            // constructor exception, so what arrives here is the TargetInvocationException's
            // message ("Exception has been thrown by the target of an invocation"). Asserting the
            // inner text instead is how this assertion was first written, and it failed while the
            // path it was checking had worked perfectly, which is worth keeping as a note.
            Assert.IsTrue(
                sink.Entries.Any(e => e.Level == LogLevel.Error
                    && e.Message.Contains("was not able to add", StringComparison.Ordinal)
                    && e.Message.Contains(LateFailingService.TestPluginName, StringComparison.Ordinal)),
                "the plugin's own exception never reached TryAddService's catch, so this test did "
                + "not exercise the release-never-held path it exists for. A quiet 'no such plugin' "
                + "returns false from resolution and reaches no catch at all, which is the outcome "
                + "this distinguishes. Logged: "
                + String.Join(" | ", sink.Entries.Select(e => e.Level + " " + e.Message)));

            AssertLockIsFree(factory,
                "TryAddService, after construction threw on the way to the lock");
        }
    }

    /// <summary>
    ///   A discoverable service plugin that can be told to refuse construction. Public and top-level
    ///   because <c>PluginFactory</c> only offers types that are, which is the same reason
    ///   <c>ThrowingOnLoadIndex</c> is; the remarks there describe what being globally discoverable
    ///   costs, and it applies here too.
    ///   <para>
    ///     It constructs happily by DEFAULT, so the name map that every service resolution shares is
    ///     built with it present and no other test is affected. Only the one test that arms it sees
    ///     it refuse, and it disarms in a <c>finally</c>. The suite is sequential (there is no
    ///     <c>[Parallelize]</c>), which is what makes a static flag safe here.
    ///   </para>
    /// </summary>
    public sealed class LateFailingService : IService
    {
        public const String TestPluginName = "LateFailingTestService";

        /// <summary>Set by the one test that needs this plugin to fail, and cleared by it.</summary>
        internal static Boolean RefuseConstruction;

        public LateFailingService()
        {
            if (RefuseConstruction)
            {
                // Surfaces as TargetInvocationException from Activator.CreateInstance, which
                // PluginFactory deliberately does NOT treat as a deployment failure, so it
                // propagates rather than becoming a quiet "no such plugin".
                throw new InvalidOperationException("this plugin refuses to be constructed");
            }
        }

        public String PluginName => TestPluginName;
        public Type PluginCategory => typeof(IService);
        public String Description => "a service whose construction can be made to fail";
        public String Manufacturer => "fallen-8 tests";
        public DateTime StartTime => DateTime.MinValue;

        public Boolean IsRunning
        {
            get; private set;
        }

        public IDictionary<String, String> Metadata => new Dictionary<String, String>();

        public void Initialize(IFallen8 fallen8, IDictionary<String, Object> parameter)
        {
        }

        public void Save(SerializationWriter writer)
        {
        }

        public void Load(SerializationReader reader, IFallen8 fallen8)
        {
        }

        public void OnServiceRestart()
        {
        }

        public Boolean TryStart()
        {
            IsRunning = true;
            return true;
        }

        public Boolean TryStop()
        {
            IsRunning = false;
            return true;
        }

        public void Dispose()
        {
        }
    }
}
