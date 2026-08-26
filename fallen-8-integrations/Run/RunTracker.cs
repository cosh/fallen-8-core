// MIT License
//
// RunTracker.cs
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
using System.Threading;
using System.Threading.Tasks;
using NoSQL.GraphDB.Integrations.Contract;

namespace NoSQL.GraphDB.Integrations.Run
{
    /// <summary>
    ///   What is happening NOW, and what happened LAST, per integration identity. Nothing else.
    ///
    ///   <para>This runtime deliberately keeps no schedule, no run history and no credential, and that
    ///   rule is load-bearing rather than tidy. So the boundary this type sits on has to be stated
    ///   exactly: <b>one slot per identity, superseded by that identity's next run.</b> There is no list
    ///   of past runs, nothing is queryable by time, nothing is persisted, and a restart loses all of
    ///   it. That is not history - it is the state of a run somebody is watching.</para>
    ///
    ///   <para>It exists because the report was unreachable. The job route's response is the only copy
    ///   of a report this runtime produces, any real source outlives the proxy that would carry it, and
    ///   the run is built on purpose to outlive its caller. Without a slot to read afterwards, a
    ///   two-hour import that half-succeeded could report that to nobody, ever - which is exactly what
    ///   happened to a real many-entity import before this existed.</para>
    /// </summary>
    public sealed class RunTracker
    {
        /// <summary>
        ///   How many identities are remembered. Bounded because the key is caller-supplied: a caller
        ///   that invents an identity per run would otherwise grow this without limit, which is the same
        ///   accumulation the identity rules warn about, one layer up.
        /// </summary>
        public const Int32 MaxIdentities = 32;

        private readonly Object _gate = new Object();
        private readonly Dictionary<String, Slot> _byInstance =
            new Dictionary<String, Slot>(StringComparer.OrdinalIgnoreCase);

        // Monotonic, not a clock: eviction needs an order, and two runs starting in the same tick must
        // still be orderable. The wall clock is reported, never compared.
        private Int64 _sequence;

        /// <summary>
        ///   Hands back the sink for a run that is about to be attempted. The slot is NOT opened yet: it
        ///   materialises on the run's first phase.
        ///
        ///   <para>Deferred on purpose, and the reason is a bug it avoids. Everything that can reject a job
        ///   - its shape, the provider, the identity, the files, and the run gate - is judged inside the
        ///   run, so opening the slot up front would mean a REJECTED job overwrote the slot of the run it
        ///   was rejected for. The commonest rejection is 409 "already running as this identity", so eager
        ///   opening would destroy exactly the progress the caller is asking about, at exactly the moment
        ///   they ask. A run that never started leaves no trace here instead.</para>
        /// </summary>
        /// <param name="runId">Supplied by the caller so a test can name it.</param>
        /// <param name="providerId">Which integration is running, echoed to readers.</param>
        /// <param name="instanceId">The identity, which is also this slot's key.</param>
        /// <param name="namespaceName">The namespace being written into, or null for the default.</param>
        /// <param name="embedRequested">Whether the job asked for summary embedding, which is a fact about
        /// the run and belongs with it rather than in a client's component state.</param>
        /// <param name="resumed">Whether this is a run PICKED UP after a restart rather than a new one.</param>
        /// <param name="startedUtc">
        ///   When the run originally began, for a resumed one. A resumed run keeps its first start, because
        ///   the elapsed time a reader wants is how long the import has been going - outage included, since
        ///   that is what actually elapsed. Null means now, which is every fresh run.
        /// </param>
        public RunHandle Begin(String runId, String providerId, String instanceId, String? namespaceName,
            Boolean embedRequested = false, Boolean resumed = false, DateTimeOffset? startedUtc = null)
        {
            if (String.IsNullOrWhiteSpace(instanceId))
            {
                throw new ArgumentException("An integration instance id is required.", nameof(instanceId));
            }

            return new RunHandle(this, runId, providerId, instanceId, namespaceName, embedRequested, resumed,
                startedUtc);
        }

        /// <summary>Opens the slot, replacing whatever that identity held before. Called on the first phase.</summary>
        private Slot Materialise(String runId, String providerId, String instanceId, String? namespaceName,
            Boolean embedRequested, CancellationTokenSource cancellation, Boolean resumed,
            DateTimeOffset? startedUtc)
        {
            if (!_byInstance.ContainsKey(instanceId))
            {
                EvictIfFull();
            }

            var slot = new Slot
            {
                RunId = runId,
                ProviderId = providerId,
                InstanceId = instanceId,
                Namespace = namespaceName,
                EmbedRequested = embedRequested,
                Resumed = resumed,
                StartedUtc = startedUtc ?? DateTimeOffset.UtcNow,
                Sequence = ++_sequence,
                Phase = null,

                // The stop signal lands on the SLOT because the slot is already the one thing that knows a
                // run is in flight under this identity. A separate registry keyed by identity would be a
                // second answer to that question, and the two would disagree the first time one was
                // updated without the other.
                Cancellation = cancellation,
            };
            _byInstance[instanceId] = slot;
            return slot;
        }

        /// <summary>
        ///   Asks the run in flight under this identity to stop at its next safe point. Returns FALSE when
        ///   there is nothing in flight to stop, which is what the route answers 404 to: a run that already
        ///   ended is not cancellable, and its slot already says what it ended as.
        ///
        ///   <para>Asking twice is not an error and answers TRUE both times. The signal is a request, not an
        ///   event: the run reaches a safe point when it reaches one, and for the embedding phase that is
        ///   after the chunk already in the model. So a second click means "yes, still stopping" rather than
        ///   "nothing happened", and <see cref="RunStateDto.CancelRequested" /> is what a reader watches in
        ///   between.</para>
        /// </summary>
        /// <param name="instanceId">The identity whose run should stop.</param>
        /// <param name="state">The run as it is at the moment the stop was recorded, so a caller need not
        /// wait for its next poll to see <c>cancelRequested</c>.</param>
        public Boolean TryCancel(String instanceId, out RunStateDto? state)
        {
            CancellationTokenSource? cancellation;

            lock (_gate)
            {
                if (instanceId == null || !_byInstance.TryGetValue(instanceId, out var slot) ||
                    slot.FinishedUtc != null)
                {
                    state = null;
                    return false;
                }

                slot.CancelRequested = true;
                cancellation = slot.Cancellation;
                state = slot.ToDto();
            }

            // OUTSIDE the lock. Cancel() runs every registered callback synchronously on this thread, and
            // one of them is the linked source the run handed its provider, whose continuation aborts an
            // HTTP read; running arbitrary continuations while holding the lock that every progress call
            // and every poll also takes is how a deadlock gets built. The state above was already captured,
            // so nothing here needs the lock back.
            try
            {
                cancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The run finished and disposed its source between the two blocks. Nothing to stop, and the
                // slot the caller was handed already says the run ended.
            }

            return true;
        }

        /// <summary>
        ///   Records the report of a run that finished, successfully or not.
        ///
        ///   <para>Scoped by RUN ID, and that is not defensive dressing. The run gate is released when the
        ///   run returns, and the report is recorded a moment later, so a second run under the same identity
        ///   can be accepted and open its own slot in between. Writing by identity alone would then stamp the
        ///   previous run's report onto the new run's slot: a run actually in flight would read as finished,
        ///   with counts from a run that already ended. A stale finish is dropped instead.</para>
        /// </summary>
        public void Finish(String instanceId, String runId, JobReport report)
        {
            lock (_gate)
            {
                if (_byInstance.TryGetValue(instanceId, out var slot) &&
                    String.Equals(slot.RunId, runId, StringComparison.Ordinal))
                {
                    slot.FinishedUtc = DateTimeOffset.UtcNow;
                    slot.Report = report;

                    // Read off the REPORT rather than off the request: a stop that was asked for after the
                    // run's last safe point does not cancel anything, the run finishes, and its report says
                    // so. CancelRequested stays true beside this, which is what lets a reader tell "stopped
                    // because you asked" from "you asked, and it had already finished".
                    slot.Cancelled = report != null && report.Cancelled;

                    // The phase a run ENDED in has to go somewhere, or every successful import shows its
                    // last phase as never having run. A clean finish completes it; a failed one records
                    // where it stopped, which is the more useful fact of the two. A CANCELLED run is the
                    // third case and belongs with the second: it carries no errorKind, so keying on that
                    // alone would complete the very phase it was stopped in the middle of.
                    if (slot.Phase != null)
                    {
                        if (String.IsNullOrEmpty(report?.ErrorKind) && !slot.Cancelled)
                        {
                            if (!slot.Completed.Contains(slot.Phase))
                            {
                                slot.Completed.Add(slot.Phase);
                            }
                        }
                        else
                        {
                            slot.StoppedInPhase = slot.Phase;
                        }
                    }

                    slot.Phase = null;
                }
            }
        }

        /// <summary>
        ///   Records a run that did not produce a report at all - it threw. Distinct from a run that
        ///   FAILED, which has a report carrying its own errorKind: this is the case where there is
        ///   nothing but the exception, and reporting nothing would leave the slot in flight forever.
        /// </summary>
        public void Abort(String instanceId, String runId, String error)
        {
            lock (_gate)
            {
                if (_byInstance.TryGetValue(instanceId, out var slot) &&
                    String.Equals(slot.RunId, runId, StringComparison.Ordinal))
                {
                    slot.FinishedUtc = DateTimeOffset.UtcNow;
                    slot.Error = error;
                    slot.StoppedInPhase = slot.Phase;
                    slot.Phase = null;
                }
            }
        }

        /// <summary>
        ///   Keeps the run's task reachable. A fire-and-forget task whose exception nobody observes is a
        ///   process-level unhandled rejection waiting to happen, and this is the one place that can hold
        ///   it without inventing a scheduler.
        /// </summary>
        public void Attach(String instanceId, String runId, Task task)
        {
            lock (_gate)
            {
                if (_byInstance.TryGetValue(instanceId, out var slot) &&
                    String.Equals(slot.RunId, runId, StringComparison.Ordinal))
                {
                    slot.Task = task;
                }
            }
        }

        /// <summary>Every tracked run, newest first.</summary>
        public IReadOnlyList<RunStateDto> All()
        {
            lock (_gate)
            {
                var all = new List<RunStateDto>(_byInstance.Count);
                foreach (var slot in _byInstance.Values)
                {
                    all.Add(slot.ToDto());
                }

                all.Sort((left, right) => right.Sequence.CompareTo(left.Sequence));
                return all;
            }
        }

        /// <summary>One identity's run, current or most recent.</summary>
        public Boolean TryGet(String instanceId, out RunStateDto? state)
        {
            lock (_gate)
            {
                if (instanceId != null && _byInstance.TryGetValue(instanceId, out var slot))
                {
                    state = slot.ToDto();
                    return true;
                }

                state = null;
                return false;
            }
        }

        /// <summary>
        ///   Makes room for a new identity by dropping the oldest FINISHED slot. An in-flight run is
        ///   never evicted: dropping the one thing somebody is watching, to remember a run that already
        ///   ended, would invert the whole point. If every slot is in flight, nothing is dropped and the
        ///   cap is exceeded until one ends - a bound worth breaking, because 32 concurrent runs is a
        ///   deployment problem this type must not make worse by going blind.
        /// </summary>
        private void EvictIfFull()
        {
            if (_byInstance.Count < MaxIdentities)
            {
                return;
            }

            String? oldest = null;
            var oldestSequence = Int64.MaxValue;
            foreach (var pair in _byInstance)
            {
                if (pair.Value.FinishedUtc == null || pair.Value.Sequence >= oldestSequence)
                {
                    continue;
                }

                oldest = pair.Key;
                oldestSequence = pair.Value.Sequence;
            }

            if (oldest != null)
            {
                _byInstance.Remove(oldest);
            }
        }

        private sealed class Slot
        {
            public String RunId = String.Empty;
            public String ProviderId = String.Empty;
            public String InstanceId = String.Empty;
            public String? Namespace;
            public DateTimeOffset StartedUtc;
            public DateTimeOffset? FinishedUtc;
            public Int64 Sequence;
            public String? Phase;
            public String? StoppedInPhase;
            public Boolean EmbedRequested;
            public Int32 PhaseDone;
            public Int32 PhaseTotal;
            public Boolean CancelRequested;
            public Boolean Cancelled;
            public Boolean Resumed;
            public CancellationTokenSource? Cancellation;
            public readonly List<String> Completed = new List<String>();
            public JobReport? Report;
            public String? Error;
            public Task? Task;

            public RunStateDto ToDto()
            {
                return new RunStateDto
                {
                    CancelRequested = CancelRequested,
                    Cancelled = Cancelled,
                    Resumed = Resumed,
                    RunId = RunId,
                    ProviderId = ProviderId,
                    IntegrationInstanceId = InstanceId,
                    Namespace = Namespace,
                    StartedAt = StartedUtc.ToString("O", CultureInfo.InvariantCulture),
                    FinishedAt = FinishedUtc?.ToString("O", CultureInfo.InvariantCulture),
                    Running = FinishedUtc == null,
                    ElapsedMilliseconds =
                        (Int64)((FinishedUtc ?? DateTimeOffset.UtcNow) - StartedUtc).TotalMilliseconds,
                    Phase = Phase,
                    StoppedInPhase = StoppedInPhase,
                    EmbedRequested = EmbedRequested,
                    PhaseDone = PhaseDone,
                    PhaseTotal = PhaseTotal,
                    CompletedPhases = Completed.ToArray(),
                    Report = Report,
                    Error = Error,
                    Sequence = Sequence,
                };
            }
        }

        /// <summary>
        ///   The sink bound to one slot. It never throws and never blocks, because a run whose graph
        ///   writes have landed must not be failed by the thing that describes it.
        /// </summary>
        /// <summary>
        ///   One run's reporting handle. Public because the job route needs the one thing only this knows:
        ///   whether the run REALLY STARTED, which is what separates a 202 from a 400 or a 409. Exposed as a
        ///   task rather than a flag so the route can await it instead of polling for it - the difference
        ///   between a deterministic answer and a timing-dependent one.
        /// </summary>
        public sealed class RunHandle : IRunProgress, IIdentifiedRun, IDisposable
        {
            private readonly TaskCompletionSource _started =
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>
            ///   The run's own stop signal, created with the handle rather than with the slot: the slot
            ///   materialises on the first phase, and the run needs something to carry from the moment it
            ///   is handed to <c>JobRunner</c>. A cancel arriving before the first phase finds no slot and
            ///   is answered 404, which is honest - there is nothing tracked to stop yet.
            /// </summary>
            private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();

            private readonly RunTracker _tracker;
            private readonly String _runId;
            private readonly String _providerId;
            private readonly String _instanceId;
            private readonly String? _namespace;
            private readonly Boolean _embedRequested;
            private readonly Boolean _resumed;
            private readonly DateTimeOffset? _startedUtc;
            private Slot? _slot;

            public RunHandle(RunTracker tracker, String runId, String providerId, String instanceId,
                String? namespaceName, Boolean embedRequested, Boolean resumed = false,
                DateTimeOffset? startedUtc = null)
            {
                _tracker = tracker;
                _runId = runId;
                _providerId = providerId;
                _instanceId = instanceId;
                _namespace = namespaceName;
                _embedRequested = embedRequested;
                _resumed = resumed;
                _startedUtc = startedUtc;
            }

            public void EnterPhase(String phase)
            {
                lock (_tracker._gate)
                {
                    // First phase IS "the run started", which is the only moment a slot may be opened.
                    if (_slot == null)
                    {
                        _slot = _tracker.Materialise(_runId, _providerId, _instanceId, _namespace,
                            _embedRequested, _cancellation, _resumed, _startedUtc);
                        _started.TrySetResult();
                    }

                    if (_slot.Phase != null && !_slot.Completed.Contains(_slot.Phase))
                    {
                        _slot.Completed.Add(_slot.Phase);
                    }

                    _slot.Phase = phase;
                    _slot.PhaseDone = 0;
                    _slot.PhaseTotal = 0;
                }
            }

            public void Advance(Int32 done, Int32 total)
            {
                lock (_tracker._gate)
                {
                    if (_slot == null)
                    {
                        // Advance before any phase would be a caller defect, and inventing a slot for it
                        // would report a run with no phase. Dropped rather than guessed at.
                        return;
                    }

                    _slot.PhaseDone = done;
                    _slot.PhaseTotal = total;
                }
            }

            /// <summary>The run id this handle reports under, so a route can tell its own run from a later one.</summary>
            public String RunId
            {
                get { return _runId; }
            }

            /// <summary>
            ///   Completes the moment the run enters its first phase. It never faults: a run that is
            ///   REJECTED simply never completes it, and the caller learns that from the run's own task.
            /// </summary>
            public Task Started
            {
                get { return _started.Task; }
            }

            /// <summary>
            ///   The run's stop signal, to be observed at safe points. See <see cref="RunAbort" /> for why
            ///   it is this type and not the token underneath it.
            /// </summary>
            public RunAbort Abort
            {
                get { return new RunAbort(_cancellation.Token); }
            }

            /// <summary>
            ///   The same signal as a token, for the ONE thing that may be aborted mid-call: the source
            ///   read. Aborting a read writes nothing, so the provider is handed a real token; everything
            ///   downstream of the first graph write gets <see cref="Abort" /> instead.
            /// </summary>
            public CancellationToken CancellationToken
            {
                get { return _cancellation.Token; }
            }

            /// <summary>
            ///   Releases the stop signal. Called by whoever ran the job, AFTER the outcome is recorded, so
            ///   a cancel racing the end of a run either finds a slot that is still in flight or one that
            ///   already says how it ended.
            /// </summary>
            public void Dispose()
            {
                _cancellation.Dispose();
            }
        }
    }
}
