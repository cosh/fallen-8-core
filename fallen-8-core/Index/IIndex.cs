// MIT License
//
// IIndex.cs
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
using System.Collections.ObjectModel;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Persistency;
using NoSQL.GraphDB.Core.Plugin;

namespace NoSQL.GraphDB.Core.Index
{
    /// <summary>
    ///   The Fallen8 index interface.
    /// </summary>
    public interface IIndex : IPlugin, IFallen8Serializable
    {
        /// <summary>
        ///   THE trim-suppression message for an index <c>Save</c>/<c>Load</c> that round-trips an
        ///   arbitrary KEY through the reflective object codec (the bucket indices and the
        ///   single-value index today). It lives on the shared contract, not in one of those classes,
        ///   so every suppression site reads the same string and a future key-serializing index has an
        ///   obvious home to reference. The spatial index resolves helper TYPE NAMES rather than keys,
        ///   so its suppression carries its own message.
        /// </summary>
        internal const String KeysAreNotTrimSafe =
            "An index key is an arbitrary IComparable property value, round-tripped through the reflective object codec; the requirement is declared on SerializationWriter.WriteObject / SerializationReader.ReadObject. The engine reaches these members only through PersistencyFactory's annotated checkpoint save/load, which needs a filesystem - they are public, so a consumer calling them directly is not warned; keys of the directly-encoded types (primitives, string, DateTime, Guid) are trim-safe.";

        /// <summary>
        ///   Whether this index can persist itself to (and reload itself from) a checkpoint via
        ///   <see cref="IFallen8Serializable.Save" /> / <see cref="IFallen8Serializable.Load" />
        ///   (finding C9). An index that returns <c>false</c> is skipped silently on save (it is
        ///   dropped from the checkpoint manifest and must be recreated after load); an index that
        ///   returns <c>true</c> is expected to serialize and rehydrate to a functional, queryable
        ///   state. This is an explicit capability flag, replacing the former practice of throwing
        ///   <see cref="NotSupportedException" /> from <c>Save</c> to signal non-persistability, so the
        ///   persistency layer can tell an intentional opt-out apart from a genuine failure without
        ///   inspecting exception types. Every built-in index is persistable and returns <c>true</c>.
        /// </summary>
        Boolean CanPersist { get; }

        /// <summary>
        ///   Whether this index answers an exact point-equality lookup by an arbitrary key: an
        ///   element stored with <see cref="AddOrUpdate" /> under a key is retrievable by that same
        ///   key via <see cref="TryGetValue" />. This is the "equality" capability, and it is the
        ///   SINGLE home for the question "can this index be used where a caller needs to find
        ///   elements by an exact key" (entity dedup, structural link resolution, the <c>/status</c>
        ///   capability inventory). The dictionary/range/single-value/fulltext indices return
        ///   <c>true</c>; the vector index (approximate nearest-neighbour, no arbitrary-key lookup)
        ///   and the spatial index (geometry keys, which a non-geometry key can never match) return
        ///   <c>false</c>. A new index type declares its own answer here rather than being classified
        ///   by a type-switch elsewhere, so no caller can misclassify it.
        /// </summary>
        Boolean SupportsPointEqualityLookup { get; }

        /// <summary>
        ///   Count of the keys.
        /// </summary>
        /// <returns> The key count. </returns>
        Int32 CountOfKeys();

        /// <summary>
        ///   Count of the values.
        /// </summary>
        /// <returns> The value count. </returns>
        Int32 CountOfValues();

        /// <summary>
        ///   Tries to add or update.
        ///
        ///   <para>NEVER INDEX A REMOVED ELEMENT: an implementation must ignore a call whose element is
        ///   already tombstoned by a committed removal. This is a contract rather than an optimisation
        ///   because the reason lives in the engine, not in any one index: the write-end purge that
        ///   strips a removed element from every index runs ONCE per removal and never comes back for
        ///   it, so an entry added after the purge pins the element's body, persists into the next
        ///   checkpoint (whose load then logs an error per stale id), and after an id-reusing Trim can
        ///   surface as a hit on an element the caller never asked about. The window is not theoretical:
        ///   index repair reads a snapshot of live elements and adds them on the CALLING thread, so a
        ///   removal can commit between that snapshot and this call.</para>
        ///
        ///   <para>An implementation that takes a write lock must check inside it, which is what orders
        ///   the check against a concurrent removal taking the same lock.</para>
        /// </summary>
        /// <param name='key'> Key. </param>
        /// <param name='graphElement'> Graph element. </param>
        void AddOrUpdate(Object key, AGraphElementModel graphElement);

        /// <summary>
        ///   Tries to remove a key.
        /// </summary>
        /// <returns> <c>true</c> if something was removed; otherwise, <c>false</c> . </returns>
        /// <param name='key'> Key. </param>
        Boolean TryRemoveKey(Object key);

        /// <summary>
        ///   Remove a value.
        /// </summary>
        /// <param name='graphElement'> Graph element. </param>
        void RemoveValue(AGraphElementModel graphElement);

        /// <summary>
        ///   Wipe this instance.
        /// </summary>
        void Wipe();

        /// <summary>
        ///   Gets the keys.
        /// </summary>
        /// <returns> The keys. </returns>
        IEnumerable<Object> GetKeys();

        /// <summary>
        ///   Gets the key values.
        /// </summary>
        /// <returns> The key values. </returns>
        IEnumerable<KeyValuePair<object, ImmutableList<AGraphElementModel>>> GetKeyValues();

        /// <summary>
        ///   Gets the value.
        /// </summary>
        /// <returns> <c>true</c> if something was found; otherwise, <c>false</c> . </returns>
        /// <param name='result'> Result. </param>
        /// <param name='key'> Key. </param>
        Boolean TryGetValue(out ImmutableList<AGraphElementModel> result, Object key);
    }
}
