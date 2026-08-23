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
        private readonly ILoggerFactory _loggers;
        private readonly ILogger<JobRunner> _logger;

        public JobRunner(ProviderCatalog catalog, SnapshotValidator validator, SnapshotApplier applier,
            CredentialResolver credentials, IGraphTargetFactory targets, IProviderHttpFactory http,
            IJobFilesFactory files, ActiveCredentials active, RunGate gate, IntegrationsMetrics metrics,
            ILoggerFactory loggers)
        {
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
        public async Task<JobReport> RunAsync(IntegrationJob job, CancellationToken cancellationToken)
        {
            if (job == null)
            {
                throw new JobRejectedException(JobErrorKinds.Configuration, "A job definition is required.");
            }

            if (!job.TryNormalize(out var normalized, out var normalizeFailure, _files.MaxFileBytes))
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

            // Created here and disposed with the lease below, so a file is readable for exactly as long as a
            // credential is: across the source read and the graph write, and not one statement longer.
            var runFiles = _files.Create(normalized.Files);

            var cancelled = false;

            // Whether control ever entered the apply phase, which is the only thing that decides what may be said
            // about what landed: the apply call is handed CancellationToken.None, so an OperationCanceledException
            // surfacing from inside it is NOT the caller's cancellation but some other one - a client-side timeout
            // on a graph write, say - that merely coincides with the caller's token being cancelled. Only this
            // frame knows which side of the call it stands on.
            var applyStarted = false;

            using (lease)
            using (runFiles)
            {
                try
                {
                    using var http = _http.Create(!lease.IsEmpty);
                    var diagnostics = new List<DiagnosticDto>();
                    var context = new ProviderContext(descriptor.Id, instanceId, settings, lease, http,
                        _loggers.CreateLogger("NoSQL.GraphDB.Integrations.Providers." + descriptor.Id),
                        diagnostics, runFiles.ReadAsync,
                        key => runFiles.TryResolve(key, out var failure) ? null : failure);

                    var snapshot = await provider.ObserveAsync(context, cancellationToken).ConfigureAwait(false);

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
                    // caller's cancellation. Everything above - the source read, which is the slow part, and the
                    // validation - is cancellable, and a caller that walks away during it loses nothing. The apply
                    // phase is different: it is a bounded handful of batched writes, seconds of work, and
                    // interrupting it midway leaves a half-applied snapshot. That is not a rollback but a
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
                    // Set BEFORE the call, not after it: the flag answers "could a write have happened", and the
                    // answer becomes yes the moment control enters the applier.
                    applyStarted = true;
                    await _applier.ApplyAsync(validated, instanceId, target, report, summary, CancellationToken.None)
                        .ConfigureAwait(false);

                    return Complete(report, stopwatch, null, null, descriptor.Id);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
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
                    else
                    {
                        LogOutcome(report);
                    }
                }
            }
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
                        effective[setting.Key] = file.Name;
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
