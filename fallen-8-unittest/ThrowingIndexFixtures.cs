// MIT License
//
// ThrowingIndexFixtures.cs
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
using System.Linq;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Serializer;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// A minimal, deliberately-broken index whose Save always throws. Used to prove that the
    /// per-index guards in PersistencyFactory skip a failing index instead of aborting the
    /// whole checkpoint. Everything else is an inert no-op - it never needs to hold data.
    /// </summary>
    /// <remarks>
    /// Deliberately <c>internal</c> and NOT <c>public</c>: <c>PluginFactory</c> rejects any
    /// candidate whose <c>Type.IsPublic</c> is false, so an internal top-level double stays out
    /// of plugin discovery exactly as the private nested type it was moved from did. Consumers
    /// register it by hand into <c>IndexFactory.Indices</c>; widening it to <c>public</c> would
    /// add it to every available-index enumeration in the suite (see the remarks on
    /// <see cref="ThrowingOnLoadIndex"/> for what that costs).
    /// </remarks>
    internal sealed class ThrowingOnSaveIndex : IIndex
    {
        public string PluginName => "ThrowingTestIndex";
        public Type PluginCategory => typeof(IIndex);
        public string Description => "A test index whose Save throws.";
        public string Manufacturer => "fallen-8 tests";

        // CLAIMS to be persistable (so it is NOT skipped silently by the CanPersist gate) but then
        // its Save throws - exactly the "genuine, unexpected serialization failure" path that must
        // be caught, logged at Error level and skipped without aborting the checkpoint.
        public bool CanPersist => true;
        public bool SupportsPointEqualityLookup => false; // inert test stub; never an equality index

        public void Initialize(IFallen8 fallen8, IDictionary<string, object> parameter) { }

        public void Save(SerializationWriter writer)
        {
            throw new InvalidOperationException("This index deliberately fails to serialize.");
        }

        public void Load(SerializationReader reader, IFallen8 fallen8) { }

        public int CountOfKeys() => 0;
        public int CountOfValues() => 0;
        public void AddOrUpdate(object key, AGraphElementModel graphElement) { }
        public bool TryRemoveKey(object key) => false;
        public void RemoveValue(AGraphElementModel graphElement) { }
        public void Wipe() { }
        public IEnumerable<object> GetKeys() => Enumerable.Empty<object>();

        public IEnumerable<KeyValuePair<object, ImmutableList<AGraphElementModel>>> GetKeyValues()
            => Enumerable.Empty<KeyValuePair<object, ImmutableList<AGraphElementModel>>>();

        public bool TryGetValue(out ImmutableList<AGraphElementModel> result, object key)
        {
            result = ImmutableList<AGraphElementModel>.Empty;
            return false;
        }

        public void Dispose() { }
    }

    /// <summary>
    /// A minimal index that SAVES fine but always throws on Load. It is deliberately a top-level
    /// public type with a public parameterless constructor so <c>PluginFactory</c> discovers it
    /// (nested types report <c>IsNestedPublic</c>, not <c>IsPublic</c>, so PluginFactory skips them):
    /// only then can <c>IndexFactory.OpenIndex</c> instantiate it by plugin name on load and actually
    /// invoke the throwing <see cref="Load"/>, exercising the per-index catch in
    /// <c>PersistencyFactory.LoadIndices</c>. Everything else is an inert no-op - it never needs to
    /// hold data.
    /// </summary>
    /// <remarks>
    /// Consequence of being globally discoverable: this double - and any other top-level public
    /// <see cref="IIndex"/> added to the test assembly - is enumerated by
    /// <c>PluginFactory.TryGetAvailablePlugins&lt;IIndex&gt;()</c> during test runs (that is what
    /// <c>IndexFactory</c> and the admin endpoint use to list index plugins). Any FUTURE test that
    /// asserts an exact set or count of available index plugins must therefore filter out these
    /// test-manufacturer doubles (e.g. by <see cref="Manufacturer"/> == "fallen-8 tests") rather than
    /// expecting only the production indices.
    /// </remarks>
    public sealed class ThrowingOnLoadIndex : IIndex
    {
        public const string TestPluginName = "ThrowingOnLoadTestIndex";

        public string PluginName => TestPluginName;
        public Type PluginCategory => typeof(IIndex);
        public string Description => "A test index that saves fine but throws on load.";
        public string Manufacturer => "fallen-8 tests";

        // Persistable: it serializes fine and reaches the manifest + its sidecar; the failure is on
        // the LOAD side, exercising the per-index catch in PersistencyFactory.LoadIndices.
        public bool CanPersist => true;
        public bool SupportsPointEqualityLookup => false; // inert test stub; never an equality index

        public void Initialize(IFallen8 fallen8, IDictionary<string, object> parameter) { }

        public void Save(SerializationWriter writer)
        {
            // Serializes cleanly (writes no payload), so it reaches the manifest and its sidecar.
        }

        public void Load(SerializationReader reader, IFallen8 fallen8)
        {
            throw new InvalidOperationException("This index deliberately fails to deserialize.");
        }

        public int CountOfKeys() => 0;
        public int CountOfValues() => 0;
        public void AddOrUpdate(object key, AGraphElementModel graphElement) { }
        public bool TryRemoveKey(object key) => false;
        public void RemoveValue(AGraphElementModel graphElement) { }
        public void Wipe() { }
        public IEnumerable<object> GetKeys() => Enumerable.Empty<object>();

        public IEnumerable<KeyValuePair<object, ImmutableList<AGraphElementModel>>> GetKeyValues()
            => Enumerable.Empty<KeyValuePair<object, ImmutableList<AGraphElementModel>>>();

        public bool TryGetValue(out ImmutableList<AGraphElementModel> result, object key)
        {
            result = ImmutableList<AGraphElementModel>.Empty;
            return false;
        }

        public void Dispose() { }
    }
}
