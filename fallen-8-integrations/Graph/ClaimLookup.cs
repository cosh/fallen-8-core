// MIT License
//
// ClaimLookup.cs
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

namespace NoSQL.GraphDB.Integrations.Graph
{
    /// <summary>
    ///   The answer to one lookup batch: what the index named, what of it is IN SCOPE for this instance, and the
    ///   state of every element involved.
    ///
    ///   <para>Narrowing happens here, on the graph side, because it is a question about element STATE that the
    ///   index cannot answer: the index says which elements carry a claim key, and only the elements themselves
    ///   say who claims them. Both answers are kept, because two rules read them differently - an ENTITY resolves
    ///   only against what is in scope, while an EDGE found by its derived key must see the foreign hit too, so
    ///   that it can fall through and create its own rather than adopting another instance's edge.</para>
    /// </summary>
    public sealed class ClaimLookup
    {
        private ClaimLookup(IReadOnlyDictionary<String, IReadOnlyList<Int32>> byKey,
            IReadOnlyDictionary<String, IReadOnlyList<Int32>> inScope,
            IReadOnlyDictionary<Int32, ElementState> elements)
        {
            ByKey = byKey;
            InScope = inScope;
            Elements = elements;
        }

        /// <summary>Every element id the index named, per claim key, in scope or not.</summary>
        public IReadOnlyDictionary<String, IReadOnlyList<Int32>> ByKey { get; }

        /// <summary>
        ///   Per claim key, only the elements this run may write to: the ones carrying this instance's claim, and
        ///   the ones carrying no claim at all.
        /// </summary>
        public IReadOnlyDictionary<String, IReadOnlyList<Int32>> InScope { get; }

        /// <summary>The state of every element named, so nothing downstream re-reads it.</summary>
        public IReadOnlyDictionary<Int32, ElementState> Elements { get; }

        /// <summary>Nothing carried any of the keys.</summary>
        public static ClaimLookup Empty { get; } = new ClaimLookup(
            new Dictionary<String, IReadOnlyList<Int32>>(StringComparer.Ordinal),
            new Dictionary<String, IReadOnlyList<Int32>>(StringComparer.Ordinal),
            new Dictionary<Int32, ElementState>());

        /// <summary>
        ///   Applies the in-scope rule to a raw index answer. THE ONE HOME of the narrowing step, called by both
        ///   targets, so the in-memory graph cannot narrow differently from the live one.
        /// </summary>
        public static ClaimLookup Build(IReadOnlyDictionary<String, IReadOnlyList<Int32>> byKey,
            IReadOnlyDictionary<Int32, ElementState> elements, String instanceId)
        {
            if (byKey == null)
            {
                throw new ArgumentNullException(nameof(byKey));
            }

            if (elements == null)
            {
                throw new ArgumentNullException(nameof(elements));
            }

            var inScope = new Dictionary<String, IReadOnlyList<Int32>>(StringComparer.Ordinal);
            foreach (var found in byKey)
            {
                var kept = new List<Int32>();
                foreach (var id in found.Value)
                {
                    if (elements.TryGetValue(id, out var state) && ElementScope.IsInScope(state, instanceId))
                    {
                        kept.Add(id);
                    }
                }

                if (kept.Count > 0)
                {
                    inScope[found.Key] = kept;
                }
            }

            return new ClaimLookup(byKey, inScope, elements);
        }
    }
}
