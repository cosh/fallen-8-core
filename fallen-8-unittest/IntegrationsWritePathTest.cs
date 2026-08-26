// MIT License
//
// IntegrationsWritePathTest.cs
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
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Integrations.Conformance;
using NoSQL.GraphDB.Integrations.Configuration;
using NoSQL.GraphDB.Integrations.Contract;
using NoSQL.GraphDB.Integrations.Credentials;
using NoSQL.GraphDB.Integrations.Diagnostics;
using NoSQL.GraphDB.Integrations.Graph;
using NoSQL.GraphDB.Integrations.Identity;
using NoSQL.GraphDB.Integrations.Run;
using NoSQL.GraphDB.Integrations.Validation;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The write path (feature integrations, spec sections 7, 10 and 11): one test per rule, each of which
    ///   turns red if that rule alone is inverted. These rules are the part of the feature most likely to be
    ///   wrong, and every one of them fails in the same direction when it is missing - the graph silently gains
    ///   duplicates, or silently loses elements a source still has.
    ///
    ///   <para>The region "what one RUN owes" covers the rules the RUNNER owns rather than the applier, because
    ///   they fail the same way: an apply phase that honours the caller's cancellation, an instance id that forks
    ///   on a capital letter, an outcome no log line accounts for, and a log line that accounts for it
    ///   falsely.</para>
    /// </summary>
    [TestClass]
    public class IntegrationsWritePathTest
    {
        private const String Instance = "garage";
        private const String Provider = "test-provider";
        private const String Foreign = "somebody-else";

        #region resolution

        [TestMethod]
        public async Task AnUnclaimedOrphanIsRECLAIMED_NotDuplicated()
        {
            // An orphan is what a withdrawal whose deletion was deferred leaves behind: this instance's identity
            // claims, and no claim property.
            var graph = new InMemoryGraphTarget();
            await graph.EnsureIndicesAsync(CancellationToken.None);
            var orphan = Seed(graph, "device", claims: new[] { "mac:44d244aabbcc" }, claimant: null);

            var report = await ApplyAsync(graph, Document(Device("44:D2:44:AA:BB:CC")));

            Assert.AreEqual(0, report.ElementsCreated,
                "excluding an unclaimed element from scope makes the orphan invisible forever, and the graph " +
                "gains a duplicate on every run, permanently");
            Assert.AreEqual(1, report.ElementsMatched);
            Assert.IsTrue(graph.TryReadElement(orphan, out var state) && state.IsClaimedBy(Instance),
                "the orphan is reclaimed, which is the whole purpose of the unclaimed arm of the in-scope rule");
        }

        [TestMethod]
        public async Task AnElementAnotherInstanceClaims_IsNeverTouched_AndBothKeepTheSharedClaimKey()
        {
            var graph = new InMemoryGraphTarget();
            await graph.EnsureIndicesAsync(CancellationToken.None);
            var foreign = Seed(graph, "device", claims: new[] { "mac:44d244aabbcc" }, claimant: Foreign);

            var report = await ApplyAsync(graph, Document(Device("44:D2:44:AA:BB:CC")));

            Assert.AreEqual(1, report.ElementsCreated,
                "zero of its OWN matched means create, even when another instance's element carries the " +
                "identical claim key: that element is not touched, and the two elements sharing one queryable " +
                "key is the whole mechanism by which an overlap becomes findable");
            Assert.IsTrue(graph.TryReadElement(foreign, out var state));
            Assert.IsTrue(state.IsClaimedBy(Foreign), "the other instance still claims its own element");
            Assert.IsFalse(state.IsClaimedBy(Instance), "and this run did not adopt it");
        }

        [TestMethod]
        public async Task AWeakClaimNeverResolves_NotEvenAgainstThisInstanceOwnElement()
        {
            var graph = new InMemoryGraphTarget();
            await graph.EnsureIndicesAsync(CancellationToken.None);
            Seed(graph, "device", claims: new[] { "ipv4:10.0.0.9" }, claimant: Instance);

            var entity = new EntityDto { Kind = "device" };
            entity.Claims.Add(new IdentityClaimDto { Type = "ipv4", Value = "10.0.0.9" });
            var report = await ApplyAsync(graph, Document(entity));

            Assert.AreEqual(1, report.ElementsCreated,
                "an address moves between devices, so matching on one attaches this run's data to whichever " +
                "element last held the value - and the most likely victim is this runtime's own element, which " +
                "is why the 'not even my own' half is explicit");
        }

        [TestMethod]
        public async Task MoreThanOneOfItsOwn_MatchesDeterministically_AndTheChosenElementSurvivesTheSameRun()
        {
            var graph = new InMemoryGraphTarget();
            await graph.EnsureIndicesAsync(CancellationToken.None);

            // One earlier run claimed one thing under two strong keys before it saw a source row carrying both.
            var byMac = Seed(graph, "device", claims: new[] { "mac:44d244aabbcc" }, claimant: Instance);
            var bySerial = Seed(graph, "device", claims: new[] { "serial:SN-1" }, claimant: Instance);

            var entity = Device("44:D2:44:AA:BB:CC");
            entity.Claims.Add(new IdentityClaimDto { Type = "serial", Value = "SN-1" });
            var report = await ApplyAsync(graph, Document(entity));

            Assert.IsTrue(report.Diagnostics.Any(d => d.Code == DiagnosticCodes.DuplicateClaimedElements),
                "the ambiguity is reported rather than silently resolved");
            Assert.AreEqual(0, report.ElementsCreated, "neither element is duplicated");

            // "mac:..." sorts before "serial:..." ordinally, so the mac element is the content-derived pick.
            Assert.IsTrue(graph.TryReadElement(byMac, out var chosen) && chosen.IsClaimedBy(Instance),
                "the chosen element contributes to the claimed-now set. Without that, reconciliation withdraws " +
                "this instance's claim from BOTH of its own elements and deletes them, on every run");
            Assert.IsFalse(graph.TryReadElement(bySerial, out _),
                "the element not chosen stops being asserted and this same run's reconciliation converges it " +
                "away, so the graph settles within the run");
        }

        [TestMethod]
        public async Task ThePickAmongSeveral_IsContentDerived_SoItSurvivesATrimThatRenumbersIds()
        {
            // The in-memory graph cannot renumber ids, so the property a trim would break is tested directly:
            // the pick must depend on the CLAIM KEYS and not on which element happens to hold the lower id.
            var lowIdIsSerial = new InMemoryGraphTarget();
            await lowIdIsSerial.EnsureIndicesAsync(CancellationToken.None);
            var serialFirst = Seed(lowIdIsSerial, "device", new[] { "serial:SN-1" }, Instance);
            Seed(lowIdIsSerial, "device", new[] { "mac:44d244aabbcc" }, Instance);

            var lowIdIsMac = new InMemoryGraphTarget();
            await lowIdIsMac.EnsureIndicesAsync(CancellationToken.None);
            var macFirst = Seed(lowIdIsMac, "device", new[] { "mac:44d244aabbcc" }, Instance);
            Seed(lowIdIsMac, "device", new[] { "serial:SN-1" }, Instance);

            var entity = Device("44:D2:44:AA:BB:CC");
            entity.Claims.Add(new IdentityClaimDto { Type = "serial", Value = "SN-1" });

            await ApplyAsync(lowIdIsSerial, Document(CloneOf(entity)));
            await ApplyAsync(lowIdIsMac, Document(CloneOf(entity)));

            Assert.IsFalse(lowIdIsSerial.TryReadElement(serialFirst, out _),
                "with the serial element holding the LOWER id, the mac element is still the pick");
            Assert.IsTrue(lowIdIsMac.TryReadElement(macFirst, out _),
                "and with the mac element holding the lower id, the same one wins. An id-based rule could land " +
                "the same entity on a different element after a trim renumbers ids in place");
        }

        #endregion

        #region the zero-mutation invariant

        [TestMethod]
        public async Task ASecondRunOverAnUnchangedSource_IssuesNoMutationCallAtAll()
        {
            var graph = new InMemoryGraphTarget();

            var first = await ApplyAsync(graph, Document(Device("44:D2:44:AA:BB:CC", ("csv.name", "printer"))));
            Assert.IsTrue(first.IssuedMutations, "the first run creates, so of course it mutates");

            var callsAfterFirst = graph.MutationCalls.Count;
            var second = await ApplyAsync(graph, Document(Device("44:D2:44:AA:BB:CC", ("csv.name", "printer"))));

            Assert.AreEqual(callsAfterFirst, graph.MutationCalls.Count, String.Format(
                "the second run issued {0}. Every write must be conditional on a difference: the platform " +
                "already treats an equal-value write as a no-op, so an unconditional writer leaves the graph " +
                "correct while churning the change feed and growing a write-ahead log nothing here bounds",
                String.Join(", ", graph.MutationCalls.Skip(callsAfterFirst))));
            Assert.IsFalse(second.IssuedMutations, "and the report says so, which is what makes it observable");
        }

        [TestMethod]
        public async Task OnlyThePropertiesThatDiffer_AreWritten()
        {
            var graph = new InMemoryGraphTarget();
            await ApplyAsync(graph, Document(Device("44:D2:44:AA:BB:CC", ("csv.name", "printer"), ("csv.note", "hall"))));
            var callsAfterFirst = graph.MutationCalls.Count;

            await ApplyAsync(graph, Document(Device("44:D2:44:AA:BB:CC", ("csv.name", "plotter"), ("csv.note", "hall"))));

            var issued = graph.MutationCalls.Skip(callsAfterFirst).ToList();
            Assert.AreEqual(1, issued.Count, "one property changed, so one property batch was issued: " +
                                             String.Join(", ", issued));
            Assert.AreEqual("setProperties(1)", issued[0],
                "the unchanged property must not ride along, or the batch size stops being evidence of change");
        }

        #endregion

        #region reconciliation

        [TestMethod]
        public async Task ACompleteSnapshotThatNoLongerMentionsAnElement_WithdrawsAndThenDeletesIt()
        {
            var graph = new InMemoryGraphTarget();
            var first = await ApplyAsync(graph, Document(Device("44:D2:44:AA:BB:CC")));
            Assert.AreEqual(1, first.ElementsCreated);

            var second = await ApplyAsync(graph, Document());

            Assert.AreEqual(1, second.ClaimsWithdrawn,
                "a complete snapshot describes the whole source, so an absence is a removal");
            Assert.AreEqual(1, second.ElementsDeleted,
                "and with no claim left at all, the element goes");
            Assert.AreEqual(0, graph.AllElements().Count());
        }

        [TestMethod]
        public async Task APartialSnapshot_WithdrawsNothing()
        {
            var graph = new InMemoryGraphTarget();
            await ApplyAsync(graph, Document(Device("44:D2:44:AA:BB:CC")));

            var partial = Document();
            partial.Declares = SnapshotCompleteness.Partial;
            var report = await ApplyAsync(graph, partial);

            Assert.AreEqual(0, report.ClaimsWithdrawn,
                "absence in a partial snapshot means nothing: an event-driven source delivers changes, so its " +
                "first delivery would otherwise withdraw the entire graph it had built");
            Assert.AreEqual(1, graph.AllElements().Count());
        }

        [TestMethod]
        public async Task WithdrawalIsEffectiveOnly_SoAThirdRunOverNothingIsStillAZeroMutationRun()
        {
            var graph = new InMemoryGraphTarget();
            await ApplyAsync(graph, Document(Device("44:D2:44:AA:BB:CC")));
            await ApplyAsync(graph, Document());

            var callsBefore = graph.MutationCalls.Count;
            var third = await ApplyAsync(graph, Document());

            Assert.AreEqual(0, third.ClaimsWithdrawn,
                "the claim index has no remove path, so it answers 'ever claimed' forever. Re-issuing the " +
                "removal would report a withdrawal and a mutation on every future run over a completely " +
                "unchanged source");
            Assert.AreEqual(0, third.ElementsDeleted, "and an id the graph no longer has is not deleted twice");
            Assert.AreEqual(callsBefore, graph.MutationCalls.Count, "so nothing was written at all");
            Assert.IsFalse(third.IssuedMutations);
        }

        [TestMethod]
        public async Task AnElementAnotherInstanceStillClaims_IsWithdrawnFromButNotDeleted()
        {
            var graph = new InMemoryGraphTarget();
            await graph.EnsureIndicesAsync(CancellationToken.None);

            // Both instances assert one element, which is what somebody unifying two records by hand leaves.
            var shared = Seed(graph, "device", new[] { "mac:44d244aabbcc" }, Instance);
            await graph.ApplyPropertyWritesAsync(new[]
            {
                PropertyWrite.Set(shared, new GraphProperty(ClaimSchema.ClaimProperty(Foreign),
                    "System.String", Foreign)),
            }, CancellationToken.None);

            var report = await ApplyAsync(graph, Document());

            Assert.AreEqual(1, report.ClaimsWithdrawn);
            Assert.AreEqual(0, report.ElementsDeleted,
                "deletion happens on the LAST claim, judged from what the elements say NOW rather than from " +
                "what the runtime believed before it wrote");
            Assert.IsTrue(graph.TryReadElement(shared, out var state) && state.IsClaimedBy(Foreign),
                "and the other instance's claim is untouched");
        }

        [TestMethod]
        public async Task DeletionIsDeferredWhenWritesAreNotReachingDisk()
        {
            await AssertDeletionDeferred(new TargetDurability(false, false, 0), "writes are not reaching disk");
        }

        [TestMethod]
        public async Task DeletionIsDeferredWhenTheLastRecoveryWasTruncated()
        {
            await AssertDeletionDeferred(new TargetDurability(true, true, 0), "prefix of committed history");
        }

        [TestMethod]
        public async Task DeletionIsDeferredWhenTheLastCheckpointDroppedAnIndex()
        {
            await AssertDeletionDeferred(new TargetDurability(true, false, 1), "index");
        }

        [TestMethod]
        public async Task ADeferredDeletionLeavesAnOrphanTheNextHealthyRunCleansUp()
        {
            var graph = new InMemoryGraphTarget();
            await ApplyAsync(graph, Document(Device("44:D2:44:AA:BB:CC")));

            graph.SetDurability(new TargetDurability(false, false, 0));
            var deferred = await ApplyAsync(graph, Document());
            Assert.AreEqual(1, deferred.DeletionsDeferred);
            Assert.AreEqual(1, graph.AllElements().Count(), "the element is still there, carrying no claim");

            graph.SetDurability(TargetDurability.Healthy);
            var healthy = await ApplyAsync(graph, Document());

            Assert.AreEqual(0, healthy.ClaimsWithdrawn,
                "there is no claim left to withdraw, so nothing is re-issued");
            Assert.AreEqual(1, healthy.ElementsDeleted,
                "an element that already carried no claim is an orphan left by a deferred deletion, cleaned up " +
                "here once durability is healthy - which is why the deletion decision is judged over EVERY " +
                "withdrawal rather than only the effective ones");
        }

        #endregion

        #region indices

        [TestMethod]
        public async Task AMissingIdentityIndex_IsRepairedFromElementStateAndTheLookupIsRetriedOnce()
        {
            var graph = new InMemoryGraphTarget();
            await ApplyAsync(graph, Document(Device("44:D2:44:AA:BB:CC")));

            // Three ordinary operations drop an index while this runtime is running.
            graph.DropIndex(ClaimSchema.IdentityIndexId);

            var report = await ApplyAsync(graph, Document(Device("44:D2:44:AA:BB:CC")));

            Assert.IsTrue(report.Diagnostics.Any(d => d.Code == DiagnosticCodes.IdentityIndexRebuilt),
                "the repair is reported, because a lookup against a fresh index answers empty and empty is " +
                "indistinguishable from 'no element carries this claim'");
            Assert.AreEqual(0, report.ElementsCreated, String.Format(
                "after the repair the element must be found again. Answering empty instead would duplicate " +
                "every element AND leave the originals claimed by an identity no later run knows about, so no " +
                "withdrawal ever removes them. Report: {0}", Describe(report)));
            Assert.AreEqual(1, report.ElementsMatched);
        }

        [TestMethod]
        public async Task AClaimIndexThatVanishesMidRun_SkipsReconciliationRatherThanWithdrawingEverything()
        {
            var graph = new InMemoryGraphTarget();
            await ApplyAsync(graph, Document(Device("44:D2:44:AA:BB:CC")));

            // Dropping it BEFORE the run would be repaired by the ensure-then-repair step, which is the ordinary
            // path and is covered above. The case this fallback exists for is the index vanishing between that
            // repair and reconciliation - a concurrent tabula rasa or save-game load - so the fixture drops it at
            // exactly that moment.
            var vanishing = new VanishingClaimIndexTarget(graph);

            // A complete snapshot that no longer mentions the element WOULD withdraw and delete it. With the
            // claim index gone, the answer would be "this instance claims nothing", so nothing may happen.
            var report = await ApplyAsync(vanishing, Document());

            Assert.IsTrue(report.Diagnostics.Any(d => d.Code == DiagnosticCodes.ReconciliationDeferred),
                "the skip is reported so the next run's reconciliation is expected");
            Assert.AreEqual(0, report.ClaimsWithdrawn);
            Assert.AreEqual(0, report.ElementsDeleted, "an empty answer from a missing index must never be read " +
                                                       "as 'this instance claims nothing'");
            Assert.AreEqual(1, graph.AllElements().Count());
        }

        [TestMethod]
        public async Task AnIndexWriteTheTargetDeclines_IsReported_BecauseAnUnfindableClaimDuplicatesTheElement()
        {
            var graph = new InMemoryGraphTarget();
            var declining = new DecliningIndexTarget(graph);

            var report = await ApplyAsync(declining, Document(Device("44:D2:44:AA:BB:CC")));

            Assert.IsTrue(report.Diagnostics.Any(d => d.Code == DiagnosticCodes.ClaimNotIndexed),
                "an element findable by none of its claims is duplicated on the next resolve, so a declined " +
                "index write is never merely informational");
        }

        #endregion

        #region edges

        [TestMethod]
        public async Task AnEdgeThisInstanceAlreadyWired_IsNotCreatedAgain()
        {
            var graph = new InMemoryGraphTarget();
            var first = await ApplyAsync(graph, TwoDevicesWithAnUplink());
            Assert.AreEqual(1, first.EdgesCreated);

            var second = await ApplyAsync(graph, TwoDevicesWithAnUplink());

            Assert.AreEqual(0, second.EdgesCreated,
                "the derived key is what makes an edge findable at all, so finding it must stop the create");
            Assert.IsFalse(second.IssuedMutations,
                "and an unchanged topology is a zero-mutation run");
        }

        [TestMethod]
        public async Task AnEdgeAnotherInstanceWired_FallsThroughAndThisInstanceCreatesItsOwn()
        {
            var graph = new InMemoryGraphTarget();
            await graph.EnsureIndicesAsync(CancellationToken.None);

            // The foreign run's own two elements and its own edge between them, carrying the SAME derived key
            // this run will compose, because the key encodes the endpoints and the type rather than the creator.
            var left = Seed(graph, "device", new[] { "mac:44d244aabbcc" }, Foreign);
            var right = Seed(graph, "device", new[] { "mac:44d244aabbdd" }, Foreign);
            var derived = ClaimKeyComposer.ForEdge("mac:44d244aabbcc", "uplink", "mac:44d244aabbdd");
            var edges = await graph.CreateEdgesAsync(new[]
            {
                new EdgeWrite(left, right, "uplink", new[]
                {
                    new GraphProperty(ClaimSchema.IdentityProperty(0), "System.String", derived),
                    new GraphProperty(ClaimSchema.ClaimProperty(Foreign), "System.String", Foreign),
                }),
            }, CancellationToken.None);
            await graph.IndexClaimsAsync(new[]
            {
                new IndexEntry(ClaimSchema.IdentityIndexId, derived, edges[0]),
            }, CancellationToken.None);

            var report = await ApplyAsync(graph, TwoDevicesWithAnUplink());

            Assert.AreEqual(1, report.EdgesCreated, String.Format(
                "admitting a foreign edge into the claimed-now set makes this instance's reconciliation " +
                "responsible for another instance's edge, and skipping the create instead leaves this instance " +
                "with no edge to claim at all. Report: {0}", Describe(report)));
            Assert.IsTrue(graph.TryReadElement(edges[0], out var foreignEdge) &&
                          foreignEdge.IsClaimedBy(Foreign),
                "and the other instance's edge is untouched");
        }

        [TestMethod]
        public async Task ARelationWhoseTargetTheSnapshotDoesNotDescribe_IsReportedAndNoEdgeIsCreated()
        {
            var graph = new InMemoryGraphTarget();

            var device = Device("44:D2:44:AA:BB:CC");
            device.Relations.Add(new RelationDto
            {
                Type = "uplink",
                Target = new ClaimReferenceDto { Type = "mac", Value = "44:D2:44:AA:BB:DD" },
            });

            var report = await ApplyAsync(graph, Document(device));

            Assert.AreEqual(0, report.EdgesCreated);
            Assert.IsTrue(report.Diagnostics.Any(d => d.Code == DiagnosticCodes.DroppedRelation),
                "an edge wired across instances would be found by its own derived key forever and could never " +
                "re-wire, so a target this run does not assert is reported rather than guessed at");
        }

        #endregion

        #region the AI surface, whose whole degradation matrix this runtime can reach

        [TestMethod]
        public async Task WithBothHalvesOfTheOptIn_TheSummaryIsEmbeddedFromTheTemplateAlone()
        {
            var graph = new InMemoryGraphTarget();

            var report = await ApplyAsync(graph, Document(Device("44:D2:44:AA:BB:CC", ("csv.name", "printer"))),
                summary: new SummaryRequest("{kind} {csv.name}, {csv.note}", "default"));

            Assert.AreEqual(1, report.SummariesEmbedded);
            Assert.AreEqual("device printer", graph.EmbeddedSummaries.Values.Single(),
                "the template renders from data with no provider-authored code on the path, and a hole the " +
                "entity cannot fill collapses along with its dangling punctuation");
        }

        [TestMethod]
        public async Task WithoutTheJobHalfOfTheOptIn_NothingIsEmbedded()
        {
            var graph = new InMemoryGraphTarget();

            var report = await ApplyAsync(graph, Document(Device("44:D2:44:AA:BB:CC", ("csv.name", "printer"))));

            Assert.AreEqual(0, report.SummariesEmbedded,
                "embedding is opt-in per provider AND per instance, default off: embedding every client on a " +
                "busy network by default is cost and noise in equal measure");
            Assert.AreEqual(0, graph.EmbeddedSummaries.Count);
        }

        [TestMethod]
        public async Task WithTheEmbeddingCapabilityAbsent_TheRunSTILLSUCCEEDS_AndTheSummariesAreSimplyAbsent()
        {
            var graph = new InMemoryGraphTarget();
            graph.SetEmbeddingState(TargetEmbedding.Absent("the target's embedding capability is switched off"));

            var report = await ApplyAsync(graph, Document(Device("44:D2:44:AA:BB:CC", ("csv.name", "printer"))),
                summary: new SummaryRequest("{kind} {csv.name}", "default"));

            Assert.AreEqual(1, report.ElementsCreated,
                "the graph write is the point of the run: every AI-dependent behaviour degrades to ABSENT " +
                "rather than to broken");
            Assert.AreEqual(0, report.SummariesEmbedded);
            Assert.IsTrue(report.Diagnostics.Any(d => d.Code == DiagnosticCodes.SummaryEmbeddingUnavailable),
                "and the absence is reported rather than silent");
        }

        [TestMethod]
        public async Task AnUnchangedEntity_IsNotReEmbedded_SoTheInvariantSurvivesTheAiSurface()
        {
            var graph = new InMemoryGraphTarget();
            var summary = new SummaryRequest("{kind} {csv.name}", "default");
            await ApplyAsync(graph, Document(Device("44:D2:44:AA:BB:CC", ("csv.name", "printer"))), summary);

            var callsBefore = graph.MutationCalls.Count;
            var second = await ApplyAsync(graph,
                Document(Device("44:D2:44:AA:BB:CC", ("csv.name", "printer"))), summary);

            Assert.AreEqual(0, second.SummariesEmbedded, "a summary is a pure function of the entity's kind and " +
                                                         "properties, so an entity that produced no property " +
                                                         "write has no new summary to embed");
            Assert.AreEqual(callsBefore, graph.MutationCalls.Count,
                "re-embedding an unchanged entity would issue a write on every run and make the zero-mutation " +
                "invariant false by construction");
        }

        [TestMethod]
        public async Task AClientTimeoutOnTheEmbeddingWrite_DegradesToAbsent_AndIsNeverACancellation()
        {
            // HttpClient reports its OWN timeout as a TaskCanceledException, which IS an
            // OperationCanceledException. The embedding write caught HttpRequestException alone, so a sidecar or
            // proxy that timed out escaped this seam as a cancellation - indistinguishable one frame up from the
            // caller walking away, and the two license opposite statements about what the run wrote.
            //
            // It now DEGRADES instead of failing, and only here: an embedding is an addition to what landed, so
            // pre-empting a model must not fail a run whose graph writes are already in. Every other call this
            // target makes still fails on a timeout.
            using var client = new HttpClient(new TimingOutHandler())
            {
                BaseAddress = new Uri("http://localhost/"),
            };
            using var target = new Fallen8RestTarget(client, "default");

            var outcome = await target.EmbedSummariesAsync("default",
                new[] { new SummaryWrite(0, "device printer") }, CancellationToken.None);

            Assert.AreEqual(0, outcome.Written, "nothing was embedded, and nothing pretends to have been");
            Assert.IsNotNull(outcome.Degraded,
                "a target that did not answer in time is an absent embedding rather than a failed run, and " +
                "silence would make it look like a write that succeeded");
            StringAssert.Contains(outcome.Degraded, "TimeoutSeconds",
                "and the reason names the knob an operator would change, because the only other lever is the " +
                "target's own embedding budget: " + outcome.Degraded);
        }

        [TestMethod]
        public async Task AClientTimeoutOnAGraphWrite_IsStillAFailure_NotADegrade()
        {
            // The other half of the degrade decision, and the half that would rot silently: a vertex create
            // that timed out may or may not have been applied, and carrying on as if it had not is how a run
            // reports elements it never claimed. Only the embedding write may treat a deadline as an absence.
            using var client = new HttpClient(new TimingOutHandler())
            {
                BaseAddress = new Uri("http://localhost/"),
            };
            using var target = new Fallen8RestTarget(client, "default");

            var failure = await Assert.ThrowsExceptionAsync<GraphTargetTimeoutException>(
                () => target.CreateVerticesAsync(
                    new[] { new VertexWrite("device", Array.Empty<GraphProperty>()) }, CancellationToken.None),
                "a write whose fate is unknown must fail the run, and it says which deadline expired rather " +
                "than leaving the caller to read a message");

            StringAssert.Contains(failure.Message, "vertices",
                "and it names the call that did not come back: " + failure.Message);
        }

        [TestMethod]
        public async Task AClientTimeoutMidChunk_DegradesWithTheCountThatLanded_AndStopsAsking()
        {
            var handler = new EmbedBatchRecordingHandler(failOnCall: 3,
                throwInstead: new TaskCanceledException(
                    "The request was canceled due to the configured HttpClient.Timeout", new TimeoutException()));
            using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            using var target = new Fallen8RestTarget(client, "default");

            var outcome = await target.EmbedSummariesAsync("default", Summaries(200), CancellationToken.None);

            Assert.AreEqual(32, outcome.Written,
                "two chunks landed before the deadline fired, and this is the exact incident the chunking was " +
                "built for: many chunks of a real extract landed and the report said zero");
            Assert.IsNotNull(outcome.Degraded, "and the shortfall is named rather than silent");
            Assert.AreEqual(3, handler.BatchSizes.Count,
                "a deadline says the model is slower than this runtime waits, which the next chunk cannot " +
                "improve on, so the loop stops for the reason 503 stops it");
        }

        [TestMethod]
        public async Task ATransportFailureMidChunk_StillCarriesTheCountThatLanded()
        {
            // The failure the chunk count was introduced against, and the one path it did not reach: a
            // connection dying mid extract threw from the send rather than from a status code, so the count
            // stayed at its default zero and the report claimed nothing had been embedded.
            var handler = new EmbedBatchRecordingHandler(failOnCall: 3,
                throwInstead: new HttpRequestException("the connection was reset"));
            using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            using var target = new Fallen8RestTarget(client, "default");

            var failure = await Assert.ThrowsExceptionAsync<GraphTargetException>(
                () => target.EmbedSummariesAsync("default", Summaries(200), CancellationToken.None),
                "a graph that stopped answering is still a failed run: this is not the provider declining, it " +
                "is the target being unreachable");

            Assert.AreEqual(32, failure.SummariesWritten,
                "the two chunks that landed put real vectors on real elements, and a report of zero sends the " +
                "operator to a tabula rasa they do not need");
        }

        [TestMethod]
        public async Task ACancellationTheCallerAskedFor_StaysACancellation_EvenOnTheEmbeddingWrite()
        {
            // The other side of the same coin: the token decides, never the exception type. A caller that
            // walked away must not be answered with a degraded outcome, because a degradation is a statement
            // that the run carried on and the embedding did not.
            using var walkedAway = new CancellationTokenSource();
            using var client = new HttpClient(new CallerCancellingHandler(walkedAway))
            {
                BaseAddress = new Uri("http://localhost/"),
            };
            using var target = new Fallen8RestTarget(client, "default");

            try
            {
                await target.EmbedSummariesAsync("default", Summaries(40), walkedAway.Token);
                Assert.Fail("the write answered a caller who had already asked for the run to stop");
            }
            catch (GraphTargetException graph)
            {
                Assert.Fail("a cancellation the caller requested became this seam's own failure: " + graph.Message);
            }
            catch (OperationCanceledException)
            {
                // The one right answer: the caller's cancellation belongs to the caller.
            }
        }

        [TestMethod]
        public async Task ALargeSummaryWrite_IsChunked_BecauseTheRouteCapsITEMSNotBytes()
        {
            // The defect this pins was silent at fixture size and fatal at real size: every summary went out in
            // ONE post, the route refuses a batch over Fallen8:Embedding:MaxBatchSize (default 64, and 32 under
            // the Nahil compose), and 400 is correctly NOT in the degrade set - so a many-entity system extract
            // failed the run with errorKind "graph" AFTER its graph writes had landed and BEFORE reconciliation.
            var handler = new EmbedBatchRecordingHandler();
            using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            using var target = new Fallen8RestTarget(client, "default");

            var outcome = await target.EmbedSummariesAsync("default", Summaries(200), CancellationToken.None);

            Assert.AreEqual(200, outcome.Written, "every summary is embedded, across however many requests it takes");
            Assert.IsNull(outcome.Degraded, "and nothing degraded, because the target answered every chunk");
            Assert.AreEqual(200, handler.BatchSizes.Sum(), "no summary is dropped, and none is sent twice");
            // TWO ceilings, and the chunk has to clear both. 32 is the smallest ITEM cap the product ships
            // (the Nahil compose), and exceeding it answers 400 - outside the degrade set, so it fails a run
            // whose graph writes already landed. 16 is the TIME budget: a chunk is model inference, and at the
            // ~3.5 s per element a CPU-backed bge-m3 actually costs, 32 elements is ~113 s against this
            // target's 120 s client timeout. A real many-entity extract died on its 86th chunk of 32 for
            // exactly that reason, so this pins the tighter bound rather than the cap.
            Assert.IsTrue(handler.BatchSizes.All(size => size <= 16),
                "a chunk must stay inside BOTH the smallest shipped item cap and the client timeout: " +
                String.Join(", ", handler.BatchSizes));
        }

        [TestMethod]
        public async Task AProviderThatStopsAnsweringMidChunk_StillReportsWhatAlreadyLanded()
        {
            var handler = new EmbedBatchRecordingHandler(failOnCall: 3, status: HttpStatusCode.ServiceUnavailable);
            using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            using var target = new Fallen8RestTarget(client, "default");

            var outcome = await target.EmbedSummariesAsync("default", Summaries(200), CancellationToken.None);

            Assert.AreEqual(32, outcome.Written,
                "two chunks landed before the provider stopped answering, and those vectors are ON their " +
                "elements - reporting zero for them would be a false report, and a bound index answers " +
                "searches over them either way");
            Assert.IsNotNull(outcome.Degraded, "and the absence of the rest is reported rather than silent");
            Assert.AreEqual(3, handler.BatchSizes.Count,
                "503 describes the PROVIDER rather than this batch, so the remaining chunks are not tried: a " +
                "large extract would otherwise spend hundreds of round-trips proving the same thing");
        }

        [TestMethod]
        public async Task ARefusalThatIsNotTheProviderBeingAbsent_IsStillAGraphFailure_EvenMidChunk()
        {
            var handler = new EmbedBatchRecordingHandler(failOnCall: 2, status: HttpStatusCode.BadRequest);
            using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            using var target = new Fallen8RestTarget(client, "default");

            var failure = await Assert.ThrowsExceptionAsync<GraphTargetException>(
                () => target.EmbedSummariesAsync("default", Summaries(200), CancellationToken.None),
                "chunking must not turn a real refusal into a degradation: 400 says the runtime sent something " +
                "the route will never accept, which is this deployable's defect and has to surface as one");

            StringAssert.Contains(failure.Message, "400", "and the status is named: " + failure.Message);
        }

        [TestMethod]
        public async Task AThrottledTargetDegrades_BecauseChunkingIsWhatMadeAThrottleReachable()
        {
            // 429 was unreachable while the write was ONE request. Chunked, a large extract is hundreds of
            // requests against the embedding route's sensitive-endpoint rate limit, so failing the run for
            // the target's own pacing would make chunking a regression for exactly the extracts it exists
            // to support.
            var handler = new EmbedBatchRecordingHandler(failOnCall: 2, status: HttpStatusCode.TooManyRequests);
            using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            using var target = new Fallen8RestTarget(client, "default");

            var outcome = await target.EmbedSummariesAsync("default", Summaries(200), CancellationToken.None);

            Assert.AreEqual(16, outcome.Written, "the first chunk landed and is reported");
            Assert.IsNotNull(outcome.Degraded, "and the throttle is named rather than swallowed");
            Assert.AreEqual(2, handler.BatchSizes.Count,
                "a throttle describes the WINDOW, not this batch, so hammering the remaining chunks would " +
                "only deepen it");
        }

        [TestMethod]
        public async Task AGraphFailureMidChunk_StillCarriesTheCountThatLanded()
        {
            var handler = new EmbedBatchRecordingHandler(failOnCall: 3, status: HttpStatusCode.BadRequest);
            using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            using var target = new Fallen8RestTarget(client, "default");

            var failure = await Assert.ThrowsExceptionAsync<GraphTargetException>(
                () => target.EmbedSummariesAsync("default", Summaries(200), CancellationToken.None));

            // The run fails, and that is right. But two chunks put real vectors on real elements, and a
            // report of zero would send the operator to a tabula rasa they do not need.
            Assert.AreEqual(32, failure.SummariesWritten,
                "the count rides on the failure, because it is a fact about the graph rather than about the " +
                "refusal");
        }

        [TestMethod]
        public async Task APartiallyEmbeddedRun_ReportsTheCountThatLanded_AndNamesOnlyTheShortfall()
        {
            var graph = new PartiallyEmbeddingTarget(new InMemoryGraphTarget(), embedsAtMost: 1);

            var report = await ApplyAsync(graph,
                Document(Device("44:D2:44:AA:BB:CC", ("csv.name", "printer")),
                    Device("44:D2:44:AA:BB:CD", ("csv.name", "switch"))),
                summary: new SummaryRequest("{kind} {csv.name}", "default"));

            Assert.AreEqual(2, report.ElementsCreated, "the graph write is the point of the run");
            Assert.AreEqual(1, report.SummariesEmbedded,
                "the report counts what LANDED: a partial embed that reports zero makes the operator re-import " +
                "a namespace whose vectors are half present");
            var degraded = report.Diagnostics.Single(d => d.Code == DiagnosticCodes.SummaryEmbeddingUnavailable);
            StringAssert.Contains(degraded.Message, "1 of 2",
                "and the diagnostic names the SHORTFALL rather than the whole batch, which is a different " +
                "false statement: " + degraded.Message);
        }

        #endregion

        #region what one RUN owes: the apply phase, the outcome line, one identity

        [TestMethod]
        public async Task TheApplyPhaseFinishesEvenAfterTheCallerHasWalkedAway()
        {
            // The trigger is not theoretical: the job endpoint binds the request-abort token and the apiApp proxy
            // has a finite timeout, so a source that legitimately takes longer than the proxy waits used to have
            // its GRAPH WRITES killed between calls. The source read is cancellable and losing it costs nothing;
            // interrupting a bounded handful of batched writes leaves a half-applied snapshot instead.
            var graph = new InMemoryGraphTarget();
            using var walkedAway = new CancellationTokenSource();

            // A target that honours the token, because the live one does: every write is an HTTP call, and
            // HttpClient throws the moment the token it was handed is cancelled.
            using var harness = Harness(new CancellationHonouringTarget(graph));
            harness.Provider.WhileObserving = (context, token) => walkedAway.Cancel();

            JobReport report = null;
            try
            {
                report = await harness.Runner.RunAsync(RunnerJob(Instance), walkedAway.Token);
            }
            catch (OperationCanceledException)
            {
                // Asserted below rather than here, so the failure says what was lost instead of naming an exception.
            }

            Assert.IsNotNull(report,
                "the apply phase passed the caller's token to the graph, so the writes died between calls and the " +
                "snapshot landed by halves - repairable, but invisible to everyone including the next run");
            Assert.IsFalse(report.Failed, "the run finished what it started: " + report.ErrorKind + " " + report.Error);
            Assert.AreEqual(1, report.ElementsCreated, Describe(report));
            Assert.AreEqual(1, graph.AllElements().Count(), "and the element really is in the graph");
        }

        [TestMethod]
        public async Task ACancelledRunIsLoggedAsAbandoned_NeverAsAFinishedRunThatCreatedNothing()
        {
            var graph = new InMemoryGraphTarget();
            using var walkedAway = new CancellationTokenSource();
            using var harness = Harness(graph);
            harness.Provider.WhileObserving = (context, token) =>
            {
                walkedAway.Cancel();
                token.ThrowIfCancellationRequested();
            };

            var propagated = false;
            try
            {
                await harness.Runner.RunAsync(RunnerJob(Instance), walkedAway.Token);
            }
            catch (OperationCanceledException)
            {
                propagated = true;
            }

            Assert.IsTrue(propagated, "a cancelled run has no report to hand back, so the cancellation propagates");
            Assert.IsFalse(harness.Log.Lines.Any(line => line.Contains("finished in", StringComparison.Ordinal)),
                "the one line a cancelled run leaves behind must not be the success-shaped one: the report it " +
                "would read was never completed, so its duration and every count on it are zero. Lines: " +
                String.Join(" | ", harness.Log.Lines));
            Assert.IsTrue(harness.Log.Lines.Any(line => line.Contains("ABANDONED", StringComparison.Ordinal)),
                "and the abandonment is logged, because a run that was asked for and then dropped is worth " +
                "exactly one honest line. Lines: " + String.Join(" | ", harness.Log.Lines));
        }

        [TestMethod]
        public async Task ARunAbandonedINSIDETheApplyPhase_IsNeverLoggedAsHavingWrittenNothing()
        {
            // The apply phase is handed CancellationToken.None, so a cancellation surfacing from inside it is not
            // the caller's: it is a write failing - a client-side timeout is the shipped shape - while the caller's
            // token happens to be cancelled too, because the proxy in front of this runtime gave up. The runner's
            // filter can only see the caller's token, so the durability claim in its ABANDONED line has to come
            // from WHICH SIDE of the apply call it stands on.
            var graph = new InMemoryGraphTarget();
            using var walkedAway = new CancellationTokenSource();
            using var harness = Harness(new TimingOutEmbeddingTarget(graph, walkedAway));

            var job = RunnerJob(Instance);
            job.EmbedSummaries = true;

            try
            {
                await harness.Runner.RunAsync(job, walkedAway.Token);
                Assert.Fail("the fixture fails the embedding write, so this run cannot come back with a report");
            }
            catch (OperationCanceledException)
            {
                // The outcome under test is the LINE, asserted below.
            }

            Assert.AreEqual(1, graph.AllElements().Count(),
                "the fixture must fail AFTER the graph write, or there is nothing for a log line to be wrong about");

            var abandoned = harness.Log.Lines.FirstOrDefault(
                line => line.Contains("ABANDONED", StringComparison.Ordinal));
            Assert.IsNotNull(abandoned,
                "the abandonment is still logged: " + String.Join(" | ", harness.Log.Lines));
            Assert.IsFalse(abandoned.Contains("nothing was written", StringComparison.Ordinal),
                "an element, its claims and its index entries are in the graph. A confident false statement about " +
                "durability-relevant work is worse than a vague one, and this is the failure class where the log " +
                "line is the only account there is. Line: " + abandoned);
            StringAssert.Contains(abandoned, "1 created",
                "and the line says what had landed, because the reader's next question is what to repair: " +
                abandoned);
        }

        [TestMethod]
        public async Task ACredentialTheRuntimeCannotUse_FailsAsCredential_AndStillLeavesALogLine()
        {
            var graph = new InMemoryGraphTarget();
            using var harness = Harness(graph);

            // A form submitted before the paste: the value arrives, and it is whitespace.
            var job = RunnerJob(Instance);
            job.CredentialValues["apiKey"] = "   ";

            var report = await harness.Runner.RunAsync(job, CancellationToken.None);

            Assert.AreEqual(JobErrorKinds.Credential, report.ErrorKind,
                "a value the runtime could not use and a source that refused one send a reader to the same place");
            Assert.AreEqual(0, graph.MutationCalls.Count, "and a failed run mutates nothing at all");
            Assert.IsTrue(
                harness.Log.Lines.Any(line => line.Contains("FAILED (credential)", StringComparison.Ordinal)),
                "this failure returns BEFORE the lease exists, so it is the one outcome that never passes the " +
                "using-block's log, and 'the run that produced no log line at all' is the worst shape for the one " +
                "failure class an operator fixes by looking at the credential. Lines: " +
                String.Join(" | ", harness.Log.Lines));
        }

        [TestMethod]
        public async Task RetypingTheInstanceIdInAnotherCase_IsTheSameIdentity_AndDuplicatesNothing()
        {
            // Every claim key is composed with the instance id and compared ordinally, so unfolded, "Garage" and
            // "GARAGE" are two identities: the second claims nothing, so it duplicates every element, and the
            // first is never reconciled again, so everything it claimed is orphaned. The run gate is
            // case-insensitive, so the two would not even collide there.
            var graph = new InMemoryGraphTarget();
            using var harness = Harness(graph);

            var first = await harness.Runner.RunAsync(RunnerJob("Garage"), CancellationToken.None);
            Assert.AreEqual(1, first.ElementsCreated, Describe(first));

            var second = await harness.Runner.RunAsync(RunnerJob("GARAGE"), CancellationToken.None);

            Assert.AreEqual(0, second.ElementsCreated,
                "retyping the name in another case is what a reader does, and it must be the same identity: " +
                Describe(second));
            Assert.AreEqual(1, second.ElementsMatched, Describe(second));
            Assert.AreEqual(1, graph.AllElements().Count(), "so the graph holds one device, not two");
            Assert.IsTrue(graph.TryReadElement(0, out var state) && state.IsClaimedBy(Instance),
                "and the claim is keyed on the folded id, so the gate, the keys and reconciliation all agree");
        }

        [TestMethod]
        public async Task AFilesFactoryThatThrows_LeavesNoCredentialHeld()
        {
            var graph = new InMemoryGraphTarget();
            using var harness = Harness(graph, new ThrowingFilesFactory());

            var job = RunnerJob(Instance);
            job.CredentialValues["apiKey"] = "s3cret-token";

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => harness.Runner.RunAsync(job, CancellationToken.None),
                "the factory's failure is nobody's expected case, so it surfaces rather than being folded into " +
                "a report");

            Assert.IsTrue(harness.ActiveCredentials.IsEmpty,
                "with no run in flight the held set is empty, and that is what makes redaction's own bookkeeping " +
                "trustworthy: a leaked hold substitutes a value nothing is using into every line this process " +
                "logs, for as long as it runs. Held: " +
                String.Join(", ", harness.ActiveCredentials.Snapshot().Select(value => value.Length + " chars")));
        }

        #endregion

        #region the phases a watcher sees

        [TestMethod]
        public async Task TheWriteEdgesPhase_IsEnteredWhileTheEdgesAreStillUnwritten()
        {
            // A phase entered AFTER its own work is a phase nobody can observe: a caller polling the run saw
            // resolve, then write-elements, then a finished run - never write-edges, however long the edges
            // took. The write-elements block carried that lesson in its own comment from the start and the
            // edge block never got it, which is why the ordering is pinned here rather than trusted.
            var journal = new List<String>();
            var graph = new InMemoryGraphTarget();
            using var target = new JournallingTarget(graph, journal);

            var report = await ApplyAsync(target, TwoDevicesWithAnUplink(),
                progress: new JournallingProgress(journal));

            Assert.AreEqual(1, report.EdgesCreated,
                "the fixture must wire an edge, or there is no work for a phase to be late for: " +
                Describe(report));

            var elementsPhase = journal.IndexOf("phase:write-elements");
            var vertexWrite = journal.IndexOf("createVertices");
            var edgesPhase = journal.IndexOf("phase:write-edges");
            var edgeWrite = journal.IndexOf("createEdges");

            Assert.IsTrue(elementsPhase >= 0 && vertexWrite > elementsPhase,
                "the elements phase is entered before the elements are written: " + String.Join(" | ", journal));
            Assert.IsTrue(edgesPhase >= 0,
                "the run never named the write-edges phase at all: " + String.Join(" | ", journal));
            Assert.IsTrue(edgeWrite > edgesPhase,
                "the edges were written before the phase that describes them was entered, so a watcher can " +
                "never see write-edges in flight: " + String.Join(" | ", journal));
            Assert.AreEqual("advance:0/1", journal[edgesPhase + 1],
                "and the counter opens at NONE done of the planned edges: entered afterwards it read 'all of " +
                "them' before any of it had been issued: " + String.Join(" | ", journal));
        }

        #endregion

        #region the deadline every call to the graph carries (Fallen8Target:TimeoutSeconds)

        [TestMethod]
        public void TheGraphClientsDeadline_SitsAboveTheTargetsOwnEmbeddingBudget()
        {
            // The hardcoded 120s this replaced was the nearer of two deadlines: the apiApp's embedding route
            // may legitimately spend Fallen8:Embedding:TimeoutSeconds (300s) on inference and a cold model's
            // warm-up, so the client gave up first and reported a local failure instead of the downstream
            // answer that names which setting to change.
            var timeout = ClientDeadlineOf(new Fallen8TargetOptions { BaseUrl = "http://graph.invalid:8080" });

            Assert.AreEqual(TimeSpan.FromSeconds(330), timeout);
            Assert.IsTrue(timeout > TimeSpan.FromSeconds(300),
                "the embedding write must be allowed to spend the target's whole embedding budget, or this " +
                "runtime pre-empts the answer it was waiting for");
        }

        [TestMethod]
        public void TheGraphClientHonoursTheConfiguredDeadline()
        {
            var timeout = ClientDeadlineOf(new Fallen8TargetOptions
            {
                BaseUrl = "http://graph.invalid:8080",
                TimeoutSeconds = 7,
            });

            Assert.AreEqual(TimeSpan.FromSeconds(7), timeout,
                "the knob must reach the transport, or it is dead configuration an operator tunes with no effect");
        }

        [TestMethod]
        public void TheGraphClientFloorsANonPositiveDeadline_RatherThanThrowingOnEveryCall()
        {
            // HttpClient.Timeout refuses anything <= 0, and the client is built per run: unfloored, a stray 0
            // would throw on every call of every run and name nothing an operator could act on.
            foreach (var bad in new[] { 0, -5 })
            {
                var timeout = ClientDeadlineOf(new Fallen8TargetOptions
                {
                    BaseUrl = "http://graph.invalid:8080",
                    TimeoutSeconds = bad,
                });

                Assert.AreEqual(TimeSpan.FromSeconds(1), timeout,
                    "'" + bad.ToString(CultureInfo.InvariantCulture) + "' must be floored, not thrown on");
            }
        }

        /// <summary>
        ///   The deadline the factory really put on the run's client. Read by reflection because the target
        ///   owns its client and publishes nothing about it, and this suite reaches non-public state that way
        ///   rather than widening visibility (as elsewhere here: no project declares InternalsVisibleTo).
        /// </summary>
        private static TimeSpan ClientDeadlineOf(Fallen8TargetOptions options)
        {
            using var target = new GraphTargetFactory(Options.Create(options)).Create(null);
            var field = typeof(Fallen8RestTarget).GetField("_client",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "Fallen8RestTarget no longer holds its client in _client");
            return ((HttpClient)field.GetValue(target)).Timeout;
        }

        #endregion

        #region helpers

        private static async Task<JobReport> ApplyAsync(IGraphTarget target, SnapshotDocument document,
            SummaryRequest summary = null, IRunProgress progress = null)
        {
            var validator = new SnapshotValidator(IdentifierVocabulary.Shipped);
            var validated = validator.Validate(document);
            Assert.IsTrue(validated.EnvelopeAccepted,
                "the fixture document must be valid: " + String.Join(", ", validated.Diagnostics.Select(d => d.Code)));

            var report = new JobReport { ProviderId = Provider, IntegrationInstanceId = Instance };
            foreach (var diagnostic in validated.Diagnostics)
            {
                report.Diagnostics.Add(diagnostic);
            }

            var applier = new SnapshotApplier(new IdentityResolver());
            await applier.ApplyAsync(validated, Instance, target, report, summary, CancellationToken.None,
                progress);
            return report;
        }

        private static async Task AssertDeletionDeferred(TargetDurability durability, String expectedReason)
        {
            var graph = new InMemoryGraphTarget();
            await ApplyAsync(graph, Document(Device("44:D2:44:AA:BB:CC")));
            graph.SetDurability(durability);

            var report = await ApplyAsync(graph, Document());

            Assert.AreEqual(1, report.ClaimsWithdrawn, "the claim is still withdrawn: that is recoverable");
            Assert.AreEqual(0, report.ElementsDeleted);
            Assert.AreEqual(1, report.DeletionsDeferred,
                "deletion is the one mutation re-running cannot undo, and it is driven by a conclusion read out " +
                "of graph content, so deferring is recoverable where deleting wrongly is not");
            var deferral = report.Diagnostics.FirstOrDefault(
                d => d.Code == DiagnosticCodes.DeletionDeferredUnsafeDurability);
            Assert.IsNotNull(deferral, "the deferral is reported");
            StringAssert.Contains(deferral.Message, expectedReason,
                "the report names WHICH durability signal held the deletion back, or an operator cannot act on it");
            Assert.AreEqual(1, graph.AllElements().Count());
        }

        private static SnapshotDocument Document(params EntityDto[] entities)
        {
            var document = new SnapshotDocument
            {
                ProviderId = Provider,
                IntegrationInstanceId = Instance,
            };
            document.Declares = SnapshotCompleteness.Complete;
            foreach (var entity in entities)
            {
                document.Entities.Add(entity);
            }

            return document;
        }

        private static EntityDto Device(String mac, params (String Key, String Value)[] properties)
        {
            var entity = new EntityDto { Kind = "device" };
            entity.Claims.Add(new IdentityClaimDto { Type = "mac", Value = mac });
            foreach (var property in properties)
            {
                entity.Properties[property.Key] = property.Value;
            }

            return entity;
        }

        private static EntityDto CloneOf(EntityDto entity)
        {
            var clone = new EntityDto { Kind = entity.Kind };
            foreach (var claim in entity.Claims)
            {
                clone.Claims.Add(new IdentityClaimDto { Type = claim.Type, Value = claim.Value });
            }

            foreach (var property in entity.Properties)
            {
                clone.Properties[property.Key] = property.Value;
            }

            return clone;
        }

        // ---- element id 0 is a real id -------------------------------------------------------------

        [TestMethod]
        public async Task ARelationWhoseEndpointIsTheGRAPHS_FIRST_Element_IsStillWired()
        {
            // THE regression this pins: the engine gives the first element of a fresh graph id 0, every
            // namespace is its own graph, and the applier used a zero-initialised map with 0 as its "no
            // element" sentinel. So the first element of any new namespace read as "endpoint with no
            // element" and every relation to it was dropped - for good, because the match path re-assigned
            // 0 to it on every later run too. The UniFi provider emits the site FIRST, which made it every
            // device's site edge on the flagship first run.
            var graph = new InMemoryGraphTarget();

            var report = await ApplyAsync(graph, TwoDevicesWithAnUplink());

            Assert.AreEqual(2, report.ElementsCreated);
            Assert.AreEqual(1, report.EdgesCreated,
                "the uplink's target is the first entity, which becomes element 0 on a fresh graph: " +
                Describe(report));
            Assert.IsFalse(report.Diagnostics.Any(d => d.Code == DiagnosticCodes.DroppedRelation),
                "element 0 is an ordinary element id, not the absence of one: " + Describe(report));
        }

        [TestMethod]
        public async Task TheFirstElementOfAFreshGraph_TakesIdZero()
        {
            // Pins the FAKE against the platform, which is the other half of why the defect above shipped
            // unseen: this fake used to hand out 1 first, so no test could produce the id that broke.
            var graph = new InMemoryGraphTarget();

            await ApplyAsync(graph, Document(Device("44:D2:44:AA:BB:CC")));

            Assert.IsTrue(graph.TouchedElements.Contains(0),
                "the in-memory target must number elements like the engine does, from 0, or it hides exactly " +
                "the class of defect it exists to catch");
        }

        // ---- an interrupted run must heal ----------------------------------------------------------

        [TestMethod]
        public async Task ClaimsAreIndexedBeforeThePropertyWritesAndTheEdges()
        {
            // Findability first. Every write after the claim index is another chance for the run to be
            // interrupted, and an element carrying its claims as properties with no index entry is the one
            // state this runtime cannot heal from the outside: unfindable by the next resolve (which
            // duplicates it) and invisible to reconciliation (which withdraws by set difference over that
            // index).
            var graph = new InMemoryGraphTarget();
            await graph.EnsureIndicesAsync(CancellationToken.None);

            // A run over creates alone has no property write to place, so the fixture gives one element a STALE
            // property: this run then issues all three call kinds and the ordering of all three is observable.
            graph.SeedVertex("device", new[]
            {
                new GraphProperty(ClaimSchema.IdentityProperty(0), "System.String", "mac:44d244aabbcc"),
                new GraphProperty(ClaimSchema.ClaimProperty(Instance), "System.String", Instance),
                new GraphProperty("csv.name", "System.String", "printer"),
            });

            var stale = Device("44:D2:44:AA:BB:CC", ("csv.name", "plotter"));
            stale.Relations.Add(new RelationDto
            {
                Type = "uplink",
                Target = new ClaimReferenceDto { Type = "mac", Value = "44:D2:44:AA:BB:DD" },
            });

            var report = await ApplyAsync(graph, Document(stale, Device("44:D2:44:AA:BB:DD")));

            var calls = graph.MutationCalls.ToList();
            var firstIndex = calls.FindIndex(c => c.StartsWith("indexClaims", StringComparison.Ordinal));
            var firstProperty = calls.FindIndex(c => c.StartsWith("setProperties", StringComparison.Ordinal));
            var firstEdge = calls.FindIndex(c => c.StartsWith("createEdges", StringComparison.Ordinal));

            Assert.AreEqual(1, report.EdgesCreated, "the fixture must wire an edge: " + Describe(report));
            Assert.IsTrue(firstIndex >= 0, "the run must index its claims: " + String.Join(", ", calls));
            Assert.IsTrue(firstProperty > firstIndex,
                "the claims are indexed before the property writes, which is the half of this rule a fixture of " +
                "pure creates cannot see at all: " + String.Join(", ", calls));
            Assert.IsTrue(firstEdge > firstIndex,
                "the entity claims are indexed before the edges are wired: " + String.Join(", ", calls));
        }

        [TestMethod]
        public async Task AClaimCarriedAsAPropertyButNotIndexed_IsReAssertedRatherThanDuplicated()
        {
            // The fingerprint of a run interrupted (or partially declined) between its creates and its index
            // write: the element says it carries both claims, the index only knows one of them. Resolution
            // still matches on the known one, and THAT is the moment to notice and repair the other - the
            // lookup already answered which ids the index named, so it costs no extra read.
            var graph = new InMemoryGraphTarget();
            await graph.EnsureIndicesAsync(CancellationToken.None);

            var id = Seed(graph, "device", new[] { "mac:44d244aabbcc", "serial:SN-1" }, Instance);

            // The element keeps BOTH claims as properties; the index forgets one of them.
            graph.RemoveIndexEntry(ClaimSchema.IdentityIndexId, "serial:SN-1", id);

            var entity = Device("44:D2:44:AA:BB:CC");
            entity.Claims.Add(new IdentityClaimDto { Type = "serial", Value = "SN-1" });

            var report = await ApplyAsync(graph, Document(entity));

            Assert.AreEqual(0, report.ElementsCreated,
                "the element is found by the claim the index does know, so it is matched, never duplicated: " +
                Describe(report));
            Assert.AreEqual(1, report.ElementsMatched);
            Assert.IsTrue(report.Diagnostics.Any(d => d.Code == DiagnosticCodes.ClaimReindexed),
                "the heal is reported, because a silent repair leaves nobody knowing the state existed: " +
                Describe(report));

            var found = await graph.ResolveClaimKeysAsync(new[] { "serial:SN-1" }, Instance, CancellationToken.None);
            Assert.IsTrue(found.ByKey.TryGetValue("serial:SN-1", out var named) && named.Contains(id),
                "after the heal the index names the element for the claim it already carried");
        }

        [TestMethod]
        public async Task AnElementClaimedNowButMissingFromTheClaimsIndex_IsReAssertedByReconciliation()
        {
            // The OTHER half of the interrupted-run fingerprint, and the half the identity-index heal cannot see:
            // the element is findable by its claim key and says it is claimed, but the CLAIMS index does not name
            // it. Reconciliation withdraws by set difference over exactly that scan, so an element it never names
            // is never withdrawn and never deleted while staying invisible to every future reconciliation.
            var graph = new InMemoryGraphTarget();
            await graph.EnsureIndicesAsync(CancellationToken.None);

            var id = Seed(graph, "device", new[] { "mac:44d244aabbcc" }, Instance);
            graph.RemoveIndexEntry(ClaimSchema.ClaimsIndexId, Instance, id);

            var report = await ApplyAsync(graph, Document(Device("44:D2:44:AA:BB:CC")));

            Assert.AreEqual(1, report.ElementsMatched, "the element resolves as usual: " + Describe(report));
            var healed = report.Diagnostics.FirstOrDefault(d => d.Code == DiagnosticCodes.ClaimReindexed);
            Assert.IsNotNull(healed, "the heal is reported: " + Describe(report));
            StringAssert.Contains(healed.Message, "reconciliation cannot see them at all",
                "and it is the RECONCILE half that reported it, not the identity-index half");

            var claimed = await graph.ElementsClaimedByAsync(Instance, CancellationToken.None);
            CollectionAssert.Contains(claimed.ToList(), id,
                "the claims index names the element again, which is the whole repair");

            // And the proof that it is repaired rather than merely reported: the next complete snapshot that no
            // longer mentions the element can now withdraw and delete it. Without the re-assert the element would
            // sit in the graph claimed by this instance forever, invisible to every reconciliation there is.
            var next = await ApplyAsync(graph, Document());
            Assert.AreEqual(1, next.ClaimsWithdrawn, Describe(next));
            Assert.AreEqual(1, next.ElementsDeleted, Describe(next));
        }

        [TestMethod]
        public async Task TwoEntitiesAssertingOneStrongClaim_AreReportedWithTheKeyThatCollided()
        {
            // A recycled strong identifier - an RMA'd serial, a swapped MAC - is how this arrives in a real
            // source. Converging is the right behaviour, but silence makes it undiagnosable: the two entities'
            // properties overwrite each other on one element and the only visible symptom is churn.
            var graph = new InMemoryGraphTarget();

            var left = Device("44:D2:44:AA:BB:CC", ("csv.name", "printer"));
            left.Claims.Add(new IdentityClaimDto { Type = "serial", Value = "SN-1" });
            var right = Device("44:D2:44:AA:BB:DD", ("csv.name", "plotter"));
            right.Claims.Add(new IdentityClaimDto { Type = "serial", Value = "SN-1" });

            var report = await ApplyAsync(graph, Document(left, right));

            var collision = report.Diagnostics.FirstOrDefault(d => d.Code == DiagnosticCodes.CollidingStrongClaim);
            Assert.IsNotNull(collision,
                "two entities of one snapshot asserting one strong claim is a provider fault the author can only " +
                "fix if it is named: " + Describe(report));
            StringAssert.Contains(collision.Message, "serial:SN-1",
                "the report says WHICH key collided, or the author has a churning integration and no lead");
            StringAssert.Contains(collision.Message, "mac:44d244aabbcc",
                "and which entities did it, so the source row is findable");

            Assert.AreEqual(2, report.ElementsCreated,
                "the report is a diagnostic and not a merge: nothing here ever unifies two elements, and each " +
                "entity keeps the element its own strong claims found");
        }

        [TestMethod]
        public async Task AnUnchangedSourceStillIssuesNoWrites_WithTheHealInPlace()
        {
            // The heal must not become churn. Only STRONG claims are checked, because the lookup batch asks
            // about strong keys only: for a weak key "the index did not name it" is unknown rather than
            // false, and healing on unknown would re-assert every weak claim on every run.
            var graph = new InMemoryGraphTarget();
            var weakly = Device("44:D2:44:AA:BB:CC");
            weakly.Claims.Add(new IdentityClaimDto { Type = "ipv4", Value = "10.0.0.9" });

            await ApplyAsync(graph, Document(CloneOf(weakly)));
            var callsAfterFirst = graph.MutationCalls.Count;

            var second = await ApplyAsync(graph, Document(CloneOf(weakly)));

            Assert.AreEqual(callsAfterFirst, graph.MutationCalls.Count,
                "the second run over an unchanged source writes nothing at all: " +
                String.Join(", ", graph.MutationCalls));
            Assert.IsFalse(second.IssuedMutations);
        }

        private static SnapshotDocument TwoDevicesWithAnUplink()
        {
            var left = Device("44:D2:44:AA:BB:CC");
            left.Relations.Add(new RelationDto
            {
                Type = "uplink",
                Target = new ClaimReferenceDto { Type = "mac", Value = "44:D2:44:AA:BB:DD" },
            });

            return Document(left, Device("44:D2:44:AA:BB:DD"));
        }

        /// <summary>
        ///   Seeds an element the way a previous run would have left it: identity claims, and a claim property
        ///   only when somebody claims it.
        /// </summary>
        private static Int32 Seed(InMemoryGraphTarget graph, String label, String[] claims, String claimant)
        {
            var properties = new List<GraphProperty>();
            for (var ordinal = 0; ordinal < claims.Length; ordinal++)
            {
                properties.Add(new GraphProperty(ClaimSchema.IdentityProperty(ordinal), "System.String",
                    claims[ordinal]));
            }

            if (claimant != null)
            {
                properties.Add(new GraphProperty(ClaimSchema.ClaimProperty(claimant), "System.String", claimant));
            }

            return graph.SeedVertex(label, properties);
        }

        private static IntegrationJob RunnerJob(String instanceId)
        {
            return new IntegrationJob { ProviderId = Provider, IntegrationInstanceId = instanceId };
        }

        private static RunnerHarness Harness(IGraphTarget target, IJobFilesFactory files = null)
        {
            return new RunnerHarness(target, files ?? new NoFilesFactory());
        }

        /// <summary>
        ///   The REAL runner over a graph a test can read, with the one seam a run's outcome cannot be judged
        ///   without: a capturing log sink, because for a cancelled or credential-refused run the log LINE is the
        ///   only account there is.
        /// </summary>
        private sealed class RunnerHarness : IDisposable
        {
            private readonly ILoggerFactory _loggers;
            private readonly IntegrationsMetrics _metrics;

            public RunnerHarness(IGraphTarget target, IJobFilesFactory files)
            {
                Log = new CapturingLoggerProvider();
                _loggers = LoggerFactory.Create(builder => builder.AddProvider(Log));
                _metrics = new IntegrationsMetrics();
                Provider = new ScriptedProvider();

                var vocabulary = IdentifierVocabulary.Shipped;
                var active = new ActiveCredentials();
                ActiveCredentials = active;
                Runner = new JobRunner(
                    new ProviderCatalog(new IIntegrationProvider[] { Provider }, vocabulary),
                    new SnapshotValidator(vocabulary),
                    new SnapshotApplier(new IdentityResolver()),
                    new CredentialResolver(active),
                    new OneTargetFactory(target),
                    new NoNetworkHttpFactory(),
                    files,
                    active,
                    new RunGate(),
                    _metrics,
                    _loggers);
            }

            public ScriptedProvider Provider { get; }

            /// <summary>Every line the runner logged, formatted.</summary>
            public CapturingLoggerProvider Log { get; }

            public JobRunner Runner { get; }

            /// <summary>What the runs are holding, which is the only place a leaked lease is visible.</summary>
            public ActiveCredentials ActiveCredentials { get; }

            public void Dispose()
            {
                _loggers.Dispose();
                _metrics.Dispose();
            }
        }

        /// <summary>
        ///   Describes one device, identically on every run, and runs a hook WHILE observing - which is where a
        ///   caller's cancellation arrives in the only place a run is meant to honour it.
        /// </summary>
        private sealed class ScriptedProvider : IIntegrationProvider
        {
            public ProviderDescriptor Descriptor { get; } = new ProviderDescriptor
            {
                Id = Provider,
                DisplayName = "Write-path fixture",
                Description = "Describes one device, reading nothing.",
                Settings = new[]
                {
                    new ProviderSetting
                    {
                        Key = "apiKey",
                        Label = "API key",
                        Kind = SettingKind.Credential,
                        Required = false,
                        Help = "Supplied only by the credential-failure fixture.",
                    },
                },
                EntityKinds = new[] { "device" },
                ClaimTypes = new[] { "mac" },

                // Declared, so the provider half of the embedding opt-in is available to the one fixture that
                // needs the apply phase to reach the embedding write. The job half stays off by default.
                EntitySummaryTemplate = "{kind} {csv.name}",
                CanObserveCompleteState = true,
                ReadOnly = true,
            };

            public Action<ProviderContext, CancellationToken> WhileObserving { get; set; }

            public Task<SnapshotDocument> ObserveAsync(ProviderContext context, CancellationToken cancellationToken)
            {
                WhileObserving?.Invoke(context, cancellationToken);

                var snapshot = new SnapshotDocument
                {
                    ProviderId = context.ProviderId,
                    IntegrationInstanceId = context.InstanceId,
                };
                snapshot.Declares = SnapshotCompleteness.Complete;
                snapshot.CapturedNow();
                snapshot.Entities.Add(Device("44:D2:44:AA:BB:CC", ("csv.name", "printer")));
                return Task.FromResult(snapshot);
            }
        }

        /// <summary>Hands every run the same graph, and survives the runner disposing it.</summary>
        private sealed class OneTargetFactory : IGraphTargetFactory
        {
            private readonly IGraphTarget _target;

            public OneTargetFactory(IGraphTarget target)
            {
                _target = target;
            }

            public IGraphTarget Create(String namespaceName)
            {
                return _target;
            }
        }

        /// <summary>A client nothing may reach a network through: this fixture's provider reads no source.</summary>
        private sealed class NoNetworkHttpFactory : IProviderHttpFactory
        {
            public HttpClient Create(Boolean holdsCredential)
            {
                return new HttpClient(new RefusingHandler(), disposeHandler: true);
            }

            private sealed class RefusingHandler : HttpMessageHandler
            {
                protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                    CancellationToken cancellationToken)
                {
                    throw new NotSupportedException("This fixture's provider reads no source.");
                }
            }
        }

        /// <summary>Runs whose jobs carry no file, for the same reason: this fixture's provider reads none.</summary>
        private sealed class NoFilesFactory : IJobFilesFactory
        {
            public Int64 MaxFileBytes => 0;

            public JobFiles Create(IReadOnlyDictionary<String, JobFilePayload> filesBySettingKey)
            {
                return new JobFiles(filesBySettingKey);
            }
        }

        /// <summary>
        ///   A files factory that cannot hand a run its files. Nothing shipped throws here today, which is
        ///   exactly why the ordering it depends on has to be pinned rather than observed.
        /// </summary>
        private sealed class ThrowingFilesFactory : IJobFilesFactory
        {
            public Int64 MaxFileBytes => 0;

            public JobFiles Create(IReadOnlyDictionary<String, JobFilePayload> filesBySettingKey)
            {
                throw new InvalidOperationException("no run may have its files");
            }
        }

        private static String Describe(JobReport report)
        {
            return String.Format(
                "created {0}, matched {1}, edges {2}, withdrawn {3}, deleted {4}, deferred {5}, diagnostics [{6}]",
                report.ElementsCreated, report.ElementsMatched, report.EdgesCreated, report.ClaimsWithdrawn,
                report.ElementsDeleted, report.DeletionsDeferred,
                String.Join(", ", report.Diagnostics.Select(d => d.Code)));
        }

        /// <summary>
        ///   A target whose claim index disappears at the moment reconciliation asks it, which is what a
        ///   concurrent tabula rasa or save-game load does to a run already in flight.
        /// </summary>
        private sealed class VanishingClaimIndexTarget : DelegatingGraphTarget
        {
            private readonly InMemoryGraphTarget _graph;

            public VanishingClaimIndexTarget(InMemoryGraphTarget graph)
                : base(graph)
            {
                _graph = graph;
            }

            public override Task<IReadOnlyList<Int32>> ElementsClaimedByAsync(String instanceId,
                CancellationToken cancellationToken)
            {
                _graph.DropIndex(ClaimSchema.ClaimsIndexId);
                return base.ElementsClaimedByAsync(instanceId, cancellationToken);
            }
        }

        /// <summary>
        ///   A target that declines every index write, which is exactly what the platform does with a plain
        ///   <c>false</c> when the index or the element is not there.
        /// </summary>
        private sealed class DecliningIndexTarget : DelegatingGraphTarget
        {
            public DecliningIndexTarget(InMemoryGraphTarget inner)
                : base(inner)
            {
            }

            public override Task<IndexWriteOutcome> IndexClaimsAsync(IReadOnlyList<IndexEntry> entries,
                CancellationToken cancellationToken)
            {
                return Task.FromResult(new IndexWriteOutcome(0,
                    System.Collections.Immutable.ImmutableArray.CreateRange(entries)));
            }
        }

        /// <summary>
        ///   A target that HONOURS the cancellation token on every write, which is what the live one does: each
        ///   write is an HTTP call and <c>HttpClient</c> throws the moment the token it was handed is cancelled.
        ///   The in-memory graph ignores the token entirely, so without this the apply phase's uncancellability
        ///   could not be observed at all.
        /// </summary>
        private sealed class CancellationHonouringTarget : DelegatingGraphTarget
        {
            public CancellationHonouringTarget(InMemoryGraphTarget inner)
                : base(inner)
            {
            }

            public override Task<IReadOnlyList<Int32>> CreateVerticesAsync(IReadOnlyList<VertexWrite> vertices,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return base.CreateVerticesAsync(vertices, cancellationToken);
            }

            public override Task<IReadOnlyList<Int32>> CreateEdgesAsync(IReadOnlyList<EdgeWrite> edges,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return base.CreateEdgesAsync(edges, cancellationToken);
            }

            public override Task ApplyPropertyWritesAsync(IReadOnlyList<PropertyWrite> writes,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return base.ApplyPropertyWritesAsync(writes, cancellationToken);
            }

            public override Task<IndexWriteOutcome> IndexClaimsAsync(IReadOnlyList<IndexEntry> entries,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return base.IndexClaimsAsync(entries, cancellationToken);
            }

            public override Task RemoveElementsAsync(IReadOnlyCollection<Int32> ids,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return base.RemoveElementsAsync(ids, cancellationToken);
            }
        }

        /// <summary>
        ///   A target whose embedding write fails the way a client-side timeout arrives - as the
        ///   <see cref="TaskCanceledException"/> it is - with the caller's request already cancelled underneath it,
        ///   which is the coincidence that let a timeout pass for the caller walking away.
        ///   <c>Fallen8RestTarget</c> rewraps that into a graph failure (pinned separately), so this fixture stands
        ///   for the RULE rather than for that target: the runner's account must hold for any
        ///   <see cref="IGraphTarget"/>, and the runner is the only frame that knows which side of the apply call
        ///   it is standing on.
        /// </summary>
        private sealed class TimingOutEmbeddingTarget : DelegatingGraphTarget
        {
            private readonly CancellationTokenSource _caller;

            public TimingOutEmbeddingTarget(InMemoryGraphTarget inner, CancellationTokenSource caller)
                : base(inner)
            {
                _caller = caller;
            }

            public override Task<EmbeddingWriteOutcome> EmbedSummariesAsync(String embeddingName,
                IReadOnlyList<SummaryWrite> summaries, CancellationToken cancellationToken,
                NoSQL.GraphDB.Integrations.Run.IRunProgress progress = null)
            {
                _caller.Cancel();
                throw new TaskCanceledException("the embedding sidecar did not answer in time");
            }
        }

        /// <summary>
        ///   A handler that fails the way <see cref="HttpClient"/> fails on its own timeout: a
        ///   <see cref="TaskCanceledException"/> wrapping a <see cref="TimeoutException"/>, with nobody's token
        ///   cancelled.
        /// </summary>
        private sealed class TimingOutHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout",
                    new TimeoutException());
            }
        }

        /// <summary>
        ///   Records the ITEM COUNT of every embedding batch, and can refuse the Nth one. The count PER REQUEST
        ///   is the whole subject: the route caps items rather than bytes, so a chunking defect is invisible in
        ///   a total and shows up only request by request.
        /// </summary>
        private sealed class EmbedBatchRecordingHandler : HttpMessageHandler
        {
            private readonly Int32 _failOnCall;
            private readonly HttpStatusCode _status;
            private readonly Exception _throwInstead;

            /// <param name="failOnCall">Which chunk fails, 1-based; 0 for a handler that answers everything.</param>
            /// <param name="status">The status that chunk answers with.</param>
            /// <param name="throwInstead">Fails that chunk from the SEND rather than with a status, which is
            /// how a dead connection and a client-side deadline arrive: no status code exists to carry them.</param>
            public EmbedBatchRecordingHandler(Int32 failOnCall = 0,
                HttpStatusCode status = HttpStatusCode.ServiceUnavailable, Exception throwInstead = null)
            {
                _failOnCall = failOnCall;
                _status = status;
                _throwInstead = throwInstead;
            }

            public List<Int32> BatchSizes { get; } = new List<Int32>();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var parsed = JsonDocument.Parse(body);
                BatchSizes.Add(parsed.RootElement.GetProperty("items").GetArrayLength());

                if (_failOnCall > 0 && BatchSizes.Count == _failOnCall)
                {
                    if (_throwInstead != null)
                    {
                        throw _throwInstead;
                    }

                    return new HttpResponseMessage(_status) { Content = new StringContent("refused") };
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("true") };
            }
        }

        /// <summary>
        ///   Writes every phase and every counter move into a journal SHARED with the target below, so the
        ///   order of the two is observable: "the phase was entered while the work was still pending" is a
        ///   statement about interleaving, which neither a list of phases nor a list of calls can make alone.
        /// </summary>
        private sealed class JournallingProgress : IRunProgress
        {
            private readonly List<String> _journal;

            public JournallingProgress(List<String> journal)
            {
                _journal = journal;
            }

            public void EnterPhase(String phase)
            {
                _journal.Add("phase:" + phase);
            }

            public void Advance(Int32 done, Int32 total)
            {
                _journal.Add(String.Format(CultureInfo.InvariantCulture, "advance:{0}/{1}", done, total));
            }
        }

        /// <summary>The write calls, into the same journal as the progress above.</summary>
        private sealed class JournallingTarget : DelegatingGraphTarget
        {
            private readonly List<String> _journal;

            public JournallingTarget(IGraphTarget inner, List<String> journal)
                : base(inner)
            {
                _journal = journal;
            }

            public override Task<IReadOnlyList<Int32>> CreateVerticesAsync(IReadOnlyList<VertexWrite> vertices,
                CancellationToken cancellationToken)
            {
                _journal.Add("createVertices");
                return base.CreateVerticesAsync(vertices, cancellationToken);
            }

            public override Task<IReadOnlyList<Int32>> CreateEdgesAsync(IReadOnlyList<EdgeWrite> edges,
                CancellationToken cancellationToken)
            {
                _journal.Add("createEdges");
                return base.CreateEdgesAsync(edges, cancellationToken);
            }
        }

        /// <summary>
        ///   Cancels the CALLER'S token and then fails the way a cancelled request does. The one shape that
        ///   must never become this seam's own timeout, however much it looks like one.
        /// </summary>
        private sealed class CallerCancellingHandler : HttpMessageHandler
        {
            private readonly CancellationTokenSource _caller;

            public CallerCancellingHandler(CancellationTokenSource caller)
            {
                _caller = caller;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                _caller.Cancel();
                throw new TaskCanceledException("the caller walked away", null, _caller.Token);
            }
        }

        /// <summary>
        ///   A target whose provider stops answering PART WAY through the chunked embedding write, which is the
        ///   state chunking introduced and which no fixture could produce before: some vectors landed and the
        ///   rest did not.
        /// </summary>
        private sealed class PartiallyEmbeddingTarget : DelegatingGraphTarget
        {
            private readonly Int32 _embedsAtMost;

            public PartiallyEmbeddingTarget(IGraphTarget inner, Int32 embedsAtMost)
                : base(inner)
            {
                _embedsAtMost = embedsAtMost;
            }

            public override Task<EmbeddingWriteOutcome> EmbedSummariesAsync(String embeddingName,
                IReadOnlyList<SummaryWrite> summaries, CancellationToken cancellationToken,
                NoSQL.GraphDB.Integrations.Run.IRunProgress progress = null)
            {
                var written = Math.Min(_embedsAtMost, summaries.Count);

                return Task.FromResult(new EmbeddingWriteOutcome(written,
                    written == summaries.Count
                        ? null
                        : "the target answered 503 to the embedding write (the backend is unavailable)"));
            }
        }

        /// <summary>Summaries enough to need more than one chunk, since one chunk is the whole defect.</summary>
        private static SummaryWrite[] Summaries(Int32 count)
        {
            var summaries = new SummaryWrite[count];
            for (var i = 0; i < count; i++)
            {
                summaries[i] = new SummaryWrite(i, "signal Odo_ST" + i.ToString(CultureInfo.InvariantCulture));
            }

            return summaries;
        }

        /// <summary>
        ///   Pass-through, so a fixture overrides ONE seam method and nothing else. Written once rather than per
        ///   fixture, because a hand-rolled second copy is where a fixture quietly stops behaving like the graph.
        /// </summary>
        private abstract class DelegatingGraphTarget : IGraphTarget
        {
            private readonly IGraphTarget _inner;

            protected DelegatingGraphTarget(IGraphTarget inner)
            {
                _inner = inner;
            }

            public Int32 IssuedMutationCount => _inner.IssuedMutationCount;

            public virtual Task<Boolean> EnsureIndicesAsync(CancellationToken cancellationToken)
            {
                return _inner.EnsureIndicesAsync(cancellationToken);
            }

            public virtual Task<IndexRepairOutcome> RepairIndicesAsync(CancellationToken cancellationToken)
            {
                return _inner.RepairIndicesAsync(cancellationToken);
            }

            public virtual Task<ClaimLookup> ResolveClaimKeysAsync(IReadOnlyCollection<String> claimKeys,
                String instanceId, CancellationToken cancellationToken)
            {
                return _inner.ResolveClaimKeysAsync(claimKeys, instanceId, cancellationToken);
            }

            public virtual Task<IReadOnlyList<Int32>> ElementsClaimedByAsync(String instanceId,
                CancellationToken cancellationToken)
            {
                return _inner.ElementsClaimedByAsync(instanceId, cancellationToken);
            }

            public virtual Task<IReadOnlyDictionary<Int32, ElementState>> ReadElementsAsync(
                IReadOnlyCollection<Int32> ids, CancellationToken cancellationToken)
            {
                return _inner.ReadElementsAsync(ids, cancellationToken);
            }

            public virtual Task<IReadOnlyList<Int32>> CreateVerticesAsync(IReadOnlyList<VertexWrite> vertices,
                CancellationToken cancellationToken)
            {
                return _inner.CreateVerticesAsync(vertices, cancellationToken);
            }

            public virtual Task<IReadOnlyList<Int32>> CreateEdgesAsync(IReadOnlyList<EdgeWrite> edges,
                CancellationToken cancellationToken)
            {
                return _inner.CreateEdgesAsync(edges, cancellationToken);
            }

            public virtual Task ApplyPropertyWritesAsync(IReadOnlyList<PropertyWrite> writes,
                CancellationToken cancellationToken)
            {
                return _inner.ApplyPropertyWritesAsync(writes, cancellationToken);
            }

            public virtual Task RemoveElementsAsync(IReadOnlyCollection<Int32> ids,
                CancellationToken cancellationToken)
            {
                return _inner.RemoveElementsAsync(ids, cancellationToken);
            }

            public virtual Task<IndexWriteOutcome> IndexClaimsAsync(IReadOnlyList<IndexEntry> entries,
                CancellationToken cancellationToken)
            {
                return _inner.IndexClaimsAsync(entries, cancellationToken);
            }

            public virtual Task<TargetDurability> ReadDurabilityAsync(CancellationToken cancellationToken)
            {
                return _inner.ReadDurabilityAsync(cancellationToken);
            }

            public virtual Task<TargetEmbedding> ReadEmbeddingStateAsync(CancellationToken cancellationToken)
            {
                return _inner.ReadEmbeddingStateAsync(cancellationToken);
            }

            public virtual Task<EmbeddingWriteOutcome> EmbedSummariesAsync(String embeddingName,
                IReadOnlyList<SummaryWrite> summaries, CancellationToken cancellationToken,
                NoSQL.GraphDB.Integrations.Run.IRunProgress progress = null)
            {
                return _inner.EmbedSummariesAsync(embeddingName, summaries, cancellationToken, progress);
            }

            public void Dispose()
            {
            }
        }

        #endregion
    }
}
