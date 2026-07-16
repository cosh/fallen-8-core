// MIT License
//
// ChangeFeedFilter.cs
//
// Copyright (c) 2025 Henning Rauch
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

namespace NoSQL.GraphDB.Core.ChangeFeed
{
    /// <summary>
    ///   A subscriber's DECLARATIVE event filter (feature change-feed): an unset dimension is a
    ///   wildcard; a set dimension requires the event to CARRY that attribute and the attribute's
    ///   value to be in the set (logical AND across dimensions, OR within one). Consequence,
    ///   spelled out: setting <see cref="Keys"/> excludes creates/removes (only property events
    ///   carry a key); an unlabeled element never matches a <see cref="Labels"/> filter.
    ///   <see cref="ChangeEventKind.Resync"/> events BYPASS every filter - continuity loss must
    ///   reach every subscriber. Never compiled code: the feed works with the dynamic-code kill
    ///   switch off.
    /// </summary>
    public sealed class ChangeFeedFilter
    {
        /// <summary>A filter matching every event.</summary>
        public static readonly ChangeFeedFilter MatchAll = new ChangeFeedFilter(0, null, null, null);

        /// <summary>The accepted kinds as a bitmask; 0 = wildcard.</summary>
        private readonly ChangeEventKind _kinds;

        /// <summary>The accepted element types; null = wildcard.</summary>
        private readonly HashSet<ChangeElementType> _elements;

        /// <summary>The accepted labels (ordinal); null = wildcard.</summary>
        private readonly HashSet<String> _labels;

        /// <summary>The accepted property keys (ordinal); null = wildcard.</summary>
        private readonly HashSet<String> _keys;

        private ChangeFeedFilter(ChangeEventKind kinds, HashSet<ChangeElementType> elements,
            HashSet<String> labels, HashSet<String> keys)
        {
            _kinds = kinds;
            _elements = elements;
            _labels = labels;
            _keys = keys;
        }

        /// <summary>
        ///   Creates a filter. Every parameter may be null/empty (wildcard). The sets are copied
        ///   with ordinal comparison, matching the engine's label/key semantics.
        /// </summary>
        public static ChangeFeedFilter Create(IEnumerable<ChangeEventKind> kinds = null,
            IEnumerable<ChangeElementType> elements = null,
            IEnumerable<String> labels = null,
            IEnumerable<String> keys = null)
        {
            ChangeEventKind kindMask = 0;
            if (kinds != null)
            {
                foreach (var kind in kinds)
                {
                    kindMask |= kind;
                }
            }

            HashSet<ChangeElementType> elementSet = null;
            if (elements != null)
            {
                foreach (var element in elements)
                {
                    (elementSet ??= new HashSet<ChangeElementType>()).Add(element);
                }
            }

            HashSet<String> labelSet = null;
            if (labels != null)
            {
                foreach (var label in labels)
                {
                    if (label != null)
                    {
                        (labelSet ??= new HashSet<String>(StringComparer.Ordinal)).Add(label);
                    }
                }
            }

            HashSet<String> keySet = null;
            if (keys != null)
            {
                foreach (var key in keys)
                {
                    if (key != null)
                    {
                        (keySet ??= new HashSet<String>(StringComparer.Ordinal)).Add(key);
                    }
                }
            }

            if (kindMask == 0 && elementSet == null && labelSet == null && keySet == null)
            {
                return MatchAll;
            }

            return new ChangeFeedFilter(kindMask, elementSet, labelSet, keySet);
        }

        /// <summary>
        ///   Whether an event passes this filter. Per-event cost is a few set lookups, so many
        ///   subscribers with different filters stay cheap on the dispatcher.
        /// </summary>
        public bool Matches(ChangeEvent changeEvent)
        {
            // Continuity loss reaches every subscriber, unconditionally.
            if (changeEvent.Kind == ChangeEventKind.Resync)
            {
                return true;
            }

            if (_kinds != 0 && (_kinds & changeEvent.Kind) == 0)
            {
                return false;
            }

            if (_elements != null && !_elements.Contains(changeEvent.Element))
            {
                return false;
            }

            if (_labels != null && (changeEvent.Label == null || !_labels.Contains(changeEvent.Label)))
            {
                return false;
            }

            if (_keys != null && (changeEvent.Key == null || !_keys.Contains(changeEvent.Key)))
            {
                return false;
            }

            return true;
        }
    }
}
