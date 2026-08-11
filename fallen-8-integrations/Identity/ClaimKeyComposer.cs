// MIT License
//
// ClaimKeyComposer.cs
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

namespace NoSQL.GraphDB.Integrations.Identity
{
    /// <summary>
    ///   THE ONE PLACE a claim key is composed, because exact string equality on this key IS the resolution
    ///   rule, so the key must be the only comparison surface in the runtime. The value is canonicalised
    ///   BEFORE the key is composed, never after.
    /// </summary>
    public static class ClaimKeyComposer
    {
        /// <summary>The segment an edge's derived key begins with. Deliberately NOT a vocabulary type.</summary>
        public const String EdgeSegment = "edge";

        /// <summary>
        ///   Composes the claim key for one identifier value.
        ///
        ///   <para>A provider- or instance-scoped type composed without its scope segment is REFUSED
        ///   (<see cref="ClaimKeyFailure.MissingScope"/>) and never falls back to the global form: a global
        ///   fallback gives two installations' different values one key, which is the false equality the
        ///   scope field exists to prevent.</para>
        /// </summary>
        /// <param name="identifier">The vocabulary entry, which supplies the canonicaliser and the scope.</param>
        /// <param name="rawValue">The value as the source reported it.</param>
        /// <param name="providerId">The provider id, required for a provider-scoped type.</param>
        /// <param name="instanceId">The integration instance id, required for an instance-scoped type.</param>
        /// <param name="key">The composed key, when this returns true.</param>
        /// <param name="failure">Why it was refused, when this returns false.</param>
        public static Boolean TryCompose(IdentifierType identifier, String? rawValue, String? providerId,
            String? instanceId, out String? key, out ClaimKeyFailure failure)
        {
            if (identifier == null)
            {
                throw new ArgumentNullException(nameof(identifier));
            }

            key = null;

            if (!identifier.TryCanonicalise(rawValue, out var canonical))
            {
                failure = ClaimKeyFailure.InvalidValue;
                return false;
            }

            switch (identifier.Scope)
            {
                case IdentifierScope.Global:
                    key = identifier.Type + ":" + canonical;
                    break;

                case IdentifierScope.Provider:
                    if (String.IsNullOrEmpty(providerId))
                    {
                        failure = ClaimKeyFailure.MissingScope;
                        return false;
                    }

                    key = identifier.Type + "@" + providerId + ":" + canonical;
                    break;

                case IdentifierScope.Instance:
                    if (String.IsNullOrEmpty(instanceId))
                    {
                        failure = ClaimKeyFailure.MissingScope;
                        return false;
                    }

                    key = identifier.Type + "@" + instanceId + ":" + canonical;
                    break;

                default:
                    failure = ClaimKeyFailure.MissingScope;
                    return false;
            }

            failure = ClaimKeyFailure.None;
            return true;
        }

        /// <summary>
        ///   The DERIVED key of an edge: <c>edge:&lt;sourcePrimaryKey&gt;|&lt;edgeType&gt;|&lt;targetPrimaryKey&gt;</c>.
        ///
        ///   <para>An edge has no intrinsic identifier and the graph cannot answer "is there already an edge
        ///   of this type between these two elements" in one call, so the key is derived from the endpoints
        ///   and the type. Its <c>edge</c> segment is deliberately not a vocabulary type, because a derived
        ///   key must never stand in for an element's identity.</para>
        /// </summary>
        public static String ForEdge(String sourcePrimaryKey, String edgeType, String targetPrimaryKey)
        {
            if (String.IsNullOrEmpty(sourcePrimaryKey))
            {
                throw new ArgumentException("A source primary key is required.", nameof(sourcePrimaryKey));
            }

            if (String.IsNullOrEmpty(edgeType))
            {
                throw new ArgumentException("An edge type is required.", nameof(edgeType));
            }

            if (String.IsNullOrEmpty(targetPrimaryKey))
            {
                throw new ArgumentException("A target primary key is required.", nameof(targetPrimaryKey));
            }

            return EdgeSegment + ":" + sourcePrimaryKey + "|" + edgeType + "|" + targetPrimaryKey;
        }

        /// <summary>
        ///   An endpoint's PRIMARY KEY: its strongest claim, and among equals of one strength the ordinally
        ///   first. Deriving from whichever claim a provider happened to list first would compose two keys
        ///   for one relation across two runs and create the edge twice.
        /// </summary>
        public static Boolean TryPrimaryKey(IReadOnlyList<ComposedClaim> claims, out String? primaryKey)
        {
            primaryKey = null;
            if (claims == null || claims.Count == 0)
            {
                return false;
            }

            var best = claims[0];
            for (var i = 1; i < claims.Count; i++)
            {
                if (Precedes(claims[i], best))
                {
                    best = claims[i];
                }
            }

            primaryKey = best.Key;
            return primaryKey != null;
        }

        /// <summary>
        ///   Whether <paramref name="candidate"/> outranks <paramref name="incumbent"/>: stronger wins,
        ///   then the ordinally smaller key.
        /// </summary>
        private static Boolean Precedes(ComposedClaim candidate, ComposedClaim incumbent)
        {
            if (candidate.Strength != incumbent.Strength)
            {
                return candidate.Strength > incumbent.Strength;
            }

            return String.CompareOrdinal(candidate.Key, incumbent.Key) < 0;
        }
    }

    /// <summary>One canonicalised, scoped claim: everything the write path and the resolver need.</summary>
    public readonly struct ComposedClaim : IEquatable<ComposedClaim>
    {
        public ComposedClaim(String key, String type, String canonicalValue, IdentifierStrength strength)
        {
            Key = key;
            Type = type;
            CanonicalValue = canonicalValue;
            Strength = strength;
        }

        /// <summary>The composed claim key: the only comparison surface in the runtime.</summary>
        public String Key { get; }

        /// <summary>The vocabulary type it came from.</summary>
        public String Type { get; }

        /// <summary>The canonical value, for a diagnostic that has to quote it.</summary>
        public String CanonicalValue { get; }

        /// <summary>Whether this claim may resolve.</summary>
        public IdentifierStrength Strength { get; }

        /// <summary>Whether this claim may resolve, as one word.</summary>
        public Boolean IsStrong => Strength == IdentifierStrength.Strong;

        public Boolean Equals(ComposedClaim other)
        {
            return String.Equals(Key, other.Key, StringComparison.Ordinal);
        }

        public override Boolean Equals(Object? obj)
        {
            return obj is ComposedClaim other && Equals(other);
        }

        public override Int32 GetHashCode()
        {
            return Key == null ? 0 : StringComparer.Ordinal.GetHashCode(Key);
        }

        public override String ToString()
        {
            return Key ?? String.Empty;
        }
    }

    /// <summary>Why a claim key could not be composed.</summary>
    public enum ClaimKeyFailure
    {
        /// <summary>It was composed.</summary>
        None = 0,

        /// <summary>The value does not canonicalise to something its type accepts.</summary>
        InvalidValue = 1,

        /// <summary>
        ///   A provider- or instance-scoped type was composed without its scope segment. Never falls back
        ///   to the global form.
        /// </summary>
        MissingScope = 2,
    }
}
