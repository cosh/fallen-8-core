// MIT License
//
// ABucketIndex.cs
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
using System.Linq;
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
    ///   consumer today.</para>
    /// </summary>
    public abstract class ABucketIndex : AThreadSafeElement, IIndex
    {
        #region Data

        /// <summary>The index dictionary: key -&gt; bucket of elements.</summary>
        protected Dictionary<IComparable, ImmutableList<AGraphElementModel>> _idx;

        /// <summary>
        ///   Reverse map: element -&gt; the set of keys it appears under (feature
        ///   index-lifecycle 3.4). Keyed by element REFERENCE identity (VertexModel/EdgeModel
        ///   use reference <c>Equals</c> + an identity hash), which stays valid across a Trim
        ///   id-renumber. Maintained under the same write lock as <see cref="_idx"/>.
        /// </summary>
        protected Dictionary<AGraphElementModel, HashSet<IComparable>> _reverse;

        /// <summary>The logger, typed to the concrete index class.</summary>
        protected ILogger _logger;

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
                    return _idx.Values.SelectMany(_ => _).Count();
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
                    ImmutableList<AGraphElementModel> values;
                    if (_idx.TryGetValue(key, out values))
                    {
                        // Existing key: only the bucket grows; the key set is unchanged.
                        _idx[key] = values.Add(graphElement);
                    }
                    else
                    {
                        _idx.Add(key, ImmutableList.Create<AGraphElementModel>(graphElement));
                        OnKeySetChanged();
                    }

                    // Maintain the reverse map so RemoveValue is O(affected keys) (index-lifecycle 3.4).
                    if (_reverse.TryGetValue(graphElement, out var keysForElement))
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
                    // removing it, so the reverse map cannot dangle (index-lifecycle 3.4).
                    if (_idx.TryGetValue(key, out var bucket))
                    {
                        foreach (var element in bucket)
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
                    // O(affected keys): the reverse map names exactly the buckets the element is
                    // in, so there is no full-key-set scan (index-lifecycle 3.4).
                    var keySetShrank = false;
                    if (_reverse.TryGetValue(graphElement, out var keysForElement))
                    {
                        foreach (var aKey in keysForElement)
                        {
                            if (_idx.TryGetValue(aKey, out var bucket))
                            {
                                var updatedValues = bucket.RemoveAll(x => ReferenceEquals(x, graphElement));
                                if (updatedValues.Count == 0)
                                {
                                    _idx.Remove(aKey);
                                    keySetShrank = true;
                                }
                                else
                                {
                                    _idx[aKey] = updatedValues;
                                }
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
                        yield return new KeyValuePair<object, ImmutableList<AGraphElementModel>>(aKv.Key, aKv.Value);
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
                    var foundSth = _idx.TryGetValue(key, out var graphElements);
                    result = foundSth ? graphElements : null;
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

        public Boolean CanPersist => true;

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
                        writer.WriteObject(aKV.Key);
                        writer.Write(aKV.Value.Count);
                        foreach (var aItem in aKV.Value)
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

        public void Load(SerializationReader reader, IFallen8 fallen8)
        {
            if (WriteResource())
            {
                try
                {
                    reader.ReadInt32(); //parameter

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
                            if (fallen8.TryGetGraphElement(out var graphElement, graphElementId))
                            {
                                value.Add(graphElement);
                            }
                            else
                            {
                                _logger.LogError(String.Format(
                                    "[{0}] Error while deserializing the index. Could not find the graph element \"{1}\"",
                                    GetType().Name, graphElementId));
                            }
                        }
                        _idx.Add((IComparable)key, ImmutableList.CreateRange<AGraphElementModel>(value));
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
            Dictionary<IComparable, ImmutableList<AGraphElementModel>> idx)
        {
            var reverse = new Dictionary<AGraphElementModel, HashSet<IComparable>>();
            foreach (var kv in idx)
            {
                foreach (var element in kv.Value)
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
            _idx = new Dictionary<IComparable, ImmutableList<AGraphElementModel>>();
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
