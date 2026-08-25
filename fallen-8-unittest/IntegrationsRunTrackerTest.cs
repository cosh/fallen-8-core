// MIT License
//
// IntegrationsRunTrackerTest.cs
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
using System.Globalization;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Integrations.Run;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The run tracker (feature integration-run-visibility): the one place this runtime remembers
    ///   anything about a run. Each test here pins a rule that, if inverted, either loses the progress
    ///   somebody is watching or turns the slot into the run history the runtime is not allowed to keep.
    /// </summary>
    [TestClass]
    public class IntegrationsRunTrackerTest
    {
        [TestMethod]
        public void ARunThatNeverStarted_LeavesNoTrace()
        {
            var tracker = new RunTracker();

            tracker.Begin("run-1", "csv-device-list", "office", null);

            // Begin is not "started" - the first PHASE is. Everything that can reject a job is judged inside
            // the run, so a slot opened at Begin would be a slot opened for jobs that never ran.
            Assert.IsFalse(tracker.TryGet("office", out _),
                "a job that was rejected before it ran is reported as a run that happened");
            Assert.AreEqual(0, tracker.All().Count);
        }

        [TestMethod]
        public void ARejectedSecondRun_DoesNotDisturbTheOneAlreadyInFlight()
        {
            // THE crux. The commonest rejection is 409 "already running as this identity", and the caller who
            // gets it is asking about the run that holds the gate. An eager slot would have destroyed exactly
            // that run's progress at exactly the moment somebody asked for it.
            var tracker = new RunTracker();
            var running = tracker.Begin("run-1", "csv-device-list", "office", "default");
            running.EnterPhase(RunPhases.Observe);
            running.Advance(3, 10);

            tracker.Begin("run-2", "csv-device-list", "office", "default");

            Assert.IsTrue(tracker.TryGet("office", out var state));
            Assert.AreEqual("run-1", state!.RunId, "the in-flight run was displaced by one that never started");
            Assert.AreEqual(RunPhases.Observe, state.Phase);
            Assert.AreEqual(3, state.PhaseDone);
        }

        [TestMethod]
        public void PhasesAccumulateInOrder_AndTheCurrentOneIsNotAlsoCompleted()
        {
            var tracker = new RunTracker();
            var progress = tracker.Begin("run-1", "csv-device-list", "office", null);

            progress.EnterPhase(RunPhases.Observe);
            progress.EnterPhase(RunPhases.Validate);
            progress.EnterPhase(RunPhases.WriteElements);

            Assert.IsTrue(tracker.TryGet("office", out var state));
            CollectionAssert.AreEqual(new[] { RunPhases.Observe, RunPhases.Validate },
                state!.CompletedPhases, "a phase is completed when the NEXT one starts, and not before");
            Assert.AreEqual(RunPhases.WriteElements, state.Phase);
        }

        [TestMethod]
        public void EnteringAPhase_ResetsTheCounter_SoOnePhaseCannotInheritAnother()
        {
            var tracker = new RunTracker();
            var progress = tracker.Begin("run-1", "csv-device-list", "office", null);

            progress.EnterPhase(RunPhases.WriteElements);
            progress.Advance(500, 500);
            progress.EnterPhase(RunPhases.EmbedSummaries);

            Assert.IsTrue(tracker.TryGet("office", out var state));
            Assert.AreEqual(0, state!.PhaseDone,
                "the new phase reports the previous phase's progress, so embedding looks finished before it began");
            Assert.AreEqual(0, state.PhaseTotal);
        }

        [TestMethod]
        public void AdvanceBeforeAnyPhase_IsDropped_RatherThanInventingAPhaselessRun()
        {
            var tracker = new RunTracker();
            var progress = tracker.Begin("run-1", "csv-device-list", "office", null);

            progress.Advance(1, 2);

            Assert.IsFalse(tracker.TryGet("office", out _),
                "a counter with no phase opened a slot, so a rejected job can still appear as a run");
        }

        [TestMethod]
        public void AFinishedRun_KeepsItsReport_BecauseThatIsTheWholePoint()
        {
            var tracker = new RunTracker();
            var progress = tracker.Begin("run-1", "csv-device-list", "office", null);
            progress.EnterPhase(RunPhases.Observe);

            tracker.Finish("office", new JobReport { ElementsCreated = 7 });

            Assert.IsTrue(tracker.TryGet("office", out var state));
            Assert.IsFalse(state!.Running, "a finished run still reports itself as running");
            Assert.IsNotNull(state.Report, "the outcome is gone, which is the failure this feature exists to fix");
            Assert.AreEqual(7, state.Report!.ElementsCreated);
            Assert.IsNull(state.Phase, "a finished run is still shown as being in a phase");
        }

        [TestMethod]
        public void ARunThatThrew_ReportsTheError_RatherThanStayingInFlightForever()
        {
            var tracker = new RunTracker();
            var progress = tracker.Begin("run-1", "csv-device-list", "office", null);
            progress.EnterPhase(RunPhases.EmbedSummaries);

            tracker.Abort("office", "the graph refused the embedding write with 400");

            Assert.IsTrue(tracker.TryGet("office", out var state));
            Assert.IsFalse(state!.Running);
            Assert.IsNull(state.Report, "a run that produced no report is shown as having one");
            StringAssert.Contains(state.Error, "400");
        }

        [TestMethod]
        public void ANewIdentityBeyondTheCap_EvictsTheOldestFINISHEDRun()
        {
            var tracker = new RunTracker();
            for (var i = 0; i < RunTracker.MaxIdentities; i++)
            {
                var id = "id-" + i.ToString(CultureInfo.InvariantCulture);
                var progress = tracker.Begin("run-" + i.ToString(CultureInfo.InvariantCulture),
                    "csv-device-list", id, null);
                progress.EnterPhase(RunPhases.Observe);
                tracker.Finish(id, new JobReport());
            }

            Assert.AreEqual(RunTracker.MaxIdentities, tracker.All().Count);

            var newest = tracker.Begin("run-new", "csv-device-list", "late-arrival", null);
            newest.EnterPhase(RunPhases.Observe);

            Assert.AreEqual(RunTracker.MaxIdentities, tracker.All().Count, "the cap is not enforced");
            Assert.IsFalse(tracker.TryGet("id-0", out _), "the OLDEST finished run survived instead of the newer ones");
            Assert.IsTrue(tracker.TryGet("id-1", out _), "more than the oldest was evicted");
            Assert.IsTrue(tracker.TryGet("late-arrival", out _));
        }

        [TestMethod]
        public void AnInFlightRun_IsNeverEvicted_EvenWhenThatBreaksTheCap()
        {
            // Dropping the one run somebody is watching, in order to remember runs that already ended, would
            // invert the whole point of the type. Exceeding the cap is the lesser evil and is deliberate.
            var tracker = new RunTracker();
            for (var i = 0; i < RunTracker.MaxIdentities; i++)
            {
                var id = "id-" + i.ToString(CultureInfo.InvariantCulture);
                tracker.Begin("run-" + i.ToString(CultureInfo.InvariantCulture), "csv-device-list", id, null)
                    .EnterPhase(RunPhases.Observe);
            }

            tracker.Begin("run-new", "csv-device-list", "late-arrival", null).EnterPhase(RunPhases.Observe);

            Assert.AreEqual(RunTracker.MaxIdentities + 1, tracker.All().Count,
                "an in-flight run was evicted to make room, so the run being watched is the one that vanished");
            Assert.IsTrue(tracker.All().All(r => r.Running));
        }

        [TestMethod]
        public void TheSameIdentityRunningAgain_SupersedesItsOwnSlot_WhichIsWhyThisIsNotAHistory()
        {
            var tracker = new RunTracker();
            var first = tracker.Begin("run-1", "csv-device-list", "office", null);
            first.EnterPhase(RunPhases.Observe);
            tracker.Finish("office", new JobReport { ElementsCreated = 1 });

            var second = tracker.Begin("run-2", "csv-device-list", "office", null);
            second.EnterPhase(RunPhases.Observe);

            Assert.AreEqual(1, tracker.All().Count, "two runs of one identity are both remembered, which is a log");
            Assert.IsTrue(tracker.TryGet("office", out var state));
            Assert.AreEqual("run-2", state!.RunId);
            Assert.IsNull(state.Report, "the new run reports the previous run's outcome as its own");
        }

        [TestMethod]
        public void TheIdentityLookupIsCaseInsensitive_BecauseTheJobBoundaryLowercasesIt()
        {
            // The runtime folds the identity to lower case at the job boundary, but a caller polls with
            // whatever it typed. A case-sensitive slot would answer 404 for a run that is right there.
            var tracker = new RunTracker();
            tracker.Begin("run-1", "csv-device-list", "Office", null).EnterPhase(RunPhases.Observe);

            Assert.IsTrue(tracker.TryGet("office", out var state));
            Assert.AreEqual("run-1", state!.RunId);
        }

        [TestMethod]
        public void TheStartedSignal_CompletesOnTheFirstPhaseAndNotBefore()
        {
            // What the job route awaits to tell a 202 from a 400: a run that never entered a phase never
            // started, and that is the definition the route relies on.
            var tracker = new RunTracker();
            var handle = tracker.Begin("run-1", "csv-device-list", "office", null);

            Assert.IsFalse(handle.Started.IsCompleted);

            handle.EnterPhase(RunPhases.Observe);

            Assert.IsTrue(handle.Started.IsCompleted);
        }
    }
}
