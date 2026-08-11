// MIT License
//
// SnapshotValidator.cs
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
using NoSQL.GraphDB.Integrations.Contract;
using NoSQL.GraphDB.Integrations.Graph;
using NoSQL.GraphDB.Integrations.Identity;

namespace NoSQL.GraphDB.Integrations.Validation
{
    /// <summary>
    ///   Judges one snapshot against the contract, and is reachable on its own at
    ///   <c>POST /integration/snapshot/validate</c> because an author writing a provider wants the verdict
    ///   on a document before wiring a source to it.
    ///
    ///   <para>The split between the two levels is load-bearing. An ENVELOPE error is fatal: the document
    ///   is left unapplied and nothing is withdrawn, because nothing in a document can be trusted whose one
    ///   field licensing deletion is absent. An ENTITY error skips exactly one entity and the rest of the
    ///   document still lands, because losing a whole run to one unidentifiable row would leave every later
    ///   row unobserved - and a skipped entity is simply not claimed this round, which is the honest answer
    ///   for a row nobody can identify.</para>
    /// </summary>
    public sealed class SnapshotValidator
    {
        private readonly IdentifierVocabulary _vocabulary;

        public SnapshotValidator(IdentifierVocabulary vocabulary)
        {
            _vocabulary = vocabulary ?? throw new ArgumentNullException(nameof(vocabulary));
        }

        /// <summary>
        ///   Validates a document.
        /// </summary>
        /// <param name="document">The snapshot a provider returned, or a caller pasted in.</param>
        /// <param name="descriptor">
        ///   The provider's descriptor when one is known. Supplying it enables the completeness-honesty
        ///   refusal: a provider whose descriptor says it cannot observe complete state and that returns a
        ///   snapshot marked complete is refused rather than trusted, because the consequence is the worst
        ///   available - every unobserved element becomes a withdrawal and the graph deletes what the source
        ///   still has. The validate route passes null, having no provider.
        /// </param>
        public ValidatedSnapshot Validate(SnapshotDocument document, ProviderDescriptor? descriptor = null)
        {
            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            var diagnostics = ImmutableArray.CreateBuilder<DiagnosticDto>();
            var envelopeAccepted = true;

            if (document.SchemaVersion != SnapshotDocument.CurrentSchemaVersion)
            {
                envelopeAccepted = false;
                diagnostics.Add(new DiagnosticDto(DiagnosticCodes.UnsupportedSchemaVersion,
                    String.Format(CultureInfo.InvariantCulture,
                        "This contract implements schemaVersion {0}; the document declares {1}.",
                        SnapshotDocument.CurrentSchemaVersion, document.SchemaVersion)));
            }

            if (String.IsNullOrWhiteSpace(document.ProviderId))
            {
                envelopeAccepted = false;
                diagnostics.Add(new DiagnosticDto(DiagnosticCodes.MissingProviderId,
                    "The document names no provider."));
            }

            if (String.IsNullOrWhiteSpace(document.IntegrationInstanceId))
            {
                envelopeAccepted = false;
                diagnostics.Add(new DiagnosticDto(DiagnosticCodes.MissingInstanceId,
                    "The document names no integration instance."));
            }
            else if (!ClaimSchema.IsValidInstanceId(document.IntegrationInstanceId))
            {
                // The id is substituted into a property key and into every instance-scoped claim key, so a
                // shape that could compose another identity's key is refused at the envelope, exactly as
                // the job's own id is refused before a provider runs.
                envelopeAccepted = false;
                diagnostics.Add(new DiagnosticDto(DiagnosticCodes.MalformedInstanceId,
                    String.Format(
                        "'{0}' is not a valid integration instance id: letters, digits, dot, dash and " +
                        "underscore only, at most {1} characters.",
                        document.IntegrationInstanceId, ClaimSchema.MaxInstanceIdLength),
                    document.IntegrationInstanceId));
            }

            if (!String.IsNullOrWhiteSpace(document.CapturedAt) &&
                !DateTimeOffset.TryParse(document.CapturedAt, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out _))
            {
                envelopeAccepted = false;
                diagnostics.Add(new DiagnosticDto(DiagnosticCodes.MalformedCapturedAt,
                    String.Format("'{0}' is not an instant.", document.CapturedAt), document.CapturedAt));
            }

            var completeness = SnapshotCompleteness.Unspecified;
            if (String.IsNullOrWhiteSpace(document.Completeness))
            {
                envelopeAccepted = false;
                diagnostics.Add(new DiagnosticDto(DiagnosticCodes.MissingCompleteness,
                    "The document declares no completeness, which is the one field that licenses a " +
                    "withdrawal, so nothing in it can be trusted."));
            }
            else if (!SnapshotCompletenessWords.TryParse(document.Completeness, out completeness))
            {
                envelopeAccepted = false;
                diagnostics.Add(new DiagnosticDto(DiagnosticCodes.UnknownCompleteness,
                    String.Format("'{0}' is neither '{1}' nor '{2}'.", document.Completeness,
                        SnapshotCompletenessWords.Complete, SnapshotCompletenessWords.Partial),
                    document.Completeness));
            }

            if (descriptor != null && !descriptor.CanObserveCompleteState &&
                completeness == SnapshotCompleteness.Complete)
            {
                envelopeAccepted = false;
                diagnostics.Add(new DiagnosticDto(DiagnosticCodes.CompletenessOverDeclared,
                    String.Format(
                        "Provider '{0}' declares that it cannot observe the source's whole state, so a " +
                        "snapshot marked complete is refused: acting on it would withdraw every element " +
                        "the run did not see and delete what the source still has.", descriptor.Id),
                    descriptor.Id));
            }

            if (!envelopeAccepted)
            {
                return new ValidatedSnapshot(false, completeness, document.ProviderId,
                    document.IntegrationInstanceId, ImmutableArray<ValidatedEntity>.Empty,
                    diagnostics.ToImmutable());
            }

            var entities = ImmutableArray.CreateBuilder<ValidatedEntity>();
            var skipped = 0;
            var source = document.Entities ?? new List<EntityDto>();

            for (var index = 0; index < source.Count; index++)
            {
                if (TryValidateEntity(source[index], index, document.ProviderId!, document.IntegrationInstanceId!,
                        diagnostics, out var entity))
                {
                    entities.Add(entity!);
                }
                else
                {
                    skipped++;
                }
            }

            return new ValidatedSnapshot(true, completeness, document.ProviderId, document.IntegrationInstanceId,
                entities.ToImmutable(), diagnostics.ToImmutable())
            {
                SkippedEntities = skipped
            };
        }

        /// <summary>
        ///   Validates one entity, appending every reason it failed plus one <c>entitySkipped</c> summary.
        ///   Every reason is reported rather than only the first, because an author fixing a provider wants
        ///   the whole list.
        /// </summary>
        private Boolean TryValidateEntity(EntityDto? entity, Int32 index, String providerId, String instanceId,
            ImmutableArray<DiagnosticDto>.Builder diagnostics, out ValidatedEntity? validated)
        {
            validated = null;
            var subject = Subject(entity, index);
            var reasons = 0;

            void Reject(String code, String message)
            {
                reasons++;
                diagnostics.Add(new DiagnosticDto(code, message, subject));
            }

            // A DATUM the source typed wrongly costs that datum; a statement the PROVIDER'S CODE makes wrongly
            // costs the entity. The split is not cosmetic: both shipped blueprints hit the other reading and it
            // deletes data. A human-typed hostname column that fails its accept pattern, or a client that picked
            // up an IPv6-only lease, would skip an entity whose MAC is perfectly good, and a skipped entity in a
            // complete snapshot is withdrawn and then deleted - one typo in a non-identifying column removing a
            // device. Dropping the claim keeps the diagnostic loud (it is never a silent drop) while leaving
            // every identity the source got right, and an entity whose every claim was dropped still fails as
            // entityWithoutIdentity, which is the case the strict reading was protecting.
            void Drop(String code, String message)
            {
                diagnostics.Add(new DiagnosticDto(code, message, subject));
            }

            if (entity == null)
            {
                Reject(DiagnosticCodes.MissingEntityKind, "The entity is null.");
                diagnostics.Add(Skipped(subject, reasons));
                return false;
            }

            if (String.IsNullOrWhiteSpace(entity.Kind))
            {
                Reject(DiagnosticCodes.MissingEntityKind, "The entity declares no kind.");
            }

            var claims = ImmutableArray.CreateBuilder<ComposedClaim>();
            var seenKeys = new HashSet<String>(StringComparer.Ordinal);
            var rawClaims = entity.Claims ?? new List<IdentityClaimDto>();

            for (var i = 0; i < rawClaims.Count; i++)
            {
                var claim = rawClaims[i];
                if (claim == null || String.IsNullOrWhiteSpace(claim.Type))
                {
                    Drop(DiagnosticCodes.UnknownIdentifierType, "A claim names no identifier type and was dropped.");
                    continue;
                }

                if (!_vocabulary.TryGet(claim.Type, out var identifier))
                {
                    Drop(DiagnosticCodes.UnknownIdentifierType, String.Format(
                        "'{0}' is not an identifier type this runtime knows, so nothing could ever resolve by " +
                        "it and the claim was dropped. An entity left with no claim at all fails as " +
                        "entityWithoutIdentity rather than being created again on every run.", claim.Type));
                    continue;
                }

                if (!String.IsNullOrWhiteSpace(claim.DeclaredStrength))
                {
                    if (!IdentifierVocabulary.StrengthWords.TryParse(claim.DeclaredStrength, out var declared))
                    {
                        Reject(DiagnosticCodes.UnknownStrengthWord, String.Format(
                            "Claim '{0}' declares strength '{1}', which is neither '{2}' nor '{3}'.",
                            claim.Type, claim.DeclaredStrength, IdentifierVocabulary.StrengthWords.Weak,
                            IdentifierVocabulary.StrengthWords.Strong));
                        continue;
                    }

                    if (declared != identifier!.Strength)
                    {
                        Reject(DiagnosticCodes.DeclaredStrengthMismatch, String.Format(
                            "Claim '{0}' declares strength '{1}' but the vocabulary says '{2}'. A provider " +
                            "declares a type and never a strength; the declaration is accepted only so this " +
                            "disagreement can be rejected.",
                            claim.Type, IdentifierVocabulary.StrengthWords.ToWord(declared),
                            IdentifierVocabulary.StrengthWords.ToWord(identifier.Strength)));
                        continue;
                    }
                }

                if (!ClaimKeyComposer.TryCompose(identifier!, claim.Value, providerId, instanceId,
                        out var key, out var failure))
                {
                    // MissingScope is unreachable here: the envelope check above guarantees both ids, and a
                    // global-scoped type needs neither. It is still reported honestly rather than assumed.
                    Drop(DiagnosticCodes.InvalidIdentifierValue, failure == ClaimKeyFailure.MissingScope
                        ? String.Format("Claim '{0}' is {1}-scoped and no {1} id was available, so it was dropped.",
                            claim.Type, identifier.Scope.ToString().ToLowerInvariant())
                        : String.Format("Claim '{0}' value '{1}' does not canonicalise to something the type " +
                            "accepts ({2}), so it was dropped; every other claim of this entity still stands.",
                            claim.Type, claim.Value, identifier.Accept));
                    continue;
                }

                if (!seenKeys.Add(key!))
                {
                    Drop(DiagnosticCodes.DuplicateClaimWithinEntity, String.Format(
                        "Two claims of this entity compose the same key '{0}'; the repeat was dropped, which " +
                        "leaves exactly the identity the source stated.", key));
                    continue;
                }

                claims.Add(new ComposedClaim(key!, identifier!.Type,
                    identifier.Canonicalise(claim.Value), identifier.Strength));
            }

            if (claims.Count == 0 && reasons == 0)
            {
                Reject(DiagnosticCodes.EntityWithoutIdentity,
                    "The entity carries no usable identity claim, so nothing could ever resolve to it and every " +
                    "run would create another copy.");
            }

            var properties = ImmutableArray.CreateBuilder<GraphProperty>();
            if (entity.Properties != null)
            {
                foreach (var property in entity.Properties)
                {
                    if (String.IsNullOrWhiteSpace(property.Key))
                    {
                        Reject(DiagnosticCodes.UnprefixedPropertyKey, "A property has no key.");
                        continue;
                    }

                    if (ClaimSchema.IsReserved(property.Key))
                    {
                        Reject(DiagnosticCodes.ReservedPropertyKey, String.Format(
                            "Property '{0}' begins with the reserved sigil '{1}': a provider writing one " +
                            "would be forging a claim or a claim set.", property.Key, ClaimSchema.ReservedSigil));
                        continue;
                    }

                    if (!IsProviderNamespaced(property.Key))
                    {
                        Reject(DiagnosticCodes.UnprefixedPropertyKey, String.Format(
                            "Property '{0}' carries no provider prefix. Two providers describing 'the name' " +
                            "of one device rarely mean the same thing, and an unprefixed key means the value " +
                            "depends on which integration ran last.", property.Key));
                        continue;
                    }

                    var rendered = WireValues.TryRender(property.Value, out var typeName, out var text);
                    if (rendered == WireValues.Outcome.Unsupported)
                    {
                        // A value of a shape the property surface cannot carry is reported and dropped:
                        // dropping one property loses less than skipping the entity, and the diagnostic names
                        // it. An ABSENT value is silently absent, whether it arrived as a CLR null or as JSON
                        // null, so a pasted document gets the same verdict as the provider that would have
                        // produced it.
                        diagnostics.Add(new DiagnosticDto(DiagnosticCodes.UnsupportedPropertyValue,
                            String.Format(
                                "Property '{0}' holds a value of a shape the property surface does not " +
                                "carry, so it was dropped. Properties are scalars.", property.Key),
                            subject));
                        continue;
                    }

                    if (rendered == WireValues.Outcome.Absent)
                    {
                        continue;
                    }

                    properties.Add(new GraphProperty(property.Key, typeName!, text!));
                }
            }

            var relations = ImmutableArray.CreateBuilder<ValidatedRelation>();
            var rawRelations = entity.Relations ?? new List<RelationDto>();
            for (var i = 0; i < rawRelations.Count; i++)
            {
                var relation = rawRelations[i];
                if (relation == null || String.IsNullOrWhiteSpace(relation.Type))
                {
                    Reject(DiagnosticCodes.MissingRelationType, "A relation declares no type.");
                    continue;
                }

                var target = relation.Target;
                if (target == null || String.IsNullOrWhiteSpace(target.Type) ||
                    String.IsNullOrWhiteSpace(target.Value))
                {
                    Reject(DiagnosticCodes.MissingRelationTarget, String.Format(
                        "Relation '{0}' addresses no target claim.", relation.Type));
                    continue;
                }

                if (!_vocabulary.TryGet(target.Type, out var targetType))
                {
                    // The entity goes, because the provider's CODE named a type that cannot address anything:
                    // it is the same class of fault as declaring a weak target, and its own code so a reader
                    // grouping by code is not told two different consequences under one name.
                    Reject(DiagnosticCodes.UnknownRelationTargetType, String.Format(
                        "Relation '{0}' addresses its target by '{1}', which is not an identifier type this " +
                        "runtime knows, so nothing could ever be found by it.", relation.Type, target.Type));
                    continue;
                }

                if (targetType!.Strength != IdentifierStrength.Strong)
                {
                    Reject(DiagnosticCodes.WeakRelationTarget, String.Format(
                        "Relation '{0}' addresses its target by the weak identifier '{1}'. There is no " +
                        "reading that does what this means: the edge would either be dropped or attach to " +
                        "whichever element last held the value.", relation.Type, target.Type));
                    continue;
                }

                if (!ClaimKeyComposer.TryCompose(targetType, target.Value, providerId, instanceId,
                        out var targetKey, out _))
                {
                    // THE RELATION goes and the entity stays, by the same datum-versus-statement rule the claim
                    // level follows: a mangled address in ONE cell of a topology column would otherwise skip a
                    // device whose own identity is perfectly good, and a skipped entity in a complete snapshot
                    // is withdrawn and then deleted. The edge is missing until the source says it better, which
                    // is a gap rather than a loss.
                    Drop(DiagnosticCodes.InvalidRelationTargetValue, String.Format(
                        "Relation '{0}' addresses target value '{1}', which does not canonicalise to " +
                        "something '{2}' accepts, so the relation was dropped and the entity kept.",
                        relation.Type, target.Value, target.Type));
                    continue;
                }

                relations.Add(new ValidatedRelation(relation.Type,
                    new ComposedClaim(targetKey!, targetType.Type, targetType.Canonicalise(target.Value),
                        targetType.Strength)));
            }

            if (reasons > 0)
            {
                diagnostics.Add(Skipped(subject, reasons));
                return false;
            }

            var composed = claims.ToImmutable();
            var hasStrong = false;
            foreach (var claim in composed)
            {
                if (claim.IsStrong)
                {
                    hasStrong = true;
                    break;
                }
            }

            if (!hasStrong)
            {
                // A WARNING, and the entity is kept: reporting a weakly known thing is legitimate, and an
                // entity-level error would drop it from the run entirely. The author needs to know nothing
                // can ever resolve to it, so a complete snapshot creates it again on every run and withdraws
                // the one the previous run created.
                diagnostics.Add(new DiagnosticDto(DiagnosticCodes.WeakOnlyIdentity,
                    "This entity carries no strong claim, so no run can ever resolve to the element it " +
                    "creates: every run creates another one and withdraws the last.", subject));
            }

            if (!ClaimKeyComposer.TryPrimaryKey(composed, out var primaryKey))
            {
                // Unreachable: an entity with no claim at all was rejected above.
                diagnostics.Add(new DiagnosticDto(DiagnosticCodes.EntityWithoutIdentity,
                    "The entity has no claim to derive a primary key from.", subject));
                diagnostics.Add(Skipped(subject, 1));
                return false;
            }

            validated = new ValidatedEntity(index, entity.Kind!, composed, primaryKey!,
                properties.ToImmutable(), relations.ToImmutable());
            return true;
        }

        /// <summary>
        ///   Whether a property key carries a provider prefix: a non-empty segment, then a dot, then a
        ///   non-empty remainder. The prefix is deliberately NOT compared with the provider id, because the
        ///   shipped prefixes are shorter than their ids on purpose (<c>unifi.model</c> from
        ///   <c>unifi-network</c>, <c>csv.name</c> from <c>csv-device-list</c>): the rule is that a key says
        ///   whose value it is, not that it repeats an id.
        /// </summary>
        private static Boolean IsProviderNamespaced(String key)
        {
            var dot = key.IndexOf('.');
            return dot > 0 && dot < key.Length - 1;
        }

        private static DiagnosticDto Skipped(String subject, Int32 reasons)
        {
            return new DiagnosticDto(DiagnosticCodes.EntitySkipped, String.Format(CultureInfo.InvariantCulture,
                "Entity skipped for {0} reason(s); it is simply not claimed this round.", reasons), subject);
        }

        /// <summary>
        ///   What a diagnostic calls an entity: its first claim if it has one, otherwise its position, so a
        ///   row nobody can identify is still findable in the source.
        /// </summary>
        private static String Subject(EntityDto? entity, Int32 index)
        {
            var claims = entity?.Claims;
            if (claims != null)
            {
                for (var i = 0; i < claims.Count; i++)
                {
                    var claim = claims[i];
                    if (claim != null && !String.IsNullOrWhiteSpace(claim.Type) &&
                        !String.IsNullOrWhiteSpace(claim.Value))
                    {
                        return claim.Type + "=" + claim.Value;
                    }
                }
            }

            return "entity#" + index.ToString(CultureInfo.InvariantCulture);
        }
    }
}
