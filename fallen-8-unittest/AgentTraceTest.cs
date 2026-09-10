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
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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

        private static TraceStep Step(String kind)
        {
            return new TraceStep { Kind = kind };
        }
    }
}
