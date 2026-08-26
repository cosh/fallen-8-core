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
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Integrations.Run;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   What the run tracker will and will not say about a run, at the level where saying it wrongly is a
    ///   one-line mistake: whether a run can be stopped, and what a stopped one looks like afterwards.
    ///
    ///   <para>Kept off the HTTP surface on purpose. A test that needs a run to still be IN FLIGHT while it
    ///   asserts has to hold one there, and holding one through a real host means a sleep and a race; here
    ///   the slot is simply not finished yet, which is the same state without the timing.</para>
    /// </summary>
    [TestClass]
    public class IntegrationsRunTrackerTest
    {
        private const String Provider = "csv-device-list";

        #region what may be cancelled

        [TestMethod]
        public void AnIdentityThatNeverRan_CannotBeCancelled()
        {
            var tracker = new RunTracker();

            Assert.IsFalse(tracker.TryCancel("never-ran", out var state),
                "cancelling an identity with no run must not answer as though it stopped something");
            Assert.IsNull(state);
        }

        [TestMethod]
        public void ARunThatHasNotReachedItsFirstPhase_CannotBeCancelledYet()
        {
            // The slot materialises on the first phase, deliberately, so that a REJECTED job cannot overwrite
            // the slot of the run it was rejected for. The honest consequence is this window: for the moment
            // between accepting a job and its first phase there is nothing tracked to stop, and 404 says so
            // rather than inventing a slot.
            var tracker = new RunTracker();
            using var handle = tracker.Begin("run-1", Provider, "pending", null);

            Assert.IsFalse(tracker.TryCancel("pending", out _),
                "a slot was materialised by a cancel, which is the bug the deferred slot exists to prevent");
        }

        [TestMethod]
        public void ARunInFlight_IsCancelled_AndSaysSoAtOnce()
        {
            var tracker = new RunTracker();
            using var handle = tracker.Begin("run-1", Provider, "office", null);
            handle.EnterPhase(RunPhases.Observe);

            Assert.IsTrue(tracker.TryCancel("office", out var state));

            Assert.IsNotNull(state);
            Assert.IsTrue(state.CancelRequested,
                "the answer carries the state as it is NOW, so a client need not wait for its next poll to " +
                "show that the stop was recorded");
            Assert.IsTrue(state.Running, "the run has not stopped yet: a stop is a request, not an event");
            Assert.IsTrue(handle.Abort.Requested,
                "the signal never reached the run, so nothing will ever observe it");
        }

        [TestMethod]
        public void CancellingTwice_IsNotAnError()
        {
            // A stop is honoured at the next safe point, and for the embedding phase that is after the chunk
            // already in the model. So a second click means "yes, still stopping" and must not read as
            // "nothing is happening".
            var tracker = new RunTracker();
            using var handle = tracker.Begin("run-1", Provider, "office", null);
            handle.EnterPhase(RunPhases.EmbedSummaries);

            Assert.IsTrue(tracker.TryCancel("office", out _));
            Assert.IsTrue(tracker.TryCancel("office", out var again),
                "the second ask answered as though there were no run, which reads as 'it already stopped'");
            Assert.IsTrue(again.CancelRequested);
        }

        [TestMethod]
        public void ARunThatAlreadyEnded_CannotBeCancelled()
        {
            var tracker = new RunTracker();
            using var handle = tracker.Begin("run-1", Provider, "office", null);
            handle.EnterPhase(RunPhases.Reconcile);
            tracker.Finish("office", "run-1", new JobReport());

            Assert.IsFalse(tracker.TryCancel("office", out _),
                "cancelling a finished run must not answer 202: a client would believe it had prevented " +
                "writes that had already landed");
        }

        [TestMethod]
        public void CancellingOneIdentity_LeavesAnotherAlone()
        {
            var tracker = new RunTracker();
            using var mine = tracker.Begin("run-1", Provider, "mine", null);
            using var theirs = tracker.Begin("run-2", Provider, "theirs", null);
            mine.EnterPhase(RunPhases.Observe);
            theirs.EnterPhase(RunPhases.Observe);

            Assert.IsTrue(tracker.TryCancel("mine", out _));

            Assert.IsFalse(theirs.Abort.Requested,
                "the gate and this tracker are keyed by identity, and a stop that crossed identities would " +
                "abort somebody else's import");
            Assert.IsTrue(tracker.TryGet("theirs", out var other) && !other.CancelRequested);
        }

        #endregion

        #region what a stopped run looks like afterwards

        [TestMethod]
        public void ACancelledRun_RecordsThePhaseItStoppedIn_RatherThanCompletingIt()
        {
            var tracker = new RunTracker();
            using var handle = tracker.Begin("run-1", Provider, "office", null);
            handle.EnterPhase(RunPhases.EmbedSummaries);

            tracker.Finish("office", "run-1", new JobReport { Cancelled = true, SummariesEmbedded = 32 });

            Assert.IsTrue(tracker.TryGet("office", out var state));
            Assert.IsTrue(state.Cancelled, "the terminal state has to name itself, or the panel guesses");
            Assert.AreEqual(RunPhases.EmbedSummaries, state.StoppedInPhase,
                "a cancelled report carries no errorKind, so keying on that alone completes the very phase " +
                "the run was stopped in the middle of");
            Assert.IsFalse(state.CompletedPhases.Contains(RunPhases.EmbedSummaries),
                "and it must not also be reported as finished: it embedded 32 of an unknown total");
            Assert.IsFalse(state.Running);
        }

        [TestMethod]
        public void AStopThatArrivedTooLate_ShowsARunThatFinished_NotACancelledOne()
        {
            // A reachable state, not a corner case: the last safe point is before reconciliation, so a stop
            // asked for during it loses the race. Reporting that run as cancelled would claim writes were
            // prevented when the import actually completed.
            var tracker = new RunTracker();
            using var handle = tracker.Begin("run-1", Provider, "office", null);
            handle.EnterPhase(RunPhases.Reconcile);
            Assert.IsTrue(tracker.TryCancel("office", out _));

            tracker.Finish("office", "run-1", new JobReport { ElementsCreated = 7 });

            Assert.IsTrue(tracker.TryGet("office", out var state));
            Assert.IsFalse(state.Cancelled, "the run finished, so it was not cancelled");
            Assert.IsTrue(state.CancelRequested,
                "but somebody DID ask, and dropping that leaves them unable to tell 'too late' from 'the " +
                "button did nothing'");
            Assert.IsTrue(state.CompletedPhases.Contains(RunPhases.Reconcile),
                "and the phase it was in really did complete");
            Assert.IsNull(state.StoppedInPhase);
        }

        [TestMethod]
        public void AFailedRun_IsStillNotACancelledOne()
        {
            // A regression guard on the branch the cancelled case was threaded into: 'failed' and 'cancelled'
            // are different answers to "why did this end", and one must not start reporting the other.
            var tracker = new RunTracker();
            using var handle = tracker.Begin("run-1", Provider, "office", null);
            handle.EnterPhase(RunPhases.Observe);

            tracker.Finish("office", "run-1",
                new JobReport { ErrorKind = JobErrorKinds.Source, Error = "the console did not answer" });

            Assert.IsTrue(tracker.TryGet("office", out var state));
            Assert.IsFalse(state.Cancelled);
            Assert.IsFalse(state.CancelRequested);
            Assert.AreEqual(RunPhases.Observe, state.StoppedInPhase);
        }

        [TestMethod]
        public void ASucceededRun_ReportsNeitherFlag()
        {
            var tracker = new RunTracker();
            using var handle = tracker.Begin("run-1", Provider, "office", null);
            handle.EnterPhase(RunPhases.Reconcile);

            tracker.Finish("office", "run-1", new JobReport { ElementsCreated = 3 });

            Assert.IsTrue(tracker.TryGet("office", out var state));
            Assert.IsFalse(state.Cancelled);
            Assert.IsFalse(state.CancelRequested);
        }

        [TestMethod]
        public void AStaleFinish_CannotStampACancellationOntoTheNextRun()
        {
            // The run-id scoping this tracker already had, asserted for the new flags too: the gate is
            // released when a run returns and its report is recorded a moment later, so a second run under the
            // same identity can open its own slot in between.
            var tracker = new RunTracker();
            using var first = tracker.Begin("run-1", Provider, "office", null);
            first.EnterPhase(RunPhases.Observe);
            using var second = tracker.Begin("run-2", Provider, "office", null);
            second.EnterPhase(RunPhases.Observe);

            tracker.Finish("office", "run-1", new JobReport { Cancelled = true });

            Assert.IsTrue(tracker.TryGet("office", out var state));
            Assert.AreEqual("run-2", state.RunId);
            Assert.IsFalse(state.Cancelled,
                "the older run's cancellation was stamped onto the run actually in flight, which would read " +
                "as a run that stopped while it is still going");
            Assert.IsTrue(state.Running);
        }

        #endregion
    }
}
