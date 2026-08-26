// MIT License
//
// JobRunner.cs
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
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Integrations.Contract;
using NoSQL.GraphDB.Integrations.Credentials;
using NoSQL.GraphDB.Integrations.Diagnostics;
using NoSQL.GraphDB.Integrations.Graph;
using NoSQL.GraphDB.Integrations.Identity;
using NoSQL.GraphDB.Integrations.Validation;

namespace NoSQL.GraphDB.Integrations.Run
{
    /// <summary>
    ///   Owns one run: the checks, the credential lease, the provider call and the report. There is no
    ///   scheduler, no configured instance and no stored state - the source is observed, what it said is written
    ///   to the graph, the report goes back, and nothing is kept.
    /// </summary>
    public sealed class JobRunner
    {
        private readonly ProviderCatalog _catalog;
        private readonly SnapshotValidator _validator;
        private readonly SnapshotApplier _applier;
        private readonly CredentialResolver _credentials;
        private readonly IGraphTargetFactory _targets;
        private readonly IProviderHttpFactory _http;
        private readonly IJobFilesFactory _files;
        private readonly ActiveCredentials _active;
        private readonly RunGate _gate;
        private readonly IntegrationsMetrics _metrics;
        private readonly RunSpool _spool;
        private readonly RunShutdown _shutdown;
        private readonly ILoggerFactory _loggers;
        private readonly ILogger<JobRunner> _logger;

        public JobRunner(ProviderCatalog catalog, SnapshotValidator validator, SnapshotApplier applier,
            CredentialResolver credentials, IGraphTargetFactory targets, IProviderHttpFactory http,
            IJobFilesFactory files, ActiveCredentials active, RunGate gate, IntegrationsMetrics metrics,
            ILoggerFactory loggers, RunSpool? spool = null, RunShutdown? shutdown = null)
        {
            // Both default to "nothing survives a restart and this process is not going anywhere", which is
            // exactly what every caller meant before resumability existed: the conformance suite, the
            // write-path harnesses and anything scripted.
            _spool = spool ?? new RunSpool(null, loggers?.CreateLogger<RunSpool>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RunSpool>.Instance);
            _shutdown = shutdown ?? RunShutdown.Never;
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _validator = validator ?? throw new ArgumentNullException(nameof(validator));
            _applier = applier ?? throw new ArgumentNullException(nameof(applier));
            _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
            _targets = targets ?? throw new ArgumentNullException(nameof(targets));
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _files = files ?? throw new ArgumentNullException(nameof(files));
            _active = active ?? throw new ArgumentNullException(nameof(active));
            _gate = gate ?? throw new ArgumentNullException(nameof(gate));
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _loggers = loggers ?? throw new ArgumentNullException(nameof(loggers));
            _logger = loggers.CreateLogger<JobRunner>();
        }

        /// <summary>
        ///   Runs one job.
        ///
        ///   <para>A job that RAN and failed comes back as a report carrying the failure: the request did what
        ///   was asked, so the interesting outcome is the run's. A job that could not be run at all raises
        ///   <see cref="JobRejectedException"/>.</para>
        /// </summary>
        /// <param name="job">The whole configuration of one run.</param>
        /// <param name="cancellationToken">
        ///   THE CALLER'S token: "nobody is listening any more". Honoured up to the point of no return, and
        ///   not after it. A run abandoned this way produces no report at all.
        /// </param>
        /// <param name="progress">
        ///   Where the run says what it is doing while it does it. Optional and defaulted to a no-op, so
        ///   every caller that drives a run without watching it - the conformance suite, the write-path
        ///   tests, anything scripted - keeps meaning exactly what it meant before.
        /// </param>
        /// <param name="stopToken">
        ///   THE RUN'S OWN token: "the operator asked for this to stop", which only the cancel route
        ///   signals. Honoured everywhere, INCLUDING past the point of no return, but only at safe points -
        ///   <see cref="RunAbort" /> says why that distinction is the whole design. Default is a token that
        ///   never fires, so every existing caller keeps meaning exactly what it meant before.
        /// </param>
        /// <param name="resume">
        ///   The spooled entry of a run this process is PICKING UP rather than starting. Its snapshot stands
        ///   in for the source read: a resumed run does not re-observe, because the file and the credential
        ///   that produced the snapshot are gone by design, and re-reading a source would in any case
        ///   describe a different moment than the one the run is half-way through applying.
        /// </param>
        public async Task<JobReport> RunAsync(IntegrationJob job, CancellationToken cancellationToken,
            IRunProgress? progress = null, CancellationToken stopToken = default, SpooledRun? resume = null)
        {
            progress ??= NoRunProgress.Instance;

            if (job == null)
            {
                throw new JobRejectedException(JobErrorKinds.Configuration, "A job definition is required.");
            }

            if (!job.TryNormalize(out var normalized, out var normalizeFailure, _files.MaxFileBytes,
                    _files.MaxJobFileBytes))
            {
                throw new JobRejectedException(JobErrorKinds.Configuration, normalizeFailure!);
            }

            if (!_catalog.TryGet(normalized!.ProviderId, out var provider))
            {
                throw new JobRejectedException(JobErrorKinds.Configuration, String.Format(
                    "There is no provider with id '{0}'. Ask GET /integration/providers for the ones this " +
                    "runtime ships.", normalized.ProviderId));
            }

            var descriptor = provider!.Descriptor;

            if (!ClaimSchema.IsValidInstanceId(normalized.InstanceId))
            {
                throw new JobRejectedException(JobErrorKinds.Configuration, String.Format(
                    "'{0}' is not a usable integrationInstanceId: letters, digits, dot, dash and underscore " +
                    "only, at most {1} characters. The value is substituted into a property key and a claim " +
                    "key, and derived edge keys join their parts with a pipe, so a colon, at sign, pipe or " +
                    "dollar would let two identities compose one identical key.",
                    normalized.InstanceId, ClaimSchema.MaxInstanceIdLength));
            }

            var settings = BuildSettings(descriptor, normalized);
            var instanceId = normalized.InstanceId!;

            using var admission = _gate.Enter(instanceId);

            var runId = (progress as IIdentifiedRun)?.RunId ?? Guid.NewGuid().ToString("N");

            // The journal wraps the progress sink from here on, so the embedding cursor advances on the
            // per-chunk counter that already exists rather than on a second seam threaded through the graph
            // target. With no spool configured there is no wrapper and no behaviour change at all.
            IRunJournal? journal = null;
            if (_spool.Enabled)
            {
                var spooling = new SpooledRunJournal(progress, _spool, instanceId, resume?.Progress);
                journal = spooling;
                progress = spooling;
            }

            var report = new JobReport
            {
                ProviderId = descriptor.Id,
                IntegrationInstanceId = instanceId,
                StartedUtc = DateTimeOffset.UtcNow,
            };

            var stopwatch = Stopwatch.StartNew();

            CredentialLease lease;
            try
            {
                // Fetched ONCE and EAGERLY, before the provider is invoked: a lazy fetch would move the failure
                // into the middle of a source read, after the run has begun making withdrawal-relevant decisions.
                lease = _credentials.Resolve(normalized.Credentials);
            }
            catch (CredentialUnavailableException ex)
            {
                stopwatch.Stop();
                var unavailable = Complete(report, stopwatch, JobErrorKinds.Credential, ex.Message, descriptor.Id);

                // Logged like every other outcome. This return happens BEFORE the lease exists, so it is the one
                // failure that never reached the using-block's LogOutcome, and "the run that produced no log line
                // at all" is the worst shape for the one failure class an operator fixes by looking at the
                // credential. There is no lease to fingerprint and no value to scrub: the exception is the
                // resolver's own message about a credential it could not obtain.
                LogOutcome(unavailable);
                return unavailable;
            }

            var cancelled = false;

            // Whether this process is going away mid-run, which is the one outcome that must LEAVE the
            // spooled entry behind: the run is unfinished rather than over, and the next start picks it up.
            var interrupted = false;

            // The entry is written at ACCEPT time, before the provider is invoked, so that a restart during a
            // long source read leaves something to say what happened. It cannot be resumed - the file and the
            // credential are gone - but a caller polling an identity this process has never heard of is the
            // "the run vanished" report that started all of this.
            var spooled = resume ?? new SpooledRun
            {
                // Read off the sink BEFORE it was wrapped, which is the only place the id exists: a resumed
                // run must report under the id a client is already polling by.
                RunId = runId,
                ProviderId = descriptor.Id,
                InstanceId = instanceId,
                Namespace = normalized.Namespace,
                EmbedSummaries = normalized.EmbedSummaries,
                EmbeddingName = normalized.EmbeddingName,
                StartedAt = report.StartedUtc.ToString("O", CultureInfo.InvariantCulture),
            };

            if (resume == null)
            {
                _spool.WriteIntent(spooled);
            }

            // Whether control ever entered the apply phase, which is the only thing that decides what may be said
            // about what landed: the apply call is handed CancellationToken.None, so an OperationCanceledException
            // surfacing from inside it is NOT the caller's cancellation but some other one - a client-side timeout
            // on a graph write, say - that merely coincides with the caller's token being cancelled. Only this
            // frame knows which side of the call it stands on.
            var applyStarted = false;

            // The run's own stop signal, wrapped once, and beside it the process's. Neither is the token the
            // graph calls take, and RunAbort states why that cannot change.
            var abort = new RunAbort(stopToken, _shutdown.Token);

            // The files are created INSIDE the lease's scope, so a factory that throws cannot leave the
            // credential held: with no run in flight the active-credential set is empty, and that is what
            // makes the redaction filter's own bookkeeping trustworthy. They are disposed with the lease, so
            // a file is readable for exactly as long as a credential is - across the source read and the
            // graph write, and not one statement longer.
            using (lease)
            using (var runFiles = _files.Create(normalized.Files))
            {
                try
                {
                    // The one place a stop signal is a real token handed downward. A source READ is safe to
                    // abort mid-call because it writes nothing, so the provider gets EVERY reason to stop at
                    // once - the caller walking away, the operator asking, and this process going away.
                    // Everything past the first graph write gets RunAbort instead, which cannot be handed to
                    // a call.
                    //
                    // The shutdown token belongs here as much as the other two: an a large size extract parses for
                    // minutes, and a container told to stop should not spend its whole grace period finishing
                    // a parse whose result it is about to throw away.
                    using var reading = CancellationTokenSource
                        .CreateLinkedTokenSource(cancellationToken, stopToken, _shutdown.Token);

                    using var http = _http.Create(!lease.IsEmpty);
                    var diagnostics = new List<DiagnosticDto>();
                    var context = new ProviderContext(descriptor.Id, instanceId, settings, lease, http,
                        _loggers.CreateLogger("NoSQL.GraphDB.Integrations.Providers." + descriptor.Id),
                        diagnostics, runFiles.ReadAsync,
                        key => runFiles.TryResolve(key, out var failure) ? null : failure,
                        runFiles.NamesOf, runFiles.ReadAtAsync);

                    // Named before the call, because this is the phase that looks like a hang: an a large size
                    // extract parses for minutes and writes nothing, so "observe" is the only thing that
                    // distinguishes working from stuck.
                    progress.EnterPhase(RunPhases.Observe);

                    // A RESUMED run does not read its source again, and the reason is not thrift. The file
                    // and the credential that produced the snapshot are gone by design; and even with them,
                    // re-reading would describe a DIFFERENT moment than the one this run is half-way through
                    // applying, so the entities it had already written would be judged against a source it
                    // never saw. The phase is still entered, because the earlier attempt of this same run
                    // really did complete it.
                    var snapshot = resume != null
                        ? resume.Snapshot
                        : await provider.ObserveAsync(context, reading.Token).ConfigureAwait(false);

                    foreach (var diagnostic in diagnostics)
                    {
                        report.Diagnostics.Add(diagnostic);
                    }

                    if (snapshot == null)
                    {
                        // A provider that returns no snapshot at all is a failure; one that observed nothing says
                        // so with an EMPTY snapshot and a completeness declaration. "I saw nothing" and "I could
                        // not look" license opposite actions.
                        return Complete(report, stopwatch, JobErrorKinds.Source,
                            "The provider returned no snapshot. A provider that observed nothing says so with an " +
                            "empty snapshot and a completeness declaration.", descriptor.Id);
                    }

                    if (snapshot.Diagnostics != null)
                    {
                        foreach (var diagnostic in snapshot.Diagnostics)
                        {
                            report.Diagnostics.Add(diagnostic);
                        }
                    }

                    // FROM HERE THE RUN IS RESUMABLE, so this is where the snapshot joins its entry: what
                    // remains to be done is now a function of this document and the graph alone, which is
                    // exactly the condition under which another process can pick the work up.
                    //
                    // BEFORE the validation, and the run then continues with the document it read BACK, for
                    // the reason WriteSnapshot states: a value is a CLR object in process and a JsonElement
                    // after a round trip, and the two do not render identically. Validating the same bytes a
                    // resumed run will validate is the other half of that - a document the round trip made
                    // unacceptable should fail HERE rather than only after a restart.
                    if (resume == null)
                    {
                        snapshot = _spool.WriteSnapshot(spooled, snapshot) ?? snapshot;
                    }

                    // Named BEFORE the validation, not after it: entered afterwards the phase claimed work
                    // that had already produced its diagnostics.
                    progress.EnterPhase(RunPhases.Validate);

                    var validated = _validator.Validate(snapshot, descriptor);
                    foreach (var diagnostic in validated.Diagnostics)
                    {
                        report.Diagnostics.Add(diagnostic);
                    }

                    if (!validated.EnvelopeAccepted)
                    {
                        return Complete(report, stopwatch, JobErrorKinds.Source,
                            "The snapshot was refused at the envelope, so nothing was applied and nothing was " +
                            "withdrawn. The named diagnostics say what is wrong with it.", descriptor.Id);
                    }

                    if (!String.Equals(validated.ProviderId, descriptor.Id, StringComparison.OrdinalIgnoreCase) ||
                        !String.Equals(validated.InstanceId, instanceId, StringComparison.Ordinal))
                    {
                        // The document's own identity is what every instance-scoped claim key was composed from,
                        // so a provider echoing a different one would write claims under an identity no run
                        // reconciles.
                        return Complete(report, stopwatch, JobErrorKinds.Source, String.Format(
                            "The snapshot declares provider '{0}' as '{1}', but this run is provider '{2}' as " +
                            "'{3}'. Applying it would write claims under an identity no run reconciles.",
                            validated.ProviderId, validated.InstanceId, descriptor.Id, instanceId), descriptor.Id);
                    }

                    // BOTH halves of the opt-in, or nothing: the provider declares a template and the job
                    // asks for it. Default off.
                    var summary = normalized.EmbedSummaries &&
                                  !String.IsNullOrWhiteSpace(descriptor.EntitySummaryTemplate)
                        ? new SummaryRequest(descriptor.EntitySummaryTemplate!,
                            String.IsNullOrWhiteSpace(normalized.EmbeddingName) ? "default" : normalized.EmbeddingName)
                        : null;

                    using var target = _targets.Create(normalized.Namespace);

                    // THE POINT OF NO RETURN, and the one place this runtime deliberately stops honouring the
                    // CALLER'S cancellation. Everything above - the source read, which is the slow part, and the
                    // validation - is cancellable, and a caller that walks away during it loses nothing. The apply
                    // phase is different: interrupting it midway leaves a half-applied snapshot.
                    //
                    // It used to be fair to call it "seconds of work". Summary embedding ended that: the embed
                    // phase is model inference, and a many-entity extract against a CPU-backed model is
                    // HOURS. The decision below is unchanged and now matters far more - but it is also why the
                    // run has to be observable from outside, because nobody can hold a connection that long. That is not a rollback but a
                    // repairable-yet-invisible state, so the run finishes what it started even if nobody is left
                    // to read the answer.
                    //
                    // The trigger is not theoretical: the job endpoint binds the request-abort token, and the
                    // apiApp proxy has a finite timeout, so a source that legitimately takes longer than the proxy
                    // waits used to have its GRAPH WRITES killed between calls. The container shutdown path
                    // already holds this principle - compose grants a stop grace period precisely so a recreate
                    // does not kill a run between writes - and this closes the same hole at the front door.
                    // A run that hangs here is bounded by the target's own per-call HTTP timeouts.
                    //
                    // WHAT DOES CROSS THIS LINE is the run's own stop signal, and only it. An operator asking
                    // for a run to stop is a decision about the import; a dropped connection is an accident of
                    // who happened to be watching. It travels as a RunAbort rather than as the token the graph
                    // calls take, which is what keeps "stoppable" from meaning "interruptible mid-write".
                    //
                    // Honoured HERE first, before the flag below makes "a write may have happened" true: a stop
                    // that arrived during the read or the validation has written nothing, and the log line for
                    // it must be able to say so.
                    abort.ThrowIfRequested();

                    // Set BEFORE the call, not after it: the flag answers "could a write have happened", and the
                    // answer becomes yes the moment control enters the applier.
                    applyStarted = true;
                    await _applier.ApplyAsync(validated, instanceId, target, report, summary,
                            CancellationToken.None, progress, abort, journal)
                        .ConfigureAwait(false);

                    return Complete(report, stopwatch, null, null, descriptor.Id);
                }
                catch (RunCancelledException)
                {
                    // STOPPED AT A SAFE POINT, on request. A reported outcome rather than an abandonment:
                    // somebody asked for this, so somebody is waiting to be told what landed. The applier has
                    // already counted onto the report everything that really happened, embedded summaries
                    // included.
                    return CompleteCancelled(report, stopwatch, descriptor.Id);
                }
                catch (RunInterruptedException)
                {
                    // THE PROCESS IS GOING AWAY. Not an outcome at all: the run is unfinished, its entry is
                    // kept, and the next start picks it up from the embedding cursor. Rethrown rather than
                    // reported, because a report is a statement that the run ENDED, and this one has not.
                    interrupted = true;
                    LogInterrupted(report, stopwatch);
                    throw;
                }
                catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
                {
                    // Stopped while the PROVIDER was reading, which is the one call this runtime aborts
                    // mid-flight because aborting it writes nothing. The cheapest cancellation there is.
                    return CompleteCancelled(report, stopwatch, descriptor.Id);
                }
                catch (OperationCanceledException) when (_shutdown.Token.IsCancellationRequested)
                {
                    // Interrupted while the provider was READING, so there is no snapshot and this run
                    // cannot be picked up. Its entry is still kept, deliberately: the next start turns it
                    // into an honest "interrupted before its source was read, submit it again" rather than
                    // leaving whoever was watching to poll an identity nothing has heard of.
                    //
                    // Reported as an interruption rather than falling through to the catch-all, which would
                    // call this a SOURCE failure and send an operator to look at a system that answered fine.
                    interrupted = true;
                    LogInterrupted(report, stopwatch);
                    throw new RunInterruptedException();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // BELOW the two clauses above, and the order is the decision: when a run is both
                    // cancelled and abandoned, the deliberate stop wins, because an operator who asked for it
                    // is owed a report while a dropped connection is owed nothing.
                    cancelled = true;
                    throw;
                }
                catch (ProviderConfigurationException ex)
                {
                    return Complete(report, stopwatch, JobErrorKinds.Configuration, ex.Message, descriptor.Id);
                }
                catch (CredentialHostRefusedException ex)
                {
                    return Complete(report, stopwatch, JobErrorKinds.Configuration, ex.Message, descriptor.Id);
                }
                catch (ProviderCredentialRejectedException ex)
                {
                    // BEFORE the catch-all, and its own kind: a source that answered "who are you" is the one
                    // failure a reader fixes by looking at the credential rather than at the network. Reported
                    // as 'credential' exactly like a value the runtime could not use, because the question
                    // "which system do I go and look at" has the same answer for both.
                    return Complete(report, stopwatch, JobErrorKinds.Credential, ex.Message, descriptor.Id);
                }
                catch (GraphIndexMissingException ex)
                {
                    return Complete(report, stopwatch, JobErrorKinds.Graph, ex.Message, descriptor.Id);
                }
                catch (GraphTargetException ex)
                {
                    return Complete(report, stopwatch, JobErrorKinds.Graph, ex.Message, descriptor.Id);
                }
                catch (Exception ex)
                {
                    // Throwing is a LEGITIMATE outcome for a provider, meaning "I could not reach the source":
                    // the job fails, the caller is told which system did not answer, and nothing is withdrawn.
                    // A failed run is cheap and the next run starts from the same graph.
                    return Complete(report, stopwatch, JobErrorKinds.Source, ex.Message, descriptor.Id);
                }
                finally
                {
                    // INSIDE the lease, all three of them: the fingerprint needs the values, the report is
                    // scrubbed before it can leave, and the failure is logged while the value is still
                    // redactable. One frame later the runtime no longer knows what the credential was.
                    report.CredentialFingerprint = lease.Fingerprint();
                    Scrub(report);

                    if (cancelled)
                    {
                        // Complete never ran, which is why this cannot go through LogOutcome: the report still
                        // reads zero of everything, and the success-shaped line would claim a run that finished in
                        // no time and created nothing. What may be said about DURABILITY-relevant work is the one
                        // thing that differs between the two sides of the apply call (see applyStarted), and a
                        // confident false statement about it is worse than no statement.
                        stopwatch.Stop();

                        if (applyStarted)
                        {
                            _logger.LogWarning(
                                "Integration run {ProviderId} as {InstanceId} was ABANDONED after {Duration} ms " +
                                "INSIDE the apply phase, so what it had already written stands: {Created} " +
                                "created, {Matched} matched, {Edges} edges, {Withdrawn} withdrawn, {Deleted} " +
                                "deleted at that point. Credential fingerprint {Fingerprint}.",
                                report.ProviderId, report.IntegrationInstanceId, stopwatch.ElapsedMilliseconds,
                                report.ElementsCreated, report.ElementsMatched, report.EdgesCreated,
                                report.ClaimsWithdrawn, report.ElementsDeleted,
                                report.CredentialFingerprint ?? "none");
                        }
                        else
                        {
                            _logger.LogWarning(
                                "Integration run {ProviderId} as {InstanceId} was ABANDONED after {Duration} ms: " +
                                "the caller cancelled before the apply phase, so nothing was written and nothing " +
                                "was withdrawn. Credential fingerprint {Fingerprint}.",
                                report.ProviderId, report.IntegrationInstanceId, stopwatch.ElapsedMilliseconds,
                                report.CredentialFingerprint ?? "none");
                        }
                    }
                    else if (!interrupted)
                    {
                        // Interruption has already logged its own line, and LogOutcome would follow it with
                        // the success-shaped one: a run that is about to be resumed would read as finished.
                        LogOutcome(report);
                    }

                    // THE ENTRY GOES WHEN THE RUN DOES, on every ending there is: succeeded, failed,
                    // cancelled and abandoned alike. An entry that outlived its run would be resumed by the
                    // next restart, re-running a job whose answer somebody already has - and the spool would
                    // quietly become the run history this runtime deliberately does not keep.
                    //
                    // Interruption is one exception; a resumed run the GRAPH refused is the other.
                    if (!interrupted && !KeepForRetry(report, resume))
                    {
                        _spool.Delete(instanceId);
                    }
                }
            }
        }

        /// <summary>
        ///   Picks up a run this process did not start, from the entry a stopped process left behind.
        ///
        ///   <para>A thin wrapper over <see cref="RunAsync" /> on purpose. A resumed run differs from a
        ///   fresh one in exactly one respect - where its snapshot comes from - so giving it its own flow
        ///   would mean two copies of the credential scrubbing, the failure mapping and the outcome logging,
        ///   which would then drift. What it does NOT have is a credential or a file, and it does not need
        ///   either: the snapshot is already made.</para>
        /// </summary>
        /// <param name="spooled">The entry to pick up. It must carry a snapshot.</param>
        /// <param name="progress">Where to report, normally the tracker handle opened under the run's own id.</param>
        /// <param name="stopToken">The run's own stop signal, so a resumed run is cancellable like any other.</param>
        public Task<JobReport> ResumeAsync(SpooledRun spooled, IRunProgress? progress = null,
            CancellationToken stopToken = default)
        {
            if (spooled == null)
            {
                throw new ArgumentNullException(nameof(spooled));
            }

            if (!spooled.Resumable)
            {
                throw new JobRejectedException(JobErrorKinds.Configuration,
                    "This spooled run carries no snapshot, so there is nothing to resume: the file and the " +
                    "credential that would produce one are dropped when a run ends, by design.");
            }

            // The job is SYNTHESISED from the entry rather than stored on it, which is what keeps a
            // credential and a file's bytes out of the spool: everything below is envelope, and the envelope
            // is all a resumed run needs.
            var job = new IntegrationJob
            {
                ProviderId = spooled.ProviderId,
                IntegrationInstanceId = spooled.InstanceId,
                Namespace = spooled.Namespace,
                EmbedSummaries = spooled.EmbedSummaries,
                EmbeddingName = spooled.EmbeddingName,
            };

            return RunAsync(job, CancellationToken.None, progress, stopToken, spooled);
        }

        /// <summary>
        ///   Merges the descriptor's defaults with the job's settings, and refuses the two shapes that would be
        ///   silently wrong: a credential arriving as a setting, and a key the descriptor never declared.
        /// </summary>
        private static IReadOnlyDictionary<String, String> BuildSettings(ProviderDescriptor descriptor,
            NormalizedJob job)
        {
            var declared = new Dictionary<String, ProviderSetting>(StringComparer.OrdinalIgnoreCase);
            foreach (var setting in descriptor.Settings ?? Array.Empty<ProviderSetting>())
            {
                declared[setting.Key] = setting;
            }

            foreach (var supplied in job.Settings)
            {
                if (!declared.TryGetValue(supplied.Key, out var setting))
                {
                    throw new JobRejectedException(JobErrorKinds.Configuration, String.Format(
                        "Provider '{0}' declares no setting '{1}'. A key it never declared would be read by " +
                        "nothing, so a typo would silently mean 'use the default'.", descriptor.Id, supplied.Key));
                }

                if (setting.Kind == SettingKind.Credential)
                {
                    throw new JobRejectedException(JobErrorKinds.Configuration, String.Format(
                        "'{0}' is a credential setting, so its value belongs in 'credentialValues', never in " +
                        "'settings': a setting is neither leased nor redacted, so a value here would be logged " +
                        "and reported like any other.", supplied.Key));
                }

                if (setting.Kind == SettingKind.File)
                {
                    // Refused HERE rather than mid-run, which is the whole point of checking eagerly: the
                    // runtime opens nothing on disk, so a bare name would pass this pass, satisfy the
                    // provider's own Required() call, and only then fail on the read - after the run has
                    // reached the provider and begun making withdrawal-relevant decisions.
                    throw new JobRejectedException(JobErrorKinds.Configuration, String.Format(
                        "'{0}' is a file setting, so the file belongs in 'files' as a name and its bytes, " +
                        "never in 'settings': the runtime opens nothing on disk, so a name on its own names " +
                        "a file nothing can read.", supplied.Key));
                }
            }

            foreach (var credential in job.Credentials)
            {
                if (!declared.TryGetValue(credential.Key, out var setting) ||
                    setting.Kind != SettingKind.Credential)
                {
                    throw new JobRejectedException(JobErrorKinds.Configuration, String.Format(
                        "Provider '{0}' declares no credential setting '{1}', so the credential supplied for it " +
                        "would never be read.", descriptor.Id, credential.Key));
                }
            }

            foreach (var file in job.Files)
            {
                if (!declared.TryGetValue(file.Key, out var setting) || setting.Kind != SettingKind.File)
                {
                    throw new JobRejectedException(JobErrorKinds.Configuration, String.Format(
                        "Provider '{0}' declares no file setting '{1}', so the file supplied for it would " +
                        "never be read.", descriptor.Id, file.Key));
                }

                // Refused HERE, where the descriptor is finally known, rather than at normalisation: the
                // caller ASKED for the multiple shape, and a provider not built to compose files would read
                // only the first of them. Silently reading one of several is the worst available outcome,
                // because this provider class declares COMPLETE snapshots - so the files that went unread
                // would be reported as parts of the source that no longer exist, and reconciliation would
                // delete everything they describe.
                if (!setting.Multiple && (file.Value.AsList || file.Value.Files.Count > 1))
                {
                    throw new JobRejectedException(JobErrorKinds.Configuration, String.Format(
                        "Setting '{1}' of provider '{0}' takes ONE file, but the job supplied a list of {2}. " +
                        "Send the file as a single object rather than an array.",
                        descriptor.Id, file.Key, file.Value.Files.Count));
                }
            }

            var effective = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);
            foreach (var setting in descriptor.Settings ?? Array.Empty<ProviderSetting>())
            {
                if (setting.Kind == SettingKind.Credential)
                {
                    if (setting.Required && !job.Credentials.ContainsKey(setting.Key))
                    {
                        throw new JobRejectedException(JobErrorKinds.Configuration, String.Format(
                            "Provider '{0}' requires a credential for setting '{1}': supply it in " +
                            "'credentialValues'.", descriptor.Id, setting.Key));
                    }

                    continue;
                }

                if (setting.Kind == SettingKind.File)
                {
                    if (job.Files.TryGetValue(setting.Key, out var file))
                    {
                        // The effective value of a file setting is the file's own NAME, so a provider reads
                        // it with Required(key) for its messages and diagnostic subjects exactly as it did
                        // when the name pointed at a mount. That is what makes "the provider does not change"
                        // true rather than aspirational: the transport a file arrived by was never its
                        // business.
                        //
                        // For a setting given SEVERAL files it is every name, joined. A provider that
                        // composes files names the one it is talking about from the file list itself; this
                        // value is what a message about the setting AS A WHOLE says, and for a set of
                        // extracts "chassis.arxml, body.arxml" is that answer. The first name alone would
                        // be a message quietly about one file of many.
                        effective[setting.Key] = file.Files.Count == 1
                            ? file.First.Name
                            : String.Join(", ", Names(file));
                    }
                    else if (setting.Required)
                    {
                        throw new JobRejectedException(JobErrorKinds.Configuration, String.Format(
                            "Provider '{0}' requires a file for setting '{1}': supply it in 'files' as a " +
                            "name and its bytes, base64. {2}", descriptor.Id, setting.Key, setting.Help));
                    }

                    continue;
                }

                if (job.Settings.TryGetValue(setting.Key, out var supplied))
                {
                    effective[setting.Key] = supplied;
                    continue;
                }

                if (!String.IsNullOrEmpty(setting.DefaultValue))
                {
                    effective[setting.Key] = setting.DefaultValue!;
                    continue;
                }

                if (setting.Required)
                {
                    throw new JobRejectedException(JobErrorKinds.Configuration, String.Format(
                        "Provider '{0}' requires setting '{1}': {2}", descriptor.Id, setting.Key, setting.Help));
                }
            }

            return effective;
        }

        /// <summary>The names of one setting's files, in job order.</summary>
        private static IEnumerable<String> Names(JobFileSet set)
        {
            foreach (var file in set.Files)
            {
                yield return file.Name;
            }
        }

        private JobReport Complete(JobReport report, Stopwatch stopwatch, String? errorKind, String? error,
            String providerId)
        {
            stopwatch.Stop();
            report.DurationMilliseconds = stopwatch.ElapsedMilliseconds;
            report.ErrorKind = errorKind;
            report.Error = error;
            _metrics.Record(report, providerId);
            return report;
        }

        /// <summary>
        ///   Ends a run that was STOPPED ON REQUEST. Deliberately no <c>errorKind</c>: nothing is wrong, and
        ///   the counts already on the report are what really landed. The one thing a reader has to know -
        ///   that it did not reconcile, and why that is the safe half of the bargain - is stated once, on
        ///   <see cref="JobReport.Cancelled" />.
        /// </summary>
        private JobReport CompleteCancelled(JobReport report, Stopwatch stopwatch, String providerId)
        {
            report.Cancelled = true;
            return Complete(report, stopwatch, null, null, providerId);
        }

        /// <summary>
        ///   Whether a run that ENDED should nevertheless keep its spooled entry, so the next start tries
        ///   again.
        ///
        ///   <para>Exactly one case, and it would otherwise defeat the whole point of the spool: this
        ///   container restarts alongside the graph it writes into and may come up first, so a resumed run
        ///   can fail simply because the graph was not answering yet. Deleting the entry on that would throw
        ///   away the hours of work it exists to protect, for a reason that says nothing about the job or the
        ///   source. Bounded by the attempt count, so a graph that is gone for good does not make the entry
        ///   immortal.</para>
        ///
        ///   <para>Only a RESUMED run and only a GRAPH failure. A fresh run still has its file and its
        ///   credential, so its caller can simply submit it again; any other failure kind is about the job or
        ///   the source, which re-running unchanged will not mend.</para>
        /// </summary>
        private static Boolean KeepForRetry(JobReport report, SpooledRun? resume)
        {
            return resume != null
                   && String.Equals(report.ErrorKind, JobErrorKinds.Graph, StringComparison.Ordinal)
                   && resume.Attempts < RunSpool.MaxAttempts;
        }

        /// <summary>
        ///   The one line an INTERRUPTED run leaves. It is neither of the other two on purpose: a success
        ///   line would claim an import that finished, and a failure line would raise an alert for a
        ///   container doing what it was told. What a reader needs is that nothing was withdrawn and that
        ///   the work continues.
        /// </summary>
        private void LogInterrupted(JobReport report, Stopwatch stopwatch)
        {
            stopwatch.Stop();
            _logger.LogWarning(
                "Integration run {ProviderId} as {InstanceId} was INTERRUPTED after {Duration} ms by this " +
                "process shutting down: {Created} created, {Matched} matched, {Summaries} summaries " +
                "embedded so far. It did not reconcile and nothing was withdrawn; the next start picks it " +
                "up from the embedding cursor.",
                report.ProviderId, report.IntegrationInstanceId, stopwatch.ElapsedMilliseconds,
                report.ElementsCreated, report.ElementsMatched, report.SummariesEmbedded);
        }

        /// <summary>
        ///   Scrubs the report inside the lease, so the report never passes through a logger and never carries a
        ///   credential a provider quoted into a diagnostic or an error message.
        /// </summary>
        private void Scrub(JobReport report)
        {
            var values = _active.Snapshot();
            if (values.Count == 0)
            {
                return;
            }

            report.Error = RedactingLoggerProvider.Scrub(report.Error, values);
            for (var i = 0; i < report.Diagnostics.Count; i++)
            {
                var diagnostic = report.Diagnostics[i];
                diagnostic.Message = RedactingLoggerProvider.Scrub(diagnostic.Message, values);
                diagnostic.Subject = RedactingLoggerProvider.Scrub(diagnostic.Subject, values);
            }
        }

        private void LogOutcome(JobReport report)
        {
            if (report.Cancelled)
            {
                // Its own line, above the failure branch and the success one, because it is neither: a
                // cancelled run has no errorKind, so the success line would claim an import that finished,
                // and a failure line would report an operator's own decision as a fault. The counts are the
                // point - they say what stands - and so is the sentence about withdrawal, because "did it
                // delete anything on the way out" is the first question a stopped import raises.
                _logger.LogWarning(
                    "Integration run {ProviderId} as {InstanceId} was CANCELLED after {Duration} ms, and what " +
                    "it had written stands: {Created} created, {Matched} matched, {Edges} edges, {Summaries} " +
                    "summaries embedded. It did not reconcile, so nothing was withdrawn and nothing was " +
                    "deleted; the next completed run of this identity converges the graph. Credential " +
                    "fingerprint {Fingerprint}.",
                    report.ProviderId, report.IntegrationInstanceId, report.DurationMilliseconds,
                    report.ElementsCreated, report.ElementsMatched, report.EdgesCreated,
                    report.SummariesEmbedded, report.CredentialFingerprint ?? "none");
                return;
            }

            if (report.Failed)
            {
                _logger.LogWarning(
                    "Integration run {ProviderId} as {InstanceId} FAILED ({ErrorKind}) after {Duration} ms, " +
                    "withdrawing nothing. Credential fingerprint {Fingerprint}. {Error}",
                    report.ProviderId, report.IntegrationInstanceId, report.ErrorKind,
                    report.DurationMilliseconds, report.CredentialFingerprint ?? "none", report.Error);
                return;
            }

            _logger.LogInformation(
                "Integration run {ProviderId} as {InstanceId} finished in {Duration} ms: {Created} created, " +
                "{Matched} matched, {Edges} edges, {Withdrawn} withdrawn, {Deleted} deleted, {Deferred} " +
                "deferred, mutations issued {Mutations}. Credential fingerprint {Fingerprint}.",
                report.ProviderId, report.IntegrationInstanceId, report.DurationMilliseconds,
                report.ElementsCreated, report.ElementsMatched, report.EdgesCreated, report.ClaimsWithdrawn,
                report.ElementsDeleted, report.DeletionsDeferred, report.IssuedMutations,
                report.CredentialFingerprint ?? "none");
        }
    }
}
