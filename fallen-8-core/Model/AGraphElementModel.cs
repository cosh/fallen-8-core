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
        private volatile KeyValuePair<String, Object>[] _properties;

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

        #region embeddings (feature element-embeddings)

        /// <summary>
        ///   The reserved property-key prefix behind which named embeddings are stored in v1
        ///   (feature element-embeddings). The PHYSICAL representation is an implementation detail
        ///   owned entirely by this class: every caller goes through
        ///   <see cref="TryGetEmbedding" />, so the layout can be promoted to a dedicated field
        ///   later without touching a single call site. Storing the vector as a property makes it
        ///   element state - checkpointed, WAL-covered, and bulk-import/exportable - for free.
        /// </summary>
        public const String EmbeddingPropertyPrefix = "$embedding:";

        /// <summary>
        ///   The reserved property-key prefix of the model-identity stamp an embedding provider
        ///   writes NEXT TO a generated embedding (feature embedding-provider), so query-time
        ///   drift after a model change is detectable. Stored/read through the same accessor
        ///   discipline as the vector itself.
        /// </summary>
        public const String EmbeddingModelStampPrefix = "$embeddingModel:";

        /// <summary>The default embedding name.</summary>
        public const String DefaultEmbeddingName = "default";

        /// <summary>Maximum length of an embedding name.</summary>
        public const Int32 MaxEmbeddingNameLength = 64;

        /// <summary>The precomputed property id of the default embedding (hot-path, no concat).</summary>
        private static readonly String DefaultEmbeddingPropertyId = EmbeddingPropertyPrefix + DefaultEmbeddingName;

        /// <summary>
        ///   Whether <paramref name="name" /> is a valid embedding name:
        ///   <c>^[A-Za-z0-9_-]{1,64}$</c> (the stored-query name grammar, shortened).
        /// </summary>
        public static Boolean IsValidEmbeddingName(String name)
        {
            if (String.IsNullOrEmpty(name) || name.Length > MaxEmbeddingNameLength)
            {
                return false;
            }

            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                var valid = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                            (c >= '0' && c <= '9') || c == '_' || c == '-';
                if (!valid)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>The reserved property id carrying the named embedding.</summary>
        internal static String GetEmbeddingPropertyId(String name)
        {
            return String.Equals(name, DefaultEmbeddingName, StringComparison.Ordinal)
                ? DefaultEmbeddingPropertyId
                : EmbeddingPropertyPrefix + name;
        }

        /// <summary>
        ///   When <paramref name="propertyId" /> is a reserved embedding property id, yields the
        ///   embedding name it carries (used by the engine's bound-index projection hooks).
        /// </summary>
        internal static Boolean TryGetEmbeddingName(String propertyId, out String name)
        {
            if (propertyId != null &&
                propertyId.Length > EmbeddingPropertyPrefix.Length &&
                propertyId.StartsWith(EmbeddingPropertyPrefix, StringComparison.Ordinal))
            {
                name = propertyId.Substring(EmbeddingPropertyPrefix.Length);
                return true;
            }

            name = null;
            return false;
        }

        /// <summary>
        ///   Tries to get the element's named embedding. This is the ONLY coupling point to the
        ///   physical embedding representation (feature element-embeddings). The returned span
        ///   aliases the stored vector, which is replaced (never mutated in place) on update -
        ///   the same copy-on-write publication discipline as every property read, so a lock-free
        ///   reader always sees a fully built vector. Callers must not hold the span across a
        ///   point where they need the LATEST value.
        /// </summary>
        /// <param name="vector"> The embedding vector. </param>
        /// <param name="name"> The embedding name. </param>
        /// <returns> <c>true</c> if the element carries the named embedding; otherwise <c>false</c>. </returns>
        public Boolean TryGetEmbedding(out ReadOnlySpan<Single> vector, String name = DefaultEmbeddingName)
        {
            return TryGetEmbeddingByPropertyId(out vector, GetEmbeddingPropertyId(name));
        }

        /// <summary>
        ///   Hot-path variant taking the precomputed reserved property id (no string concat per
        ///   element); never throws on a non-vector value stored under the reserved key.
        /// </summary>
        internal Boolean TryGetEmbeddingByPropertyId(out ReadOnlySpan<Single> vector, String embeddingPropertyId)
        {
            if (TryGetProperty<Object>(out var value, embeddingPropertyId) && value is Single[] array)
            {
                vector = array;
                return true;
            }

            vector = default;
            return false;
        }

        /// <summary>The reserved property id carrying the named embedding's model stamp.</summary>
        internal static String GetEmbeddingModelStampPropertyId(String name)
        {
            return EmbeddingModelStampPrefix + name;
        }

        /// <summary>
        ///   Tries to get the model-identity stamp stored next to the named embedding (written
        ///   by the embedding provider; absent for bring-your-own-vector embeddings).
        /// </summary>
        public Boolean TryGetEmbeddingModelStamp(out String stamp, String name = DefaultEmbeddingName)
        {
            if (TryGetProperty<Object>(out var value, GetEmbeddingModelStampPropertyId(name)) && value is String text)
            {
                stamp = text;
                return true;
            }

            stamp = null;
            return false;
        }

        #endregion

        #region internal methods

        /// <summary>
        ///   Serialization-only accessor to the raw, key-sorted compact property store (finding N1).
        ///   Returns the live backing array (its entries sorted ordinally by key) or <c>null</c> when
        ///   the element has no properties. The persistency layer emits this directly instead of
        ///   building a throwaway <see cref="ImmutableDictionary{TKey,TValue}" /> per element via
        ///   <see cref="GetAllProperties" /> on every save, saving that per-element allocation.
        ///
        ///   The returned reference is safe to read: the store is published copy-on-write and never
        ///   mutated in place (the same single-writer / lock-free-reader discipline
        ///   <see cref="TryGetProperty{TProperty}" /> relies on), and save runs over an O(1) element
        ///   snapshot. Emitting this array changes the on-disk property BYTE ORDER to ordinal key
        ///   order (the former dictionary emitted hash order); this is load-compatible because the
        ///   constructor rebuilds - and re-sorts - the store from whatever order it reads, so any
        ///   stored order round-trips. Callers must NOT mutate the returned array.
        /// </summary>
        internal KeyValuePair<String, Object>[] GetPropertyStoreForSerialization()
        {
            return _properties;
        }

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
        /// <exception cref='ArgumentException'>Is thrown when the key already exists with a different value.</exception>
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
        internal bool RemoveProperty(String propertyId)
        {
            var store = _properties;
            if (store == null)
            {
                return false;
            }

            int idx = IndexOfKey(store, propertyId);
            if (idx < 0)
            {
                return false;
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
            return true;
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

        /// <summary>
        ///   Frees the heavy per-element state of a TOMBSTONE - a removed element whose store slot is
        ///   kept so ids stay stable (feature trim-reader-safety Part B). Nulls the property store;
        ///   <see cref="VertexModel"/> overrides this to also null its adjacency. Each is a single
        ///   volatile write, so a lock-free reader observes either the prior fully-built field or
        ///   <c>null</c> - never a torn value - the same publication discipline that governs every
        ///   property/adjacency mutation. MUST be called only for an already-committed, removed element
        ///   on the single writer thread (post-commit): the removal will not roll back and the volatile
        ///   publish makes the reader race benign (a stale reference to a removed element now reads
        ///   null state rather than stale contents, consistent with the element being excluded from
        ///   searches and its live adjacency already detached by the removal cascade).
        /// </summary>
        internal virtual void ReleaseBodyForTombstone()
        {
            _properties = null;
        }

        /// <summary>
        ///   Canonicalizes a property value exactly as <see cref="SetProperty" /> does, so a caller
        ///   tracking intra-batch pending values compares canonical-to-canonical (matching the values
        ///   held in the store).
        /// </summary>
        internal static Object CanonicalizeProperty(Object value)
        {
            return Canonicalize(value);
        }

        /// <summary>
        ///   Value-equality using the same semantics as <see cref="SetProperty" />'s conflict check.
        /// </summary>
        internal static Boolean ArePropertyValuesEqual(Object a, Object b)
        {
            return ValueEquals(a, b);
        }

        /// <summary>
        ///   Restores a property to its pre-transaction state as part of a rolled-back batch
        ///   (feature transaction-atomicity): drops whatever value the batch set (if any) and, when
        ///   the key existed before the batch, re-adds its prior value. Implemented via
        ///   <see cref="RemoveProperty" /> + <see cref="SetProperty" /> so the re-add never hits the
        ///   conflict throw (the key is absent after the remove).
        /// </summary>
        internal void RestoreProperty(String propertyId, Boolean hadValueBefore, Object priorValue)
        {
            RemoveProperty(propertyId);
            if (hadValueBefore)
            {
                SetProperty(propertyId, priorValue);
            }
        }

        #endregion
    }
}
