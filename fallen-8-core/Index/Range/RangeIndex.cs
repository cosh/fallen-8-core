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

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Core.Error;
using NoSQL.GraphDB.Core.Helper;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Serializer;

namespace NoSQL.GraphDB.Core.Index.Range
{
    /// <summary>
    /// Fallen8 range index.
    /// </summary>
    public sealed class RangeIndex : AThreadSafeElement, IRangeIndex
    {
        #region Data

        /// <summary>
        /// The index dictionary.
        /// </summary>
        private Dictionary<IComparable, ImmutableList<AGraphElementModel>> _idx;

        /// <summary>
        ///   A lazily-built, cached ascending-sorted snapshot of <see cref="_idx" />'s KEYS, used to
        ///   answer range queries in O(log n + k) via binary search instead of the former O(n)
        ///   parallel scan over every key (finding P4). This sorted array is the "ordered structure"
        ///   backing the range operations; the point operations keep using the dictionary unchanged,
        ///   so their semantics - multi-value buckets, key identity by Equals, and the B3 correctness
        ///   fixes (Between bounds, keeping the returned immutable list, dropping emptied keys) - are
        ///   preserved exactly. It is invalidated (set to <c>null</c>) whenever the KEY SET changes -
        ///   a new key, a removed key, an emptied key, a wipe or a load - and rebuilt on the next
        ///   range query. Adding another value under an EXISTING key does not change the key set and
        ///   therefore does not invalidate it; range queries always gather the current values from
        ///   <see cref="_idx" /> at query time. It is built/read only under the read lock and
        ///   invalidated only under the write lock (mutually exclusive via <c>AThreadSafeElement</c>),
        ///   so it never observes a torn <see cref="_idx" />; <c>volatile</c> gives readers a
        ///   consistent view of the published array.
        /// </summary>
        private volatile IComparable[] _sortedKeys;

        /// <summary>
        /// The description of the plugin
        /// </summary>
        private String _description = "A very very simple range index";

        /// <summary>
        /// The logger
        /// </summary>
        private ILogger<RangeIndex> _logger;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the RangeIndex class.
        /// </summary>
        public RangeIndex()
        {
        }

        #endregion

        #region IDisposable implementation
        public void Dispose()
        {
            _idx.Clear();
            _idx = null;
            _sortedKeys = null;
        }
        #endregion

        #region IPlugin implementation
        public void Initialize(IFallen8 fallen8, IDictionary<string, object> parameter)
        {
            _idx = new Dictionary<IComparable, ImmutableList<AGraphElementModel>>();
            _sortedKeys = null;
            _logger = fallen8.LoggerFactory.CreateLogger<RangeIndex>();
        }

        public string PluginName
        {

            get
            {
                return "RangeIndex";
            }
        }

        public Type PluginCategory
        {
            get
            {
                return typeof(IIndex);
            }
        }

        public string Description
        {
            get
            {
                return _description;
            }
        }

        public string Manufacturer
        {
            get
            {
                return "Henning Rauch";
            }
        }
        #endregion

        #region IFallen8Serializable implementation
        public void Save(SerializationWriter writer)
        {
            if (ReadResource())
            {
                writer.Write(0);//parameter
                writer.Write(_idx.Count);
                foreach (var aKV in _idx)
                {
                    writer.WriteObject(aKV.Key);
                    writer.Write(aKV.Value.Count);
                    foreach (var aItem in aKV.Value)
                    {
                        writer.Write(aItem.Id);
                    }
                }

                FinishReadResource();

                return;
            }

            throw new CollisionException();
        }

        public void Load(SerializationReader reader, IFallen8 fallen8)
        {

            if (WriteResource())
            {
                reader.ReadInt32();//parameter

                var keyCount = reader.ReadInt32();

                _idx = new Dictionary<IComparable, ImmutableList<AGraphElementModel>>(keyCount);

                for (var i = 0; i < keyCount; i++)
                {
                    var key = reader.ReadObject();
                    var value = new List<AGraphElementModel>();
                    var valueCount = reader.ReadInt32();
                    for (var j = 0; j < valueCount; j++)
                    {
                        var graphElementId = reader.ReadInt32();
                        AGraphElementModel graphElement;
                        if (fallen8.TryGetGraphElement(out graphElement, graphElementId))
                        {
                            value.Add(graphElement);
                        }
                        else
                        {
                            _logger.LogError(String.Format("Error while deserializing the index. Could not find the graph element \"{0}\"", graphElementId));
                        }
                    }
                    _idx.Add((IComparable)key, ImmutableList.CreateRange<AGraphElementModel>(value));
                }

                // The key set was rebuilt from disk; rebuild the sorted-key snapshot lazily (P4).
                _sortedKeys = null;

                FinishWriteResource();

                return;
            }

            throw new CollisionException();
        }
        #endregion

        #region IIndex implementation

        public Int32 CountOfKeys()
        {
            if (ReadResource())
            {
                var keyCount = _idx.Keys.Count;

                FinishReadResource();

                return keyCount;
            }

            throw new CollisionException();
        }

        public Int32 CountOfValues()
        {
            if (ReadResource())
            {
                var valueCount = _idx.Values.SelectMany(_ => _).Count();

                FinishReadResource();

                return valueCount;
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
                ImmutableList<AGraphElementModel> values;
                if (_idx.TryGetValue(key, out values))
                {
                    // Existing key: only the value bucket grows, the key set is unchanged, so the
                    // sorted-key snapshot stays valid (range queries read the current values).
                    _idx[key] = values.Add(graphElement);
                }
                else
                {
                    _idx.Add(key, ImmutableList.Create<AGraphElementModel>(graphElement));
                    // New key -> the sorted-key snapshot is stale (finding P4).
                    _sortedKeys = null;
                }

                FinishWriteResource();

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
                var foundSth = _idx.Remove(key);
                if (foundSth)
                {
                    // A key left the index -> the sorted-key snapshot is stale (finding P4).
                    _sortedKeys = null;
                }

                FinishWriteResource();

                return foundSth;
            }

            throw new CollisionException();
        }

        public void RemoveValue(AGraphElementModel graphElement)
        {
            if (WriteResource())
            {
                var toBeRemovedKeys = new List<IComparable>();

                foreach (var aKey in _idx.Keys.ToList())
                {
                    var updatedValues = _idx[aKey].Remove(graphElement);
                    _idx[aKey] = updatedValues;
                    if (updatedValues.Count == 0)
                    {
                        toBeRemovedKeys.Add(aKey);
                    }
                }

                toBeRemovedKeys.ForEach(_ => _idx.Remove(_));
                if (toBeRemovedKeys.Count > 0)
                {
                    // One or more keys were emptied and dropped -> the sorted-key snapshot is stale.
                    _sortedKeys = null;
                }

                FinishWriteResource();

                return;
            }

            throw new CollisionException();
        }

        public void Wipe()
        {
            if (WriteResource())
            {
                _idx.Clear();
                _sortedKeys = null;

                FinishWriteResource();

                return;
            }

            throw new CollisionException();
        }

        public IEnumerable<Object> GetKeys()
        {
            if (ReadResource())
            {
                var keys = new List<IComparable>(_idx.Keys);

                FinishReadResource();

                return keys;
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
                        yield return new KeyValuePair<object, ImmutableList<AGraphElementModel>>(aKv.Key, aKv.Value);
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
                ImmutableList<AGraphElementModel> graphElements;
                var foundSth = _idx.TryGetValue(key, out graphElements);

                result = foundSth ? graphElements : null;

                FinishReadResource();

                return foundSth;
            }

            throw new CollisionException();
        }
        #endregion

        #region IRangeIndex implementation
        public bool LowerThan(out ImmutableList<AGraphElementModel> result, IComparable key, bool includeKey)
        {
            if (ReadResource())
            {
                var keys = EnsureSortedKeys();
                // Keys lower than the given key: inclusive -> [0, UpperBound(key)) (all keys <= key);
                // exclusive -> [0, LowerBound(key)) (all keys < key). O(log n) to locate the end.
                int end = includeKey ? UpperBound(keys, key) : LowerBound(keys, key);
                result = GatherRange(keys, 0, end);

                FinishReadResource();

                return result != null;
            }

            throw new CollisionException();
        }

        public bool GreaterThan(out ImmutableList<AGraphElementModel> result, IComparable key, bool includeKey)
        {
            if (ReadResource())
            {
                var keys = EnsureSortedKeys();
                // Keys greater than the given key: inclusive -> [LowerBound(key), n) (all keys >= key);
                // exclusive -> [UpperBound(key), n) (all keys > key). O(log n) to locate the start.
                int start = includeKey ? LowerBound(keys, key) : UpperBound(keys, key);
                result = GatherRange(keys, start, keys.Length);

                FinishReadResource();

                return result != null;
            }

            throw new CollisionException();
        }

        public bool Between(out ImmutableList<AGraphElementModel> result, IComparable lowerLimit, IComparable upperLimit, bool includeLowerLimit, bool includeUpperLimit)
        {
            if (ReadResource())
            {
                var keys = EnsureSortedKeys();
                // Two binary searches bracket the range: start honours the lower bound's inclusivity,
                // end honours the upper bound's. An inverted range (lower > upper) yields start >= end,
                // which GatherRange returns as empty - matching the old "no key satisfies both" result.
                int start = includeLowerLimit ? LowerBound(keys, lowerLimit) : UpperBound(keys, lowerLimit);
                int end = includeUpperLimit ? UpperBound(keys, upperLimit) : LowerBound(keys, upperLimit);
                result = GatherRange(keys, start, end);

                FinishReadResource();

                return result != null;
            }

            throw new CollisionException();
        }
        #endregion

        #region range helpers (finding P4)

        /// <summary>
        ///   Returns the ascending-sorted key snapshot, building it once (lazily) when stale. Called
        ///   only while holding the read lock, so <see cref="_idx" /> is stable (writes are exclusive
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
