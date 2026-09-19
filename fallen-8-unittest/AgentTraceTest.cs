// MIT License
//
// AgentTraceTest.cs
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
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Agents.Configuration;
using NoSQL.GraphDB.Agents.Runtime;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The trace's own rules (feature agent-host, spec 3.2): the bound, the byte caps, and the
    ///   mechanical citation count.
    ///
    ///   <para>
    ///     Everything here is about the same property: a trace that has lost something must SAY it
    ///     has lost something. A bounded buffer and a capped capture are both fine; a bounded buffer
    ///     that reads like a complete run is not, because a reviewer would draw a conclusion from
    ///     what they could see.
    ///   </para>
    /// </summary>
    [TestClass]
    public class AgentTraceTest
    {
        private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-10T09:00:00Z");

        #region the bound

        [TestMethod]
        public void AnUnboundedTraceKeepsEverythingItWasGiven()
        {
            var trace = new AgentTrace(0);
            for (var i = 0; i < 50; i++)
            {
                trace.Record(Step("modelCall"), At);
            }

            Assert.AreEqual(50, trace.Steps().Count);
            Assert.AreEqual(0L, trace.Dropped);
            Assert.AreEqual(50L, trace.Recorded);
        }

        [TestMethod]
        public void PastItsBoundTheOldestGoAndAMarkerSaysHowMany()
        {
            var trace = new AgentTrace(5);
            for (var i = 1; i <= 12; i++)
            {
                var step = Step("modelCall");
                step.Model = "m" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                trace.Record(step, At);
            }

            var steps = trace.Steps();
            Assert.AreEqual(5, steps.Count, "the bound was not applied");

            // Never silent: the newest marker carries the RUNNING total, so it is the whole answer.
            var marker = steps.LastOrDefault(s => s.Kind == "dropped");
            Assert.IsNotNull(marker, "steps were dropped with no marker, so the trace reads as complete");
            Assert.AreEqual(trace.Dropped, marker.DroppedSteps);
            Assert.IsTrue(trace.Dropped > 0);

            // The newest real step survived: review needs recency.
            Assert.IsTrue(steps.Any(s => s.Model == "m12"), "the newest step was dropped");
            Assert.IsFalse(steps.Any(s => s.Model == "m1"), "the oldest step survived");
        }

        [TestMethod]
        public void TheSequenceNumbersDoNotRestartSoAGapIsItselfTheEvidence()
        {
            var trace = new AgentTrace(4);
            for (var i = 0; i < 20; i++)
            {
                trace.Record(Step("modelCall"), At);
            }

            var steps = trace.Steps();
            Assert.IsTrue(steps[0].Seq > 1,
                "the surviving steps start at 1, so a reader cannot tell that anything was dropped");

            // In order, and contiguous among the survivors: the gap is at the FRONT, which is where
            // the dropping happened.
            for (var i = 1; i < steps.Count; i++)
            {
                Assert.AreEqual(steps[i - 1].Seq + 1, steps[i].Seq);
            }

            Assert.AreEqual(steps[^1].Seq, trace.Recorded);
        }

        [TestMethod]
        public void AMarkerCannotItselfPushTheBufferOverTheBound()
        {
            // The marker replaces what it reports on. Getting this wrong grows the buffer by one
            // every time it overflows, which is a slow leak in the one structure that exists to be
            // bounded.
            var trace = new AgentTrace(3);
            for (var i = 0; i < 200; i++)
            {
                trace.Record(Step("modelCall"), At);
            }

            Assert.AreEqual(3, trace.Steps().Count);
        }

        [TestMethod]
        public void TheVeryFIRSTOverflowAlreadyStaysInsideTheBound()
        {
            // The case every other bound test missed, because they all overshoot by 20 to 500 steps
            // and so only ever measure steady state. The marker's row was reserved only on rounds
            // where a marker ALREADY existed, so the round that created it dropped one step and then
            // added a row: measured, a bound of 5 held 6 rows after the sixth step, and stayed
            // correct forever after. One row over, once, on every trace that ever truncates.
            var trace = new AgentTrace(5);
            for (var i = 1; i <= 5; i++)
            {
                trace.Record(Step("modelCall"), At);
            }

            Assert.AreEqual(5, trace.Steps().Count, "the bound was hit exactly, so nothing goes yet");
            Assert.IsFalse(trace.Steps().Any(s => s.Kind == "dropped"),
                "nothing was dropped, so there is nothing for a marker to report");

            trace.Record(Step("modelCall"), At);

            var steps = trace.Steps();
            Assert.AreEqual(5, steps.Count,
                "the first overflow left " + steps.Count + " rows against a bound of 5");
            Assert.AreEqual(1, steps.Count(s => s.Kind == "dropped"));
            Assert.AreEqual(4, steps.Count(s => s.Kind != "dropped"),
                "the marker takes one of the five rows from the moment it exists");
            Assert.AreEqual(2L, trace.Dropped,
                "two had to go, not one: the marker needs a row of its own");
        }

        [TestMethod]
        public void ABoundBelowTwoIsFlooredRatherThanSilentlyDoubled()
        {
            // A single row cannot hold both a step and the news that steps were lost, and quietly
            // keeping both would hand an operator who configured 1 a view of 2. The floor is the
            // honest version of the same behaviour, and it is documented on the setting.
            foreach (var configured in new[] { 1, 2 })
            {
                var trace = new AgentTrace(configured);
                for (var i = 0; i < 40; i++)
                {
                    trace.Record(Step("modelCall"), At);
                }

                var steps = trace.Steps();
                Assert.AreEqual(2, steps.Count,
                    "a bound of " + configured + " held " + steps.Count + " rows");
                Assert.AreEqual("dropped", steps[0].Kind);
                Assert.AreEqual(39L, trace.Dropped);
                Assert.AreEqual(trace.Recorded - trace.Dropped,
                    steps.Count(s => s.Kind != "dropped"));
            }
        }

        [TestMethod]
        public void ThereIsExactlyONEDropMarkerHoweverManyTimesTheBoundIsHit()
        {
            // The test the first bound tests should have been. They pinned only the buffer's Count,
            // so a marker appended per overflow round passed them while the buffer converged to
            // alternating real steps and markers: measured, a bound of 1000 after 3000 steps held
            // 500 real steps and 500 markers.
            var trace = new AgentTrace(10);
            for (var i = 0; i < 500; i++)
            {
                trace.Record(Step("modelCall"), At);
            }

            var steps = trace.Steps();
            var markers = steps.Where(s => s.Kind == "dropped").ToList();

            Assert.AreEqual(1, markers.Count, "one marker, not one per overflow round");
            Assert.AreSame(steps[0], markers[0], "the marker belongs at the FRONT, where the loss was");
            Assert.AreEqual(10, steps.Count, "the whole view stays inside the bound");
            Assert.AreEqual(9, steps.Count(s => s.Kind != "dropped"),
                "the marker takes one row, so nine real steps survive a bound of ten");
            Assert.AreEqual(trace.Dropped, markers[0].DroppedSteps,
                "the marker carries the running total");
        }

        [TestMethod]
        public void RecordedAndDroppedCountAnAgentsWorkAndNotTheClassesOwnBookkeeping()
        {
            var trace = new AgentTrace(5);
            for (var i = 0; i < 100; i++)
            {
                trace.Record(Step("modelCall"), At);
            }

            Assert.AreEqual(100L, trace.Recorded,
                "a marker took a sequence number, so the count of steps ever recorded was inflated");

            // 100 recorded, 4 real steps still held, so 96 went. Markers are not steps and must not
            // appear in either number.
            Assert.AreEqual(96L, trace.Dropped);
            Assert.AreEqual(4, trace.Steps().Count(s => s.Kind != "dropped"));
            Assert.AreEqual(trace.Recorded - trace.Dropped,
                trace.Steps().Count(s => s.Kind != "dropped"),
                "recorded minus dropped has to equal what is held, or the numbers are not counting "
                + "the same thing");
        }

        [TestMethod]
        public void AMarkersSequenceMakesItReadAsConsecutiveWithTheStepAfterIt()
        {
            var trace = new AgentTrace(4);
            for (var i = 0; i < 20; i++)
            {
                trace.Record(Step("modelCall"), At);
            }

            var steps = trace.Steps();
            Assert.AreEqual("dropped", steps[0].Kind);
            Assert.AreEqual(steps[0].Seq + 1, steps[1].Seq,
                "the marker and the step after it should read as consecutive: this many went, and "
                + "the record resumes here");
        }

        [TestMethod]
        public void ABoundOfOneStillHoldsTheStepJustRecorded()
        {
            // The degenerate bound, which the old shape got wrong in the worst way: the step just
            // recorded was dequeued to make room for a marker, so Record returned a step that was
            // not in the trace and ToolsCalled() was always empty, which would have made
            // GroundingCheck dangle every citation on that host.
            var trace = new AgentTrace(1);

            var call = Step("toolCall");
            call.Tool = "count_vertices";
            var recorded = trace.Record(call, At);

            trace.Record(Step("modelCall"), At);

            Assert.IsTrue(trace.ToolsCalled().Contains("count_vertices"),
                "a tool call vanished from the grounding set at a bound of one");
            Assert.AreEqual("count_vertices", recorded.Tool);
        }

        [TestMethod]
        public void AToolNameSurvivesTheStepBeingDroppedFromTheBuffer()
        {
            // The grounding check counts against what the run CALLED, not against what the buffer
            // still holds. Counting against the buffer made a citation to a real early call dangle
            // on any run long enough to overflow, which is a false accusation of fabrication.
            var trace = new AgentTrace(3);

            var call = Step("toolCall");
            call.Tool = "count_vertices";
            trace.Record(call, At);

            for (var i = 0; i < 50; i++)
            {
                trace.Record(Step("modelCall"), At);
            }

            Assert.IsFalse(trace.Steps().Any(s => s.Tool == "count_vertices"),
                "this test needs the tool-call step to have been dropped");
            Assert.IsTrue(trace.ToolsCalled().Contains("count_vertices"),
                "the tool name did not survive its step being dropped");
            Assert.AreEqual(1, GroundingCheck.Count("8 [t:count_vertices]", trace.ToolsCalled()).Valid);
        }

        [TestMethod]
        public void ASnapshotDoesNotChangeUnderTheCallerWhenMoreStepsAreDropped()
        {
            // The marker is one object mutated in place, so a snapshot has to copy it. Otherwise a
            // response already being serialized would change its own numbers mid-flight.
            var trace = new AgentTrace(3);
            for (var i = 0; i < 10; i++)
            {
                trace.Record(Step("modelCall"), At);
            }

            var before = trace.Steps();
            var reported = before[0].DroppedSteps;

            for (var i = 0; i < 10; i++)
            {
                trace.Record(Step("modelCall"), At);
            }

            Assert.AreEqual(reported, before[0].DroppedSteps,
                "the earlier snapshot's marker changed when more steps were dropped");
            Assert.AreNotEqual(reported, trace.Steps()[0].DroppedSteps,
                "and a fresh snapshot should show the new total");
        }

        [TestMethod]
        public void TheViewReportsRowsAndTotalsFromOneSnapshot()
        {
            var trace = new AgentTrace(5);
            for (var i = 0; i < 30; i++)
            {
                trace.Record(Step("modelCall"), At);
            }

            var view = trace.View();

            Assert.AreEqual(30L, view.Recorded);
            Assert.AreEqual(view.Steps.Count(s => s.Kind == "dropped") == 1 ? 26L : 0L, view.Dropped);
            Assert.AreEqual(view.Recorded - view.Dropped, view.Steps.Count(s => s.Kind != "dropped"));
        }

        [TestMethod]
        public void ACappedCaptureIncludingItsMarkerStaysInsideTheConfiguredCap()
        {
            // ArgsBytes is what a stored capture costs. Capping and THEN appending the marker put it
            // over, so the setting was the cost before the marker rather than the cost.
            var capped = TraceStepKinds.CapWithMarker(new String('x', 5000), 200,
                out var total, out var truncated);

            Assert.IsTrue(truncated);
            Assert.AreEqual(5000L, total);
            Assert.IsTrue(Encoding.UTF8.GetByteCount(capped) <= 200,
                "the capture plus its marker is " + Encoding.UTF8.GetByteCount(capped)
                + " bytes against a cap of 200");
            StringAssert.Contains(capped, "5000");
        }

        [TestMethod]
        public void ACapTooSmallForTheMarkerKeepsTheMarkerRatherThanAFragment()
        {
            // The marker says there was something and how much; a few bytes of payload says
            // neither, so it is the half worth keeping.
            var capped = TraceStepKinds.CapWithMarker(new String('x', 5000), 4, out _, out var truncated);

            Assert.IsTrue(truncated);
            StringAssert.Contains(capped, "5000");
            Assert.IsFalse(capped.StartsWith("xxxx", StringComparison.Ordinal));
        }

        [TestMethod]
        public void AnUncutCaptureGetsNoMarker()
        {
            var capped = TraceStepKinds.CapWithMarker("hello", 200, out var total, out var truncated);

            Assert.AreEqual("hello", capped);
            Assert.AreEqual(5L, total);
            Assert.IsFalse(truncated);
        }

        [TestMethod]
        public void TheTailIsTheNewestStepsAndNeverMoreThanExist()
        {
            var trace = new AgentTrace(0);
            for (var i = 1; i <= 10; i++)
            {
                var step = Step("modelCall");
                step.Model = "m" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                trace.Record(step, At);
            }

            var tail = trace.Tail(3);
            CollectionAssert.AreEqual(new[] { "m8", "m9", "m10" }, tail.Select(s => s.Model).ToArray());

            Assert.AreEqual(10, trace.Tail(50).Count, "a tail longer than the trace is the trace");
            Assert.AreEqual(10, trace.Tail(0).Count, "a non-positive tail is not a silent empty answer");
        }

        [TestMethod]
        public void OnlyToolCallStepsContributeToolNames()
        {
            var trace = new AgentTrace(0);
            trace.Record(Step("modelCall"), At);

            var call = Step("toolCall");
            call.Tool = "count_vertices";
            trace.Record(call, At);

            var second = Step("toolCall");
            second.Tool = "COUNT_VERTICES";
            trace.Record(second, At);

            var names = trace.ToolsCalled();
            Assert.AreEqual(1, names.Count, "a tool name is matched case-insensitively");
            Assert.IsTrue(names.Contains("count_vertices"));
        }

        #endregion

        #region the byte caps

        [TestMethod]
        public void ACaptureInsideTheCapIsUntouchedAndReportsItsSize()
        {
            var capped = TraceStepKinds.Cap("hello", 100, out var total, out var truncated);

            Assert.AreEqual("hello", capped);
            Assert.AreEqual(5L, total);
            Assert.IsFalse(truncated);
        }

        [TestMethod]
        public void ACaptureOverTheCapIsCutAndReportsTheORIGINALSize()
        {
            var capped = TraceStepKinds.Cap(new String('x', 5000), 100, out var total, out var truncated);

            Assert.AreEqual(100, capped.Length);
            Assert.IsTrue(truncated);
            Assert.AreEqual(5000L, total,
                "the reported size must be of the original, since it is what tells a reader how much "
                + "they are not seeing");
        }

        [TestMethod]
        public void ANonPositiveCapMeansNoCapRatherThanNoCapture()
        {
            var text = new String('x', 5000);

            Assert.AreEqual(text, TraceStepKinds.Cap(text, 0, out _, out var zeroCut));
            Assert.IsFalse(zeroCut);
            Assert.AreEqual(text, TraceStepKinds.Cap(text, -1, out _, out var negativeCut));
            Assert.IsFalse(negativeCut);
        }

        [TestMethod]
        public void ACutNeverLandsInsideAMultiByteCharacter()
        {
            // The reason this is not a substring: a cut mid-sequence is invalid UTF-8, so a reviewer
            // would get a replacement character or a serializer failure instead of the text. Three
            // bytes each, and a cap that does not divide evenly by three.
            var text = String.Concat(Enumerable.Repeat("中", 100));

            var capped = TraceStepKinds.Cap(text, 10, out var total, out var truncated);

            Assert.IsTrue(truncated);
            Assert.AreEqual(300L, total);
            Assert.AreEqual(9, Encoding.UTF8.GetByteCount(capped),
                "the cut landed mid-character: 10 bytes is three whole characters plus one byte");
            Assert.AreEqual(3, capped.Length);

            // And it round-trips, which is the property that actually matters.
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(capped),
                Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(capped))));
        }

        [TestMethod]
        public void ASurrogatePairIsKeptWholeOrDroppedWhole()
        {
            // An emoji is one four-byte sequence and two chars. A cap of 2 admits neither half, and
            // half a pair is an unpaired surrogate, which is not valid text at all.
            var text = "\U0001F600\U0001F600";

            var capped = TraceStepKinds.Cap(text, 2, out var total, out var truncated);

            Assert.IsTrue(truncated);
            Assert.AreEqual(8L, total);
            Assert.AreEqual(String.Empty, capped, "half a surrogate pair was kept");

            var one = TraceStepKinds.Cap(text, 5, out _, out _);
            Assert.AreEqual("\U0001F600", one, "one whole pair fits in five bytes and should be kept");
        }

        [TestMethod]
        public void ANullCaptureStaysNullRatherThanBecomingEmpty()
        {
            // The difference is real to a reader: a tool that returned nothing and a tool whose
            // result was the empty string are not the same event.
            Assert.IsNull(TraceStepKinds.Cap(null, 100, out var total, out var truncated));
            Assert.AreEqual(0L, total);
            Assert.IsFalse(truncated);
        }

        [TestMethod]
        public void TheTruncationMarkerNamesTheTotalSize()
        {
            StringAssert.Contains(TraceStepKinds.Marker(5000), "5000");
            StringAssert.Contains(TraceStepKinds.Marker(5000), "truncated");
        }

        #endregion

        #region the citation count

        [TestMethod]
        public void ACitationNamingAToolThatWasCalledIsValid()
        {
            var counts = GroundingCheck.Count(
                "The default namespace has 8 vertices. [t:count_vertices]",
                new[] { "count_vertices" });

            Assert.AreEqual(1, counts.Valid);
            Assert.AreEqual(0, counts.Dangling);
        }

        [TestMethod]
        public void ACitationNamingATooolThatWasNeverCalledDangles()
        {
            var counts = GroundingCheck.Count(
                "There are 40 edges. [t:count_edges]",
                new[] { "count_vertices" });

            Assert.AreEqual(0, counts.Valid);
            Assert.AreEqual(1, counts.Dangling);
        }

        [TestMethod]
        public void AnAnswerWithNoCitationsCountsZeroOfBothWhichIsTheFabricationShape()
        {
            // The shape a fabricating run has, and the one this count exists to make visible: every
            // figure asserted, nothing cited, no tool called.
            var counts = GroundingCheck.Count(
                "The default namespace contains 10,000 vertices.", Array.Empty<String>());

            Assert.AreEqual(0, counts.Valid);
            Assert.AreEqual(0, counts.Dangling);
        }

        [TestMethod]
        public void OccurrencesAreCountedRatherThanDistinctNames()
        {
            // Ten figures should carry ten citations. Collapsing them would make an answer that
            // cited its first figure and asserted the other nine look fully grounded.
            var counts = GroundingCheck.Count(
                "8 vertices [t:count_vertices], 12 edges [t:count_vertices], 3 labels [t:count_vertices]",
                new[] { "count_vertices" });

            Assert.AreEqual(3, counts.Valid);
        }

        [TestMethod]
        public void TheOccurrenceSuffixIsToleratedBecauseASmallModelReproducesAFormatApproximately()
        {
            var counts = GroundingCheck.Count(
                "8 [t:count_vertices], and again 9 [t:count_vertices#2], and [t: count_vertices #3]",
                new[] { "count_vertices" });

            Assert.AreEqual(3, counts.Valid,
                "a real citation counted as absent is worse than a lenient marker");
            Assert.AreEqual(0, counts.Dangling);
        }

        [TestMethod]
        public void AToolNameIsMatchedCaseInsensitivelyButIsNotOtherwiseGuessedAt()
        {
            Assert.AreEqual(1, GroundingCheck.Count("[t:Count_Vertices]", new[] { "count_vertices" }).Valid);

            // Not fuzzy: the name is the part being checked, so a near miss dangles.
            Assert.AreEqual(1, GroundingCheck.Count("[t:count_vertice]", new[] { "count_vertices" }).Dangling);
        }

        [TestMethod]
        public void NothingCitableInTheTextIsZeroRatherThanAnError()
        {
            foreach (var text in new String[] { null, "", "   ", "[t:]", "[t]", "[tool:x]", "[t:a b]" })
            {
                var counts = GroundingCheck.Count(text, new[] { "count_vertices" });
                Assert.AreEqual(0, counts.Valid, "text: " + (text ?? "(null)"));
                Assert.AreEqual(0, counts.Dangling, "text: " + (text ?? "(null)"));
            }
        }

        [TestMethod]
        public void ACitationWithNoToolsCalledAtAllDanglesRatherThanThrowing()
        {
            var counts = GroundingCheck.Count("[t:count_vertices]", null);

            Assert.AreEqual(0, counts.Valid);
            Assert.AreEqual(1, counts.Dangling);
        }

        #endregion

        #region the filter grammar

        [TestMethod]
        public void AnUnknownEventKindIsRefusedWithTheAcceptedSetNamed()
        {
            // A parser and not a compiler: a subscriber whose typo produced silence cannot tell it
            // from an idle host, and would wait for events that were being discarded.
            Assert.IsFalse(AgentEventKinds.TryParse(null, new[] { "agentExploded" }, out _, out var problem));
            StringAssert.Contains(problem, "agentExploded");
            StringAssert.Contains(problem, "agentSpawned");
            StringAssert.Contains(problem, "toolCalled");
        }

        [TestMethod]
        public void KindsArriveCommaSeparatedOrRepeatedAndAreMatchedCaseInsensitively()
        {
            Assert.IsTrue(AgentEventKinds.TryParse(null, new[] { "agentSpawned,toolCalled" },
                out var commas, out _));
            Assert.AreEqual(2, commas.Kinds.Count);

            Assert.IsTrue(AgentEventKinds.TryParse(null, new[] { "agentSpawned", "toolCalled" },
                out var repeated, out _));
            Assert.AreEqual(2, repeated.Kinds.Count);

            Assert.IsTrue(AgentEventKinds.TryParse(null, new[] { "AGENTSPAWNED" }, out var shouted, out _));
            Assert.AreEqual(1, shouted.Kinds.Count);
        }

        [TestMethod]
        public void NoFilterMeansEverythingAndAnEmptyValueIsNotAnError()
        {
            Assert.IsTrue(AgentEventKinds.TryParse(null, null, out var none, out _));
            Assert.IsNull(none.Kinds, "no filter must mean every kind, not no kind");
            Assert.IsNull(none.Agents);

            // "?kinds=" is how a client says "no filter"; refusing it would make the empty case an
            // error for no reason.
            Assert.IsTrue(AgentEventKinds.TryParse(new String[] { "" }, new String[] { "" },
                out var blank, out _));
            Assert.IsNull(blank.Kinds);
            Assert.IsNull(blank.Agents);
        }

        [TestMethod]
        public void TheEmittedKindsAreASubsetOfTheAcceptedOnesAndTheDifferenceIsNamed()
        {
            // A kind the filter accepts and nothing emits leaves a subscriber waiting forever for
            // an event that cannot arrive, which is the failure this feed's parser-not-compiler
            // stance prevents everywhere else. The difference is reported rather than refused,
            // because refusing would break a client's filter the day the conversation route lands.
            var accepted = AgentEventKinds.Names;
            var emitted = AgentEventKinds.Emitted;

            foreach (var kind in emitted)
            {
                Assert.IsTrue(accepted.Contains(kind, StringComparer.OrdinalIgnoreCase),
                    "an emitted kind the filter would refuse: " + kind);
            }

            Assert.AreEqual(6, accepted.Count);
            Assert.AreEqual(5, emitted.Count);
            Assert.IsFalse(emitted.Contains("agentMessage", StringComparer.OrdinalIgnoreCase),
                "agentMessage is emitted by nothing until the deferred conversation route exists: "
                + "the swarm shipped and publishes none. If that changed, add it to Emitted so the "
                + "status route stops understating what a subscriber can receive");
        }

        [TestMethod]
        public void TheEmittedSetIsCheckedAgainstTheCODERatherThanAgainstItself()
        {
            // The test above was described, in a commit message and in findings.md, as failing when
            // the two lists stop matching the CODE. It observes no code: it checks a subset
            // relation between two hand-written lists and pins their counts. So the one drift
            // direction that was explicitly worried about, something starting to call
            // AgentJournal.Message while Emitted still omits agentMessage, failed nothing, and
            // GET /agent/status would have told every client that agentMessage cannot arrive while
            // it was arriving.
            //
            // This reads the product SOURCES, in the same spirit as the convention gate: the one
            // method that publishes agentMessage is AgentJournal.Message, so whether it has a
            // caller is exactly the question Emitted answers.
            var product = Path.Combine(TestRepo.Root(), "fallen-8-agents");
            var callers = new List<String>();

            foreach (var file in Directory.EnumerateFiles(product, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                        StringComparison.Ordinal)
                    || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
                        StringComparison.Ordinal)
                    || Path.GetFileName(file) == "AgentJournal.cs")
                {
                    // AgentJournal declares it; a call from inside its own file would be the
                    // declaration, not a use.
                    continue;
                }

                var text = File.ReadAllText(file);
                if (text.Contains("_journal.Message(", StringComparison.Ordinal)
                    || text.Contains("Journal.Message(", StringComparison.Ordinal))
                {
                    callers.Add(Path.GetFileName(file));
                }
            }

            var emitsMessages = AgentEventKinds.Emitted
                .Contains("agentMessage", StringComparer.OrdinalIgnoreCase);

            Assert.AreEqual(callers.Count > 0, emitsMessages,
                callers.Count > 0
                    ? "AgentJournal.Message is now called from " + String.Join(", ", callers)
                        + ", so agentMessage IS emitted and belongs in AgentEventKinds.Emitted; "
                        + "GET /agent/status is currently telling clients it cannot arrive"
                    : "nothing calls AgentJournal.Message, so agentMessage must not be advertised "
                        + "as emitted: a subscriber filtering on it would wait forever");
        }

        [TestMethod]
        public void ARefusalNamesBothWhatIsAcceptedAndWhatIsActuallyEmitted()
        {
            Assert.IsFalse(AgentEventKinds.TryParse(null, new[] { "agentExploded" }, out _, out var problem));

            StringAssert.Contains(problem, "agentMessage", "the accepted set has to be complete");
            StringAssert.Contains(problem, "emitted today",
                "a caller told only the accepted set can pick one that never arrives");
        }

        [TestMethod]
        public void AKindFilterAdmitsOnlyThatKind()
        {
            Assert.IsTrue(AgentEventKinds.TryParse(null, new[] { "toolCalled" }, out var filter, out _));

            Assert.IsTrue(filter.Admits(new AgentEvent { Kind = "toolCalled", AgentId = "a1" }));
            Assert.IsFalse(filter.Admits(new AgentEvent { Kind = "agentSpawned", AgentId = "a1" }));
        }

        [TestMethod]
        public void AnAgentFilterAlsoAdmitsTheWorkersThatAgentSpawned()
        {
            // Subscribing to an orchestrator should show the swarm it is running, not only its own
            // two events. Without this a caller would have to discover worker ids first, which is
            // the thing the feed exists to tell it.
            Assert.IsTrue(AgentEventKinds.TryParse(new[] { "boss" }, null, out var filter, out _));

            Assert.IsTrue(filter.Admits(new AgentEvent { Kind = "agentSpawned", AgentId = "boss" }));
            Assert.IsTrue(filter.Admits(new AgentEvent
            {
                Kind = "agentSpawned",
                AgentId = "worker-1",
                ParentId = "boss",
            }));
            Assert.IsFalse(filter.Admits(new AgentEvent { Kind = "agentSpawned", AgentId = "someone-else" }));
        }

        [TestMethod]
        public void AnAgentIdIsNotValidatedBecauseSubscribingBeforeASpawnIsLegitimate()
        {
            // There is no catch-up buffer, so a caller that wants to miss nothing has to be able to
            // subscribe first and spawn second.
            Assert.IsTrue(AgentEventKinds.TryParse(new[] { "an-agent-that-does-not-exist-yet" }, null,
                out var filter, out _));
            Assert.AreEqual(1, filter.Agents.Count);
        }

        #endregion

        #region the dispatcher's own bounds

        [TestMethod]
        public async Task ASubscriberPastTheQueueBoundIsDroppedRatherThanSilentlyThinned()
        {
            // THE test whose absence let a real defect ship green. The queue was created with
            // FullMode.DropWrite, and TryWrite returns TRUE on a full DropWrite channel and discards
            // the event: only FullMode.Wait reports a full queue. So the dispatcher's "drop the
            // subscriber" branch was dead code and a lagging subscriber was silently thinned, which
            // is the one failure this design exists to prevent.
            using var feed = Dispatcher(maxQueued: 3);

            Assert.IsTrue(feed.TrySubscribe(AgentFeedFilter.All, out var subscription, out _));
            using (subscription)
            {
                // Published without reading, so the queue fills and then overflows.
                for (var i = 0; i < 10; i++)
                {
                    feed.Publish(Event("agentStateChanged", "a1"), At);
                }

                // What survived is a PREFIX, and then the stream ENDS. It does not continue with
                // later events, which is what "dropped rather than thinned" means.
                // BOUNDED, and the bound is the point rather than caution: a dropped subscriber's
                // stream ends, so a read that never returns IS the regression. Left unbounded this
                // test wedged the suite under the old drop mode instead of failing it, which is the
                // one outcome worse than not testing at all.
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var seen = new List<Int64>();
                try
                {
                    while (true)
                    {
                        var next = await subscription.ReadAsync(budget.Token);
                        if (next == null)
                        {
                            break;
                        }

                        seen.Add(next.Seq);
                    }
                }
                catch (OperationCanceledException)
                {
                    Assert.Fail("the stream never ended: the subscriber was thinned rather than "
                        + "dropped, having seen " + seen.Count + " event(s)");
                }

                Assert.AreEqual(3, seen.Count, "the queue bound was not applied");
                CollectionAssert.AreEqual(new[] { 1L, 2L, 3L }, seen.ToArray(),
                    "a thinned subscriber would have seen later events too");

                // INSIDE the using, which is the whole value of the assertion. Outside it, the
                // scope has already disposed the subscription and Dispose removes it from the
                // table, so the count read zero whether or not the drop removed anything: the
                // assertion pinned Dispose and its message described the drop.
                Assert.AreEqual(0, feed.SubscriberCount,
                    "the dropped subscriber was left in the table, so it still reads as an open "
                    + "stream and still holds a MaxSubscribers slot");
            }
        }

        [TestMethod]
        public void ADroppedSubscriberReleasesItsSlotAndIsReportedOnce()
        {
            // The consequences of leaving a completed subscriber IN the table, which is the half of
            // dropping that shows. Its channel is full and complete forever, so TryWrite keeps
            // failing and every later publish re-enters the drop branch: measured, one slow reader
            // and eleven further events produced eleven warnings for one drop, and the slot stayed
            // taken until the reader disposed - which a reader that has stopped reading is
            // precisely the one not about to do. A host would run out of subscriber slots with no
            // stream open.
            using var sink = new TestLogSink();
            using var feed = Dispatcher(maxQueued: 2, maxSubscribers: 1, sink: sink);

            Assert.IsTrue(feed.TrySubscribe(AgentFeedFilter.All, out var slow, out _));
            Assert.IsFalse(feed.TrySubscribe(AgentFeedFilter.All, out _, out _),
                "this test needs the single slot to be taken");

            // Never read, so the queue of two fills and the next publish drops the subscriber. The
            // ten after that are the point: they must not each report the drop again.
            for (var i = 0; i < 13; i++)
            {
                feed.Publish(Event("agentStateChanged", "a1"), At);
            }

            var warnings = sink.Entries
                .Count(e => e.Level >= LogLevel.Warning
                    && e.Message.Contains("MaxQueuedEvents", StringComparison.Ordinal));

            Assert.AreEqual(1, warnings,
                "one subscriber was dropped once, so one warning: " + warnings + " were logged, "
                + "which is one per event published after the drop");
            Assert.AreEqual(0, feed.SubscriberCount);
            Assert.IsTrue(feed.TrySubscribe(AgentFeedFilter.All, out var replacement, out var problem),
                "the dropped subscriber still held the only slot: " + problem);

            replacement.Dispose();
            slow.Dispose();
        }

        [TestMethod]
        public async Task ASubscriberThatKeepsUpIsNeverDropped()
        {
            // The control arm: the bound must not fire on a reader that is reading, or the feed
            // would be useless for its actual purpose.
            using var feed = Dispatcher(maxQueued: 2);

            Assert.IsTrue(feed.TrySubscribe(AgentFeedFilter.All, out var subscription, out _));
            using (subscription)
            {
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                for (var i = 0; i < 20; i++)
                {
                    feed.Publish(Event("agentStateChanged", "a1"), At);
                    var next = await subscription.ReadAsync(budget.Token);
                    Assert.IsNotNull(next, "a subscriber that read every event was dropped");
                }

                Assert.AreEqual(1, feed.SubscriberCount);
            }
        }

        [TestMethod]
        public async Task DeliveryOrderIsSequenceOrderUnderConcurrentPublishers()
        {
            // Stamping the sequence under the lock and writing outside it let two publishing agents
            // interleave, so a subscriber could receive seq 7 before seq 6. With no catch-up buffer,
            // out of order is indistinguishable from loss.
            using var feed = Dispatcher(maxQueued: 4096);

            Assert.IsTrue(feed.TrySubscribe(AgentFeedFilter.All, out var subscription, out _));
            using (subscription)
            {
                const Int32 Publishers = 8;
                const Int32 Each = 60;

                await Task.WhenAll(Enumerable.Range(0, Publishers).Select(p => Task.Run(() =>
                {
                    for (var i = 0; i < Each; i++)
                    {
                        feed.Publish(Event("agentStateChanged", "a" + p), At);
                    }
                })));

                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var seen = new List<Int64>();
                for (var i = 0; i < Publishers * Each; i++)
                {
                    AgentEvent next = null;
                    try
                    {
                        next = await subscription.ReadAsync(budget.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        Assert.Fail("the feed stopped delivering after " + seen.Count + " of "
                            + (Publishers * Each) + " events");
                    }

                    Assert.IsNotNull(next, "an event was lost: " + seen.Count + " of "
                        + (Publishers * Each));
                    seen.Add(next.Seq);
                }

                var sorted = seen.OrderBy(s => s).ToList();
                CollectionAssert.AreEqual(sorted, seen,
                    "events arrived out of sequence order, which a subscriber cannot tell from loss");
                Assert.AreEqual(Publishers * Each, seen.Distinct().Count(),
                    "a sequence number was reused");
            }
        }

        [TestMethod]
        public void TheSubscriberBoundIsEnforcedAndTheRefusalNamesTheLimit()
        {
            using var feed = Dispatcher(maxSubscribers: 2);

            Assert.IsTrue(feed.TrySubscribe(AgentFeedFilter.All, out var first, out _));
            Assert.IsTrue(feed.TrySubscribe(AgentFeedFilter.All, out var second, out _));
            Assert.IsFalse(feed.TrySubscribe(AgentFeedFilter.All, out _, out var problem));
            StringAssert.Contains(problem, "MaxSubscribers");

            // A slot is released by disposing, so a browser that closed a tab does not cost a
            // subscriber slot for the life of the process.
            first.Dispose();
            Assert.AreEqual(1, feed.SubscriberCount);
            Assert.IsTrue(feed.TrySubscribe(AgentFeedFilter.All, out var third, out _));

            second.Dispose();
            third.Dispose();
            Assert.AreEqual(0, feed.SubscriberCount);
        }

        [TestMethod]
        public async Task DisposingTheDispatcherEndsEveryOpenStream()
        {
            // A host shutting down has to end its streams, or a subscriber waits on a feed that will
            // never produce another event.
            var feed = Dispatcher();

            Assert.IsTrue(feed.TrySubscribe(AgentFeedFilter.All, out var subscription, out _));
            using (subscription)
            {
                feed.Dispose();

                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                Assert.IsNull(await subscription.ReadAsync(budget.Token),
                    "the stream did not end when the host stopped");
                Assert.AreEqual(0, feed.SubscriberCount);
            }

            // Idempotent, and a publish after disposal is a no-op rather than a throw: the shutdown
            // path is exactly when a last event may still be in flight.
            feed.Dispose();
            feed.Publish(Event("agentFailed", "a1"), At);
        }

        [TestMethod]
        public async Task AFilterIsAppliedAtPublishSoAnUninterestedSubscriberQueuesNothing()
        {
            // The filter has to keep events OUT of the queue, not be applied on the way out: a
            // subscriber watching one agent must not be dropped because a different agent was busy.
            using var feed = Dispatcher(maxQueued: 2);

            Assert.IsTrue(AgentEventKinds.TryParse(new[] { "mine" }, null, out var filter, out _));
            Assert.IsTrue(feed.TrySubscribe(filter, out var subscription, out _));
            using (subscription)
            {
                for (var i = 0; i < 50; i++)
                {
                    feed.Publish(Event("agentStateChanged", "someone-else"), At);
                }

                feed.Publish(Event("agentStateChanged", "mine"), At);

                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var next = await subscription.ReadAsync(budget.Token);
                Assert.IsNotNull(next, "the subscriber was dropped by traffic it had filtered out");
                Assert.AreEqual("mine", next.AgentId);
            }
        }

        [TestMethod]
        public void PublishedCountsEveryEventStampedRegardlessOfSubscribers()
        {
            using var feed = Dispatcher();

            Assert.AreEqual(0L, feed.Published);
            var one = feed.Publish(Event("agentSpawned", "a1"), At);
            var two = feed.Publish(Event("agentSpawned", "a2"), At);

            Assert.AreEqual(1L, one.Seq);
            Assert.AreEqual(2L, two.Seq);
            Assert.AreEqual(2L, feed.Published,
                "the count has to hold with no subscriber, or the status route reads zero on a busy host");
        }

        private static AgentFeedDispatcher Dispatcher(Int32 maxQueued = 512, Int32 maxSubscribers = 16,
            TestLogSink sink = null)
        {
            var options = new AgentsOptions();
            options.Feed.MaxQueuedEvents = maxQueued;
            options.Feed.MaxSubscribers = maxSubscribers;

            var factory = sink == null ? TestLoggerFactory.Create() : sink.CreateFactory();
            return new AgentFeedDispatcher(Options.Create(options),
                factory.CreateLogger<AgentFeedDispatcher>());
        }

        private static AgentEvent Event(String kind, String agentId)
        {
            return new AgentEvent { Kind = kind, AgentId = agentId };
        }

        #endregion

        private static TraceStep Step(String kind)
        {
            return new TraceStep { Kind = kind };
        }
    }
}
