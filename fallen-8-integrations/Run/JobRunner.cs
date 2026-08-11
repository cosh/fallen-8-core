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
        private readonly IProviderFileStore _files;
        private readonly ActiveCredentials _active;
        private readonly RunGate _gate;
        private readonly IntegrationsMetrics _metrics;
        private readonly ILoggerFactory _loggers;
        private readonly ILogger<JobRunner> _logger;

        public JobRunner(ProviderCatalog catalog, SnapshotValidator validator, SnapshotApplier applier,
            CredentialResolver credentials, IGraphTargetFactory targets, IProviderHttpFactory http,
            IProviderFileStore files, ActiveCredentials active, RunGate gate, IntegrationsMetrics metrics,
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

            if (!job.TryNormalize(out var normalized, out var normalizeFailure))
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
                return Complete(report, stopwatch, JobErrorKinds.Credential, ex.Message, descriptor.Id);
            }

            using (lease)
            {
                try
                {
                    using var http = _http.Create(!lease.IsEmpty);
                    var diagnostics = new List<DiagnosticDto>();
                    var context = new ProviderContext(descriptor.Id, instanceId, settings, lease, http,
                        _loggers.CreateLogger("NoSQL.GraphDB.Integrations.Providers." + descriptor.Id),
                        diagnostics, (key, token) => ReadFileAsync(settings, key, token),
                        key => ResolveFile(settings, key));

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
                    await _applier.ApplyAsync(validated, instanceId, target, report, summary, cancellationToken)
                        .ConfigureAwait(false);

                    return Complete(report, stopwatch, null, null, descriptor.Id);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
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
                    LogOutcome(report);
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

        private Task<String> ReadFileAsync(IReadOnlyDictionary<String, String> settings, String settingKey,
            CancellationToken cancellationToken)
        {
            if (!settings.TryGetValue(settingKey, out var fileName) || String.IsNullOrWhiteSpace(fileName))
            {
                throw new ProviderConfigurationException(String.Format(
                    "Setting '{0}' names no file.", settingKey));
            }

            return _files.ReadAsync(fileName, cancellationToken);
        }

        private String? ResolveFile(IReadOnlyDictionary<String, String> settings, String settingKey)
        {
            if (!settings.TryGetValue(settingKey, out var fileName) || String.IsNullOrWhiteSpace(fileName))
            {
                return String.Format("Setting '{0}' names no file.", settingKey);
            }

            return _files.TryResolve(fileName, out var failure) ? null : failure;
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
