// MIT License
//
// DiagnosticCodes.cs
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

namespace NoSQL.GraphDB.Integrations.Contract
{
    /// <summary>
    ///   Every diagnostic code this runtime can put on a report, in one place, because a code is a
    ///   stable contract with whoever reads a report: a test asserts on it, the Studio groups by it, and
    ///   an author greps for it. Grouped by who raises it.
    /// </summary>
    public static class DiagnosticCodes
    {
        // --- Envelope: fatal, because applying part of a document whose envelope is broken would be
        // guessing. Each leaves the whole document unapplied and withdraws nothing. ----------------

        /// <summary>The document declares a schema version this contract does not implement.</summary>
        public const String UnsupportedSchemaVersion = "unsupportedSchemaVersion";

        /// <summary>No provider id.</summary>
        public const String MissingProviderId = "missingProviderId";

        /// <summary>No integration instance id.</summary>
        public const String MissingInstanceId = "missingInstanceId";

        /// <summary>
        ///   An integration instance id whose SHAPE could compose another identity's key. Its own code, because
        ///   "you sent nothing" and "you sent something dangerous" are different things for a caller to fix.
        /// </summary>
        public const String MalformedInstanceId = "malformedInstanceId";

        /// <summary>A captured-at that is not an instant.</summary>
        public const String MalformedCapturedAt = "malformedCapturedAt";

        /// <summary>No completeness declaration at all.</summary>
        public const String MissingCompleteness = "missingCompleteness";

        /// <summary>A completeness word that is neither <c>complete</c> nor <c>partial</c>.</summary>
        public const String UnknownCompleteness = "unknownCompleteness";

        /// <summary>
        ///   A provider whose descriptor says it cannot observe complete state returned a snapshot
        ///   marked complete. Refused rather than trusted, because the consequence is the worst
        ///   available: every unobserved element becomes a withdrawal and the graph deletes what the
        ///   source still has.
        /// </summary>
        public const String CompletenessOverDeclared = "completenessOverDeclared";

        // --- Entity level: exactly one entity is skipped, so one bad row of a spreadsheet does not
        // leave every other entity unobserved. -----------------------------------------------------

        /// <summary>The wrapper diagnostic naming the entity that was skipped and why.</summary>
        public const String EntitySkipped = "entitySkipped";

        /// <summary>An entity with no kind.</summary>
        public const String MissingEntityKind = "missingEntityKind";

        /// <summary>
        ///   An entity left with no usable identity claim, INCLUDING one whose every claim was dropped. This is
        ///   what the claim-level drops fall back to, and it is the case the entity-level reading was protecting.
        /// </summary>
        public const String EntityWithoutIdentity = "entityWithoutIdentity";

        /// <summary>Two claims of one entity composing the same claim key. The repeat is dropped.</summary>
        public const String DuplicateClaimWithinEntity = "duplicateClaimWithinEntity";

        /// <summary>
        ///   A claim naming an identifier type the vocabulary does not have. THE CLAIM is dropped, not the
        ///   entity: see <see cref="InvalidIdentifierValue"/> for why.
        /// </summary>
        public const String UnknownIdentifierType = "unknownIdentifierType";

        /// <summary>
        ///   A claim value that fails its type's accept pattern. THE CLAIM is dropped and the entity is KEPT,
        ///   because the value is a datum the source typed rather than a statement the provider's code made: a
        ///   human-edited hostname or an IPv6-only lease would otherwise skip an entity whose MAC is perfectly
        ///   good, and a skipped entity in a complete snapshot is withdrawn and then deleted.
        /// </summary>
        public const String InvalidIdentifierValue = "invalidIdentifierValue";

        /// <summary>A declared strength that is neither <c>weak</c> nor <c>strong</c>.</summary>
        public const String UnknownStrengthWord = "unknownStrengthWord";

        /// <summary>A declared strength the vocabulary disagrees with.</summary>
        public const String DeclaredStrengthMismatch = "declaredStrengthMismatch";

        /// <summary>A provider-supplied property key beginning with the reserved sigil.</summary>
        public const String ReservedPropertyKey = "reservedPropertyKey";

        /// <summary>A property key carrying no provider prefix.</summary>
        public const String UnprefixedPropertyKey = "unprefixedPropertyKey";

        /// <summary>
        ///   A property value of a shape the property surface cannot carry (an object, an array, a
        ///   provider's own class). The property is dropped and the entity is KEPT: dropping one property
        ///   loses less than skipping an identifiable entity, and this names which one went.
        /// </summary>
        public const String UnsupportedPropertyValue = "unsupportedPropertyValue";

        /// <summary>A relation with no type.</summary>
        public const String MissingRelationType = "missingRelationType";

        /// <summary>A relation with no target claim.</summary>
        public const String MissingRelationTarget = "missingRelationTarget";

        /// <summary>
        ///   A relation addressed by a weak target. An ERROR rather than a warning because there is no
        ///   reading that does what its author meant: the edge would either be dropped or attach to
        ///   whichever element last held the value.
        /// </summary>
        public const String WeakRelationTarget = "weakRelationTarget";

        /// <summary>
        ///   A relation addressing its target by an identifier type the vocabulary does not have. The ENTITY is
        ///   skipped, like every other fault in the provider's own code, and this has its own code so that one
        ///   code never means two different consequences to a reader grouping by it.
        /// </summary>
        public const String UnknownRelationTargetType = "unknownRelationTargetType";

        /// <summary>
        ///   A relation whose target VALUE does not canonicalise. THE RELATION is dropped and the entity kept,
        ///   by the same datum-versus-statement rule as <see cref="InvalidIdentifierValue"/>: a mangled address
        ///   in one topology cell must not delete a device whose own identity is fine.
        /// </summary>
        public const String InvalidRelationTargetValue = "invalidRelationTargetValue";

        /// <summary>
        ///   A WARNING, not an error: an entity carrying no strong claim is still reported, because
        ///   reporting a weakly known thing is legitimate and an entity-level error would drop it from
        ///   the run entirely. The author needs to know nothing can ever resolve to it.
        /// </summary>
        public const String WeakOnlyIdentity = "weakOnlyIdentity";

        // --- The write path and reconciliation. ---------------------------------------------------

        /// <summary>More than one of this instance's own elements matched one entity.</summary>
        public const String DuplicateClaimedElements = "duplicateClaimedElements";

        /// <summary>A relation whose target is not an element this instance claims.</summary>
        public const String DroppedRelation = "droppedRelation";

        /// <summary>More than one of this instance's own elements carried a relation's target key.</summary>
        public const String AmbiguousRelationTarget = "ambiguousRelationTarget";

        /// <summary>
        ///   An index write the platform declined. Never merely informational: an element findable by
        ///   none of its claims is duplicated on the next resolve.
        /// </summary>
        public const String ClaimNotIndexed = "claimNotIndexed";

        /// <summary>
        ///   A claim an element carried as a PROPERTY that the claim index did not name, re-asserted by this run:
        ///   the fingerprint of an earlier run interrupted between its creates and its index write, which used to
        ///   be permanent. <see cref="Run.SnapshotApplier"/> owns which shapes of that state the heal reaches and
        ///   which two it leaves to an index rebuild.
        /// </summary>
        public const String ClaimReindexed = "claimReindexed";

        /// <summary>
        ///   Two entities in ONE snapshot asserted the same strong claim, so the snapshot says they are one
        ///   thing. They converge onto a single element by design; this reports it, because the two entities'
        ///   properties then overwrite each other and every run issues writes over an unchanged source, whose
        ///   only other symptom is unexplained churn.
        /// </summary>
        public const String CollidingStrongClaim = "collidingStrongClaim";

        /// <summary>An index had to be created or repaired from element state before it could be trusted.</summary>
        public const String IdentityIndexRebuilt = "identityIndexRebuilt";

        /// <summary>Reconciliation was skipped because the claim index was missing.</summary>
        public const String ReconciliationDeferred = "reconciliationDeferred";

        /// <summary>Deletion was deferred because the target's durability is not safe to delete on.</summary>
        public const String DeletionDeferredUnsafeDurability = "deletionDeferredUnsafeDurability";

        /// <summary>
        ///   Both halves of the embedding opt-in were set and the target cannot embed, so the summaries are
        ///   ABSENT rather than the run being broken. This is the whole of this runtime's dependence on the AI
        ///   capabilities.
        /// </summary>
        public const String SummaryEmbeddingUnavailable = "summaryEmbeddingUnavailable";

        // --- The shipped blueprints. Provider diagnostics live with the runtime's because a reader of
        // a report should not have to know which side raised one. ----------------------------------

        /// <summary><c>csv-device-list</c>: a row with no MAC address.</summary>
        public const String RowWithoutMac = "rowWithoutMac";

        /// <summary><c>csv-device-list</c>: the same MAC on more than one row.</summary>
        public const String DuplicateMacInFile = "duplicateMacInFile";

        /// <summary>
        ///   <c>csv-device-list</c>: a quoted field left open at the end of a line. A newline inside a quoted
        ///   field is unsupported, and the row is reported as the row it LOOKS like rather than silently
        ///   mis-parsed, because a silent mis-parse moves one row's cells into another row's columns.
        /// </summary>
        public const String UnterminatedQuotedField = "unterminatedQuotedField";

        /// <summary><c>unifi-network</c>: a device listed and then gone before its details were read.</summary>
        public const String DeviceRemovedDuringRun = "deviceRemovedDuringRun";

        /// <summary><c>unifi-network</c>: VPN and Teleport clients, which carry no hardware identity.</summary>
        public const String ClientsWithoutHardwareIdentity = "clientsWithoutHardwareIdentity";

        /// <summary><c>unifi-network</c>: a client with no id, which breaks the vendor's own contract.</summary>
        public const String ClientWithoutId = "clientWithoutId";

        /// <summary><c>fronius-solar</c>: the base URL names a host rather than an IPv4 literal, so no
        /// address claim is asserted at all.</summary>
        public const String AddressIsNotAnIpv4Literal = "addressIsNotAnIpv4Literal";

        /// <summary><c>fronius-solar</c>: an inverter with no UniqueID.</summary>
        public const String InverterWithoutUniqueId = "inverterWithoutUniqueId";

        /// <summary><c>fronius-solar</c>: <c>GetLoggerInfo</c> failed the documented way, which is a fact
        /// about the device rather than a failed run.</summary>
        public const String LoggerInfoUnavailable = "loggerInfoUnavailable";

        /// <summary>
        ///   <c>autosar-arxml</c>: the extract references an AUTOSAR path it does not define, so whatever
        ///   pointed at it was dropped. The usual cause is a partial export: an extract referencing a
        ///   package it did not include.
        /// </summary>
        public const String ArxmlUnresolvedReference = "arxmlUnresolvedReference";

        /// <summary>
        ///   <c>autosar-arxml</c>: two elements compose one AUTOSAR reference path. The first was kept,
        ///   because keeping both would make which one wins depend on the order the file is written in.
        /// </summary>
        public const String ArxmlDuplicatePath = "arxmlDuplicatePath";

        /// <summary>
        ///   <c>autosar-arxml</c>: a port exists but declares no direction the reader understands, so the
        ///   flow edge is dropped rather than pointed by a guess. A guessed direction would invert a
        ///   sender and a receiver, and a wrong edge answers a query confidently.
        /// </summary>
        public const String ArxmlUndecidablePortDirection = "arxmlUndecidablePortDirection";
    }
}
