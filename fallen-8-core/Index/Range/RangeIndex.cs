// MIT License
//
// RangeIndex.cs
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

#region Usings

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using NoSQL.GraphDB.Core.Error;
using NoSQL.GraphDB.Core.Model;

#endregion

namespace NoSQL.GraphDB.Core.Index.Range
{
    /// <summary>
    /// Fallen8 range index: the bucket index (all point operations, persistence and the
    /// reverse map live in <see cref="ABucketIndex"/>, feature code-quality) plus ordered
    /// range queries over a lazily cached sorted-key snapshot (finding P4).
    /// </summary>
    public sealed class RangeIndex : ABucketIndex, IRangeIndex
    {
        #region Data

        /// <summary>
        ///   A lazily-built, cached ascending-sorted snapshot of the index KEYS, used to
        ///   answer range queries in O(log n + k) via binary search instead of an O(n) scan
        ///   over every key (finding P4). Point operations keep using the dictionary
        ///   unchanged, so their semantics - multi-value buckets, key identity by Equals, and
        ///   the B3 correctness fixes - are preserved exactly. Invalidated (via
        ///   <see cref="OnKeySetChanged"/>, under the write lock) whenever the KEY SET changes
        ///   - a new key, a removed key, an emptied key, a wipe or a load - and rebuilt on the
        ///   next range query. Adding another value under an EXISTING key does not change the
        ///   key set and therefore does not invalidate it. Built/read only under the read lock,
        ///   invalidated only under the write lock (mutually exclusive), so it never observes
        ///   a torn key set; <c>volatile</c> gives readers a consistent view.
        /// </summary>
        private volatile IComparable[] _sortedKeys;

        #endregion

        /// <summary>A key-set change makes the sorted-key snapshot stale (finding P4).</summary>
        protected override void OnKeySetChanged()
        {
            _sortedKeys = null;
        }

        #region IPlugin / IDisposable overrides

        public override void Initialize(IFallen8 fallen8, IDictionary<string, object> parameter)
        {
            base.Initialize(fallen8, parameter);
            _sortedKeys = null;
        }

        public override void Dispose()
        {
            base.Dispose();
            _sortedKeys = null;
        }

        public override string PluginName => "RangeIndex";

        public override string Description => "A very very simple range index";

        #endregion

        #region IRangeIndex implementation

        public bool LowerThan(out ImmutableList<AGraphElementModel> result, IComparable key, bool includeKey)
        {
            if (ReadResource())
            {
                try
                {
                    var keys = EnsureSortedKeys();
                    // Keys lower than the given key: inclusive -> [0, UpperBound(key)) (all keys <= key);
                    // exclusive -> [0, LowerBound(key)) (all keys < key). O(log n) to locate the end.
                    int end = includeKey ? UpperBound(keys, key) : LowerBound(keys, key);
                    result = GatherRange(keys, 0, end);
                    return result != null;
                }
                finally
                {
                    FinishReadResource();
                }
            }

            throw new CollisionException();
        }

        public bool GreaterThan(out ImmutableList<AGraphElementModel> result, IComparable key, bool includeKey)
        {
            if (ReadResource())
            {
                try
                {
                    var keys = EnsureSortedKeys();
                    // Keys greater than the given key: inclusive -> [LowerBound(key), n) (all keys >= key);
                    // exclusive -> [UpperBound(key), n) (all keys > key). O(log n) to locate the start.
                    int start = includeKey ? LowerBound(keys, key) : UpperBound(keys, key);
                    result = GatherRange(keys, start, keys.Length);
                    return result != null;
                }
                finally
                {
                    FinishReadResource();
                }
            }

            throw new CollisionException();
        }

        public bool Between(out ImmutableList<AGraphElementModel> result, IComparable lowerLimit, IComparable upperLimit, bool includeLowerLimit, bool includeUpperLimit)
        {
            if (ReadResource())
            {
                try
                {
                    var keys = EnsureSortedKeys();
                    // Two binary searches bracket the range: start honours the lower bound's inclusivity,
                    // end honours the upper bound's. An inverted range (lower > upper) yields start >= end,
                    // which GatherRange returns as empty - matching the old "no key satisfies both" result.
                    int start = includeLowerLimit ? LowerBound(keys, lowerLimit) : UpperBound(keys, lowerLimit);
                    int end = includeUpperLimit ? UpperBound(keys, upperLimit) : LowerBound(keys, upperLimit);
                    result = GatherRange(keys, start, end);
                    return result != null;
                }
                finally
                {
                    FinishReadResource();
                }
            }

            throw new CollisionException();
        }

        #endregion

        #region range helpers (finding P4)

        /// <summary>
        ///   Returns the ascending-sorted key snapshot, building it once (lazily) when stale. Called
        ///   only while holding the read lock, so the key set is stable (writes are exclusive
        ///   of reads); concurrent readers may each build an equal array - benign - and the last
        ///   volatile publish wins.
        /// </summary>
        private IComparable[] EnsureSortedKeys()
        {
            var keys = _sortedKeys;
            if (keys != null)
            {
                return keys;
            }

            keys = new IComparable[_idx.Count];
            _idx.Keys.CopyTo(keys, 0);
            Array.Sort(keys); // default comparer == IComparable.CompareTo, matching the old range predicates
            _sortedKeys = keys;
            return keys;
        }

        /// <summary>
        ///   Collects the value buckets for the sorted keys in <c>[startInclusive, endExclusive)</c>
        ///   into one immutable list (O(k) over the matched keys). Keeps the multi-value-per-key
        ///   semantics: every element in each bucket is included.
        /// </summary>
        private ImmutableList<AGraphElementModel> GatherRange(IComparable[] keys, int startInclusive, int endExclusive)
        {
            if (startInclusive >= endExclusive)
            {
                return ImmutableList<AGraphElementModel>.Empty;
            }

            var builder = ImmutableList.CreateBuilder<AGraphElementModel>();
            for (int i = startInclusive; i < endExclusive; i++)
            {
                ImmutableList<AGraphElementModel> bucket;
                if (_idx.TryGetValue(keys[i], out bucket))
                {
                    builder.AddRange(bucket);
                }
            }
            return builder.ToImmutable();
        }

        /// <summary>
        ///   Smallest index <c>i</c> in <c>[0, n]</c> with <c>keys[i].CompareTo(key) &gt;= 0</c> (the
        ///   first key not less than <paramref name="key" />); <c>n</c> if every key is less.
        /// </summary>
        private static int LowerBound(IComparable[] keys, IComparable key)
        {
            int lo = 0;
            int hi = keys.Length;
            while (lo < hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (keys[mid].CompareTo(key) < 0)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }
            return lo;
        }

        /// <summary>
        ///   Smallest index <c>i</c> in <c>[0, n]</c> with <c>keys[i].CompareTo(key) &gt; 0</c> (the
        ///   first key strictly greater than <paramref name="key" />); <c>n</c> if none is greater.
        /// </summary>
        private static int UpperBound(IComparable[] keys, IComparable key)
        {
            int lo = 0;
            int hi = keys.Length;
            while (lo < hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (keys[mid].CompareTo(key) <= 0)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }
            return lo;
        }

        #endregion
    }
}
