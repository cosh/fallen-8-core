// MIT License
//
// IntegrationsConformanceTest.cs
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
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Integrations.Conformance;
using NoSQL.GraphDB.Integrations.Contract;
using NoSQL.GraphDB.Integrations.Graph;
using NoSQL.GraphDB.Integrations.Identity;
using NoSQL.GraphDB.Integrations.Run;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The conformance suite's own tests (feature integrations, spec section 13). The suite, not prose, is what
    ///   makes the "fourth integration without review" claim safe, so the suite itself needs one deliberately
    ///   broken provider per check: a verifier answering only "invalid" cannot be tested, and the only way to know
    ///   it looks at the right thing is for a broken candidate to fail the check it was broken for, BY NAME.
    ///
    ///   <para>Every negative asserts on <c>Failed</c> rather than on "does not conform", because asserting the
    ///   latter passes for the wrong reason. Where one fixture legitimately trips a second check, that collateral
    ///   is asserted too rather than hidden, since it is a true statement about the provider.</para>
    /// </summary>
    [TestClass]
    public class IntegrationsConformanceTest
    {
        private const String Instance = "fixture-instance";
        private const String CredentialValue = "s3cr3t-console-key";
        private const String SourceUrl = "https://console.invalid/devices";

        #region the positives, which are what stop every check from being unconditional

        [TestMethod]
        public async Task AWellBehavedProviderConforms()
        {
            var report = await VerifyAsync(WellBehaved());

            Assert.AreEqual(0, report.Failed.Count, String.Format(
                "a provider that does everything right must pass every check, or a check is unconditional and " +
                "everyone who sees it fail reads it as evidence. Failed: {0}",
                String.Join(", ", report.Failed.Select(c => c + " (" + report.DetailOf(c) + ")"))));
            Assert.IsTrue(report.Conforms);
        }

        [TestMethod]
        public async Task EveryCheckTheEnumDeclaresIsActuallyReported()
        {
            var report = await VerifyAsync(WellBehaved());

            foreach (ConformanceCheck check in Enum.GetValues(typeof(ConformanceCheck)))
            {
                Assert.IsTrue(report.Findings.Any(f => f.Check == check), String.Format(
                    "the enum declares {0} and the verifier never recorded it. Without this test a thirteenth " +
                    "check that is never reported reads as a passing suite, and the report becomes a shorter " +
                    "document than it claims to be.", check));
            }

            Assert.AreEqual(Enum.GetValues(typeof(ConformanceCheck)).Length, report.Findings.Length,
                "one finding per check, no more: a duplicate would let one check's pass hide another's failure");
        }

        #endregion

        #region one negative per check, each failing its OWN check

        [TestMethod]
        public async Task AProviderWithNoCompletenessDeclarationFailsTheEnvelopeCheck()
        {
            var candidate = WellBehaved();
            candidate.Mutate = document => document.Completeness = null;

            var report = await VerifyAsync(candidate);

            CollectionAssert.Contains(report.Failed.ToList(), ConformanceCheck.SnapshotValid,
                "completeness is the ONE field that licenses a withdrawal, so nothing in a document without it " +
                "can be trusted");
        }

        [TestMethod]
        public async Task AProviderEmittingAnUnknownIdentifierTypeFailsTheClaimCheck()
        {
            var candidate = WellBehaved();
            candidate.Mutate = document => document.Entities[0].Claims.Add(
                new IdentityClaimDto { Type = "invented-identifier", Value = "whatever" });

            var report = await VerifyAsync(candidate);

            CollectionAssert.Contains(report.Failed.ToList(), ConformanceCheck.ClaimsWellFormed,
                "an identifier nothing knows can never resolve, so the entity relying on it would be created " +
                "again on every run");
        }

        [TestMethod]
        public async Task AProviderThatPromotesItsOwnWeakIdentifierFailsTheStrengthCheck()
        {
            var candidate = WellBehaved();
            candidate.Mutate = document => document.Entities[0].Claims.Add(new IdentityClaimDto
            {
                Type = "ipv4",
                Value = "10.0.0.9",
                DeclaredStrength = "strong",
            });

            var report = await VerifyAsync(candidate);

            CollectionAssert.Contains(report.Failed.ToList(), ConformanceCheck.StrengthDeclarationHonest,
                "a provider able to call its own weak identifier strong makes an address resolve, and the run " +
                "then attaches its data to whichever element last held that address");
        }

        [TestMethod]
        public async Task ANonDeterministicProviderFailsTheDeterminismCheck()
        {
            var candidate = WellBehaved();
            var runs = 0;
            candidate.Mutate = document =>
            {
                runs++;
                document.Entities[0].Properties["test.reading"] = runs;
            };

            var report = await VerifyAsync(candidate);

            CollectionAssert.Contains(report.Failed.ToList(), ConformanceCheck.Deterministic,
                "two runs over ONE unchanged fixture describing it differently means every run is a write, and " +
                "the zero-mutation invariant can never hold");
            CollectionAssert.Contains(report.Failed.ToList(), ConformanceCheck.Idempotent,
                "and the same fixture is honestly non-idempotent too, which is asserted rather than hidden");
        }

        [TestMethod]
        public async Task AProviderWhoseValueTypeDriftsFailsTheIdempotenceCheckAlone()
        {
            // The sharp case: the two snapshots SERIALISE identically, so determinism passes, and yet the second
            // run writes - because the value's declared TYPE changed and the graph stores the type alongside the
            // text. This is the failure mode a provider author cannot see in a diff of their own output.
            var candidate = WellBehaved();
            var runs = 0;
            candidate.Mutate = document =>
            {
                runs++;
                document.Entities[0].Properties["test.port"] = runs == 1 ? (Object)(Int32)5 : (Int64)5;
            };

            var report = await VerifyAsync(candidate);

            CollectionAssert.DoesNotContain(report.Failed.ToList(), ConformanceCheck.Deterministic,
                "the snapshots are identical as text, which is exactly what makes this one hard to see");
            CollectionAssert.Contains(report.Failed.ToList(), ConformanceCheck.Idempotent,
                "a second run over an unchanged source must issue zero write calls, or the change feed churns " +
                "and the write-ahead log grows on every run for nothing");
        }

        [TestMethod]
        public async Task ACrossInstanceResolverSubstitutedIntoTheSameStackFailsTheClaimScopeCheck()
        {
            // THE ONE CHECK NO CANDIDATE PROVIDER CAN TURN RED, because the runtime owns every claim write. Its
            // red path is therefore not provider-shaped: a resolver that looks ACROSS instances, substituted into
            // the real stack over a graph seeded with an element another instance claims.
            var candidate = WellBehaved();

            var report = await VerifyAsync(candidate, options: new ConformanceOptions
            {
                Seed = graph => graph.SeedVertex("device", new[]
                {
                    new GraphProperty(ClaimSchema.IdentityProperty(0), "System.String", "mac:44d244aabbcc"),
                    new GraphProperty(ClaimSchema.ClaimProperty("another-instance"), "System.String",
                        "another-instance"),
                }),
                DecorateTarget = graph => new CrossInstanceResolvingTarget(graph),
            });

            CollectionAssert.Contains(report.Failed.ToList(), ConformanceCheck.ClaimScoped,
                "a run writes only to what it claims, to what it withdraws its own claim from, and to an " +
                "unclaimed orphan it reclaims. Nothing here may adopt another integration's element: the two are " +
                "meant to share a queryable claim key, which is how an overlap becomes findable");
        }

        [TestMethod]
        public async Task AProviderThatOffersASimilarityScoreFailsTheSimilarityCheck()
        {
            var candidate = WellBehaved();
            candidate.Mutate = document => document.Entities[0].Properties["test.matchScore"] = "0.93";

            var report = await VerifyAsync(candidate);

            CollectionAssert.Contains(report.Failed.ToList(), ConformanceCheck.NoSimilarityIdentity,
                "two identical smart plugs produce identical text and therefore identical vectors, and they are " +
                "different devices: identity is exact or it is nothing");
        }

        [TestMethod]
        public async Task AProviderThatReachesTheNetworkCannotBeJudgedOffline()
        {
            // No stand-in supplied at all, and the provider still tries to reach its source.
            var report = await VerifyAsync(WellBehaved(), withSourceDouble: false);

            CollectionAssert.Contains(report.Failed.ToList(), ConformanceCheck.RunsOffline,
                "an author who needs a controller on the desk to iterate will not iterate, and an integration " +
                "nobody can iterate on is one nobody writes");
            StringAssert.Contains(report.DetailOf(ConformanceCheck.RunsOffline), "console.invalid",
                "the refusal names the address that was tried, or the author cannot tell which seam escaped");
        }

        [TestMethod]
        public async Task AProviderThatLogsItsCredentialFailsTheLeakCheck()
        {
            var candidate = WellBehaved();
            candidate.Extra = (context, snapshot) =>
                context.Logger.LogWarning("Authenticating with {Key}", context.RequiredCredential("apiKey"));

            var report = await VerifyAsync(candidate);

            CollectionAssert.Contains(report.Failed.ToList(), ConformanceCheck.NoCredentialLeak,
                "redaction is a safety net and not a licence: the net caught this one, but a careless line in an " +
                "HTTP failure path is the author's to fix, and a check that only looked at the scrubbed sink " +
                "could never turn red at all");
        }

        [TestMethod]
        public async Task AProviderWhoseFAILUREQuotesItsCredential_DoesNotLeakItThroughTheReport()
        {
            var candidate = WellBehaved();
            candidate.Extra = (context, snapshot) => throw new ProviderSourceException(
                "the console refused " + context.RequiredCredential("apiKey"));

            var report = await VerifyAsync(candidate);

            // A provider whose exception message quotes the request it sent is ORDINARY, and the report is the
            // one thing a run hands back. This is the only check that exercises the runner's scrub of the
            // report: remove that scrub and NoCredentialLeak's in-report arm turns this green assertion red.
            CollectionAssert.DoesNotContain(report.Failed.ToList(), ConformanceCheck.NoCredentialLeak,
                "the credential reached the report through the provider's failure message. The runtime scrubs " +
                "the report inside the lease for exactly this shape: " +
                report.DetailOf(ConformanceCheck.NoCredentialLeak));

            // Collateral, asserted rather than hidden: the run failed on purpose, so the checks that need a
            // snapshot legitimately fail too.
            CollectionAssert.Contains(report.Failed.ToList(), ConformanceCheck.SnapshotValid,
                "a run that threw produced no snapshot, and that is a true statement about this fixture");
        }

        [TestMethod]
        public async Task AProviderThatPutsItsCredentialInThePropertiesIsCaught()
        {
            var candidate = WellBehaved();
            candidate.Extra = (context, snapshot) =>
                snapshot.Entities[0].Properties["test.key"] = context.RequiredCredential("apiKey");

            var report = await VerifyAsync(candidate);

            CollectionAssert.Contains(report.Failed.ToList(), ConformanceCheck.NoCredentialLeak,
                "a credential written into the graph outlives the run, the lease and the rotation, and every " +
                "reader of that namespace can see it");
        }

        [TestMethod]
        public async Task AProviderNamingAFileTheFixtureDoesNotHaveFails()
        {
            var candidate = WellBehaved();
            candidate.Descriptor.Settings = candidate.Descriptor.Settings.Concat(new[]
            {
                new ProviderSetting { Key = "file", Label = "File", Kind = SettingKind.Text, Required = false },
            }).ToList();
            candidate.Extra = (context, snapshot) => context.ReadFileAsync("file", CancellationToken.None)
                .GetAwaiter().GetResult();

            var job = Job();
            job.Settings["file"] = "../etc/shadow";

            var report = await VerifyAsync(candidate, job: job);

            CollectionAssert.Contains(report.Failed.ToList(), ConformanceCheck.NoPathEscape,
                "a provider that could name a path could be pointed at anything this container can read and " +
                "made to hand the contents back in a report or write them into the graph, and blocklisting a " +
                "directory only moves the target");
        }

        [TestMethod]
        public async Task AProviderThatOverDeclaresCompletenessFailsTheCompletenessCheck()
        {
            var candidate = WellBehaved();
            candidate.Descriptor.CanObserveCompleteState = false;

            var report = await VerifyAsync(candidate);

            CollectionAssert.Contains(report.Failed.ToList(), ConformanceCheck.CompletenessHonest,
                "the consequence is the worst available: every unobserved element becomes a withdrawal and the " +
                "graph deletes what the source still has");
        }

        [TestMethod]
        public async Task AProviderThatTurnsAnUnreachableSourceIntoAnEmptySnapshotFailsTheUnreadableSourceCheck()
        {
            var candidate = WellBehaved();

            // The source answers unusably and the provider says "there is nothing there" instead of failing.
            candidate.Mutate = document => document.Entities.Clear();

            var report = await VerifyAsync(candidate, sourceDouble: Refusing(HttpStatusCode.InternalServerError));

            CollectionAssert.Contains(report.Failed.ToList(), ConformanceCheck.UnreadableSourceFails,
                "an answer that cannot be trusted is a failure, not an empty snapshot: 'I could not look' must " +
                "never become 'there is nothing there', because a complete snapshot with no entities withdraws " +
                "everything this identity ever claimed");
        }

        [TestMethod]
        public async Task AProviderThatHidesItsSnapshotCannotBeJudged()
        {
            var report = await VerifyAsync(new OpaqueProvider());

            CollectionAssert.Contains(report.Failed.ToList(), ConformanceCheck.SnapshotValid,
                "the snapshot checks need the document the provider returned, and unjudgeable is not a pass");
            Assert.IsFalse(report.Conforms,
                "a provider recorded as unjudgeable must not conform: a check that cannot fail is not a check");
            StringAssert.Contains(report.DetailOf(ConformanceCheck.SnapshotValid), "IObservableProvider",
                "and the detail says exactly what to implement");
        }

        #endregion

        #region fixtures

        private static async Task<ConformanceReport> VerifyAsync(IIntegrationProvider candidate,
            IntegrationJob job = null, HttpMessageHandler sourceDouble = null,
            ConformanceOptions options = null, Boolean withSourceDouble = true)
        {
            return await ConformanceVerifier.VerifyAsync(
                candidate,
                job ?? Job(),
                files: new Dictionary<String, String>(StringComparer.Ordinal) { ["devices.csv"] = "mac\n" },
                sourceDouble: sourceDouble ?? (withSourceDouble ? Answering() : null),
                options: options,
                cancellationToken: CancellationToken.None);
        }

        private static IntegrationJob Job()
        {
            var job = new IntegrationJob
            {
                ProviderId = "fixture-provider",
                IntegrationInstanceId = Instance,
            };
            job.CredentialValues["apiKey"] = CredentialValue;
            return job;
        }

        /// <summary>A stand-in that answers usably, which is what a well-behaved provider needs to read.</summary>
        private static HttpMessageHandler Answering()
        {
            return new StubHandler((request, token) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"devices\":[]}"),
            });
        }

        /// <summary>A stand-in that answers unusably, so "I could not look" can be told from "nothing is there".</summary>
        private static HttpMessageHandler Refusing(HttpStatusCode status)
        {
            return new StubHandler((request, token) => new HttpResponseMessage(status)
            {
                Content = new StringContent("the console is unwell"),
            });
        }

        /// <summary>
        ///   A provider that does everything right, which every negative fixture then breaks in exactly one way.
        ///   It reads a credential, reaches its source over https through the client it was given, and describes
        ///   one device.
        /// </summary>
        private static FixtureProvider WellBehaved()
        {
            return new FixtureProvider
            {
                Descriptor = new ProviderDescriptor
                {
                    Id = "fixture-provider",
                    DisplayName = "Fixture provider",
                    Description = "Reads a fixture source over https.",
                    Settings = new[]
                    {
                        new ProviderSetting
                        {
                            Key = "apiKey",
                            Label = "API key",
                            Kind = SettingKind.Credential,
                            Required = true,
                            Help = "The API key for the fixture source.",
                        },
                    },
                    EntityKinds = new[] { "device" },
                    ClaimTypes = new[] { "mac", "ipv4" },
                    RelationTypes = Array.Empty<String>(),
                    CanObserveCompleteState = true,
                    ReadOnly = true,
                },
            };
        }

        /// <summary>
        ///   The candidate every test shapes. Its <c>Mutate</c> hook is applied to the document AFTER it is built,
        ///   so one fixture breaks in one way and every other rule stays satisfied - which is what makes a failed
        ///   check attributable.
        /// </summary>
        private sealed class FixtureProvider : IIntegrationProvider, IObservableProvider
        {
            public ProviderDescriptor Descriptor { get; set; }

            public SnapshotDocument LastSnapshot { get; private set; }

            /// <summary>Breaks the document in exactly one way.</summary>
            public Action<SnapshotDocument> Mutate { get; set; }

            /// <summary>Does something extra with the context, such as leaking its credential.</summary>
            public Action<ProviderContext, SnapshotDocument> Extra { get; set; }

            public async Task<SnapshotDocument> ObserveAsync(ProviderContext context,
                CancellationToken cancellationToken)
            {
                // The credential is read the way a real provider reads it, so a leak fixture has something to leak
                // and the lease is exercised on every run.
                context.RequiredCredential("apiKey");

                // Reached through the client the runtime handed over, which is the only way a run can be judged
                // offline at all.
                using (var response = await context.Http.GetAsync(SourceUrl, cancellationToken))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        // A well-behaved provider would fail here. The unreadable-source fixture deliberately does
                        // not, which is the whole point of that negative.
                        context.Logger.LogWarning("The fixture source answered {Status}.", response.StatusCode);
                    }
                }

                var device = new EntityDto { Kind = "device" };
                device.Claims.Add(new IdentityClaimDto { Type = "mac", Value = "44:D2:44:AA:BB:CC" });
                device.Properties["test.name"] = "fixture device";

                var snapshot = new SnapshotDocument
                {
                    ProviderId = context.ProviderId,
                    IntegrationInstanceId = context.InstanceId,
                };
                snapshot.Declares = SnapshotCompleteness.Complete;
                snapshot.CapturedNow();
                snapshot.Entities.Add(device);

                Extra?.Invoke(context, snapshot);
                Mutate?.Invoke(snapshot);

                LastSnapshot = snapshot;
                return snapshot;
            }
        }

        /// <summary>
        ///   A provider that does not implement <see cref="IObservableProvider"/>, so nothing can look at what it
        ///   returned.
        /// </summary>
        private sealed class OpaqueProvider : IIntegrationProvider
        {
            public ProviderDescriptor Descriptor { get; } = new ProviderDescriptor
            {
                Id = "fixture-provider",
                DisplayName = "Opaque provider",
                Description = "Returns a snapshot and shows nobody.",
                EntityKinds = new[] { "device" },
                ClaimTypes = new[] { "mac" },
                CanObserveCompleteState = true,
                ReadOnly = true,
            };

            public Task<SnapshotDocument> ObserveAsync(ProviderContext context, CancellationToken cancellationToken)
            {
                var snapshot = new SnapshotDocument
                {
                    ProviderId = context.ProviderId,
                    IntegrationInstanceId = context.InstanceId,
                };
                snapshot.Declares = SnapshotCompleteness.Complete;
                return Task.FromResult(snapshot);
            }
        }

        /// <summary>
        ///   A resolver that looks ACROSS instances: it widens the in-scope set to every element the index named,
        ///   including elements another instance claims. Substituted into the real stack, this is the only red path
        ///   the claim-scope check has.
        /// </summary>
        private sealed class CrossInstanceResolvingTarget : IGraphTarget
        {
            private readonly InMemoryGraphTarget _inner;

            public CrossInstanceResolvingTarget(InMemoryGraphTarget inner)
            {
                _inner = inner;
            }

            public Int32 IssuedMutationCount => _inner.IssuedMutationCount;

            public async Task<ClaimLookup> ResolveClaimKeysAsync(IReadOnlyCollection<String> claimKeys,
                String instanceId, CancellationToken cancellationToken)
            {
                var honest = await _inner.ResolveClaimKeysAsync(claimKeys, instanceId, cancellationToken);

                // The bug being modelled: every element the index named is treated as in scope, whoever claims it.
                var widened = new Dictionary<String, IReadOnlyList<Int32>>(StringComparer.Ordinal);
                foreach (var found in honest.ByKey)
                {
                    widened[found.Key] = found.Value;
                }

                return WidenedLookup.Build(widened, honest.Elements);
            }

            public Task<Boolean> EnsureIndicesAsync(CancellationToken cancellationToken)
            {
                return _inner.EnsureIndicesAsync(cancellationToken);
            }

            public Task<IndexRepairOutcome> RepairIndicesAsync(CancellationToken cancellationToken)
            {
                return _inner.RepairIndicesAsync(cancellationToken);
            }

            public Task<IReadOnlyList<Int32>> ElementsClaimedByAsync(String instanceId,
                CancellationToken cancellationToken)
            {
                return _inner.ElementsClaimedByAsync(instanceId, cancellationToken);
            }

            public Task<IReadOnlyDictionary<Int32, ElementState>> ReadElementsAsync(IReadOnlyCollection<Int32> ids,
                CancellationToken cancellationToken)
            {
                return _inner.ReadElementsAsync(ids, cancellationToken);
            }

            public Task<IReadOnlyList<Int32>> CreateVerticesAsync(IReadOnlyList<VertexWrite> vertices,
                CancellationToken cancellationToken)
            {
                return _inner.CreateVerticesAsync(vertices, cancellationToken);
            }

            public Task<IReadOnlyList<Int32>> CreateEdgesAsync(IReadOnlyList<EdgeWrite> edges,
                CancellationToken cancellationToken)
            {
                return _inner.CreateEdgesAsync(edges, cancellationToken);
            }

            public Task ApplyPropertyWritesAsync(IReadOnlyList<PropertyWrite> writes,
                CancellationToken cancellationToken)
            {
                return _inner.ApplyPropertyWritesAsync(writes, cancellationToken);
            }

            public Task RemoveElementsAsync(IReadOnlyCollection<Int32> ids, CancellationToken cancellationToken)
            {
                return _inner.RemoveElementsAsync(ids, cancellationToken);
            }

            public Task<IndexWriteOutcome> IndexClaimsAsync(IReadOnlyList<IndexEntry> entries,
                CancellationToken cancellationToken)
            {
                return _inner.IndexClaimsAsync(entries, cancellationToken);
            }

            public Task<TargetDurability> ReadDurabilityAsync(CancellationToken cancellationToken)
            {
                return _inner.ReadDurabilityAsync(cancellationToken);
            }

            public Task<TargetEmbedding> ReadEmbeddingStateAsync(CancellationToken cancellationToken)
            {
                return _inner.ReadEmbeddingStateAsync(cancellationToken);
            }

            public Task<EmbeddingWriteOutcome> EmbedSummariesAsync(String embeddingName,
                IReadOnlyList<SummaryWrite> summaries, CancellationToken cancellationToken)
            {
                return _inner.EmbedSummariesAsync(embeddingName, summaries, cancellationToken);
            }

            public void Dispose()
            {
            }
        }

        /// <summary>
        ///   Builds a lookup whose in-scope set is deliberately wrong. It goes through the same
        ///   <see cref="ClaimLookup.Build"/> as the honest path, with an instance id no element carries, and then
        ///   swaps the narrowed map for the un-narrowed one - which is the shortest way to model "the narrowing
        ///   was skipped" without a second constructor on the production type.
        /// </summary>
        private static class WidenedLookup
        {
            public static ClaimLookup Build(IReadOnlyDictionary<String, IReadOnlyList<Int32>> byKey,
                IReadOnlyDictionary<Int32, ElementState> elements)
            {
                // Every element is claimed by SOMEBODY in this fixture, so pretending the run is that claimant
                // makes the narrowing keep them all: the same wrong answer a resolver looking across instances
                // would produce.
                var claimant = FirstClaimant(elements);
                return ClaimLookup.Build(byKey, elements, claimant);
            }

            private static String FirstClaimant(IReadOnlyDictionary<Int32, ElementState> elements)
            {
                foreach (var element in elements.Values)
                {
                    foreach (var key in element.Properties.Keys)
                    {
                        var claimant = ClaimSchema.ClaimantOf(key);
                        if (claimant != null)
                        {
                            return claimant;
                        }
                    }
                }

                return "nobody";
            }
        }

        /// <summary>A stand-in for a provider's own service.</summary>
        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _answer;

            public StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> answer)
            {
                _answer = answer;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                return Task.FromResult(_answer(request, cancellationToken));
            }
        }

        #endregion
    }
}
