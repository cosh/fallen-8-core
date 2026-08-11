// MIT License
//
// ValidatedSnapshot.cs
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
using System.Text.Json.Serialization;
using NoSQL.GraphDB.Integrations.Contract;
using NoSQL.GraphDB.Integrations.Graph;
using NoSQL.GraphDB.Integrations.Identity;

namespace NoSQL.GraphDB.Integrations.Validation
{
    /// <summary>
    ///   What the validator hands the write path: the envelope's verdict, the entities that survived, and
    ///   every diagnostic raised. The split is the design - an ENVELOPE error leaves the whole document
    ///   unapplied, because applying part of a document whose envelope is broken would be guessing, while
    ///   an ENTITY error skips exactly one entity, because one bad row of a spreadsheet should not leave
    ///   every other entity unobserved.
    /// </summary>
    public sealed class ValidatedSnapshot
    {
        internal ValidatedSnapshot(Boolean envelopeAccepted, SnapshotCompleteness completeness,
            String? providerId, String? instanceId, ImmutableArray<ValidatedEntity> entities,
            ImmutableArray<DiagnosticDto> diagnostics)
        {
            EnvelopeAccepted = envelopeAccepted;
            Completeness = completeness;
            ProviderId = providerId;
            InstanceId = instanceId;
            Entities = entities;
            Diagnostics = diagnostics;
        }

        /// <summary>Whether the envelope may be acted on at all. False means nothing is applied.</summary>
        [JsonPropertyName("envelopeAccepted")]
        public Boolean EnvelopeAccepted { get; }

        /// <summary>The declaration that licenses reconciliation, once the envelope is accepted.</summary>
        [JsonPropertyName("completeness")]
        public SnapshotCompleteness Completeness { get; }

        /// <summary>The provider the document names.</summary>
        [JsonPropertyName("providerId")]
        public String? ProviderId { get; }

        /// <summary>The identity the document asserts as.</summary>
        [JsonPropertyName("integrationInstanceId")]
        public String? InstanceId { get; }

        /// <summary>The entities that survived entity-level validation, in document order.</summary>
        [JsonIgnore]
        public ImmutableArray<ValidatedEntity> Entities { get; }

        /// <summary>Everything a reader needs to know, envelope and entity level together.</summary>
        [JsonPropertyName("diagnostics")]
        public ImmutableArray<DiagnosticDto> Diagnostics { get; }

        /// <summary>How many entities were accepted, for the validate route's summary.</summary>
        [JsonPropertyName("acceptedEntities")]
        public Int32 AcceptedEntities => Entities.Length;

        /// <summary>How many entities were skipped, for the validate route's summary.</summary>
        [JsonPropertyName("skippedEntities")]
        public Int32 SkippedEntities { get; internal set; }
    }

    /// <summary>
    ///   One entity that survived validation: its claims already canonicalised and composed, its properties
    ///   already rendered for the wire, and its primary key already derived. Nothing downstream re-derives
    ///   any of that, which is what keeps the claim key's composition in one place.
    /// </summary>
    public sealed class ValidatedEntity
    {
        internal ValidatedEntity(Int32 documentIndex, String kind, ImmutableArray<ComposedClaim> claims,
            String primaryKey, ImmutableArray<GraphProperty> properties,
            ImmutableArray<ValidatedRelation> relations)
        {
            DocumentIndex = documentIndex;
            Kind = kind;
            Claims = claims;
            PrimaryKey = primaryKey;
            Properties = properties;
            Relations = relations;
        }

        /// <summary>Where it sat in the document, so a diagnostic can name it even with no identity.</summary>
        public Int32 DocumentIndex { get; }

        /// <summary>The element label to write.</summary>
        public String Kind { get; }

        /// <summary>Every claim, strong and weak, canonicalised and scoped.</summary>
        public ImmutableArray<ComposedClaim> Claims { get; }

        /// <summary>The strongest claim key, ties broken ordinally: what a relation addresses it by.</summary>
        public String PrimaryKey { get; }

        /// <summary>The properties to write, already rendered.</summary>
        public ImmutableArray<GraphProperty> Properties { get; }

        /// <summary>The edges to wire.</summary>
        public ImmutableArray<ValidatedRelation> Relations { get; }

        /// <summary>The claims that may resolve.</summary>
        public IEnumerable<ComposedClaim> StrongClaims()
        {
            foreach (var claim in Claims)
            {
                if (claim.IsStrong)
                {
                    yield return claim;
                }
            }
        }
    }

    /// <summary>One relation, its target already composed into the claim key it will be looked up by.</summary>
    public readonly struct ValidatedRelation
    {
        internal ValidatedRelation(String type, ComposedClaim target)
        {
            Type = type;
            Target = target;
        }

        /// <summary>The edge type.</summary>
        public String Type { get; }

        /// <summary>The target's claim. Always strong: a weak target is an entity-level error.</summary>
        public ComposedClaim Target { get; }
    }
}
