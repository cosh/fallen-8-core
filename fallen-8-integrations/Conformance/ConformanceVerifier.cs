// MIT License
//
// ConformanceVerifier.cs
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
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.Integrations.Configuration;
using NoSQL.GraphDB.Integrations.Contract;
using NoSQL.GraphDB.Integrations.Credentials;
using NoSQL.GraphDB.Integrations.Diagnostics;
using NoSQL.GraphDB.Integrations.Graph;
using NoSQL.GraphDB.Integrations.Identity;
using NoSQL.GraphDB.Integrations.Run;
using NoSQL.GraphDB.Integrations.Validation;

namespace NoSQL.GraphDB.Integrations.Conformance
{
    /// <summary>
    ///   Judges a candidate provider by OBSERVING it, never by asking it.
    ///
    ///   <para>The suite, not prose, is what makes the "fourth integration without review" claim safe. A provider
    ///   that promotes its own weak identifier attaches a run's data to the wrong element; one that declares a
    ///   complete snapshot it cannot observe deletes what the source still has; one that returns an empty snapshot
    ///   when it could not reach its source withdraws everything it ever claimed. None looks wrong in a diff, and
    ///   all three fail a named check.</para>
    ///
    ///   <para>It runs the candidate TWICE through the REAL <see cref="JobRunner"/>,
    ///   <see cref="ProviderCatalog"/>, <see cref="SnapshotValidator"/>, <see cref="CredentialResolver"/> and
    ///   redacting logger provider against <see cref="InMemoryGraphTarget"/>, then looks at what reached the
    ///   graph, what reached the log sink and what the second run wrote. Twice rather than once because
    ///   determinism and idempotence are statements about a repeat. A verifier that trusted a declaration would
    ///   certify exactly the provider that lies.</para>
    /// </summary>
    public static class ConformanceVerifier
    {
        /// <summary>Words that would make identity a matter of degree. Identity is exact or it is nothing.</summary>
        private static readonly String[] SimilarityWords =
        {
            "score", "similarity", "confidence", "threshold", "probability", "distance",
        };

        /// <summary>
        ///   Verifies one candidate.
        /// </summary>
        /// <param name="candidate">The provider under test.</param>
        /// <param name="job">The job to run it with, twice.</param>
        /// <param name="files">The files the fixture offers, by name. A candidate naming any other file fails.</param>
        /// <param name="credentials">The credentials the fixture offers, by name.</param>
        /// <param name="sourceDouble">A stand-in for the provider's own service. With one supplied the candidate
        /// must reach its source through it; with none, nothing may be attempted.</param>
        /// <param name="options">Substitutions a negative fixture needs, such as a resolver that looks across
        /// instances, and the seeding of a graph with a history.</param>
        /// <param name="cancellationToken">Aborts the runs.</param>
        public static async Task<ConformanceReport> VerifyAsync(IIntegrationProvider candidate, IntegrationJob job,
            IReadOnlyDictionary<String, String>? files = null,
            IReadOnlyDictionary<String, String>? credentials = null,
            HttpMessageHandler? sourceDouble = null,
            ConformanceOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (candidate == null)
            {
                throw new ArgumentNullException(nameof(candidate));
            }

            if (job == null)
            {
                throw new ArgumentNullException(nameof(job));
            }

            options ??= new ConformanceOptions();

            var runtimeOptions = new IntegrationsOptions();
            var settings = Options.Create(runtimeOptions);

            var graph = new InMemoryGraphTarget();

            // The indices exist before the fixture seeds, so a seeded element with a history is FINDABLE by its
            // claim key, exactly as a previous run would have left it.
            await graph.EnsureIndicesAsync(cancellationToken).ConfigureAwait(false);
            options.Seed?.Invoke(graph);

            var target = options.DecorateTarget == null ? graph : options.DecorateTarget(graph);

            var active = new ActiveCredentials();
            var attemptedSink = new CapturingLoggerProvider();
            var reachedSink = new CapturingLoggerProvider();

            // Two sinks, and the pair is the point. The REDACTED one proves the safety net works; the RAW one
            // sees what the provider tried to log. A provider that logs its credential must fail even though the
            // net caught it, because the line is the author's to fix - and a check that only looked at the
            // redacted sink could never go red at all.
            using var loggers = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Trace);
                builder.AddProvider(new RedactingLoggerProvider(reachedSink, active));
                builder.AddProvider(attemptedSink);
            });

            var vocabulary = IdentifierVocabulary.Shipped;
            var catalog = new ProviderCatalog(new[] { candidate }, vocabulary);
            var validator = new SnapshotValidator(vocabulary);
            var applier = new SnapshotApplier(new IdentityResolver());
            var fileStore = new FixtureFileStore(files);
            var credentialStore = new FixtureCredentialStore(credentials);
            var handler = new RecordingHandler(sourceDouble);
            using var metrics = new IntegrationsMetrics();

            var runner = new JobRunner(catalog, validator, applier,
                new CredentialResolver(credentialStore, active),
                new StaticGraphTargetFactory(target),
                new RecordingHttpFactory(handler, runtimeOptions),
                fileStore, active, new RunGate(), metrics, loggers);

            // Which elements are OUT OF SCOPE, captured before each run rather than inferred afterwards. It
            // has to be before: a run that wrongly adopts another instance's element ends up carrying its own
            // claim on it too, so the final state alone cannot tell adoption from the legitimate case of
            // withdrawing this instance's own claim from an element somebody unified by hand.
            var forbidden = new HashSet<Int32>();
            CollectOutOfScope(graph, job.IntegrationInstanceId ?? String.Empty, forbidden);
            var first = await RunOnceAsync(runner, job, candidate, cancellationToken).ConfigureAwait(false);
            var mutationsAfterFirst = graph.MutationCalls.Count;
            CollectOutOfScope(graph, job.IntegrationInstanceId ?? String.Empty, forbidden);
            var second = await RunOnceAsync(runner, job, candidate, cancellationToken).ConfigureAwait(false);

            var findings = ImmutableArray.CreateBuilder<ConformanceFinding>(12);

            var observed = candidate is IObservableProvider;
            var validation = first.Snapshot == null ? null : validator.Validate(first.Snapshot, candidate.Descriptor);

            findings.Add(SnapshotValid(observed, first, validation));
            findings.Add(ClaimsWellFormed(observed, validation));
            findings.Add(StrengthHonest(observed, validation));
            findings.Add(Deterministic(observed, first, second));
            findings.Add(Idempotent(graph, mutationsAfterFirst, second));
            findings.Add(ClaimScoped(graph, forbidden));
            findings.Add(NoSimilarityIdentity(observed, first));
            findings.Add(RunsOffline(handler, fileStore, first));
            findings.Add(NoCredentialLeak(CredentialValuesToWatch(credentials, job), attemptedSink, reachedSink,
                graph, first, second));
            findings.Add(NoPathEscape(fileStore, files));
            findings.Add(CompletenessHonest(observed, candidate.Descriptor, first));
            findings.Add(UnreadableSourceFails(handler, first, second));

            return new ConformanceReport(findings.ToImmutable());
        }

        private static async Task<Observation> RunOnceAsync(JobRunner runner, IntegrationJob job,
            IIntegrationProvider candidate, CancellationToken cancellationToken)
        {
            JobReport? report = null;
            String? rejection = null;
            try
            {
                report = await runner.RunAsync(job, cancellationToken).ConfigureAwait(false);
            }
            catch (JobRejectedException ex)
            {
                rejection = ex.Kind + ": " + ex.Message;
            }

            var snapshot = (candidate as IObservableProvider)?.LastSnapshot;
            return new Observation(report, rejection, snapshot);
        }

        private static ConformanceFinding SnapshotValid(Boolean observed, Observation run,
            ValidatedSnapshot? validation)
        {
            if (!observed)
            {
                return Unjudgeable(ConformanceCheck.SnapshotValid);
            }

            if (run.Snapshot == null)
            {
                return new ConformanceFinding(ConformanceCheck.SnapshotValid, false, String.Format(
                    "The run produced no snapshot to judge ({0}). A provider that observed nothing says so with " +
                    "an empty snapshot and a completeness declaration.", run.Outcome()));
            }

            return validation!.EnvelopeAccepted
                ? new ConformanceFinding(ConformanceCheck.SnapshotValid, true,
                    "The envelope declares a supported schema version, a provider, an instance and a completeness.")
                : new ConformanceFinding(ConformanceCheck.SnapshotValid, false,
                    "The envelope was refused: " + Codes(validation));
        }

        private static ConformanceFinding ClaimsWellFormed(Boolean observed, ValidatedSnapshot? validation)
        {
            if (!observed || validation == null)
            {
                return Unjudgeable(ConformanceCheck.ClaimsWellFormed);
            }

            var offenders = Matching(validation, DiagnosticCodes.UnknownIdentifierType,
                DiagnosticCodes.InvalidIdentifierValue);
            return offenders.Count == 0
                ? new ConformanceFinding(ConformanceCheck.ClaimsWellFormed, true,
                    "Every claim names a vocabulary type whose value canonicalises and then validates.")
                : new ConformanceFinding(ConformanceCheck.ClaimsWellFormed, false,
                    "Claims the vocabulary rejected: " + String.Join("; ", offenders));
        }

        private static ConformanceFinding StrengthHonest(Boolean observed, ValidatedSnapshot? validation)
        {
            if (!observed || validation == null)
            {
                return Unjudgeable(ConformanceCheck.StrengthDeclarationHonest);
            }

            var offenders = Matching(validation, DiagnosticCodes.DeclaredStrengthMismatch,
                DiagnosticCodes.UnknownStrengthWord);
            return offenders.Count == 0
                ? new ConformanceFinding(ConformanceCheck.StrengthDeclarationHonest, true,
                    "No claim declares a strength the vocabulary disagrees with.")
                : new ConformanceFinding(ConformanceCheck.StrengthDeclarationHonest, false,
                    "A provider that calls its own weak identifier strong makes an address resolve, and the run " +
                    "then attaches its data to whichever element last held that address: " +
                    String.Join("; ", offenders));
        }

        private static ConformanceFinding Deterministic(Boolean observed, Observation first, Observation second)
        {
            if (!observed)
            {
                return Unjudgeable(ConformanceCheck.Deterministic);
            }

            if (first.Snapshot == null || second.Snapshot == null)
            {
                return new ConformanceFinding(ConformanceCheck.Deterministic, false,
                    "One of the two runs produced no snapshot, so the pair cannot be compared.");
            }

            var left = Normalized(first.Snapshot);
            var right = Normalized(second.Snapshot);
            return String.Equals(left, right, StringComparison.Ordinal)
                ? new ConformanceFinding(ConformanceCheck.Deterministic, true,
                    "Two runs over one fixture describe it identically.")
                : new ConformanceFinding(ConformanceCheck.Deterministic, false,
                    "Two runs over ONE unchanged fixture described it differently, so every run is a write and " +
                    "the zero-mutation invariant can never hold.");
        }

        private static ConformanceFinding Idempotent(InMemoryGraphTarget graph, Int32 mutationsAfterFirst,
            Observation second)
        {
            var afterSecond = graph.MutationCalls.Count;
            if (afterSecond != mutationsAfterFirst)
            {
                var issued = new List<String>();
                for (var i = mutationsAfterFirst; i < afterSecond; i++)
                {
                    issued.Add(graph.MutationCalls[i]);
                }

                return new ConformanceFinding(ConformanceCheck.Idempotent, false, String.Format(
                    "The second run over an unchanged source issued {0} write call(s): {1}. Every write must be " +
                    "conditional on a difference, or the change feed churns on every run and the write-ahead log " +
                    "grows with nothing to show for it.", issued.Count, String.Join(", ", issued)));
            }

            if (second.Report != null && second.Report.IssuedMutations)
            {
                return new ConformanceFinding(ConformanceCheck.Idempotent, false,
                    "The second run reported issuing mutations while the graph saw none, which means the report " +
                    "and the call channel disagree.");
            }

            return new ConformanceFinding(ConformanceCheck.Idempotent, true,
                "A second run over an unchanged source issued no write call at all.");
        }

        private static void CollectOutOfScope(InMemoryGraphTarget graph, String instanceId,
            HashSet<Int32> forbidden)
        {
            foreach (var element in graph.AllElements())
            {
                if (!ElementScope.IsInScope(element, instanceId))
                {
                    forbidden.Add(element.Id);
                }
            }
        }

        private static ConformanceFinding ClaimScoped(InMemoryGraphTarget graph, HashSet<Int32> forbidden)
        {
            var offenders = new List<String>();

            foreach (var id in graph.TouchedElements)
            {
                if (forbidden.Contains(id))
                {
                    offenders.Add(id.ToString(CultureInfo.InvariantCulture));
                }
            }

            return offenders.Count == 0
                ? new ConformanceFinding(ConformanceCheck.ClaimScoped, true,
                    "The run wrote only to elements it claims, to elements it withdrew its own claim from, and to " +
                    "unclaimed orphans it reclaimed.")
                : new ConformanceFinding(ConformanceCheck.ClaimScoped, false, String.Format(
                    "The run wrote to element(s) {0}, which another instance claims. Nothing here may unify or " +
                    "adopt another integration's element: the two elements are meant to share a queryable claim " +
                    "key, which is how an overlap becomes findable.", String.Join(", ", offenders)));
        }

        private static ConformanceFinding NoSimilarityIdentity(Boolean observed, Observation run)
        {
            if (!observed)
            {
                return Unjudgeable(ConformanceCheck.NoSimilarityIdentity);
            }

            if (run.Snapshot == null)
            {
                return new ConformanceFinding(ConformanceCheck.NoSimilarityIdentity, false,
                    "The run produced no snapshot to judge.");
            }

            var offenders = new List<String>();
            foreach (var entity in run.Snapshot.Entities ?? new List<EntityDto>())
            {
                if (entity?.Properties == null)
                {
                    continue;
                }

                foreach (var key in entity.Properties.Keys)
                {
                    foreach (var word in SimilarityWords)
                    {
                        if (key.Contains(word, StringComparison.OrdinalIgnoreCase))
                        {
                            offenders.Add(key);
                            break;
                        }
                    }
                }
            }

            return offenders.Count == 0
                ? new ConformanceFinding(ConformanceCheck.NoSimilarityIdentity, true,
                    "Nothing in the snapshot offers a score, a threshold or a confidence.")
                : new ConformanceFinding(ConformanceCheck.NoSimilarityIdentity, false, String.Format(
                    "The snapshot offers {0}. Similarity is never an identity signal at any strength under any " +
                    "configuration: two identical smart plugs produce identical text and therefore identical " +
                    "vectors, and they are different devices.", String.Join(", ", offenders)));
        }

        private static ConformanceFinding RunsOffline(RecordingHandler handler, FixtureFileStore files,
            Observation first)
        {
            if (!handler.HasSourceDouble)
            {
                return handler.Attempts.Length == 0
                    ? new ConformanceFinding(ConformanceCheck.RunsOffline, true,
                        "With no stand-in supplied, nothing was attempted.")
                    : new ConformanceFinding(ConformanceCheck.RunsOffline, false, String.Format(
                        "With no stand-in supplied, the provider tried to reach {0}. The suite must be able to " +
                        "stand in for everything a run touches, or nobody can iterate on this integration.",
                        handler.Attempts[0].Address));
            }

            if (first.Report == null)
            {
                return new ConformanceFinding(ConformanceCheck.RunsOffline, false,
                    "The run did not produce a report: " + first.Outcome());
            }

            // Reaching a stand-in OR reading a fixture file is what "completed against substituted seams" looks
            // like. A run that produced entities while touching neither got its data from somewhere the suite did
            // not provide, which is exactly the provider that opened its own socket behind the runtime's back -
            // and a check reading only "attempted no request" would PASS it.
            if (handler.Attempts.Length > 0 || files.Requested.Count > 0)
            {
                return new ConformanceFinding(ConformanceCheck.RunsOffline, true, String.Format(
                    CultureInfo.InvariantCulture,
                    "The run completed against substituted seams alone: {0} request(s) through the stand-in and " +
                    "{1} file read(s) from the fixture.", handler.Attempts.Length, files.Requested.Count));
            }

            return new ConformanceFinding(ConformanceCheck.RunsOffline, false,
                "The run touched neither the supplied stand-in nor any fixture file, so whatever it described " +
                "came from a seam this suite does not control.");
        }

        /// <summary>
        ///   Every credential value this run could leak: the ones the fixture offers BY NAME and the ones the job
        ///   carries INLINE. Both, because a check that watched only the fixture would pass a candidate that logs
        ///   a credential its caller typed - which is the same leak from the same lease.
        /// </summary>
        private static IReadOnlyList<String> CredentialValuesToWatch(
            IReadOnlyDictionary<String, String>? fixtureCredentials, IntegrationJob job)
        {
            var watched = new List<String>();
            if (fixtureCredentials != null)
            {
                watched.AddRange(fixtureCredentials.Values);
            }

            if (job.CredentialValues != null)
            {
                watched.AddRange(job.CredentialValues.Values);
            }

            return watched;
        }

        private static ConformanceFinding NoCredentialLeak(IReadOnlyList<String> credentialValues,
            CapturingLoggerProvider attempted, CapturingLoggerProvider reached, InMemoryGraphTarget graph,
            Observation first, Observation second)
        {
            if (credentialValues.Count == 0)
            {
                return new ConformanceFinding(ConformanceCheck.NoCredentialLeak, true,
                    "Neither the fixture nor the job carried a credential, so there was none to leak.");
            }

            var offenders = new List<String>();
            foreach (var value in credentialValues)
            {
                var trimmed = value?.TrimEnd('\r', '\n');
                if (String.IsNullOrEmpty(trimmed))
                {
                    continue;
                }

                if (Contains(attempted, trimmed!))
                {
                    offenders.Add("the provider tried to log it (the redaction net caught it, but the line is " +
                                  "yours to fix)");
                }

                if (Contains(reached, trimmed!))
                {
                    offenders.Add("it reached a log sink");
                }

                if (InGraph(graph, trimmed!))
                {
                    offenders.Add("it was written into the graph as a property value");
                }

                if (InReport(first, trimmed!) || InReport(second, trimmed!))
                {
                    offenders.Add("it appears on the job report");
                }
            }

            return offenders.Count == 0
                ? new ConformanceFinding(ConformanceCheck.NoCredentialLeak, true,
                    "No credential value reached a log sink, the job report or the graph.")
                : new ConformanceFinding(ConformanceCheck.NoCredentialLeak, false,
                    "A credential leaked: " + String.Join("; ", offenders));
        }

        private static ConformanceFinding NoPathEscape(FixtureFileStore files,
            IReadOnlyDictionary<String, String>? offered)
        {
            var offenders = new List<String>();
            foreach (var requested in files.Requested)
            {
                if (offered == null || !offered.ContainsKey(requested))
                {
                    offenders.Add(requested);
                }
            }

            return offenders.Count == 0
                ? new ConformanceFinding(ConformanceCheck.NoPathEscape, true,
                    "Every file read was one the fixture offered, by name.")
                : new ConformanceFinding(ConformanceCheck.NoPathEscape, false, String.Format(
                    "The provider named file(s) the fixture does not have: {0}. A provider never opens a file, " +
                    "and a name that resolves anywhere but the files directory is refused.",
                    String.Join(", ", offenders)));
        }

        private static ConformanceFinding CompletenessHonest(Boolean observed, ProviderDescriptor descriptor,
            Observation first)
        {
            if (!observed)
            {
                return Unjudgeable(ConformanceCheck.CompletenessHonest);
            }

            if (first.Snapshot == null)
            {
                return new ConformanceFinding(ConformanceCheck.CompletenessHonest, false,
                    "The run produced no snapshot to judge.");
            }

            if (!descriptor.CanObserveCompleteState &&
                first.Snapshot.Declares == SnapshotCompleteness.Complete)
            {
                return new ConformanceFinding(ConformanceCheck.CompletenessHonest, false,
                    "The descriptor says this provider cannot observe the source's whole state, and the snapshot " +
                    "declares itself complete. Completeness licenses withdrawal, so acting on that would withdraw " +
                    "every element the run did not see and delete what the source still has.");
            }

            return new ConformanceFinding(ConformanceCheck.CompletenessHonest, true,
                "The completeness the snapshot declares is one the descriptor supports.");
        }

        private static ConformanceFinding UnreadableSourceFails(RecordingHandler handler, Observation first,
            Observation second)
        {
            foreach (var run in new[] { first, second })
            {
                var report = run.Report;
                if (report == null || !report.Failed)
                {
                    continue;
                }

                if (report.ClaimsWithdrawn > 0 || report.ElementsDeleted > 0)
                {
                    return new ConformanceFinding(ConformanceCheck.UnreadableSourceFails, false, String.Format(
                        CultureInfo.InvariantCulture,
                        "A run that failed ({0}) withdrew {1} claim(s) and deleted {2} element(s). A failed run " +
                        "is cheap: nothing withdrawn, nothing deleted, so the next run starts from the same graph.",
                        report.ErrorKind, report.ClaimsWithdrawn, report.ElementsDeleted));
                }
            }

            foreach (var attempt in handler.Attempts)
            {
                if (attempt.Answered)
                {
                    continue;
                }

                if (first.Report != null && !first.Report.Failed)
                {
                    return new ConformanceFinding(ConformanceCheck.UnreadableSourceFails, false, String.Format(
                        "The source answered unusably ({0} {1} -> {2}) and the run SUCCEEDED anyway. An answer " +
                        "that cannot be trusted is a failure, not an empty snapshot: 'I could not look' must " +
                        "never become 'there is nothing there', because a complete snapshot with no entities " +
                        "withdraws everything this identity ever claimed.",
                        attempt.Method, attempt.Address,
                        attempt.Failure ?? attempt.Status.ToString(CultureInfo.InvariantCulture)));
                }
            }

            return new ConformanceFinding(ConformanceCheck.UnreadableSourceFails, true,
                "Every failed run withdrew nothing, and no run succeeded over a source that answered unusably.");
        }

        private static ConformanceFinding Unjudgeable(ConformanceCheck check)
        {
            // Recorded as UNJUDGEABLE AND FAILING rather than passed by default: the snapshot checks need the
            // document the provider returned, the runtime never needs it, and a check that cannot fail is not a
            // check.
            return new ConformanceFinding(check, false,
                "This candidate does not implement IObservableProvider, so the document it returned cannot be " +
                "looked at. A provider that hides its snapshot cannot be judged, and unjudgeable is not a pass.");
        }

        private static List<String> Matching(ValidatedSnapshot validation, params String[] codes)
        {
            var offenders = new List<String>();
            foreach (var diagnostic in validation.Diagnostics)
            {
                foreach (var code in codes)
                {
                    if (String.Equals(diagnostic.Code, code, StringComparison.Ordinal))
                    {
                        offenders.Add(diagnostic.Code + " (" + diagnostic.Subject + ")");
                        break;
                    }
                }
            }

            return offenders;
        }

        private static String Codes(ValidatedSnapshot validation)
        {
            var codes = new List<String>();
            foreach (var diagnostic in validation.Diagnostics)
            {
                if (diagnostic.Code != null)
                {
                    codes.Add(diagnostic.Code);
                }
            }

            return String.Join(", ", codes);
        }

        /// <summary>
        ///   The snapshot as text with <c>capturedAt</c> normalised, which is the only field two runs over one
        ///   unchanged fixture are allowed to differ in.
        /// </summary>
        private static String Normalized(SnapshotDocument snapshot)
        {
            var captured = snapshot.CapturedAt;
            try
            {
                snapshot.CapturedAt = "(normalised)";
                return JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
            finally
            {
                snapshot.CapturedAt = captured;
            }
        }

        private static Boolean Contains(CapturingLoggerProvider sink, String value)
        {
            foreach (var line in sink.Lines)
            {
                if (line.Contains(value, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static Boolean InGraph(InMemoryGraphTarget graph, String value)
        {
            foreach (var element in graph.AllElements())
            {
                foreach (var property in element.Properties)
                {
                    if (property.Value.Text != null &&
                        property.Value.Text.Contains(value, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static Boolean InReport(Observation run, String value)
        {
            var report = run.Report;
            if (report == null)
            {
                return run.Rejection != null && run.Rejection.Contains(value, StringComparison.Ordinal);
            }

            if (report.Error != null && report.Error.Contains(value, StringComparison.Ordinal))
            {
                return true;
            }

            foreach (var diagnostic in report.Diagnostics)
            {
                if ((diagnostic.Message != null && diagnostic.Message.Contains(value, StringComparison.Ordinal)) ||
                    (diagnostic.Subject != null && diagnostic.Subject.Contains(value, StringComparison.Ordinal)))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>What one of the two runs produced.</summary>
        private sealed class Observation
        {
            public Observation(JobReport? report, String? rejection, SnapshotDocument? snapshot)
            {
                Report = report;
                Rejection = rejection;
                Snapshot = snapshot;
            }

            public JobReport? Report { get; }

            public String? Rejection { get; }

            public SnapshotDocument? Snapshot { get; }

            public String Outcome()
            {
                if (Rejection != null)
                {
                    return "the job was rejected: " + Rejection;
                }

                if (Report == null)
                {
                    return "no report";
                }

                return Report.Failed ? Report.ErrorKind + ": " + Report.Error : "the run succeeded";
            }
        }
    }

    /// <summary>
    ///   The substitutions a negative fixture needs. They exist because ONE check has no provider-shaped red
    ///   path: the runtime owns every claim write, so no candidate can violate the claim scope, and the only way
    ///   to turn that check red is to substitute a seam inside the real stack.
    /// </summary>
    public sealed class ConformanceOptions
    {
        /// <summary>
        ///   Gives the graph a history before the first run: an element another instance claims, or an orphan left
        ///   by a deferred deletion. The two claim indices already exist when this runs, so a seeded element is
        ///   findable by its claim key.
        /// </summary>
        public Action<InMemoryGraphTarget>? Seed { get; set; }

        /// <summary>
        ///   Wraps the graph target, which is where resolution's narrowing lives. A wrapper that widens the
        ///   in-scope set to elements another instance claims is a resolver that looks across instances, and it is
        ///   what makes the claim-scope check red.
        /// </summary>
        public Func<InMemoryGraphTarget, IGraphTarget>? DecorateTarget { get; set; }
    }
}
