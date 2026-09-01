// MIT License
//
// GraphWrites.cs
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
using NoSQL.GraphDB.Integrations.Identity;

namespace NoSQL.GraphDB.Integrations.Graph
{
    /// <summary>
    ///   One property as the graph carries it: a key, the platform's literal type name, and the invariant
    ///   text form. The triple has one home because the validator produces it, the comparison consumes it and
    ///   the write path sends it; three copies of a three-field record would be three chances for one of them
    ///   to render a value differently and make every run a write.
    /// </summary>
    public readonly struct GraphProperty : IEquatable<GraphProperty>
    {
        public GraphProperty(String key, String typeName, String text)
        {
            Key = key;
            TypeName = typeName;
            Text = text;
        }

        /// <summary>The property key.</summary>
        public String Key { get; }

        /// <summary>The platform literal type name, e.g. <c>System.String</c>.</summary>
        public String TypeName { get; }

        /// <summary>The invariant text form the platform stores and returns.</summary>
        public String Text { get; }

        /// <summary>
        ///   Whether two renderings are the same value. BOTH the type and the text must match: the platform's
        ///   egress mirrors its ingress by design, so a value read back compares equal to the value that
        ///   would be written, and that is exactly what lets "write only where they differ" tell same from
        ///   different rather than churning every property on every run.
        /// </summary>
        public Boolean Equals(GraphProperty other)
        {
            return String.Equals(Key, other.Key, StringComparison.Ordinal) &&
                   String.Equals(TypeName, other.TypeName, StringComparison.Ordinal) &&
                   String.Equals(Text, other.Text, StringComparison.Ordinal);
        }

        public override Boolean Equals(Object? obj)
        {
            return obj is GraphProperty other && Equals(other);
        }

        public override Int32 GetHashCode()
        {
            return HashCode.Combine(
                Key == null ? 0 : StringComparer.Ordinal.GetHashCode(Key),
                TypeName == null ? 0 : StringComparer.Ordinal.GetHashCode(TypeName),
                Text == null ? 0 : StringComparer.Ordinal.GetHashCode(Text));
        }

        /// <summary>Whether the stored value differs from this one, which is the only reason to write.</summary>
        public Boolean DiffersFrom(GraphProperty stored)
        {
            return !String.Equals(TypeName, stored.TypeName, StringComparison.Ordinal) ||
                   !String.Equals(Text, stored.Text, StringComparison.Ordinal);
        }
    }

    /// <summary>One vertex to create: the entity's kind as the label, plus everything it carries.</summary>
    public sealed class VertexWrite
    {
        public VertexWrite(String label, IReadOnlyList<GraphProperty> properties)
        {
            Label = label;
            Properties = properties;
        }

        /// <summary>The element label, which is the entity's kind.</summary>
        public String Label { get; }

        /// <summary>The provider's namespaced properties plus this run's identity and claim properties.</summary>
        public IReadOnlyList<GraphProperty> Properties { get; }
    }

    /// <summary>One edge to create, typed by the relation type.</summary>
    public sealed class EdgeWrite
    {
        public EdgeWrite(Int32 sourceId, Int32 targetId, String edgeType, IReadOnlyList<GraphProperty> properties)
        {
            SourceId = sourceId;
            TargetId = targetId;
            EdgeType = edgeType;
            Properties = properties;
        }

        /// <summary>The element the edge leaves.</summary>
        public Int32 SourceId { get; }

        /// <summary>The element the edge enters.</summary>
        public Int32 TargetId { get; }

        /// <summary>The edge's type: the adjacency group traversals key on.</summary>
        public String EdgeType { get; }

        /// <summary>The derived identity key and this run's claim.</summary>
        public IReadOnlyList<GraphProperty> Properties { get; }
    }

    /// <summary>One property set or removal on one element.</summary>
    public sealed class PropertyWrite
    {
        private PropertyWrite(Int32 elementId, String key, String? typeName, String? text, Boolean remove)
        {
            ElementId = elementId;
            Key = key;
            TypeName = typeName;
            Text = text;
            Remove = remove;
        }

        /// <summary>Sets a property to a value.</summary>
        public static PropertyWrite Set(Int32 elementId, GraphProperty property)
        {
            return new PropertyWrite(elementId, property.Key, property.TypeName, property.Text, false);
        }

        /// <summary>Removes a property. Removing an absent one is a committed no-op, which is what makes a
        /// replayed withdrawal safe.</summary>
        public static PropertyWrite Remove_(Int32 elementId, String key)
        {
            return new PropertyWrite(elementId, key, null, null, true);
        }

        /// <summary>The element to write to.</summary>
        public Int32 ElementId { get; }

        /// <summary>The property key.</summary>
        public String Key { get; }

        /// <summary>The literal type name, null for a removal.</summary>
        public String? TypeName { get; }

        /// <summary>The invariant text form, null for a removal.</summary>
        public String? Text { get; }

        /// <summary>Whether this removes the property rather than setting it.</summary>
        public Boolean Remove { get; }
    }

    /// <summary>One (index, key, element) entry to add.</summary>
    public readonly struct IndexEntry
    {
        public IndexEntry(String indexId, String key, Int32 elementId)
        {
            IndexId = indexId;
            Key = key;
            ElementId = elementId;
        }

        /// <summary>Which index.</summary>
        public String IndexId { get; }

        /// <summary>The key: a claim key in the identity index, an instance id in the claim index.</summary>
        public String Key { get; }

        /// <summary>The element the key should find.</summary>
        public Int32 ElementId { get; }

        public override String ToString()
        {
            return String.Format(CultureInfo.InvariantCulture, "{0}[{1}] -> {2}", IndexId, Key, ElementId);
        }
    }

    /// <summary>What an index write did, including what it declined.</summary>
    public sealed class IndexWriteOutcome
    {
        public IndexWriteOutcome(Int32 accepted, ImmutableArray<IndexEntry> declined)
        {
            Accepted = accepted;
            Declined = declined;
        }

        /// <summary>How many entries the index took.</summary>
        public Int32 Accepted { get; }

        /// <summary>
        ///   The entries the index refused. Each becomes a <c>claimNotIndexed</c> diagnostic, because an
        ///   element findable by none of its claims is duplicated on the next resolve.
        /// </summary>
        public ImmutableArray<IndexEntry> Declined { get; }

        /// <summary>Nothing was written and nothing was refused.</summary>
        public static IndexWriteOutcome Empty { get; } = new IndexWriteOutcome(0, ImmutableArray<IndexEntry>.Empty);
    }

    /// <summary>What a repair pass restored, per index, so the report can say it happened.</summary>
    public sealed class IndexRepairOutcome
    {
        public IndexRepairOutcome(Int32 identityEntries, Int32 claimEntries)
        {
            IdentityEntries = identityEntries;
            ClaimEntries = claimEntries;
        }

        /// <summary>How many identity entries the backfill restored.</summary>
        public Int32 IdentityEntries { get; }

        /// <summary>How many claim entries the backfill restored.</summary>
        public Int32 ClaimEntries { get; }
    }

    /// <summary>
    ///   The target's durability posture, reduced to the one question this runtime asks: may it DELETE?
    ///   Deferring is recoverable; deleting wrongly is not.
    /// </summary>
    public sealed class TargetDurability
    {
        public TargetDurability(Boolean writesReachDisk, Boolean lastRecoveryTruncated, Int32 droppedIndices)
        {
            WritesReachDisk = writesReachDisk;
            LastRecoveryTruncated = lastRecoveryTruncated;
            DroppedIndices = droppedIndices;
        }

        /// <summary>
        ///   Whether writes are actually reaching disk. False means transactions still commit in memory and
        ///   still report success while not being durable, which is the only signal a caller gets.
        /// </summary>
        public Boolean WritesReachDisk { get; }

        /// <summary>Whether the served graph is a PREFIX of committed history rather than all of it.</summary>
        public Boolean LastRecoveryTruncated { get; }

        /// <summary>How many indices the last checkpoint dropped, which is how a caller learns it must repair.</summary>
        public Int32 DroppedIndices { get; }

        /// <summary>
        ///   Whether deletion may proceed: writes reaching disk, the last recovery not truncated, and the
        ///   last checkpoint dropping no indices.
        /// </summary>
        public Boolean SafeToDelete => WritesReachDisk && !LastRecoveryTruncated && DroppedIndices == 0;

        /// <summary>Why deletion is unsafe, for the deferral diagnostic. Empty when it is safe.</summary>
        public String Reason()
        {
            if (SafeToDelete)
            {
                return String.Empty;
            }

            var reasons = new List<String>(3);
            if (!WritesReachDisk)
            {
                reasons.Add("writes are not reaching disk");
            }

            if (LastRecoveryTruncated)
            {
                reasons.Add("the last recovery was truncated, so the graph is a prefix of committed history");
            }

            if (DroppedIndices > 0)
            {
                reasons.Add(String.Format(CultureInfo.InvariantCulture,
                    "the last checkpoint dropped {0} index/indices, so the claim state judged here may be incomplete",
                    DroppedIndices));
            }

            return String.Join("; ", reasons);
        }

        /// <summary>A healthy posture, for a target with no durability of its own to report.</summary>
        public static TargetDurability Healthy { get; } = new TargetDurability(true, false, 0);
    }

    /// <summary>One entity summary to embed, on the element that entity resolved to.</summary>
    public readonly struct SummaryWrite
    {
        public SummaryWrite(Int32 elementId, String text)
        {
            ElementId = elementId;
            Text = text;
        }

        /// <summary>The element the summary describes.</summary>
        public Int32 ElementId { get; }

        /// <summary>The rendered summary text.</summary>
        public String Text { get; }
    }

    /// <summary>
    ///   What the target can embed. Dimension and metric are the TARGET'S, read from what it publishes, because a
    ///   runtime that hardcoded either would write vectors a bound index refuses the day the model changes.
    /// </summary>
    public sealed class TargetEmbedding
    {
        public TargetEmbedding(Boolean available, Int32 dimension, String? intendedMetric, String? model,
            String? reason)
        {
            Available = available;
            Dimension = dimension;
            IntendedMetric = intendedMetric;
            Model = model;
            Reason = reason;
        }

        /// <summary>Whether the target can embed text at all right now.</summary>
        public Boolean Available { get; }

        /// <summary>The dimension the target's provider produces.</summary>
        public Int32 Dimension { get; }

        /// <summary>The metric the target's provider intends its vectors to be ranked by.</summary>
        public String? IntendedMetric { get; }

        /// <summary>The model the target's provider serves.</summary>
        public String? Model { get; }

        /// <summary>Why it is unavailable, for the degradation diagnostic.</summary>
        public String? Reason { get; }

        /// <summary>The absent posture: no provider wired, or the capability switched off.</summary>
        public static TargetEmbedding Absent(String reason)
        {
            return new TargetEmbedding(false, 0, null, null, reason);
        }
    }

    /// <summary>What an embedding write did, or why it degraded to absent.</summary>
    public sealed class EmbeddingWriteOutcome
    {
        public EmbeddingWriteOutcome(Int32 written, String? degraded)
        {
            Written = written;
            Degraded = degraded;
        }

        /// <summary>How many summaries were embedded, which is a count of what LANDED rather than of what was
        /// asked for: the write is sent in chunks, so this can be short of the batch and still non-zero.</summary>
        public Int32 Written { get; }

        /// <summary>
        ///   Why the rest was not embedded, when some or all of it was not. Null when the whole batch landed.
        ///   Read WITH <see cref="Written" /> and never instead of it: a degraded write is not the same as an
        ///   empty one, because the chunks that already landed put real vectors on real elements.
        /// </summary>
        public String? Degraded { get; }

        /// <summary>Nothing to embed, which is not a degradation.</summary>
        public static EmbeddingWriteOutcome None { get; } = new EmbeddingWriteOutcome(0, null);
    }

    /// <summary>
    ///   One element's current state, as the write path needs it: what it holds, what it claims, and by which
    ///   claim keys it is known. Deliberately no adjacency: this answers "what does it hold now", which is
    ///   what "write only if something actually changed" requires.
    /// </summary>
    public sealed class ElementState
    {
        public ElementState(Int32 id, String? label, ImmutableDictionary<String, GraphProperty> properties)
        {
            Id = id;
            Label = label;
            Properties = properties;
        }

        /// <summary>The element id, valid only within this call sequence.</summary>
        public Int32 Id { get; }

        /// <summary>The element label.</summary>
        public String? Label { get; }

        /// <summary>Every property, keyed by property key.</summary>
        public ImmutableDictionary<String, GraphProperty> Properties { get; }

        /// <summary>
        ///   Whether the named instance asserts this element IN THIS SCOPE exactly. A null or empty
        ///   scope asks about the unscoped claim, which is a different property from any scoped one.
        ///
        ///   <para>This is the question the write path and reconciliation ask, because a run may only
        ///   add or withdraw its OWN scope's claim. For "may this run touch the element at all", which
        ///   is a question about the identity rather than the scope, see
        ///   <see cref="IsClaimedByIdentity"/>.</para>
        /// </summary>
        public Boolean IsClaimedBy(String instanceId, String? scope = null)
        {
            return Properties.ContainsKey(ClaimSchema.ClaimProperty(instanceId, scope));
        }

        /// <summary>
        ///   Whether the named instance asserts this element under ANY scope, or unscoped.
        ///
        ///   <para>THIS is what decides whether a run may write to an element, and it is deliberately
        ///   scope-blind: two scopes of one source routinely describe the same element - a signal
        ///   carried on two buses is the case this exists for - and the second scope's run must be
        ///   allowed to write to what the first one created. Asking the scoped question here would
        ///   make every shared element unwritable by all but the scope that happened to create it.</para>
        /// </summary>
        public Boolean IsClaimedByIdentity(String instanceId)
        {
            foreach (var key in Properties.Keys)
            {
                if (String.Equals(ClaimSchema.ClaimantOf(key), instanceId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Whether ANY instance asserts this element.</summary>
        public Boolean HasAnyClaim()
        {
            foreach (var key in Properties.Keys)
            {
                if (ClaimSchema.IsClaimProperty(key))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Every canonical claim key this element carries.</summary>
        public IEnumerable<String> IdentityKeys()
        {
            foreach (var property in Properties)
            {
                if (ClaimSchema.IsIdentityProperty(property.Key))
                {
                    yield return property.Value.Text;
                }
            }
        }

        /// <summary>
        ///   The next free identity ordinal. Derived from the highest ordinal present rather than from the
        ///   count, so a gap left by any earlier writer cannot make a new claim overwrite an existing one.
        /// </summary>
        public Int32 NextIdentityOrdinal()
        {
            var next = 0;
            foreach (var key in Properties.Keys)
            {
                if (!ClaimSchema.IsIdentityProperty(key))
                {
                    continue;
                }

                var suffix = key.Substring(ClaimSchema.IdentityPrefix.Length);
                if (Int32.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ordinal) &&
                    ordinal >= next)
                {
                    next = ordinal + 1;
                }
            }

            return next;
        }
    }

    /// <summary>
    ///   THE ONE HOME of the in-scope rule, which decides whether an element found by a claim key is one this
    ///   run may write to.
    ///
    ///   <para>In scope means the element carries this instance's claim, OR carries no claim property at all.
    ///   THE UNCLAIMED ARM IS LOAD-BEARING, not lax: it is the orphan-reclaim path. A withdrawal removes only
    ///   the claim property and the deletion that follows can be deferred under degraded durability, leaving
    ///   an element carrying this instance's identity claims and no claim; excluding it makes that element
    ///   invisible forever and the graph gains a duplicate on every run, permanently.</para>
    ///
    ///   <para>Scope is read from element STATE, never by intersecting with the claim index: that index has
    ///   no remove path and so answers "ever claimed", and using it would re-attach this instance to an
    ///   element it deliberately abandoned.</para>
    /// </summary>
    public static class ElementScope
    {
        /// <summary>Whether this run may write to the element.</summary>
        public static Boolean IsInScope(ElementState element, String instanceId)
        {
            if (element == null)
            {
                return false;
            }

            // Scope-blind ON PURPOSE: see IsClaimedByIdentity. A run must be able to write to an
            // element another scope of the SAME identity created, or a shared element would be
            // writable only by whichever scope reached it first.
            return element.IsClaimedByIdentity(instanceId) || !element.HasAnyClaim();
        }
    }
}
