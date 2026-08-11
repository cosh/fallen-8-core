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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Integrations.Contract;
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

        #endregion

        #region helpers

        private static async Task<JobReport> ApplyAsync(IGraphTarget target, SnapshotDocument document,
            SummaryRequest summary = null)
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
            await applier.ApplyAsync(validated, Instance, target, report, summary, CancellationToken.None);
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
                IReadOnlyList<SummaryWrite> summaries, CancellationToken cancellationToken)
            {
                return _inner.EmbedSummariesAsync(embeddingName, summaries, cancellationToken);
            }

            public void Dispose()
            {
            }
        }

        #endregion
    }
}
