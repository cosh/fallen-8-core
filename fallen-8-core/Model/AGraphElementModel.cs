// MIT License
//
// AGraphElement.cs
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
using NoSQL.GraphDB.Core.Error;
using NoSQL.GraphDB.Core.Helper;

namespace NoSQL.GraphDB.Core.Model
{
    /// <summary>
    ///   A graph element.
    /// </summary>
    public abstract class AGraphElementModel
    {
        #region Data

        /// <summary>
        ///   The identifier of this graph element.
        /// </summary>
        public Int32 Id;

        /// <summary>
        /// The label of the graph element
        /// </summary>
        public String Label;

        /// <summary>
        ///   The creation date.
        /// </summary>
        public readonly UInt32 CreationDate;

        /// <summary>
        ///   The modification date.
        /// </summary>
        public UInt32 ModificationDate;

        /// <summary>
        ///   The properties, stored as a compact array of key/value pairs kept sorted by key
        ///   (ordinal), or <c>null</c> when the element has no properties (finding M1). This
        ///   replaces the former per-element <see cref="ImmutableDictionary{TKey,TValue}" />, whose
        ///   hash-array-mapped-trie carried ~100 bytes of container overhead for even a single
        ///   property; the array is one object plus 16 bytes per entry, and a binary search over it
        ///   is as fast as the dictionary lookup for the handful-of-properties case that dominates.
        ///   An absent/empty property set allocates nothing (the field stays <c>null</c>).
        ///
        ///   Concurrency: mutation is copy-on-write (a new array is published by a single reference
        ///   assignment; the array is never mutated in place) and only ever runs on the single
        ///   transaction-writer thread, while lock-free readers capture the field into a local
        ///   once - the same discipline the immutable dictionary provided, so a reader always sees
        ///   a fully built, self-consistent array.
        /// </summary>
        private KeyValuePair<String, Object>[] _properties;

        /// <summary>
        ///  Defines if the object has been removed. If it is set to true then it will not be returned in searches
        /// </summary>
        internal bool _removed = false;

        #endregion

        #region value canonicalization (de-boxing, finding M1)

        /// <summary>The single shared box for <c>true</c>.</summary>
        private static readonly Object BoxedTrue = true;

        /// <summary>The single shared box for <c>false</c>.</summary>
        private static readonly Object BoxedFalse = false;

        /// <summary>Lowest integer value with a cached box.</summary>
        private const int SmallIntMin = -128;

        /// <summary>Number of cached small-integer boxes (covers <c>-128..383</c>).</summary>
        private const int SmallIntCount = 512;

        /// <summary>Cached boxes for common small integers, so N elements holding, say, the int 0 share one box.</summary>
        private static readonly Object[] SmallInts = BuildSmallIntCache();

        private static Object[] BuildSmallIntCache()
        {
            var cache = new Object[SmallIntCount];
            for (int i = 0; i < SmallIntCount; i++)
            {
                cache[i] = SmallIntMin + i;
            }
            return cache;
        }

        /// <summary>
        ///   Returns a canonical, shared box for the common value-typed properties (booleans and
        ///   small integers) so that many elements storing the same value share one boxed instance
        ///   instead of each retaining its own (finding M1). The returned object is always
        ///   value-equal to and of the same runtime type as the input, so typing and round-trip
        ///   fidelity are unchanged; any other value (including <c>null</c>) is returned as-is.
        /// </summary>
        private static Object Canonicalize(Object value)
        {
            if (value is bool b)
            {
                return b ? BoxedTrue : BoxedFalse;
            }

            if (value is int i && i >= SmallIntMin && i < SmallIntMin + SmallIntCount)
            {
                return SmallInts[i - SmallIntMin];
            }

            return value;
        }

        /// <summary>
        ///   Builds the compact, key-sorted property store from a source dictionary, or returns
        ///   <c>null</c> for an absent/empty map (keeping the "no container for no properties"
        ///   invariant). Values are canonicalized for de-boxing.
        /// </summary>
        private static KeyValuePair<String, Object>[] BuildStore(Dictionary<String, Object> properties)
        {
            if (properties == null || properties.Count == 0)
            {
                return null;
            }

            var store = new KeyValuePair<String, Object>[properties.Count];
            var idx = 0;
            foreach (var kv in properties)
            {
                store[idx++] = new KeyValuePair<String, Object>(kv.Key, Canonicalize(kv.Value));
            }

            // Dictionary keys are unique, so no duplicates; sort by key so lookups binary-search.
            Array.Sort(store, static (a, b) => String.CompareOrdinal(a.Key, b.Key));
            return store;
        }

        /// <summary>
        ///   Binary-searches the (key-sorted) store for a key. Returns the index when found, or the
        ///   bitwise complement of the insertion point (a negative value) when not.
        /// </summary>
        private static int IndexOfKey(KeyValuePair<String, Object>[] store, String key)
        {
            int lo = 0;
            int hi = store.Length - 1;
            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                int cmp = String.CompareOrdinal(store[mid].Key, key);
                if (cmp == 0)
                {
                    return mid;
                }
                if (cmp < 0)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return ~lo;
        }

        private static Boolean ValueEquals(Object a, Object b)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }
            if (a == null || b == null)
            {
                return false;
            }
            return a.Equals(b);
        }

        #endregion

        #region constructor

        /// <summary>
        ///   Initializes a new instance of the <see cref="AGraphElementModel" /> class.
        /// </summary>
        /// <param name='id'> Identifier. </param>
        /// <param name='creationDate'> Creation date. </param>
        /// <param name='label'> Label. </param>
        /// <param name='properties'> Properties. </param>
        protected AGraphElementModel(Int32 id, UInt32 creationDate, String label = null, Dictionary<String, Object> properties = null)
        {
            Id = id;
            CreationDate = creationDate;
            ModificationDate = 0;
            _properties = BuildStore(properties);
            Label = label;
        }

        #endregion

        #region public methods

        /// <summary>
        ///  Gets the creation date
        /// </summary>
        /// <returns> Creation date </returns>
        public DateTime GetCreationDate()
        {
            return DateHelper.GetDateTimeFromUnixTimeStamp(CreationDate);
        }

        /// <summary>
        ///  Gets the modification date
        /// </summary>
        /// <returns> Modification date </returns>
        public DateTime GetModificationDate()
        {
            return DateHelper.GetDateTimeFromUnixTimeStamp(CreationDate + ModificationDate);
        }

        /// <summary>
        ///   Returns the count of properties
        /// </summary>
        /// <returns> Count of Properties </returns>
        public Int32 GetPropertyCount()
        {
            var store = _properties;
            return store == null ? 0 : store.Length;
        }

        /// <summary>
        ///   Gets all properties. Returns an immutable snapshot (never <c>null</c>; empty when the
        ///   element has no properties), built on demand from the compact store so the public
        ///   contract - an <see cref="ImmutableDictionary{TKey,TValue}" /> of the current
        ///   properties - is unchanged.
        /// </summary>
        /// <returns> All properties. </returns>
        public ImmutableDictionary<String, Object> GetAllProperties()
        {
            var store = _properties;
            if (store == null || store.Length == 0)
            {
                return ImmutableDictionary.Create<String, Object>();
            }

            var builder = ImmutableDictionary.CreateBuilder<String, Object>();
            for (int i = 0; i < store.Length; i++)
            {
                builder[store[i].Key] = store[i].Value;
            }
            return builder.ToImmutable();
        }

        /// <summary>
        ///   Tries the get property.
        /// </summary>
        /// <typeparam name="TProperty"> Type of the property </typeparam>
        /// <param name="result"> Result. </param>
        /// <param name="propertyId"> Property identifier. </param>
        /// <returns> <c>true</c> if something was found; otherwise, <c>false</c> . </returns>
        public Boolean TryGetProperty<TProperty>(out TProperty result, String propertyId)
        {
            // Capture the store once: a concurrent single-writer mutation republishes a new array
            // by reference, so a local read yields a self-consistent snapshot.
            var store = _properties;
            if (store != null)
            {
                int idx = IndexOfKey(store, propertyId);
                if (idx >= 0)
                {
                    result = (TProperty)store[idx].Value;

                    return true;
                }
            }

            result = default(TProperty);

            return false;
        }

        #endregion

        #region internal methods

        /// <summary>
        ///   Trims the graph element
        /// </summary>
        internal virtual void Trim()
        {
            //do nothing
        }

        /// <summary>
        ///   Sets a property
        /// </summary>
        /// <returns> <c>true</c> if it was an update; otherwise, <c>false</c> . </returns>
        /// <param name='propertyId'> If set to <c>true</c> property identifier. </param>
        /// <param name='property'> If set to <c>true</c> property. </param>
        /// <exception cref='CollisionException'>Is thrown when the collision exception.</exception>
        internal void SetProperty(String propertyId, object property)
        {
            property = Canonicalize(property);
            var store = _properties;

            if (store == null)
            {
                // First property on this element: allocate a one-entry store. (The former
                // ImmutableDictionary field threw here for a null store; creating it is strictly
                // safer and matches the "add" intent.)
                _properties = new[] { new KeyValuePair<String, Object>(propertyId, property) };
            }
            else
            {
                int idx = IndexOfKey(store, propertyId);
                if (idx >= 0)
                {
                    // Key present: preserve the previous ImmutableDictionary.Add semantics - a
                    // no-op when the value is equal, an error when it differs (callers update via
                    // RemoveProperty followed by SetProperty).
                    if (!ValueEquals(store[idx].Value, property))
                    {
                        throw new ArgumentException(
                            "An element with the same key but a different value already exists.", nameof(propertyId));
                    }
                }
                else
                {
                    // Insert at the sorted position, copy-on-write (never mutate the live array).
                    int insert = ~idx;
                    var next = new KeyValuePair<String, Object>[store.Length + 1];
                    Array.Copy(store, 0, next, 0, insert);
                    next[insert] = new KeyValuePair<String, Object>(propertyId, property);
                    Array.Copy(store, insert, next, insert + 1, store.Length - insert);
                    _properties = next;
                }
            }

            ModificationDate = DateHelper.GetModificationDate(CreationDate);
        }

        /// <summary>
        ///   Tries to remove a property.
        /// </summary>
        /// <returns> <c>true</c> if the property was removed; otherwise, <c>false</c> if there was no such property. </returns>
        /// <param name='propertyId'> If set to <c>true</c> property identifier. </param>
        /// <exception cref='CollisionException'>Is thrown when the collision exception.</exception>
        internal void RemoveProperty(String propertyId)
        {
            var store = _properties;
            if (store == null)
            {
                return;
            }

            int idx = IndexOfKey(store, propertyId);
            if (idx < 0)
            {
                return;
            }

            if (store.Length == 1)
            {
                // Last property removed: drop the container so an empty property set stays null.
                _properties = null;
            }
            else
            {
                var next = new KeyValuePair<String, Object>[store.Length - 1];
                Array.Copy(store, 0, next, 0, idx);
                Array.Copy(store, idx + 1, next, idx, store.Length - idx - 1);
                _properties = next;
            }

            ModificationDate = DateHelper.GetModificationDate(CreationDate);
        }

        /// <summary>
        /// Set the label for the graph element
        /// </summary>
        /// <param name="newLabel">The new Label</param>
        internal void SetLabel(String newLabel)
        {
            Label = newLabel;
        }

        /// <summary>
        /// Sets the id of the element
        /// </summary>
        /// <param name="newId">The new id</param>
        internal void SetId(Int32 newId)
        {
            Id = newId;
        }

        /// <summary>
        /// Marks the graph element as removed
        /// </summary>
        internal void MarkAsRemoved()
        {
            _removed = true;
        }

        // <summary>
        /// Marks the graph element as not removed
        /// </summary>
        internal void MarkAsNotRemoved()
        {
            _removed = false;
        }

        #endregion
    }
}
