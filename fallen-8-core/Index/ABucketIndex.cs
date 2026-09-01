// MIT License
//
// ABucketIndex.cs
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

#region Usings

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Core.Error;
using NoSQL.GraphDB.Core.Helper;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Serializer;

#endregion

namespace NoSQL.GraphDB.Core.Index
{
    /// <summary>
    ///   The shared implementation of the multi-value ("bucket") comparable-keyed index family
    ///   (feature code-quality: DictionaryIndex and RangeIndex previously carried ~200
    ///   byte-identical lines of IIndex + persistence + reverse-map code each, drifting in
    ///   try/finally discipline). Owns the key -&gt; bucket dictionary, the REFERENCE-keyed
    ///   reverse map that makes <see cref="RemoveValue"/> O(affected keys)
    ///   (feature index-lifecycle 3.4), and the sidecar save/load format.
    ///
    ///   <para>Derived classes hook <see cref="OnKeySetChanged"/> (invoked under the write
    ///   lock whenever a key is added, removed, emptied, wiped or reloaded) to invalidate any
    ///   key-set-derived cache - RangeIndex's sorted-key snapshot (finding P4) is the one
    ///   consumer today - and reach the buckets through <see cref="KeyCount"/>,
    ///   <see cref="CopyKeysTo"/> and <see cref="TryGetLiveValues"/> rather than through the
    ///   dictionary itself, so a bucket's internal representation stays private to this class.</para>
    ///
    ///   <para><b>Why a bucket is not just an ImmutableList (feature cheap-withdrawal).</b>
    ///   Removal used to be <c>bucket.RemoveAll(x =&gt; ReferenceEquals(x, element))</c>, which
    ///   walks and rebuilds the WHOLE posting list for one element, while an add costs
    ///   <c>ImmutableList.Add</c>, log time. That asymmetry is invisible while buckets are small
    ///   and quadratic when one is not, and one is not: an integration's claim index puts every
    ///   element an identity claims under a SINGLE key, so that bucket is the whole graph.
    ///   Measured on this machine before the change, removing every value from one bucket:
    ///   8,000 elements 0.57 s, 16,000 elements 1.93 s, 32,000 elements 7.79 s - each doubling
    ///   quadrupling the time - while the same element count spread over 4,000 keys stayed flat
    ///   at over a million removals a second. A real withdrawal of many claimed entries
    ///   extrapolates to about 520 s of index work alone.</para>
    ///
    ///   <para>So a removal now only RECORDS that an element is gone, in log time, and the
    ///   posting list is rebuilt once per halving instead of once per element. Reads never see a
    ///   recorded-removed element: <see cref="Bucket.Live"/> returns the list itself when nothing
    ///   is pending, which is the steady state, and filters otherwise. Removing every value from
    ///   a bucket of B therefore costs O(B log B) rather than O(B squared), because compaction
    ///   runs at B/2, then B/4, and so on, summing to 2B.</para>
    /// </summary>
    public abstract class ABucketIndex : AThreadSafeElement, IIndex
    {
        #region Data

        /// <summary>
        ///   One key's posting list, plus the elements removed from it that have not been
        ///   compacted out of the list yet.
        ///
        ///   <para>MUTATED ONLY UNDER THE WRITE LOCK. Nothing but <see cref="Values"/> and the
        ///   filtered list <see cref="Live"/> builds ever escapes the lock, and both are
        ///   immutable, so a caller may keep iterating a returned list after releasing its read
        ///   lock - which <see cref="TryGetValue"/>'s callers do.</para>
        ///
        ///   <para>INVARIANT: every element in <see cref="Removed"/> appears in
        ///   <see cref="Values"/>. The one way that can be violated is a bucket carrying
        ///   duplicate entries for one element from a pre-fix checkpoint (see the note in
        ///   <see cref="AddOrUpdate"/>), where <see cref="LiveCountEstimate"/> reads high; every
        ///   decision that must be exact compacts first and then counts, so the estimate is only
        ///   ever used to decide WHETHER to compact.</para>
        /// </summary>
        private sealed class Bucket
        {
            /// <summary>Compact once the recorded removals reach this fraction of the list.</summary>
            private const Int32 CompactWhenRemovedTimes = 2;

            internal Bucket(AGraphElementModel first)
            {
                Values = ImmutableList.Create(first);
                Removed = null;
            }

            private Bucket(ImmutableList<AGraphElementModel> values)
            {
                Values = values;
                Removed = null;
            }

            /// <summary>The posting list, which may still contain recorded-removed elements.</summary>
            internal ImmutableList<AGraphElementModel> Values { get; private set; }

            /// <summary>
            ///   Elements removed but not yet compacted out of <see cref="Values"/>. Null rather
            ///   than an empty set in the steady state, because that is the case every read takes
            ///   and a null check is cheaper than an emptiness check on an allocated set.
            /// </summary>
            private HashSet<AGraphElementModel> Removed { get; set; }

            /// <summary>True when the posting list contains no recorded-removed element.</summary>
            internal Boolean IsClean => Removed == null;

            /// <summary>
            ///   Live element count, HIGH by the number of duplicate entries a pre-fix checkpoint
            ///   left in the list. Only ever used to decide whether to compact.
            /// </summary>
            internal Int32 LiveCountEstimate => Values.Count - (Removed?.Count ?? 0);

            /// <summary>
            ///   The live elements, in insertion order. Returns the posting list ITSELF when
            ///   nothing is pending, so the steady-state read costs nothing; filters into a new
            ///   immutable list otherwise. Order is preserved either way, which is why removal
            ///   records elements rather than tombstoning positions.
            /// </summary>
            internal ImmutableList<AGraphElementModel> Live
            {
                get
                {
                    var removed = Removed;
                    return removed == null ? Values : Values.RemoveAll(removed.Contains);
                }
            }

            /// <summary>
            ///   Appends an element. Re-adding one whose removal was recorded but not yet compacted
            ///   away puts it BACK AT THE END rather than leaving it where it was, because that is
            ///   where the pre-fix code put it: removal compacted immediately, so a re-add always
            ///   appended. Leaving it in place would make the observable order depend on whether a
            ///   compaction happened to have run yet, which is a worse contract than either order.
            ///   It costs O(bucket) once, on a path the pre-fix code also paid O(bucket) for, and
            ///   the bulk-removal path this change exists for never takes it.
            /// </summary>
            internal void Add(AGraphElementModel graphElement)
            {
                var removed = Removed;
                if (removed != null && removed.Remove(graphElement))
                {
                    if (removed.Count == 0)
                    {
                        Removed = null;
                    }

                    // Reference equality is the default comparer for these types, so this drops the
                    // one stale entry and nothing else.
                    Values = Values.Remove(graphElement).Add(graphElement);
                    return;
                }

                Values = Values.Add(graphElement);
            }

            /// <summary>
            ///   Records that an element is gone, in log time, and compacts when the recorded
            ///   removals have reached half the list. Returns true when the bucket is now empty
            ///   and its key should be dropped.
            /// </summary>
            internal Boolean Remove(AGraphElementModel graphElement)
            {
                (Removed ??= new HashSet<AGraphElementModel>()).Add(graphElement);

                if (LiveCountEstimate <= 0)
                {
                    // Might be an over-count from duplicate entries, so compact and then look
                    // rather than trusting the estimate at the one point where it decides
                    // whether a key disappears.
                    Compact();
                    return Values.Count == 0;
                }

                if (Removed.Count * CompactWhenRemovedTimes >= Values.Count)
                {
                    Compact();
                }

                return false;
            }

            private void Compact()
            {
                var removed = Removed;
                if (removed == null)
                {
                    return;
                }

                Values = Values.RemoveAll(removed.Contains);
                Removed = null;
            }

            internal static Bucket FromLoadedValues(ImmutableList<AGraphElementModel> values)
                => new Bucket(values);
        }

        /// <summary>The index dictionary: key -&gt; bucket of elements.</summary>
        private Dictionary<IComparable, Bucket> _idx;

        /// <summary>
        ///   Reverse map: element -&gt; the set of keys it appears under (feature
        ///   index-lifecycle 3.4). Keyed by element REFERENCE identity (VertexModel/EdgeModel
        ///   use reference <c>Equals</c> + an identity hash), which stays valid across a Trim
        ///   id-renumber. Maintained under the same write lock as the index dictionary.
        /// </summary>
        private Dictionary<AGraphElementModel, HashSet<IComparable>> _reverse;

        /// <summary>The logger, typed to the concrete index class.</summary>
        protected ILogger _logger;

        #endregion

        #region derived-class access to the buckets

        /// <summary>The number of keys. Call only while holding the read or write lock.</summary>
        protected Int32 KeyCount => _idx.Count;

        /// <summary>
        ///   Copies the key set into <paramref name="destination" />, which must have room for
        ///   <see cref="KeyCount" /> keys. Call only while holding the read or write lock.
        /// </summary>
        protected void CopyKeysTo(IComparable[] destination) => _idx.Keys.CopyTo(destination, 0);

        /// <summary>
        ///   The live elements under one key, or false when the key is absent. Call only while
        ///   holding the read or write lock; the returned list is immutable and may be kept.
        /// </summary>
        protected Boolean TryGetLiveValues(IComparable key, out ImmutableList<AGraphElementModel> values)
        {
            if (_idx.TryGetValue(key, out var bucket))
            {
                values = bucket.Live;
                return true;
            }

            values = null;
            return false;
        }

        #endregion

        /// <summary>Invoked UNDER THE WRITE LOCK whenever the KEY SET changed (new key,
        /// removed key, emptied key, wipe, load). Value-only bucket growth does not raise it.</summary>
        protected virtual void OnKeySetChanged()
        {
        }

        #region IIndex implementation

        public Int32 CountOfKeys()
        {
            if (ReadResource())
            {
                try
                {
                    return _idx.Keys.Count;
                }
                finally
                {
                    FinishReadResource();
                }
            }

            throw new CollisionException();
        }

        public Int32 CountOfValues()
        {
            if (ReadResource())
            {
                try
                {
                    var count = 0;
                    foreach (var bucket in _idx.Values)
                    {
                        // Exact, so it pays the filter on a bucket with a pending removal rather
                        // than trusting the estimate. Already O(all values) before this change.
                        count += bucket.IsClean ? bucket.Values.Count : bucket.Live.Count;
                    }

                    return count;
                }
                finally
                {
                    FinishReadResource();
                }
            }

            throw new CollisionException();
        }

        public void AddOrUpdate(Object keyObject, AGraphElementModel graphElement)
        {
            IComparable key;
            if (!IndexHelper.CheckObject(out key, keyObject))
            {
                return;
            }

            if (WriteResource())
            {
                try
                {
                    // Never index a removed element (the rule and its reason: IIndex.AddOrUpdate). Inside
                    // the write lock, which is what orders it against a concurrent RemoveValue.
                    if (graphElement != null && graphElement._removed)
                    {
                        return;
                    }

                    // IDEMPOTENT (feature platform-integrity-audit W3): adding the same
                    // (key, element) pair twice must not duplicate the bucket entry. The bucket append
                    // below is unconditional, so without this guard a re-add doubles the posting list,
                    // a second re-add triples it, and the inflated bucket is then PERSISTED into the
                    // next checkpoint - which makes any rebuild-from-element-state or replayed
                    // population a silent bucket-multiplication machine.
                    //
                    // The reverse map answers "is this pair already present" in O(1); scanning the
                    // bucket would be O(bucket) on the write hot path. It is kept in lockstep with the
                    // forward map here and rebuilt from the buckets on load, so it is authoritative.
                    // (A bucket that already carries duplicates from a pre-fix checkpoint keeps them
                    // until it is rebuilt; this guard prevents new ones rather than repairing old.)
                    var keysForElementPresent = _reverse.TryGetValue(graphElement, out var keysForElement);
                    if (keysForElementPresent && keysForElement.Contains(key))
                    {
                        return;
                    }

                    if (_idx.TryGetValue(key, out var bucket))
                    {
                        // Existing key: only the bucket grows; the key set is unchanged. Bucket.Add
                        // resurrects the element instead of appending when its removal was recorded
                        // but not yet compacted away, so the list cannot end up holding it twice.
                        bucket.Add(graphElement);
                    }
                    else
                    {
                        _idx.Add(key, new Bucket(graphElement));
                        OnKeySetChanged();
                    }

                    // Maintain the reverse map so RemoveValue is O(affected keys) (index-lifecycle 3.4).
                    if (keysForElementPresent)
                    {
                        keysForElement.Add(key);
                    }
                    else
                    {
                        _reverse[graphElement] = new HashSet<IComparable> { key };
                    }
                }
                finally
                {
                    FinishWriteResource();
                }

                return;
            }

            throw new CollisionException();
        }

        public bool TryRemoveKey(Object keyObject)
        {
            IComparable key;
            if (!IndexHelper.CheckObject(out key, keyObject))
            {
                return false;
            }

            if (WriteResource())
            {
                try
                {
                    // Drop this key from the reverse set of every element in its bucket before
                    // removing it, so the reverse map cannot dangle (index-lifecycle 3.4). Walks the
                    // raw posting list rather than the live view: an element whose removal is
                    // already recorded has no reverse entry left, so it simply misses below, and
                    // building the filtered list here would be work for nothing.
                    if (_idx.TryGetValue(key, out var bucket))
                    {
                        foreach (var element in bucket.Values)
                        {
                            if (_reverse.TryGetValue(element, out var keysForElement))
                            {
                                keysForElement.Remove(key);
                                if (keysForElement.Count == 0)
                                {
                                    _reverse.Remove(element);
                                }
                            }
                        }
                    }

                    var foundSth = _idx.Remove(key);
                    if (foundSth)
                    {
                        OnKeySetChanged();
                    }

                    return foundSth;
                }
                finally
                {
                    FinishWriteResource();
                }
            }

            throw new CollisionException();
        }

        public void RemoveValue(AGraphElementModel graphElement)
        {
            if (WriteResource())
            {
                try
                {
                    // O(affected keys) in the buckets it touches, and log time in each of them:
                    // the reverse map names exactly the buckets the element is in, so there is no
                    // full-key-set scan (index-lifecycle 3.4), and the bucket records the removal
                    // rather than rebuilding its posting list (feature cheap-withdrawal).
                    var keySetShrank = false;
                    if (_reverse.TryGetValue(graphElement, out var keysForElement))
                    {
                        foreach (var aKey in keysForElement)
                        {
                            if (_idx.TryGetValue(aKey, out var bucket) && bucket.Remove(graphElement))
                            {
                                _idx.Remove(aKey);
                                keySetShrank = true;
                            }
                        }

                        _reverse.Remove(graphElement);
                    }

                    if (keySetShrank)
                    {
                        OnKeySetChanged();
                    }
                }
                finally
                {
                    FinishWriteResource();
                }

                return;
            }

            throw new CollisionException();
        }

        public void Wipe()
        {
            if (WriteResource())
            {
                try
                {
                    _idx.Clear();
                    _reverse.Clear();
                    OnKeySetChanged();
                }
                finally
                {
                    FinishWriteResource();
                }

                return;
            }

            throw new CollisionException();
        }

        public IEnumerable<Object> GetKeys()
        {
            if (ReadResource())
            {
                try
                {
                    return new List<IComparable>(_idx.Keys);
                }
                finally
                {
                    FinishReadResource();
                }
            }

            throw new CollisionException();
        }

        public IEnumerable<KeyValuePair<object, ImmutableList<AGraphElementModel>>> GetKeyValues()
        {
            if (ReadResource())
            {
                try
                {
                    foreach (var aKv in _idx)
                    {
                        yield return new KeyValuePair<object, ImmutableList<AGraphElementModel>>(
                            aKv.Key, aKv.Value.Live);
                    }
                }
                finally
                {
                    FinishReadResource();
                }

                yield break;
            }

            throw new CollisionException();
        }

        public bool TryGetValue(out ImmutableList<AGraphElementModel> result, Object keyObject)
        {
            IComparable key;
            if (!IndexHelper.CheckObject(out key, keyObject))
            {
                result = null;
                return false;
            }

            if (ReadResource())
            {
                try
                {
                    var foundSth = _idx.TryGetValue(key, out var bucket);
                    result = foundSth ? bucket.Live : null;
                    return foundSth;
                }
                finally
                {
                    FinishReadResource();
                }
            }

            throw new CollisionException();
        }

        #endregion

        #region IFallen8Serializable implementation

        // Keyed by object equality (dictionary) / comparable (range): exact key lookup is the point.
        public Boolean SupportsPointEqualityLookup => true;

        public Boolean CanPersist => true;

        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = IIndex.KeysAreNotTrimSafe)]
        public void Save(SerializationWriter writer)
        {
            if (ReadResource())
            {
                try
                {
                    writer.Write(0); //parameter
                    writer.Write(_idx.Count);
                    foreach (var aKV in _idx)
                    {
                        // The LIVE list, so a checkpoint never carries an element whose removal was
                        // recorded but not yet compacted away. Reading it here also means a save is
                        // the one read that can pay the filter on every dirty bucket, which is
                        // cheaper than the alternative of compacting on the write path every time.
                        var values = aKV.Value.Live;
                        writer.WriteObject(aKV.Key);
                        writer.Write(values.Count);
                        foreach (var aItem in values)
                        {
                            writer.Write(aItem.Id);
                        }
                    }
                }
                finally
                {
                    FinishReadResource();
                }

                return;
            }

            throw new CollisionException();
        }

        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = IIndex.KeysAreNotTrimSafe)]
        public void Load(SerializationReader reader, IFallen8 fallen8)
        {
            // The real load path (IndexFactory.OpenIndex) activates the plugin WITHOUT calling
            // Initialize, so the logger must be wired here or the not-found branch below would
            // dereference a null logger (consolidation-audit CA-16; mirrors VectorIndex.Load).
            _logger ??= fallen8?.LoggerFactory?.CreateLogger(GetType());

            if (WriteResource())
            {
                try
                {
                    reader.ReadInt32(); //parameter

                    var keyCount = reader.ReadInt32();
                    _idx = new Dictionary<IComparable, Bucket>(keyCount);

                    for (var i = 0; i < keyCount; i++)
                    {
                        var key = reader.ReadObject();
                        var value = new List<AGraphElementModel>();
                        var valueCount = reader.ReadInt32();
                        for (var j = 0; j < valueCount; j++)
                        {
                            var graphElementId = reader.ReadInt32();
                            if (fallen8.TryGetGraphElement(out var graphElement, graphElementId))
                            {
                                value.Add(graphElement);
                            }
                            else
                            {
                                _logger?.LogError(
                                    "[{IndexType}] Error while deserializing the index. Could not find the graph element \"{GraphElementId}\"",
                                    GetType().Name, graphElementId);
                            }
                        }
                        _idx.Add((IComparable)key, Bucket.FromLoadedValues(
                            ImmutableList.CreateRange<AGraphElementModel>(value)));
                    }

                    // Rebuild the reverse map from the freshly loaded buckets (index-lifecycle 3.4).
                    _reverse = BuildReverseMap(_idx);

                    // The key set was rebuilt from disk.
                    OnKeySetChanged();
                }
                finally
                {
                    FinishWriteResource();
                }

                return;
            }

            throw new CollisionException();
        }

        private static Dictionary<AGraphElementModel, HashSet<IComparable>> BuildReverseMap(
            Dictionary<IComparable, Bucket> idx)
        {
            var reverse = new Dictionary<AGraphElementModel, HashSet<IComparable>>();
            foreach (var kv in idx)
            {
                // A freshly loaded bucket has nothing pending, so the raw list IS the live list.
                foreach (var element in kv.Value.Values)
                {
                    if (reverse.TryGetValue(element, out var keysForElement))
                    {
                        keysForElement.Add(kv.Key);
                    }
                    else
                    {
                        reverse[element] = new HashSet<IComparable> { kv.Key };
                    }
                }
            }
            return reverse;
        }

        #endregion

        #region IPlugin implementation

        public virtual void Initialize(IFallen8 fallen8, IDictionary<string, object> parameter)
        {
            _idx = new Dictionary<IComparable, Bucket>();
            _reverse = new Dictionary<AGraphElementModel, HashSet<IComparable>>();
            _logger = fallen8.LoggerFactory.CreateLogger(GetType());
        }

        public abstract string PluginName
        {
            get;
        }

        public Type PluginCategory => typeof(IIndex);

        public abstract string Description
        {
            get;
        }

        public string Manufacturer => "Henning Rauch";

        #endregion

        #region IDisposable Members

        public virtual void Dispose()
        {
            _idx?.Clear();
            _idx = null;
            _reverse?.Clear();
            _reverse = null;
        }

        #endregion
    }
}
