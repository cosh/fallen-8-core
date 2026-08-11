// MIT License
//
// IdentityResolver.cs
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
using NoSQL.GraphDB.Integrations.Validation;

namespace NoSQL.GraphDB.Integrations.Identity
{
    /// <summary>
    ///   Resolution asks ONE question per entity: is there an element I myself already claimed that carries
    ///   one of this entity's STRONG claim keys? No configuration, no threshold, no second question.
    ///
    ///   <para>This type DECIDES from a pre-narrowed lookup: entity claims plus lookup result in, a
    ///   <see cref="Resolution"/> out, with no graph, no network and no clock. These rules are the part of the
    ///   feature most likely to be wrong, and purity is what makes them reviewable and testable with nothing
    ///   in the way.</para>
    ///
    ///   <para>WEAK CLAIMS ARE NEVER CONSULTED: not across instances, and not even against an element this
    ///   instance already claims. An address moves between devices, so matching on one attaches this run's
    ///   data to whichever element last held the value, and the most likely victim is this runtime's own
    ///   element - which is why the "not even my own" half is explicit. Similarity of any kind is likewise
    ///   never an identity signal, at any strength, under any configuration: two identical smart plugs produce
    ///   identical text and therefore identical vectors, and they are different devices.</para>
    /// </summary>
    public sealed class IdentityResolver
    {
        /// <summary>
        ///   Decides what to do with one entity.
        /// </summary>
        /// <param name="entity">The validated entity, whose claims are already canonicalised and composed.</param>
        /// <param name="inScopeByClaimKey">
        ///   Per claim key, the element ids that carry it AND are in scope for this instance. Narrowing is the
        ///   caller's job because it is a question about element STATE that an index cannot answer; the rule
        ///   itself has exactly one home, <c>ElementScope.IsInScope</c>.
        /// </param>
        public Resolution Resolve(ValidatedEntity entity,
            IReadOnlyDictionary<String, IReadOnlyList<Int32>> inScopeByClaimKey)
        {
            if (entity == null)
            {
                throw new ArgumentNullException(nameof(entity));
            }

            if (inScopeByClaimKey == null)
            {
                throw new ArgumentNullException(nameof(inScopeByClaimKey));
            }

            // Per matched element, the ordinally FIRST of this entity's keys that found it. Content-derived
            // rather than id-derived because a trim renumbers element ids in place, so an id-based rule could
            // land the same entity on a different element after a trim.
            var firstKeyByElement = new SortedDictionary<Int32, String>();

            foreach (var claim in entity.Claims)
            {
                if (!claim.IsStrong)
                {
                    continue;
                }

                if (!inScopeByClaimKey.TryGetValue(claim.Key, out var elements) || elements == null)
                {
                    continue;
                }

                foreach (var elementId in elements)
                {
                    if (!firstKeyByElement.TryGetValue(elementId, out var existing) ||
                        String.CompareOrdinal(claim.Key, existing) < 0)
                    {
                        firstKeyByElement[elementId] = claim.Key;
                    }
                }
            }

            if (firstKeyByElement.Count == 0)
            {
                // ZERO OF ITS OWN MATCHED: create, even when another instance's element carries the identical
                // claim key. That element is not touched, and the two elements share a queryable key, which
                // is the whole mechanism by which an overlap becomes findable.
                return Resolution.Create();
            }

            if (firstKeyByElement.Count == 1)
            {
                foreach (var single in firstKeyByElement)
                {
                    return Resolution.Matched(single.Key, ImmutableArray.Create(single.Key));
                }
            }

            // MORE THAN ONE OF ITS OWN MATCHED: match deterministically and report. An earlier run claimed
            // one thing under two strong keys before it saw a source row carrying both. Resolving to NEITHER
            // is not an option: it would contribute no element id to what the run claims, so reconciliation
            // would withdraw this instance's claim from BOTH of its own elements and delete them, on every
            // run. Nothing is unified here either: the element not chosen keeps its own claims, stops being
            // asserted, and this same run's reconciliation withdraws this instance's claim from it, so the
            // graph converges within the run.
            // Ascending element id, because firstKeyByElement is sorted: with two elements found by ONE key
            // content cannot separate them, and taking the first of an ascending walk IS the documented
            // "ties to the lower id". A second clause comparing ids would read as the rule while never
            // executing, which is worse than no clause at all.
            var chosen = 0;
            String? chosenKey = null;
            var all = ImmutableArray.CreateBuilder<Int32>(firstKeyByElement.Count);

            foreach (var candidate in firstKeyByElement)
            {
                all.Add(candidate.Key);

                if (chosenKey == null)
                {
                    chosen = candidate.Key;
                    chosenKey = candidate.Value;
                    continue;
                }

                if (String.CompareOrdinal(candidate.Value, chosenKey) < 0)
                {
                    chosen = candidate.Key;
                    chosenKey = candidate.Value;
                }
            }

            return Resolution.MatchedMoreThanOne(chosen, all.ToImmutable());
        }
    }

    /// <summary>What resolution decided about one entity.</summary>
    public readonly struct Resolution
    {
        private Resolution(ResolutionOutcome outcome, Int32 elementId, ImmutableArray<Int32> matchedElements)
        {
            Outcome = outcome;
            ElementId = elementId;
            MatchedElements = matchedElements;
        }

        /// <summary>Nothing of this instance's own matched.</summary>
        public static Resolution Create()
        {
            return new Resolution(ResolutionOutcome.Create, 0, ImmutableArray<Int32>.Empty);
        }

        /// <summary>Exactly one of this instance's own matched.</summary>
        public static Resolution Matched(Int32 elementId, ImmutableArray<Int32> matchedElements)
        {
            return new Resolution(ResolutionOutcome.Match, elementId, matchedElements);
        }

        /// <summary>More than one matched; the pick is content-derived and the run reports it.</summary>
        public static Resolution MatchedMoreThanOne(Int32 elementId, ImmutableArray<Int32> matchedElements)
        {
            return new Resolution(ResolutionOutcome.MatchedMoreThanOne, elementId, matchedElements);
        }

        /// <summary>Which of the three cases this is.</summary>
        public ResolutionOutcome Outcome { get; }

        /// <summary>The element to write to, for either matched case.</summary>
        public Int32 ElementId { get; }

        /// <summary>
        ///   Every one of this instance's own elements that matched. More than one is the reportable case, and
        ///   the ones not chosen are exactly the elements this run's reconciliation converges away.
        /// </summary>
        public ImmutableArray<Int32> MatchedElements { get; }

        /// <summary>Whether an element already exists to write to.</summary>
        public Boolean IsMatch => Outcome != ResolutionOutcome.Create;
    }

    /// <summary>The three, and only three, outcomes of resolution.</summary>
    public enum ResolutionOutcome
    {
        /// <summary>Create a new element and claim it.</summary>
        Create = 0,

        /// <summary>Write to the one element that matched.</summary>
        Match = 1,

        /// <summary>
        ///   Write to the deterministically chosen one of several that matched, and report
        ///   <c>duplicateClaimedElements</c>.
        /// </summary>
        MatchedMoreThanOne = 2,
    }
}
