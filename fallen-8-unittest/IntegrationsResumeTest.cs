// MIT License
//
// IntegrationsResumeTest.cs
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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Integrations.Contract;
using NoSQL.GraphDB.Integrations.Credentials;
using NoSQL.GraphDB.Integrations.Diagnostics;
using NoSQL.GraphDB.Integrations.Graph;
using NoSQL.GraphDB.Integrations.Hosting;
using NoSQL.GraphDB.Integrations.Identity;
using NoSQL.GraphDB.Integrations.Run;
using NoSQL.GraphDB.Integrations.Validation;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   A run interrupted by a restart is PICKED UP where it stopped (feature integration-run-lifecycle).
    ///
    ///   <para>Every test here stops a run at a chosen boundary, throws the whole runtime away, and builds a
    ///   NEW one over the same graph and the same spool directory - which is exactly what a container
    ///   restart is. Nothing but the spool crosses that line, so anything the second runtime knows it read
    ///   off disk.</para>
    ///
    ///   <para>The failure being defended against is specific and was unrecoverable. A run's graph writes
    ///   are idempotent by re-resolution, so re-running one is safe; its EMBEDDING set is not, because only
    ///   entities whose data changed are embedded. So a run interrupted after twenty of twelve thousand
    ///   summaries, simply re-run, finds everything present and unchanged, embeds NOTHING, and leaves the
    ///   rest permanently unembedded - curable only by clearing the namespace and importing again.</para>
    /// </summary>
    [TestClass]
    public class IntegrationsResumeTest
    {
        private const String Provider = "resume-fixture";
        private const String Instance = "garage";
        private const Int32 Entities = 10;

        /// <summary>Two at a time, so an interruption can land in the middle of the embedding phase.</summary>
        private const Int32 ChunkSize = 2;

        private String _spool;

        [TestInitialize]
        public void CreateSpool()
        {
            _spool = Path.Combine(Path.GetTempPath(), "f8-spool-" + Guid.NewGuid().ToString("N"));
        }

        [TestCleanup]
        public void RemoveSpool()
        {
            try
            {
                if (System.IO.Directory.Exists(_spool))
                {
                    System.IO.Directory.Delete(_spool, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }

        #region the embedding phase, which is the one thing a re-run cannot recover

        [TestMethod]
        public async Task ARunInterruptedMidEmbed_ResumesAndEmbedsExactlyWhatWasLeft()
        {
            var graph = new InMemoryGraphTarget();

            // FIRST PROCESS: stopped by shutdown after two chunks, which is four of the ten summaries.
            using (var first = new Runtime(_spool, graph, stopAfterChunks: 2))
            {
                await Assert.ThrowsExceptionAsync<RunInterruptedException>(() => first.RunAsync(),
                    "a shutdown mid-embed must interrupt the run rather than finish or fail it");
            }

            Assert.AreEqual(4, graph.EmbeddedSummaries.Count,
                "the fixture has to stop where it says it does, or nothing below is testing a resume");

            // SECOND PROCESS, over the same graph and the same spool. Nothing else crosses the line.
            using (var second = new Runtime(_spool, graph))
            {
                var resumed = await second.ResumeAllAsync();

                Assert.AreEqual(1, resumed.Count, "the interrupted run was not picked up at all");
                Assert.IsFalse(resumed[0].Failed, "the resumed run failed: " + resumed[0].Error);
            }

            Assert.AreEqual(Entities, graph.EmbeddedSummaries.Count,
                "every entity has to end up embedded. This is the whole feature: without the journal the " +
                "resumed run recomputes an EMPTY plan, because every element it wrote now compares equal, " +
                "and the six unembedded summaries are lost for good");
        }

        [TestMethod]
        public async Task AResumedRun_WritesNoElementTwice()
        {
            var graph = new InMemoryGraphTarget();

            using (var first = new Runtime(_spool, graph, stopAfterChunks: 1))
            {
                await Assert.ThrowsExceptionAsync<RunInterruptedException>(() => first.RunAsync());
            }

            var afterFirst = graph.AllElements().Count();
            Assert.AreEqual(Entities, afterFirst,
                "the writes all landed before the embedding began, so the interruption is mid-embed");

            using (var second = new Runtime(_spool, graph))
            {
                var resumed = await second.ResumeAllAsync();

                Assert.AreEqual(0, resumed[0].ElementsCreated,
                    "the resumed run created elements again, which means re-resolution did not match what " +
                    "the interrupted one wrote: that is a duplicate of the whole import");
                Assert.AreEqual(Entities, resumed[0].ElementsMatched, "and it matched every one of them");
            }

            Assert.AreEqual(afterFirst, graph.AllElements().Count(),
                "the graph grew across the resume, so the same source is now in it twice");
        }

        [TestMethod]
        public async Task AnInterruptionBeforeTheEmbeddingBegins_StillEmbedsEverythingOnResume()
        {
            // THE JOURNAL-AHEAD INVARIANT, which is the subtle half of the design. The plan is written
            // BEFORE the writes that make it unknowable, so it exists even for a run that died before it
            // embedded anything. Written afterwards - the obvious way round - this run would resume with no
            // journal, recompute an empty plan against the elements it had already written, and embed
            // nothing at all.
            var graph = new InMemoryGraphTarget();

            using (var first = new Runtime(_spool, graph, stopAfterChunks: 0))
            {
                await Assert.ThrowsExceptionAsync<RunInterruptedException>(() => first.RunAsync());
            }

            Assert.AreEqual(0, graph.EmbeddedSummaries.Count, "the fixture stopped before the first chunk");
            Assert.AreEqual(Entities, graph.AllElements().Count(),
                "but after the writes, which is the state that makes the plan unrecomputable");

            using (var second = new Runtime(_spool, graph))
            {
                await second.ResumeAllAsync();
            }

            Assert.AreEqual(Entities, graph.EmbeddedSummaries.Count,
                "the journal was written after the writes rather than before them, so the resumed run had " +
                "nothing to go on and every summary was lost");
        }

        [TestMethod]
        public async Task AResumedRunWhoseJournalIsGone_EmbedsEverythingRatherThanNothing()
        {
            // The one way the journal-ahead invariant can still be broken: a spool that took the entry and
            // then refused the small file beside it, which is what a full volume looks like. Recomputing the
            // plan then gives an EMPTY one, because the elements are already written and nothing compares
            // different, and every summary the interrupted attempt had not reached would be stranded for
            // good. Over-embedding is an idempotent overwrite of the same vectors; under-embedding is the
            // loss this whole feature exists to prevent, so the run embeds the lot.
            var graph = new InMemoryGraphTarget();

            using (var first = new Runtime(_spool, graph, stopAfterChunks: 1))
            {
                await Assert.ThrowsExceptionAsync<RunInterruptedException>(() => first.RunAsync());
            }

            Assert.AreEqual(2, graph.EmbeddedSummaries.Count, "the fixture stopped after one chunk");
            graph.EmbeddedSummaries.Clear();

            var journal = SpoolFiles().Single(path =>
                path.EndsWith(".progress.json", StringComparison.Ordinal));
            File.Delete(journal);

            using (var second = new Runtime(_spool, graph))
            {
                var resumed = await second.ResumeAllAsync();
                Assert.IsFalse(resumed[0].Failed, resumed[0].Error);
            }

            Assert.AreEqual(Entities, graph.EmbeddedSummaries.Count,
                "with no journal to go on, a resumed run that recomputed its plan would embed NOTHING: " +
                "every element it wrote now compares equal. It has to embed the whole snapshot instead");
        }

        [TestMethod]
        public async Task AResumedRun_ReconcilesAtTheTrueEnd()
        {
            // Reconciliation is skipped by an interrupted run for the same reason a cancelled one skips it,
            // so the resumed run is what finally converges the graph. Here an element from an EARLIER import
            // is no longer described, and only the resumed run may withdraw it.
            var graph = new InMemoryGraphTarget();
            using (var older = new Runtime(_spool, graph))
            {
                var report = await older.RunAsync(extraEntity: "44:D2:44:FF:FF:FF");
                Assert.IsFalse(report.Failed, report.Error);
                Assert.AreEqual(Entities + 1, report.ElementsCreated);
            }

            // A LATER generation of the same source, which is what makes this run have summaries to embed and
            // therefore a boundary to be interrupted at: over an unchanged source it would correctly have
            // nothing to embed and would finish in one go.
            using (var first = new Runtime(_spool, graph, stopAfterChunks: 1))
            {
                await Assert.ThrowsExceptionAsync<RunInterruptedException>(
                    () => first.RunAsync(generation: 2));
            }

            var claimedBeforeResume = await graph.ElementsClaimedByAsync(Instance, CancellationToken.None);
            Assert.AreEqual(Entities + 1, claimedBeforeResume.Count,
                "the interrupted run must not have withdrawn anything, which is what makes the assertion " +
                "below about the RESUMED run");

            using (var second = new Runtime(_spool, graph))
            {
                var resumed = await second.ResumeAllAsync();

                Assert.AreEqual(1, resumed[0].ClaimsWithdrawn,
                    "the resumed run has to reconcile at the true end, or an element the source stopped " +
                    "describing is never withdrawn by anybody: " + Describe(resumed[0]));
                Assert.AreEqual(1, resumed[0].ElementsDeleted, Describe(resumed[0]));
            }
        }

        [TestMethod]
        public async Task AResumedRunRewritesNoProperty_ThoughItsSnapshotCameBackThroughJson()
        {
            // The spool holds the snapshot as JSON, so a resumed run reads its property values as
            // JsonElement where the first attempt had real CLR values - an Int32 and a Boolean here. If those
            // two renderings disagreed by so much as a suffix, every matched element would be rewritten on
            // every resume: a graph churning on restart, a change feed full of writes nobody made, and a
            // write-ahead log growing for no reason. Nothing about the run's own report would show it.
            var graph = new InMemoryGraphTarget();
            var writes = new PropertyWriteCounter(graph);

            using (var first = new Runtime(_spool, graph, stopAfterChunks: 1, target: writes))
            {
                await Assert.ThrowsExceptionAsync<RunInterruptedException>(() => first.RunAsync());
            }

            // Not a vacuous pass: the typed properties really did reach the graph, so the comparison below
            // is comparing something.
            var element = (await graph.ElementsClaimedByAsync(Instance, CancellationToken.None))[0];
            Assert.IsTrue(graph.TryReadElement(element, out var state));
            Assert.IsTrue(state.Properties.ContainsKey("csv.port"),
                "the typed properties were dropped instead of stored, so nothing here is being tested");
            Assert.IsTrue(state.Properties.ContainsKey("csv.active"));

            var before = writes.Calls;
            using (var second = new Runtime(_spool, graph, target: writes))
            {
                var resumed = await second.ResumeAllAsync();
                Assert.IsFalse(resumed[0].Failed, resumed[0].Error);
            }

            Assert.AreEqual(before, writes.Calls,
                "the resumed run issued property writes, which means its round-tripped snapshot did not " +
                "compare equal to what the first attempt stored. Every restart would then rewrite the whole " +
                "import");
        }

        #endregion

        #region what may and may not be picked up

        [TestMethod]
        public async Task ARunInterruptedBeforeItsSourceWasRead_CannotBeResumed_AndSaysSo()
        {
            // The honest limit. The snapshot is what makes a run resumable; before it exists, the file and
            // the credential that would produce one are gone, and this runtime never had anywhere to keep
            // them. Reported rather than retried, and rather than left as a 404 that reads as "it never ran".
            var graph = new InMemoryGraphTarget();

            using (var first = new Runtime(_spool, graph, stopDuringObserve: true))
            {
                await Assert.ThrowsExceptionAsync<RunInterruptedException>(() => first.RunAsync(),
                    "a shutdown during the source read aborts the read - which writes nothing - and must be " +
                    "reported as an interruption rather than as the source failing, which would send an " +
                    "operator to look at a system that answered perfectly well");
            }

            using (var second = new Runtime(_spool, graph))
            {
                var pending = second.Spool.Pending();
                Assert.AreEqual(1, pending.Count, "the accepted run left no trace, so it vanished silently");
                Assert.IsFalse(pending[0].Resumable,
                    "an entry with no snapshot must not claim to be resumable");

                var resumed = await second.ResumeAllAsync();
                Assert.AreEqual(0, resumed.Count, "nothing may be re-run from it");

                Assert.IsTrue(second.Tracker.TryGet(Instance, out var state),
                    "and the identity has to have something honest to answer with rather than a 404");
                Assert.IsFalse(state.Running);
                Assert.IsTrue(state.Resumed);
                StringAssert.Contains(state.Error, "Submit the job again",
                    "the slot must say what to do about it: " + state.Error);
            }

            Assert.AreEqual(0, graph.AllElements().Count(), "and nothing was written on the way past");
            Assert.AreEqual(0, SpoolFiles().Length, "the entry is dropped once it has been accounted for");
        }

        [TestMethod]
        public async Task AResumedRunTheGraphRefused_KeepsItsEntryForTheNextStart()
        {
            // The failure that would otherwise defeat the whole feature. This container restarts alongside
            // the graph and may come up first, so a resumed run can fail for no reason but "the graph was
            // not there yet". Deleting the entry on that throws away the hours of work the spool exists to
            // protect.
            var graph = new InMemoryGraphTarget();
            using (var first = new Runtime(_spool, graph, stopAfterChunks: 1))
            {
                await Assert.ThrowsExceptionAsync<RunInterruptedException>(() => first.RunAsync());
            }

            using (var refused = new Runtime(_spool, graph, graphRefuses: true))
            {
                var reports = await refused.ResumeAllAsync();

                Assert.AreEqual(1, reports.Count);
                Assert.AreEqual(JobErrorKinds.Graph, reports[0].ErrorKind, Describe(reports[0]));
            }

            var pending = new RunSpool(_spool, NullLogger<RunSpool>.Instance).Pending();
            Assert.AreEqual(1, pending.Count,
                "the entry was deleted because the GRAPH refused it, which loses the run for a reason that " +
                "says nothing about the job or the source");
            Assert.AreEqual(1, pending[0].Attempts, "and the attempt has to be counted, or the bound never bites");

            // And the next start, with a graph that answers, finishes it.
            using (var healthy = new Runtime(_spool, graph))
            {
                var reports = await healthy.ResumeAllAsync();
                Assert.AreEqual(1, reports.Count);
                Assert.IsFalse(reports[0].Failed, Describe(reports[0]));
            }

            Assert.AreEqual(Entities, graph.EmbeddedSummaries.Count,
                "and the work the first attempt had not done is done now");
            Assert.AreEqual(0, SpoolFiles().Length);
        }

        [TestMethod]
        public async Task ARunTheGraphKeepsRefusing_IsEventuallyGivenUpOn()
        {
            // The bound on the retry above. A graph that is gone for good must not leave an entry that is
            // retried on every start for ever, which would be a runtime that never settles.
            var graph = new InMemoryGraphTarget();
            using (var first = new Runtime(_spool, graph, stopAfterChunks: 1))
            {
                await Assert.ThrowsExceptionAsync<RunInterruptedException>(() => first.RunAsync());
            }

            // Every attempt but the last: the entry survives, with the count going up, which is what makes
            // the bound below a bound rather than an accident.
            for (var attempt = 1; attempt < RunSpool.MaxAttempts; attempt++)
            {
                using var refused = new Runtime(_spool, graph, graphRefuses: true);
                await refused.ResumeAllAsync();

                var pending = new RunSpool(_spool, NullLogger<RunSpool>.Instance).Pending();
                Assert.AreEqual(1, pending.Count, "attempt " + attempt + " gave up too early");
                Assert.AreEqual(attempt, pending[0].Attempts,
                    "every attempt has to count, including the ones that failed the same way");
            }

            JobReport last;
            using (var final = new Runtime(_spool, graph, graphRefuses: true))
            {
                last = (await final.ResumeAllAsync()).Single();
            }

            Assert.AreEqual(JobErrorKinds.Graph, last.ErrorKind,
                "the last attempt is still reported as the graph failure it was, rather than as some " +
                "synthetic giving-up: that message names the system to go and look at");
            Assert.AreEqual(0, SpoolFiles().Length,
                "and the entry goes with it, or a graph that is gone for good leaves a run retried on every " +
                "start for ever. Left: " + String.Join(", ", SpoolFiles()));
        }

        [TestMethod]
        public async Task AFinishedRunLeavesTheSpoolEmpty()
        {
            var graph = new InMemoryGraphTarget();
            using var runtime = new Runtime(_spool, graph);

            var report = await runtime.RunAsync();

            Assert.IsFalse(report.Failed, report.Error);
            Assert.AreEqual(0, SpoolFiles().Length,
                "an entry that outlived its run would be resumed by the next restart, re-running a job " +
                "whose answer somebody already has - and the spool would quietly become the run history " +
                "this runtime deliberately does not keep. Left: " + String.Join(", ", SpoolFiles()));
        }

        [TestMethod]
        public async Task AFailedRunLeavesTheSpoolEmptyToo()
        {
            var graph = new InMemoryGraphTarget();
            using var runtime = new Runtime(_spool, graph, failTheSource: true);

            var report = await runtime.RunAsync();

            Assert.IsTrue(report.Failed, "the fixture has to fail for this to be testing anything");
            Assert.AreEqual(0, SpoolFiles().Length,
                "a failed run is over, so its entry goes: resuming it would re-read a source that already " +
                "refused. Left: " + String.Join(", ", SpoolFiles()));
        }

        [TestMethod]
        public async Task ACancelledRunLeavesTheSpoolEmpty_UnlikeAnInterruptedOne()
        {
            // The two stops differ in exactly this, and it is the difference between "somebody decided this
            // should not finish" and "this process cannot be the one to finish it".
            var graph = new InMemoryGraphTarget();

            using (var cancelling = new Runtime(_spool, graph, cancelAfterChunks: 1))
            {
                var report = await cancelling.RunAsync();
                Assert.IsTrue(report.Cancelled, "the fixture has to cancel: " + Describe(report));
            }

            Assert.AreEqual(0, SpoolFiles().Length,
                "a cancelled run is over and must not be resumed behind the operator's back. Left: " +
                String.Join(", ", SpoolFiles()));

            using (var interrupting = new Runtime(_spool, graph, stopAfterChunks: 1))
            {
                // A later generation, so this run has summaries to embed and can be stopped between chunks.
                await Assert.ThrowsExceptionAsync<RunInterruptedException>(
                    () => interrupting.RunAsync(generation: 2));
            }

            Assert.AreEqual(2, SpoolFiles().Length,
                "an interrupted run keeps BOTH its entry and its journal, or there is nothing to resume " +
                "from. Left: " + String.Join(", ", SpoolFiles()));
        }

        [TestMethod]
        public async Task WithNoSpoolConfigured_NothingIsWrittenToDiskAtAll()
        {
            // The default, and it has to be provable rather than asserted: the whole feature is opt-in, and
            // an operator who configured nothing gets exactly the behaviour they had before it existed.
            var graph = new InMemoryGraphTarget();
            System.IO.Directory.CreateDirectory(_spool);

            using var runtime = new Runtime(spool: null, graph);
            var report = await runtime.RunAsync();

            Assert.IsFalse(report.Failed, report.Error);
            Assert.AreEqual(0, SpoolFiles().Length,
                "a runtime with no spool directory configured touched the disk anyway: " +
                String.Join(", ", SpoolFiles()));
            Assert.IsFalse(runtime.Spool.Enabled);
        }

        [TestMethod]
        public async Task ATruncatedEntryIsRefused_NotGuessedAt()
        {
            var graph = new InMemoryGraphTarget();
            using (var first = new Runtime(_spool, graph, stopAfterChunks: 1))
            {
                await Assert.ThrowsExceptionAsync<RunInterruptedException>(() => first.RunAsync());
            }

            // What a process dying DURING the write leaves, if the write were not atomic. The rename makes
            // this state unreachable in practice, which is exactly why the handling of it has to be pinned
            // by hand: nothing else can produce it.
            var entry = SpoolFiles().Single(path => path.EndsWith(".job.json", StringComparison.Ordinal));
            var whole = File.ReadAllText(entry);
            File.WriteAllText(entry, whole.Substring(0, whole.Length / 2));

            using (var second = new Runtime(_spool, graph))
            {
                Assert.AreEqual(0, second.Spool.Pending().Count,
                    "a half-written entry must not be offered for resume: a run resumed from a truncated " +
                    "snapshot would write a graph nobody described");
                var resumed = await second.ResumeAllAsync();
                Assert.AreEqual(0, resumed.Count);
            }
        }

        [TestMethod]
        public async Task AnEntryFromAFutureFormatIsRefused()
        {
            var graph = new InMemoryGraphTarget();
            using (var first = new Runtime(_spool, graph, stopAfterChunks: 1))
            {
                await Assert.ThrowsExceptionAsync<RunInterruptedException>(() => first.RunAsync());
            }

            var entry = SpoolFiles().Single(path => path.EndsWith(".job.json", StringComparison.Ordinal));
            File.WriteAllText(entry, File.ReadAllText(entry)
                .Replace("\"version\":1", "\"version\":9999", StringComparison.Ordinal));

            using (var second = new Runtime(_spool, graph))
            {
                Assert.AreEqual(0, second.Spool.Pending().Count,
                    "an entry a newer runtime wrote must be refused rather than half-understood");
            }

            Assert.AreEqual(0, SpoolFiles().Length, "and dropped, so it is not reconsidered every start");
        }

        [TestMethod]
        public async Task TwoInterruptedIdentitiesAreBothPickedUp()
        {
            var graph = new InMemoryGraphTarget();

            using (var first = new Runtime(_spool, graph, stopAfterChunks: 1))
            {
                await Assert.ThrowsExceptionAsync<RunInterruptedException>(
                    () => first.RunAsync(instanceId: "garage"));
            }

            using (var second = new Runtime(_spool, graph, stopAfterChunks: 1))
            {
                await Assert.ThrowsExceptionAsync<RunInterruptedException>(
                    () => second.RunAsync(instanceId: "office"));
            }

            using (var restarted = new Runtime(_spool, graph))
            {
                var resumed = await restarted.ResumeAllAsync();

                Assert.AreEqual(2, resumed.Count, "both interrupted runs have to be picked up, not just one");
                CollectionAssert.AreEquivalent(new[] { "garage", "office" },
                    resumed.Select(report => report.IntegrationInstanceId).ToArray());
            }

            Assert.AreEqual(0, SpoolFiles().Length);
        }

        [TestMethod]
        public async Task AResumedRunKeepsItsRunIdAndItsOriginalStart()
        {
            // Both matter to a client. The id, because F8 Studio polls by identity and compares the run id
            // it was given: a new id reads as a different run and the panel it was watching disappears. The
            // start time, because the elapsed figure people want is how long the import has been going -
            // outage included, since that is what actually elapsed.
            var graph = new InMemoryGraphTarget();
            String runId;
            String startedAt;

            using (var first = new Runtime(_spool, graph, stopAfterChunks: 1))
            {
                await Assert.ThrowsExceptionAsync<RunInterruptedException>(() => first.RunAsync());
                var spooled = first.Spool.Pending().Single();
                runId = spooled.RunId;
                startedAt = spooled.StartedAt;
            }

            Assert.IsFalse(String.IsNullOrWhiteSpace(runId), "the entry has to carry the run's own id");

            using (var second = new Runtime(_spool, graph))
            {
                await second.ResumeAllAsync();

                Assert.IsTrue(second.Tracker.TryGet(Instance, out var state));
                Assert.AreEqual(runId, state.RunId,
                    "a resumed run reported under a NEW id is a run the client was watching that vanished");
                Assert.IsTrue(state.Resumed, "and it says it was picked up rather than started here");
                Assert.AreEqual(DateTimeOffset.Parse(startedAt, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind).ToUniversalTime(),
                    DateTimeOffset.Parse(state.StartedAt, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind).ToUniversalTime(),
                    "and it keeps the start the import really had");
            }
        }

        [TestMethod]
        public async Task AResumedRunIsCancellableLikeAnyOther()
        {
            var graph = new InMemoryGraphTarget();
            using (var first = new Runtime(_spool, graph, stopAfterChunks: 1))
            {
                await Assert.ThrowsExceptionAsync<RunInterruptedException>(() => first.RunAsync());
            }

            using (var second = new Runtime(_spool, graph, cancelAfterChunks: 1))
            {
                var resumed = await second.ResumeAllAsync();

                Assert.AreEqual(1, resumed.Count);
                Assert.IsTrue(resumed[0].Cancelled,
                    "picking a run up must not make it unstoppable: " + Describe(resumed[0]));
            }

            Assert.AreEqual(0, SpoolFiles().Length,
                "and cancelling a resumed run ends it, so its entry goes with it");
        }

        #endregion

        #region helpers

        private String[] SpoolFiles()
        {
            return System.IO.Directory.Exists(_spool)
                ? System.IO.Directory.GetFiles(_spool)
                : Array.Empty<String>();
        }

        private static String Describe(JobReport report)
        {
            return String.Format(CultureInfo.InvariantCulture,
                "created {0}, matched {1}, withdrawn {2}, deleted {3}, embedded {4}, cancelled {5}, error {6}",
                report.ElementsCreated, report.ElementsMatched, report.ClaimsWithdrawn, report.ElementsDeleted,
                report.SummariesEmbedded, report.Cancelled, report.Error ?? "none");
        }

        /// <summary>
        ///   ONE RUNTIME PROCESS: its own runner, tracker and spool handle over a graph the test owns.
        ///   Throwing one away and building another over the same directory is what a container restart is,
        ///   and it is the only thing these tests do to simulate one - nothing is reached into.
        /// </summary>
        private sealed class Runtime : IDisposable
        {
            private readonly ILoggerFactory _loggers;
            private readonly IntegrationsMetrics _metrics;
            private readonly JobRunner _runner;
            private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
            private readonly StoppingProvider _provider;

            /// <param name="target">
            ///   A decorator to sit between the chunking wrapper and the graph, for a test that has to
            ///   observe what the run asked the graph to do ACROSS the restart. It outlives the runtime, as a
            ///   test-owned object rather than a per-process one.
            /// </param>
            /// <param name="graphRefuses">
            ///   The graph answers nothing, which is what this container sees when it restarts alongside the
            ///   graph and comes up first.
            /// </param>
            public Runtime(String spool, InMemoryGraphTarget graph, Int32 stopAfterChunks = -1,
                Int32 cancelAfterChunks = -1, Boolean stopDuringObserve = false,
                Boolean failTheSource = false, IGraphTarget target = null, Boolean graphRefuses = false)
            {
                _loggers = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.None));
                _metrics = new IntegrationsMetrics();
                Spool = new RunSpool(spool, NullLogger<RunSpool>.Instance);
                Tracker = new RunTracker();

                _provider = new StoppingProvider
                {
                    FailTheSource = failTheSource,
                    StopDuringObserve = stopDuringObserve ? _shutdown : null,
                };

                var vocabulary = IdentifierVocabulary.Shipped;
                var active = new ActiveCredentials();

                // The chunking target is the fixture that makes an interruption possible at all: the
                // in-memory graph embeds everything in one call, where the real target sends chunks and
                // looks at the stop signal between them. It mirrors that, and nothing else.
                var chunking = new ChunkedEmbeddingTarget(
                    graphRefuses ? new RefusingTarget(target ?? graph) : target ?? graph, ChunkSize,
                    chunks =>
                    {
                        if (stopAfterChunks >= 0 && chunks >= stopAfterChunks)
                        {
                            _shutdown.Cancel();
                        }

                        // Through the TRACKER, exactly as the cancel route does, so what a test cancels is
                        // the run's own signal rather than a token only the harness knows about.
                        if (cancelAfterChunks >= 0 && chunks >= cancelAfterChunks && _current != null)
                        {
                            Tracker.TryCancel(_current, out _);
                        }
                    });

                _runner = new JobRunner(
                    new ProviderCatalog(new IIntegrationProvider[] { _provider }, vocabulary),
                    new SnapshotValidator(vocabulary),
                    new SnapshotApplier(new IdentityResolver()),
                    new CredentialResolver(active),
                    new OneTarget(chunking),
                    new NoNetwork(),
                    new NoFiles(),
                    active,
                    new RunGate(),
                    _metrics,
                    _loggers,
                    Spool,
                    new RunShutdown(_shutdown.Token));
            }

            public RunSpool Spool { get; }

            public RunTracker Tracker { get; }

            /// <summary>Which identity this runtime is executing, so a cancel can name it.</summary>
            private String _current;

            /// <param name="generation">
            ///   Which version of the source this run sees. A later generation renames every device, so the
            ///   run has property writes to make and therefore summaries to embed - which is what makes a
            ///   SECOND run over an already-imported graph interruptible mid-embed at all. Without it the
            ///   run has nothing to embed, correctly finishes in one go, and there is no boundary to stop at.
            /// </param>
            public async Task<JobReport> RunAsync(String instanceId = Instance, String extraEntity = null,
                Int32 generation = 1)
            {
                _provider.ExtraEntity = extraEntity;
                _provider.Generation = generation;
                _current = instanceId;
                var job = new IntegrationJob
                {
                    ProviderId = Provider,
                    IntegrationInstanceId = instanceId,
                    EmbedSummaries = true,
                };

                // The handle's own token, exactly as the job route passes it, so a cancel in these tests
                // travels the same path a cancel from the route does.
                using var handle = Tracker.Begin(Guid.NewGuid().ToString("N"), Provider, instanceId, null,
                    embedRequested: true);
                var report = await _runner
                    .RunAsync(job, CancellationToken.None, handle, handle.CancellationToken)
                    .ConfigureAwait(false);
                Tracker.Finish(instanceId, handle.RunId, report);
                return report;
            }

            /// <summary>
            ///   Runs the REAL resume service to completion, which is what a start does. Driving the actual
            ///   hosted service rather than a copy of its loop is the point: the attempt counting, the
            ///   give-up bound and the honest slots for what cannot be resumed all live in it, and a test
            ///   harness that reimplemented them would be asserting against itself.
            /// </summary>
            public async Task<List<JobReport>> ResumeAllAsync()
            {
                var identities = new List<String>();
                foreach (var spooled in Spool.Pending())
                {
                    identities.Add(spooled.InstanceId);
                    _current = spooled.InstanceId;
                }

                var service = new RunResumeService(Spool, _runner, Tracker,
                    NullLogger<RunResumeService>.Instance);
                await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
                if (service.ExecuteTask != null)
                {
                    await service.ExecuteTask.ConfigureAwait(false);
                }

                var reports = new List<JobReport>();
                foreach (var identity in identities)
                {
                    if (Tracker.TryGet(identity, out var state) && state.Report != null)
                    {
                        reports.Add(state.Report);
                    }
                }

                return reports;
            }

            public void Dispose()
            {
                _shutdown.Dispose();
                _metrics.Dispose();
                _loggers.Dispose();
            }

            private sealed class OneTarget : IGraphTargetFactory
            {
                private readonly IGraphTarget _target;

                public OneTarget(IGraphTarget target)
                {
                    _target = target;
                }

                public IGraphTarget Create(String namespaceName)
                {
                    return _target;
                }
            }

            private sealed class NoNetwork : IProviderHttpFactory
            {
                public HttpClient Create(Boolean holdsCredential)
                {
                    return new HttpClient(new Refusing(), disposeHandler: true);
                }

                private sealed class Refusing : HttpMessageHandler
                {
                    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                        CancellationToken cancellationToken)
                    {
                        throw new NotSupportedException("This fixture's provider reads no source.");
                    }
                }
            }

            private sealed class NoFiles : IJobFilesFactory
            {
                public Int64 MaxFileBytes => 0;

                public Int64 MaxJobFileBytes => 0;

                public JobFiles Create(IReadOnlyDictionary<String, JobFileSet> filesBySettingKey)
                {
                    return new JobFiles(filesBySettingKey);
                }
            }
        }

        /// <summary>
        ///   Embeds in CHUNKS and looks at the stop signal between them, which is what the real target does
        ///   and what the in-memory graph - one call, all of it - cannot. The hook runs after each chunk, so
        ///   a test can stop a run in the middle of the one phase that runs for hours.
        /// </summary>
        private sealed class ChunkedEmbeddingTarget : DelegatingGraphTarget
        {
            private readonly Int32 _chunk;
            private readonly Action<Int32> _afterChunk;

            public ChunkedEmbeddingTarget(IGraphTarget inner, Int32 chunk, Action<Int32> afterChunk)
                : base(inner)
            {
                _chunk = chunk;
                _afterChunk = afterChunk;
            }

            public override async Task<EmbeddingWriteOutcome> EmbedSummariesAsync(String embeddingName,
                IReadOnlyList<SummaryWrite> summaries, CancellationToken cancellationToken,
                IRunProgress progress = null, RunAbort abort = default)
            {
                var written = 0;
                var chunks = 0;

                // The hook fires BEFORE the first chunk too, so a test can stop a run that has written its
                // elements and not yet embedded anything - the state the journal-ahead rule exists for.
                _afterChunk(chunks);

                for (var offset = 0; offset < summaries.Count; offset += _chunk)
                {
                    abort.ThrowIfRequested(written);

                    var take = Math.Min(_chunk, summaries.Count - offset);
                    for (var i = offset; i < offset + take; i++)
                    {
                        await base
                            .EmbedSummariesAsync(embeddingName, new[] { summaries[i] }, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    written += take;
                    chunks++;
                    progress?.Advance(written, summaries.Count);
                    _afterChunk(chunks);
                }

                abort.ThrowIfRequested(written);
                return new EmbeddingWriteOutcome(written, null);
            }
        }

        /// <summary>
        ///   A graph that answers nothing, which is what this container sees when it restarts alongside the
        ///   graph it writes into and comes up first.
        /// </summary>
        private sealed class RefusingTarget : DelegatingGraphTarget
        {
            public RefusingTarget(IGraphTarget inner)
                : base(inner)
            {
            }

            public override Task<ClaimLookup> ResolveClaimKeysAsync(IReadOnlyCollection<String> claimKeys,
                String instanceId, CancellationToken cancellationToken)
            {
                throw new GraphTargetException("the graph did not answer the claim lookup");
            }
        }

        /// <summary>
        ///   Counts the property-write calls a run makes, across a restart, which is the only way to ask
        ///   "did the resumed run believe anything had changed" from outside.
        /// </summary>
        private sealed class PropertyWriteCounter : DelegatingGraphTarget
        {
            public PropertyWriteCounter(IGraphTarget inner)
                : base(inner)
            {
            }

            public Int32 Calls { get; private set; }

            public override Task ApplyPropertyWritesAsync(IReadOnlyList<PropertyWrite> writes,
                CancellationToken cancellationToken)
            {
                Calls++;
                return base.ApplyPropertyWritesAsync(writes, cancellationToken);
            }
        }

        /// <summary>
        ///   Describes a fixed set of devices, and can stop while OBSERVING - which is the one boundary
        ///   before a run becomes resumable at all.
        /// </summary>
        private sealed class StoppingProvider : IIntegrationProvider
        {
            public Boolean FailTheSource { get; set; }

            public CancellationTokenSource StopDuringObserve { get; set; }

            /// <summary>An extra device, for the test that needs an element to withdraw later.</summary>
            public String ExtraEntity { get; set; }

            /// <summary>Which version of the source this is. A later one renames every device.</summary>
            public Int32 Generation { get; set; } = 1;

            public ProviderDescriptor Descriptor { get; } = new ProviderDescriptor
            {
                Id = Provider,
                DisplayName = "Resume fixture",
                Description = "Describes a fixed set of devices, reading nothing.",
                Settings = Array.Empty<ProviderSetting>(),
                EntityKinds = new[] { "device" },
                ClaimTypes = new[] { "mac" },
                RelationTypes = Array.Empty<String>(),
                EntitySummaryTemplate = "{kind} {csv.name}",
                CanObserveCompleteState = true,
                ReadOnly = true,
            };

            public Task<SnapshotDocument> ObserveAsync(ProviderContext context,
                CancellationToken cancellationToken)
            {
                if (FailTheSource)
                {
                    throw new ProviderSourceException("the console did not answer");
                }

                if (StopDuringObserve != null)
                {
                    StopDuringObserve.Cancel();
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var snapshot = new SnapshotDocument
                {
                    ProviderId = context.ProviderId,
                    IntegrationInstanceId = context.InstanceId,
                    Declares = SnapshotCompleteness.Complete,
                }.CapturedNow();

                var suffix = Generation == 1
                    ? String.Empty
                    : "-g" + Generation.ToString(CultureInfo.InvariantCulture);

                for (var i = 0; i < Entities; i++)
                {
                    snapshot.Entities.Add(Device(
                        String.Format(CultureInfo.InvariantCulture, "44:D2:44:AA:BB:{0:X2}", i),
                        "device-" + i.ToString(CultureInfo.InvariantCulture) + suffix));
                }

                if (ExtraEntity != null)
                {
                    snapshot.Entities.Add(Device(ExtraEntity, "going-away" + suffix));
                }

                return Task.FromResult(snapshot);
            }

            private static EntityDto Device(String mac, String name)
            {
                var entity = new EntityDto { Kind = "device" };
                entity.Claims.Add(new IdentityClaimDto { Type = "mac", Value = mac });
                entity.Properties["csv.name"] = name;

                // TYPED, not stringly, and that is the point of them being here: a spooled snapshot goes
                // through JSON, so a resumed run sees these as JsonElement where the first attempt saw real
                // CLR values. If the two rendered differently the resumed run would rewrite every property
                // of every element it matched, on every restart.
                entity.Properties["csv.port"] = 8080;
                entity.Properties["csv.active"] = true;
                return entity;
            }
        }

        #endregion
    }
}
