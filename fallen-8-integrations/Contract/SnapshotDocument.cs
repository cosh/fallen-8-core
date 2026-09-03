// MIT License
//
// SnapshotDocument.cs
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
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.Integrations.Contract
{
    /// <summary>
    ///   What a provider returns: everything one observation of one source said, and nothing about what
    ///   the graph should do with it. It travels as camelCase JSON, which is what makes
    ///   <c>POST /integration/snapshot/validate</c> able to judge a document an author pasted in.
    ///
    ///   <para>Two fields carry their wire WORD rather than a parsed value -
    ///   <see cref="Completeness"/> and <see cref="IdentityClaimDto.DeclaredStrength"/> - because a
    ///   value that is neither of the words it may be has to become a named diagnostic rather than a
    ///   deserialization failure with no subject. Both have typed companions for a C# author.</para>
    /// </summary>
    public sealed class SnapshotDocument
    {
        /// <summary>The one supported contract version, honoured rather than assumed: a document from a
        /// later contract is refused instead of being read with the fields this one recognises.</summary>
        public const Int32 CurrentSchemaVersion = 1;

        /// <summary>The contract version this document was written against.</summary>
        [JsonPropertyName("schemaVersion")]
        public Int32 SchemaVersion { get; set; } = CurrentSchemaVersion;

        /// <summary>The provider that produced it.</summary>
        [JsonPropertyName("providerId")]
        public String? ProviderId { get; set; }

        /// <summary>The identity the run asserts as.</summary>
        [JsonPropertyName("integrationInstanceId")]
        public String? IntegrationInstanceId { get; set; }

        /// <summary>
        ///   When the source was observed, ISO 8601, optional. A string rather than a
        ///   <see cref="DateTimeOffset"/> so a malformed value is the <c>malformedCapturedAt</c>
        ///   diagnostic rather than a deserialization failure; <see cref="CapturedNow"/> sets it.
        /// </summary>
        [JsonPropertyName("capturedAt")]
        public String? CapturedAt { get; set; }

        /// <summary>The source's own version string, when it has one (a console's firmware, say).</summary>
        [JsonPropertyName("sourceVersion")]
        public String? SourceVersion { get; set; }

        /// <summary>
        ///   The completeness declaration as the word it arrived as: <c>complete</c> or <c>partial</c>.
        ///   Absent is the <c>missingCompleteness</c> diagnostic and any other word is
        ///   <c>unknownCompleteness</c>; both are ENVELOPE errors and therefore fatal, because nothing
        ///   in a document can be trusted whose one field licensing deletion is absent.
        /// </summary>
        [JsonPropertyName("completeness")]
        public String? Completeness { get; set; }

        /// <summary>Everything the source said it has.</summary>
        [JsonPropertyName("entities")]
        public IList<EntityDto> Entities { get; set; } = new List<EntityDto>();

        /// <summary>
        ///   What the source could not tell the run. These ride along into the job report's own
        ///   diagnostics list, so one report covers both what the source could not say and what the
        ///   graph could not be told.
        /// </summary>
        [JsonPropertyName("diagnostics")]
        public IList<DiagnosticDto> Diagnostics { get; set; } = new List<DiagnosticDto>();

        /// <summary>
        ///   The typed view of <see cref="Completeness"/> for a C# author:
        ///   <c>snapshot.Declares = SnapshotCompleteness.Complete</c>. Reading it yields
        ///   <see cref="SnapshotCompleteness.Unspecified"/> for both an absent and an unrecognised word,
        ///   which is why the validator reads the raw word and not this.
        /// </summary>
        [JsonIgnore]
        public SnapshotCompleteness Declares
        {
            get
            {
                SnapshotCompletenessWords.TryParse(Completeness, out var value);
                return value;
            }

            set => Completeness = SnapshotCompletenessWords.ToWord(value);
        }

        /// <summary>Stamps <see cref="CapturedAt"/> with the current UTC instant in round-trip form.</summary>
        public SnapshotDocument CapturedNow()
        {
            CapturedAt = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            return this;
        }
    }

    /// <summary>
    ///   Whether the snapshot describes the WHOLE source. This is the single field that licenses
    ///   reconciliation to withdraw a claim and, on the last claim, delete the element, so
    ///   <see cref="Unspecified"/> deliberately occupies the zero value: neither of the other two can
    ///   then be inherited by a provider that forgot to declare.
    /// </summary>
    public enum SnapshotCompleteness
    {
        /// <summary>Nothing was declared. Fails validation at the envelope level.</summary>
        Unspecified = 0,

        /// <summary>The snapshot describes the whole source, so an absence is a removal.</summary>
        Complete = 1,

        /// <summary>
        ///   The snapshot describes part of the source, so an absence means nothing. It exists before
        ///   any provider needs it, because it is the single field that keeps an event-driven provider
        ///   addable later without reopening the identity model: such a source produces the same
        ///   assertions incrementally and must be able to say "absence here means nothing", or its
        ///   first delivery would withdraw the entire graph it had built.
        /// </summary>
        Partial = 2,
    }

    /// <summary>The wire words of <see cref="SnapshotCompleteness"/>, in one place.</summary>
    public static class SnapshotCompletenessWords
    {
        /// <summary>The word for <see cref="SnapshotCompleteness.Complete"/>.</summary>
        public const String Complete = "complete";

        /// <summary>The word for <see cref="SnapshotCompleteness.Partial"/>.</summary>
        public const String Partial = "partial";

        /// <summary>
        ///   Parses a completeness word. Returns false for an unrecognised word (with
        ///   <paramref name="value"/> set to <see cref="SnapshotCompleteness.Unspecified"/>), which is
        ///   how the validator tells <c>unknownCompleteness</c> from <c>missingCompleteness</c>: the
        ///   latter is a null or blank word, which the caller checks first.
        /// </summary>
        public static Boolean TryParse(String? word, out SnapshotCompleteness value)
        {
            if (String.Equals(word, Complete, StringComparison.OrdinalIgnoreCase))
            {
                value = SnapshotCompleteness.Complete;
                return true;
            }

            if (String.Equals(word, Partial, StringComparison.OrdinalIgnoreCase))
            {
                value = SnapshotCompleteness.Partial;
                return true;
            }

            value = SnapshotCompleteness.Unspecified;
            return false;
        }

        /// <summary>The wire word of a value, or null for <see cref="SnapshotCompleteness.Unspecified"/>.</summary>
        public static String? ToWord(SnapshotCompleteness value)
        {
            switch (value)
            {
                case SnapshotCompleteness.Complete:
                    return Complete;
                case SnapshotCompleteness.Partial:
                    return Partial;
                default:
                    return null;
            }
        }
    }

    /// <summary>
    ///   One thing the source has: what kind of thing it is, how it is identified, what it holds, and
    ///   what it points at. There is deliberately nothing weaker than an entity in a snapshot - no
    ///   collection of non-asserted observations - because with nothing in this runtime unifying
    ///   elements such a collection would have no consumer.
    /// </summary>
    public sealed class EntityDto
    {
        /// <summary>What kind of thing this is. It becomes the element's label.</summary>
        [JsonPropertyName("kind")]
        public String? Kind { get; set; }

        /// <summary>Every identifier the source reported for it. Each becomes a claim on the element.</summary>
        [JsonPropertyName("claims")]
        public IList<IdentityClaimDto> Claims { get; set; } = new List<IdentityClaimDto>();

        /// <summary>
        ///   The provider's own properties, keyed with its prefix (<c>unifi.model</c>,
        ///   <c>fronius.status</c>, <c>csv.name</c>): two providers describing "the name" of one device
        ///   rarely mean the same thing, and an unprefixed key means the value depends on which
        ///   integration ran last. Values are CLR scalars (or, over the wire, JSON scalars); an ABSENT
        ///   value is absent, never an empty string, because writing empty makes the property exist and
        ///   overwrites what another integration knows.
        /// </summary>
        [JsonPropertyName("properties")]
        public IDictionary<String, Object?> Properties { get; set; }
            = new Dictionary<String, Object?>(StringComparer.Ordinal);

        /// <summary>What it points at, each target addressed by claim rather than by element id.</summary>
        [JsonPropertyName("relations")]
        public IList<RelationDto> Relations { get; set; } = new List<RelationDto>();

        /// <summary>
        ///   Writes one property, or nothing at all when the source did not answer: null is ABSENT, and so is
        ///   a string that is empty or nothing but whitespace, because a source answering with blanks did not
        ///   answer either. What getting that wrong costs is on <see cref="Properties"/>, and this is where
        ///   the whole snapshot contract decides it, so a provider needs no rule of its own.
        /// </summary>
        public void SetIfPresent(String key, Object? value)
        {
            if (IsPresent(value))
            {
                Properties[key] = value;
            }
        }

        /// <summary>Claims one identifier, or nothing at all for a value absent under
        /// <see cref="SetIfPresent"/>'s rule. What the value and the missing strength mean is on
        /// <see cref="IdentityClaimDto"/>.</summary>
        public void ClaimIfPresent(String type, String? value)
        {
            if (IsPresent(value))
            {
                Claims.Add(new IdentityClaimDto { Type = type, Value = value });
            }
        }

        /// <summary>Points at whatever carries one identifier value, or at nothing when the source named no
        /// target, under the same absence rule. Addressed by claim, per <see cref="ClaimReferenceDto"/>.</summary>
        public void RelateIfPresent(String relationType, String targetClaimType, String? targetValue)
        {
            if (IsPresent(targetValue))
            {
                Relations.Add(new RelationDto
                {
                    Type = relationType,
                    Target = new ClaimReferenceDto { Type = targetClaimType, Value = targetValue },
                });
            }
        }

        /// <summary>The one presence test the three writers above share.</summary>
        private static Boolean IsPresent(Object? value)
        {
            return value is String text ? !String.IsNullOrWhiteSpace(text) : value != null;
        }
    }

    /// <summary>
    ///   One statement that a source reported one identifier value for one entity. A provider declares
    ///   a TYPE and never a strength: the vocabulary is what says which types resolve, and a provider able
    ///   to call its own weak identifier strong makes an address resolve, so the run attaches its data to
    ///   whichever element last held that address.
    /// </summary>
    public sealed class IdentityClaimDto
    {
        /// <summary>The vocabulary identifier type, e.g. <c>mac</c>.</summary>
        [JsonPropertyName("type")]
        public String? Type { get; set; }

        /// <summary>
        ///   The value AS THE SOURCE REPORTED IT. The runtime canonicalises it, and a provider that
        ///   canonicalised first would be the second home of a rule that only works where there is exactly
        ///   one: what "the same value" means is the vocabulary's fold and nothing else.
        /// </summary>
        [JsonPropertyName("value")]
        public String? Value { get; set; }

        /// <summary>
        ///   Optional, and accepted ONLY so that a disagreement with the vocabulary can be rejected
        ///   (<c>declaredStrengthMismatch</c>): that is how an author who has misunderstood finds out
        ///   instead of shipping. A word that is neither <c>weak</c> nor <c>strong</c> is
        ///   <c>unknownStrengthWord</c>.
        /// </summary>
        [JsonPropertyName("declaredStrength")]
        public String? DeclaredStrength { get; set; }
    }

    /// <summary>
    ///   A reference to whatever carries one identifier value: how a relation names its target, so a
    ///   provider never needs to know whether the thing it points at exists yet or in which order its
    ///   entities are applied.
    /// </summary>
    public sealed class ClaimReferenceDto
    {
        /// <summary>The vocabulary identifier type of the target's identifier.</summary>
        [JsonPropertyName("type")]
        public String? Type { get; set; }

        /// <summary>The target's identifier value, as the source reported it.</summary>
        [JsonPropertyName("value")]
        public String? Value { get; set; }
    }

    /// <summary>One edge the source describes, from the entity carrying it to whatever the claim names.</summary>
    public sealed class RelationDto
    {
        /// <summary>The relation type. It becomes the edge's type.</summary>
        [JsonPropertyName("type")]
        public String? Type { get; set; }

        /// <summary>What the relation points at, addressed by claim rather than by element id.</summary>
        [JsonPropertyName("target")]
        public ClaimReferenceDto? Target { get; set; }
    }

    /// <summary>
    ///   Something a reader needs to know, with a stable code they can act on. A provider's ride along on
    ///   its snapshot into the job report's list, next to the ones the runtime raised; a report carries at
    ///   most <see cref="Run.DiagnosticBudget.PerCode"/> of any one code and the run's log carries every
    ///   one of them.
    /// </summary>
    public sealed class DiagnosticDto
    {
        public DiagnosticDto()
        {
        }

        /// <param name="code">The stable code, from <see cref="DiagnosticCodes"/>.</param>
        /// <param name="message">What happened, in a sentence a reader can act on.</param>
        /// <param name="subject">What it concerns: an entity's identifier, a file row, a device id.</param>
        public DiagnosticDto(String code, String message, String? subject = null)
        {
            Code = code;
            Message = message;
            Subject = subject;
        }

        /// <summary>The stable code.</summary>
        [JsonPropertyName("code")]
        public String? Code { get; set; }

        /// <summary>What happened.</summary>
        [JsonPropertyName("message")]
        public String? Message { get; set; }

        /// <summary>What it concerns.</summary>
        [JsonPropertyName("subject")]
        public String? Subject { get; set; }
    }
}
