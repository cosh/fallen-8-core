// MIT License
//
// SnapshotApplier.cs
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
using NoSQL.GraphDB.Integrations.Graph;
using NoSQL.GraphDB.Integrations.Identity;
using NoSQL.GraphDB.Integrations.Summary;
using NoSQL.GraphDB.Integrations.Validation;

namespace NoSQL.GraphDB.Integrations.Run
{
    /// <summary>
    ///   What to embed, when a provider declared a template AND a job asked for it. Both halves are required, and
    ///   the default is off, because embedding every client on a busy network by default is cost and noise in
    ///   equal measure.
    /// </summary>
    public sealed class SummaryRequest
    {
        public SummaryRequest(String template, String embeddingName)
        {
            Template = template;
            EmbeddingName = embeddingName;
        }

        /// <summary>The provider's declarative template.</summary>
        public String Template { get; }

        /// <summary>The named embedding to write.</summary>
        public String EmbeddingName { get; }
    }

    /// <summary>
    ///   Turns one validated snapshot into graph writes, IN THIS ORDER AND NO OTHER: resolve, create or match,
    ///   write properties only where they differ, wire edges, index claims, reconcile.
    ///
    ///   <para>The order is the design, because reconciliation withdraws by SET DIFFERENCE and so must run
    ///   after everything the run asserts: a withdrawal can then never precede the assertion that would have
    ///   kept an element alive.</para>
    /// </summary>
    public sealed class SnapshotApplier
    {
        private readonly IdentityResolver _resolver;

        public SnapshotApplier(IdentityResolver resolver)
        {
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        }

        /// <summary>
        ///   Applies a snapshot whose envelope has already been accepted, counting what it did onto
        ///   <paramref name="report"/>.
        /// </summary>
        /// <param name="snapshot">The validated snapshot, whose envelope has already been accepted.</param>
        /// <param name="instanceId">The identity this run asserts as.</param>
        /// <param name="target">The graph to write into, created for this job and disposed by the runner.</param>
        /// <param name="report">The run's account, which this counts onto.</param>
        /// <param name="summary">The entity summary to embed, when BOTH halves of the opt-in are set. Null
        /// otherwise, which is the default and the common case.</param>
        /// <param name="cancellationToken">Aborts the run.</param>
        public async Task ApplyAsync(ValidatedSnapshot snapshot, String instanceId, IGraphTarget target,
            JobReport report, SummaryRequest? summary, CancellationToken cancellationToken)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            if (report == null)
            {
                throw new ArgumentNullException(nameof(report));
            }

            // "It existed when I started" is not a fact that stays true, so this runs before EVERY job. An
            // index it had to create is empty, and empty is indistinguishable from "no element carries this
            // claim", which obliges the repair before any lookup is trusted.
            if (await target.EnsureIndicesAsync(cancellationToken).ConfigureAwait(false))
            {
                await RepairAsync(target, report, cancellationToken).ConfigureAwait(false);
            }

            var plan = PlanEdges(snapshot, report);

            var keys = new HashSet<String>(StringComparer.Ordinal);
            foreach (var entity in snapshot.Entities)
            {
                foreach (var claim in entity.StrongClaims())
                {
                    keys.Add(claim.Key);
                }
            }

            foreach (var edge in plan)
            {
                keys.Add(edge.DerivedKey);
            }

            // ONE lookup batch asking two questions at once: does the edge already exist, by its derived key,
            // and which elements carry the entities' claims. Both are answerable up front because an
            // endpoint's primary key comes from the claims THIS snapshot asserts, which makes the whole run a
            // function of what the source said. The batch comes back already narrowed to what this instance may
            // write to, and also carries the un-narrowed answer, which the edge rule needs.
            var lookup = await ResolveWithRepairAsync(target, keys, instanceId, report, cancellationToken)
                .ConfigureAwait(false);
            var states = lookup.Elements;
            var inScope = lookup.InScope;

            var claimedNow = new HashSet<Int32>();
            var elementIdByEntity = new Int32[snapshot.Entities.Length];
            var propertyWrites = new List<PropertyWrite>();
            var indexEntries = new List<IndexEntry>();
            var creates = new List<VertexWrite>();
            var createdEntityIndex = new List<Int32>();

            // Which entities' own data changed this run. A summary is a pure function of an entity's kind and
            // properties, so an entity that produced no property write cannot have a different summary - which is
            // what keeps the embedding pass from making every run a write.
            var summaryDirty = new List<Int32>();

            var claimProperty = ClaimSchema.ClaimProperty(instanceId);

            for (var i = 0; i < snapshot.Entities.Length; i++)
            {
                var entity = snapshot.Entities[i];
                var resolution = _resolver.Resolve(entity, inScope);

                if (resolution.Outcome == ResolutionOutcome.Create)
                {
                    var properties = new List<GraphProperty>(entity.Properties.Length + entity.Claims.Length + 1);
                    properties.AddRange(entity.Properties);

                    for (var ordinal = 0; ordinal < entity.Claims.Length; ordinal++)
                    {
                        properties.Add(new GraphProperty(ClaimSchema.IdentityProperty(ordinal),
                            WireValues.StringTypeName, entity.Claims[ordinal].Key));
                    }

                    properties.Add(new GraphProperty(claimProperty, WireValues.StringTypeName, instanceId));

                    creates.Add(new VertexWrite(entity.Kind, properties));
                    createdEntityIndex.Add(i);
                    continue;
                }

                if (resolution.Outcome == ResolutionOutcome.MatchedMoreThanOne)
                {
                    report.Diagnostics.Add(new DiagnosticDto(DiagnosticCodes.DuplicateClaimedElements,
                        String.Format(CultureInfo.InvariantCulture,
                            "{0} elements this instance claims carry this entity's strong claims; element {1} " +
                            "was chosen by content and the others stop being asserted, so this run's " +
                            "reconciliation converges them away.",
                            resolution.MatchedElements.Length, resolution.ElementId),
                        entity.PrimaryKey));
                }

                var elementId = resolution.ElementId;
                elementIdByEntity[i] = elementId;
                claimedNow.Add(elementId);
                report.ElementsMatched++;

                var state = states[elementId];
                var existingKeys = new HashSet<String>(StringComparer.Ordinal);
                foreach (var key in state.IdentityKeys())
                {
                    existingKeys.Add(key);
                }

                var nextOrdinal = state.NextIdentityOrdinal();
                foreach (var claim in entity.Claims)
                {
                    if (existingKeys.Contains(claim.Key))
                    {
                        continue;
                    }

                    // Missing claims at EVERY strength are appended: a weak claim never resolves, but it is
                    // what makes an overlap findable, so it is written and indexed exactly like a strong one.
                    propertyWrites.Add(PropertyWrite.Set(elementId, new GraphProperty(
                        ClaimSchema.IdentityProperty(nextOrdinal), WireValues.StringTypeName, claim.Key)));
                    indexEntries.Add(new IndexEntry(ClaimSchema.IdentityIndexId, claim.Key, elementId));
                    nextOrdinal++;
                }

                foreach (var property in entity.Properties)
                {
                    if (!state.Properties.TryGetValue(property.Key, out var stored) ||
                        property.DiffersFrom(stored))
                    {
                        propertyWrites.Add(PropertyWrite.Set(elementId, property));

                        if (!summaryDirty.Contains(i))
                        {
                            summaryDirty.Add(i);
                        }
                    }
                }

                if (!state.IsClaimedBy(instanceId))
                {
                    // The absent case is the ORPHAN BEING RECLAIMED: an element carrying this instance's
                    // identity claims and no claim property, left by a withdrawal whose deletion was deferred.
                    propertyWrites.Add(PropertyWrite.Set(elementId,
                        new GraphProperty(claimProperty, WireValues.StringTypeName, instanceId)));
                    indexEntries.Add(new IndexEntry(ClaimSchema.ClaimsIndexId, instanceId, elementId));
                }
            }

            if (creates.Count > 0)
            {
                var ids = await target.CreateVerticesAsync(creates, cancellationToken).ConfigureAwait(false);
                for (var i = 0; i < ids.Count; i++)
                {
                    var entityIndex = createdEntityIndex[i];
                    var entity = snapshot.Entities[entityIndex];
                    elementIdByEntity[entityIndex] = ids[i];
                    claimedNow.Add(ids[i]);
                    report.ElementsCreated++;
                    summaryDirty.Add(entityIndex);

                    foreach (var claim in entity.Claims)
                    {
                        indexEntries.Add(new IndexEntry(ClaimSchema.IdentityIndexId, claim.Key, ids[i]));
                    }

                    indexEntries.Add(new IndexEntry(ClaimSchema.ClaimsIndexId, instanceId, ids[i]));
                }
            }

            if (propertyWrites.Count > 0)
            {
                await target.ApplyPropertyWritesAsync(propertyWrites, cancellationToken).ConfigureAwait(false);
            }

            await WireEdgesAsync(plan, instanceId, target, report, lookup, elementIdByEntity, claimedNow,
                indexEntries, cancellationToken).ConfigureAwait(false);

            if (indexEntries.Count > 0)
            {
                var outcome = await target.IndexClaimsAsync(indexEntries, cancellationToken).ConfigureAwait(false);
                foreach (var declined in outcome.Declined)
                {
                    report.Diagnostics.Add(new DiagnosticDto(DiagnosticCodes.ClaimNotIndexed,
                        String.Format(
                            "The target declined to index claim '{0}' for element {1}. An element findable by " +
                            "none of its claims is duplicated on the next resolve, so re-run the repair.",
                            declined.Key, declined.ElementId),
                        declined.Key));
                }
            }

            if (summary != null)
            {
                await EmbedSummariesAsync(snapshot, summary, target, report, elementIdByEntity, summaryDirty,
                    cancellationToken).ConfigureAwait(false);
            }

            if (snapshot.Completeness == SnapshotCompleteness.Complete)
            {
                await ReconcileAsync(instanceId, target, report, claimedNow, cancellationToken)
                    .ConfigureAwait(false);
            }

            report.IssuedMutations = target.IssuedMutations;
        }

        /// <summary>
        ///   Works out which edges the snapshot asks for, and reports the ones it cannot address.
        ///
        ///   <para>A relation whose target THIS SNAPSHOT does not describe is reported as
        ///   <c>droppedRelation</c> and no edge is created. A complete snapshot describes the whole source, so
        ///   a target it does not mention is one the source no longer has, and this same run's reconciliation
        ///   withdraws this instance's claim from it: wiring an edge to an element the run is about to stop
        ///   asserting would create an edge that the same reconciliation immediately deletes. It also keeps the
        ///   derived key computable from the snapshot alone, which is what lets the whole edge question ride in
        ///   one lookup. (A partial snapshot would need the other reading, and none is produced yet: the
        ///   revisit trigger is the first event-driven provider.)</para>
        /// </summary>
        private static List<EdgePlan> PlanEdges(ValidatedSnapshot snapshot, JobReport report)
        {
            var entityByClaimKey = new Dictionary<String, Int32>(StringComparer.Ordinal);
            for (var i = 0; i < snapshot.Entities.Length; i++)
            {
                foreach (var claim in snapshot.Entities[i].StrongClaims())
                {
                    // First wins: two entities asserting one strong key is a provider fault that converges on
                    // one element anyway, and picking a side here would make the edge's endpoint depend on
                    // document order.
                    if (!entityByClaimKey.ContainsKey(claim.Key))
                    {
                        entityByClaimKey.Add(claim.Key, i);
                    }
                }
            }

            var plan = new List<EdgePlan>();
            for (var i = 0; i < snapshot.Entities.Length; i++)
            {
                var entity = snapshot.Entities[i];
                foreach (var relation in entity.Relations)
                {
                    if (!entityByClaimKey.TryGetValue(relation.Target.Key, out var targetIndex))
                    {
                        report.Diagnostics.Add(new DiagnosticDto(DiagnosticCodes.DroppedRelation,
                            String.Format(
                                "Relation '{0}' points at '{1}', which this snapshot does not describe, so no " +
                                "edge was created. It is created on a later run if this integration comes to " +
                                "claim that element, and never if another integration owns it: an edge wired " +
                                "across instances would be found by its own derived key forever and could not " +
                                "heal.", relation.Type, relation.Target.Key),
                            entity.PrimaryKey));
                        continue;
                    }

                    plan.Add(new EdgePlan(i, targetIndex, relation.Type,
                        ClaimKeyComposer.ForEdge(entity.PrimaryKey, relation.Type,
                            snapshot.Entities[targetIndex].PrimaryKey),
                        relation.Target.Key));
                }
            }

            return plan;
        }

        private static async Task WireEdgesAsync(List<EdgePlan> plan, String instanceId, IGraphTarget target,
            JobReport report, ClaimLookup lookup, Int32[] elementIdByEntity, HashSet<Int32> claimedNow,
            List<IndexEntry> indexEntries, CancellationToken cancellationToken)
        {
            if (plan.Count == 0)
            {
                return;
            }

            var writes = new List<EdgeWrite>();
            var writtenKeys = new List<String>();
            var claimProperty = ClaimSchema.ClaimProperty(instanceId);

            foreach (var edge in plan)
            {
                if (lookup.InScope.TryGetValue(edge.TargetClaimKey, out var targets) && targets.Count > 1)
                {
                    report.Diagnostics.Add(new DiagnosticDto(DiagnosticCodes.AmbiguousRelationTarget,
                        String.Format(CultureInfo.InvariantCulture,
                            "{0} elements this instance claims carry the target claim '{1}'; the edge was wired " +
                            "to the element the target entity itself resolved to, so the pick is the same one " +
                            "the run wrote to.", targets.Count, edge.TargetClaimKey),
                        edge.TargetClaimKey));
                }

                // THE ONE ASYMMETRY: an edge found by its derived key counts as already wired only if it
                // carries THIS instance's claim. The derived key encodes the endpoints and the type, not the
                // creator, so two instances asserting one relation find the same edge; admitting a foreign
                // edge id into the claimed-now set would make this instance's reconciliation responsible for
                // another instance's edge, and skipping creation instead would leave this instance with no
                // edge to claim at all. An UNCLAIMED edge does not count either: that is an orphan left by a
                // deferred deletion, and the fresh claimed edge plus the next healthy reconciliation is what
                // heals it.
                var alreadyWired = 0;
                if (lookup.ByKey.TryGetValue(edge.DerivedKey, out var existing))
                {
                    foreach (var id in existing)
                    {
                        if (lookup.Elements.TryGetValue(id, out var state) && state.IsClaimedBy(instanceId) &&
                            (alreadyWired == 0 || id < alreadyWired))
                        {
                            alreadyWired = id;
                        }
                    }
                }

                if (alreadyWired != 0)
                {
                    claimedNow.Add(alreadyWired);
                    continue;
                }

                var sourceId = elementIdByEntity[edge.SourceEntity];
                var targetId = elementIdByEntity[edge.TargetEntity];
                if (sourceId == 0 || targetId == 0)
                {
                    // Unreachable while every entity is created or matched, and reported rather than assumed:
                    // an endpoint with no element would otherwise become a dangling edge.
                    report.Diagnostics.Add(new DiagnosticDto(DiagnosticCodes.DroppedRelation,
                        String.Format("Relation '{0}' has an endpoint with no element.", edge.EdgeType),
                        edge.DerivedKey));
                    continue;
                }

                writes.Add(new EdgeWrite(sourceId, targetId, edge.EdgeType, new[]
                {
                    new GraphProperty(ClaimSchema.IdentityProperty(0), WireValues.StringTypeName, edge.DerivedKey),
                    new GraphProperty(claimProperty, WireValues.StringTypeName, instanceId),
                }));
                writtenKeys.Add(edge.DerivedKey);
            }

            if (writes.Count == 0)
            {
                return;
            }

            var ids = await target.CreateEdgesAsync(writes, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < ids.Count; i++)
            {
                claimedNow.Add(ids[i]);
                report.EdgesCreated++;
                indexEntries.Add(new IndexEntry(ClaimSchema.IdentityIndexId, writtenKeys[i], ids[i]));
                indexEntries.Add(new IndexEntry(ClaimSchema.ClaimsIndexId, instanceId, ids[i]));
            }
        }

        /// <summary>
        ///   Embeds the entity summaries of the entities whose data CHANGED this run, and degrades to absent when
        ///   the target cannot embed.
        ///
        ///   <para>Only the changed ones, because re-embedding an unchanged entity would issue a write on every
        ///   run and make the zero-mutation invariant false by construction - and a summary is a pure function of
        ///   the entity's kind and properties, so an entity that produced no property write has no new summary to
        ///   embed.</para>
        ///
        ///   <para>Degrading to ABSENT rather than to broken is the whole of this runtime's dependence on the AI
        ///   capabilities: the graph write is the point of the run, and an embedding is an addition to what landed
        ///   rather than a precondition for it.</para>
        /// </summary>
        private static async Task EmbedSummariesAsync(ValidatedSnapshot snapshot, SummaryRequest summary,
            IGraphTarget target, JobReport report, Int32[] elementIdByEntity, List<Int32> summaryDirty,
            CancellationToken cancellationToken)
        {
            var writes = new List<SummaryWrite>();
            foreach (var entityIndex in summaryDirty)
            {
                var elementId = elementIdByEntity[entityIndex];
                if (elementId == 0)
                {
                    continue;
                }

                var text = EntitySummaryTemplate.Render(summary.Template, snapshot.Entities[entityIndex]);
                if (text != null)
                {
                    writes.Add(new SummaryWrite(elementId, text));
                }
            }

            if (writes.Count == 0)
            {
                return;
            }

            // The dimension and the metric are the TARGET'S, read from what it publishes, so no model, dimension
            // or metric is named anywhere in this runtime.
            var state = await target.ReadEmbeddingStateAsync(cancellationToken).ConfigureAwait(false);
            if (!state.Available)
            {
                report.Diagnostics.Add(new DiagnosticDto(DiagnosticCodes.SummaryEmbeddingUnavailable,
                    String.Format(CultureInfo.InvariantCulture,
                        "{0} entity summary/summaries were not embedded because {1}. Everything else the run " +
                        "asserted still landed.", writes.Count, state.Reason ?? "the target cannot embed text")));
                return;
            }

            var outcome = await target.EmbedSummariesAsync(summary.EmbeddingName, writes, cancellationToken)
                .ConfigureAwait(false);

            if (outcome.Degraded != null)
            {
                report.Diagnostics.Add(new DiagnosticDto(DiagnosticCodes.SummaryEmbeddingUnavailable,
                    String.Format(CultureInfo.InvariantCulture,
                        "{0} entity summary/summaries were not embedded because {1}. Everything else the run " +
                        "asserted still landed.", writes.Count, outcome.Degraded)));
                return;
            }

            report.SummariesEmbedded = outcome.Written;
        }

        /// <summary>
        ///   Withdraws by set difference, then deletes only on the LAST claim, and only when the target's
        ///   durability makes deleting safe.
        /// </summary>
        private static async Task ReconcileAsync(String instanceId, IGraphTarget target, JobReport report,
            HashSet<Int32> claimedNow, CancellationToken cancellationToken)
        {
            IReadOnlyList<Int32> claimedBefore;
            try
            {
                claimedBefore = await target.ElementsClaimedByAsync(instanceId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (GraphIndexMissingException)
            {
                // An empty answer from a missing index reads as "this instance claims nothing", which would
                // withdraw everything the instance ever asserted and then delete whatever nothing else claims.
                // So reconciliation is SKIPPED, not retried.
                await RepairAsync(target, report, cancellationToken).ConfigureAwait(false);
                report.Diagnostics.Add(new DiagnosticDto(DiagnosticCodes.ReconciliationDeferred,
                    "The claim index was missing, so this run withdrew nothing and repaired the index instead. " +
                    "The next run reconciles."));
                return;
            }

            var withdrawSet = new List<Int32>();
            foreach (var id in claimedBefore)
            {
                if (!claimedNow.Contains(id))
                {
                    withdrawSet.Add(id);
                }
            }

            if (withdrawSet.Count == 0)
            {
                return;
            }

            var before = await target.ReadElementsAsync(withdrawSet, cancellationToken).ConfigureAwait(false);

            // WITHDRAWAL IS EFFECTIVE-ONLY: the claim index has no remove path, so it answers "ever claimed"
            // and an element this instance already stopped claiming stays in that answer forever. Re-issuing
            // the removal would report a withdrawal and a mutation on every future run over a completely
            // unchanged source. Ids the index still names but the graph no longer has are simply absent here,
            // and are neither withdrawn nor deleted.
            var claimProperty = ClaimSchema.ClaimProperty(instanceId);
            var removals = new List<PropertyWrite>();
            foreach (var id in withdrawSet)
            {
                if (before.TryGetValue(id, out var state) && state.IsClaimedBy(instanceId))
                {
                    removals.Add(PropertyWrite.Remove_(id, claimProperty));
                }
            }

            var after = before;
            if (removals.Count > 0)
            {
                await target.ApplyPropertyWritesAsync(removals, cancellationToken).ConfigureAwait(false);
                report.ClaimsWithdrawn = removals.Count;

                // Re-read, because the deletion decision is made from what the elements say NOW rather than
                // from what the runtime believed before it wrote. With no removal issued, what they said before
                // is still what they say now.
                after = await target.ReadElementsAsync(withdrawSet, cancellationToken).ConfigureAwait(false);
            }

            // DELETION HAPPENS ON THE LAST CLAIM, judged over every withdrawal rather than only the effective
            // ones: an element that already carried no claim is an orphan left by a deferred deletion, cleaned
            // up here once durability is healthy.
            var deletable = new List<Int32>();
            foreach (var id in withdrawSet)
            {
                if (after.TryGetValue(id, out var state) && !state.HasAnyClaim())
                {
                    deletable.Add(id);
                }
            }

            if (deletable.Count == 0)
            {
                return;
            }

            var durability = await target.ReadDurabilityAsync(cancellationToken).ConfigureAwait(false);
            if (!durability.SafeToDelete)
            {
                report.DeletionsDeferred = deletable.Count;
                report.Diagnostics.Add(new DiagnosticDto(DiagnosticCodes.DeletionDeferredUnsafeDurability,
                    String.Format(CultureInfo.InvariantCulture,
                        "{0} element(s) carry no claim any more but were NOT deleted: {1}. Deletion is the one " +
                        "mutation re-running cannot undo, and it is driven by a conclusion read out of graph " +
                        "content, so deferring is recoverable where deleting wrongly is not.",
                        deletable.Count, durability.Reason())));
                return;
            }

            await target.RemoveElementsAsync(deletable, cancellationToken).ConfigureAwait(false);
            report.ElementsDeleted = deletable.Count;
        }

        /// <summary>
        ///   One lookup, and on a missing index exactly ONE ensure-repair-retry. Once only: a second failure is
        ///   a real fault and must surface rather than loop.
        /// </summary>
        private async Task<ClaimLookup> ResolveWithRepairAsync(IGraphTarget target,
            IReadOnlyCollection<String> keys, String instanceId, JobReport report,
            CancellationToken cancellationToken)
        {
            try
            {
                return await target.ResolveClaimKeysAsync(keys, instanceId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (GraphIndexMissingException)
            {
                await target.EnsureIndicesAsync(cancellationToken).ConfigureAwait(false);
                await RepairAsync(target, report, cancellationToken).ConfigureAwait(false);
                return await target.ResolveClaimKeysAsync(keys, instanceId, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        private static async Task RepairAsync(IGraphTarget target, JobReport report,
            CancellationToken cancellationToken)
        {
            var outcome = await target.RepairIndicesAsync(cancellationToken).ConfigureAwait(false);
            report.Diagnostics.Add(new DiagnosticDto(DiagnosticCodes.IdentityIndexRebuilt,
                String.Format(CultureInfo.InvariantCulture,
                    "The claim indices were rebuilt from element state before any lookup was trusted: {0} " +
                    "identity entry/entries and {1} claim entry/entries restored. An index is dropped by three " +
                    "ordinary operations, and a fresh one is empty, which is indistinguishable from 'nothing " +
                    "carries this claim'.",
                    outcome.IdentityEntries, outcome.ClaimEntries)));
        }

        /// <summary>One edge the snapshot asks for, with its derived key already composed.</summary>
        /// <remarks>Placed next to the plan it belongs to rather than in the contract, because nothing outside
        /// this file needs it.</remarks>
        private sealed class EdgePlan
        {
            public EdgePlan(Int32 sourceEntity, Int32 targetEntity, String edgeType, String derivedKey,
                String targetClaimKey)
            {
                SourceEntity = sourceEntity;
                TargetEntity = targetEntity;
                EdgeType = edgeType;
                DerivedKey = derivedKey;
                TargetClaimKey = targetClaimKey;
            }

            public Int32 SourceEntity { get; }

            public Int32 TargetEntity { get; }

            public String EdgeType { get; }

            public String DerivedKey { get; }

            public String TargetClaimKey { get; }
        }
    }
}
