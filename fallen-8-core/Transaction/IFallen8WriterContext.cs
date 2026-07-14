// MIT License
//
// IFallen8WriterContext.cs
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
using System.Linq;
using NoSQL.GraphDB.Core.Model;

namespace NoSQL.GraphDB.Core.Transaction
{
    /// <summary>
    ///   The sanctioned mutation surface handed to a <see cref="DelegateTransaction"/> body (feature
    ///   plugin-write-transactions). It exposes the safe subset of the engine's mutation operations
    ///   behind a stable, public contract so a plugin can compose a mutation against the LIVE graph
    ///   WITHOUT touching the sealed <c>Fallen8</c> type or implementing the frozen
    ///   <see cref="ATransaction"/> vocabulary.
    ///
    ///   <para>
    ///   The body runs on the single transaction-writer thread (the <see cref="DelegateTransaction"/>
    ///   is executed there like any built-in), so every call here is a single-writer mutation. The
    ///   context is valid ONLY for the duration of the body: it is invalidated when the body returns, so
    ///   a plugin cannot stash it and mutate off the writer thread later (every method then throws
    ///   <see cref="InvalidOperationException"/>).
    ///   </para>
    ///
    ///   <para>
    ///   Lifecycle operations (<c>Save</c>/<c>Load</c>/<c>Trim</c>/<c>TabulaRasa</c>) are deliberately
    ///   ABSENT: they mutate the id space / persistence baseline and would break WAL/snapshot ordering
    ///   if interleaved inside a body. They remain caller-enqueued lifecycle transactions.
    ///   </para>
    /// </summary>
    public interface IFallen8WriterContext
    {
        /// <summary>Creates a vertex and returns it. Reversible on rollback.</summary>
        VertexModel CreateVertex(UInt32 creationDate, String label, IDictionary<String, Object> properties = null);

        /// <summary>Creates a batch of vertices in one append and returns them. Reversible on rollback.</summary>
        IReadOnlyList<VertexModel> CreateVertices(IEnumerable<VertexDefinition> definitions);

        /// <summary>
        ///   Creates an edge between two LIVE vertices. Returns <c>false</c> (no throw) when either
        ///   endpoint is missing/removed - matching the engine's null-resolution. Reversible on rollback.
        /// </summary>
        Boolean TryCreateEdge(out EdgeModel edge, Int32 sourceVertexId, String edgePropertyId, Int32 targetVertexId,
            UInt32 creationDate, String label = null, IDictionary<String, Object> properties = null);

        /// <summary>Sets (adds or updates) a property on a graph element. Reversible on rollback.</summary>
        void SetProperty(Int32 graphElementId, String propertyId, Object value);

        /// <summary>Removes a property from a graph element. Reversible on rollback.</summary>
        void RemoveProperty(Int32 graphElementId, String propertyId);

        /// <summary>
        ///   Removes a graph element (vertex or edge). NOTE: unlike the create/property operations, a
        ///   removal performed here is NOT reverted by a rolled-back <see cref="DelegateTransaction"/>
        ///   (the soft-delete model has no generic in-body "un-remove"); a body that removes elements
        ///   therefore has a weaker rollback guarantee than one that only creates/sets. Callers needing
        ///   reversible bulk removal should use the dedicated <c>RemoveGraphElementsTransaction</c>.
        /// </summary>
        Boolean TryRemoveGraphElement(Int32 graphElementId);
    }

    /// <summary>
    ///   The concrete <see cref="IFallen8WriterContext"/> a <see cref="DelegateTransaction"/> binds to
    ///   the <see cref="Fallen8"/> it received, with an undo journal so a rolled-back body has no
    ///   observable effect for the create/property surface (feature plugin-write-transactions §3.3). It
    ///   forwards to the engine's <c>*_internal</c> mutation methods (all on the single writer thread).
    /// </summary>
    internal sealed class DelegateWriterContext : IFallen8WriterContext
    {
        private readonly Fallen8 _f8;

        // Undo journal: created element ids (removed on rollback, in reverse) and property changes
        // (restored on rollback via the tested RestoreProperties_internal).
        private readonly List<Int32> _createdIds = new List<Int32>();
        private readonly List<PropertyMutationUndo> _propertyUndo = new List<PropertyMutationUndo>();

        private bool _valid = true;

        internal DelegateWriterContext(Fallen8 f8)
        {
            _f8 = f8;
        }

        /// <summary>Invalidates the context once the body returns; later use throws.</summary>
        internal void Invalidate()
        {
            _valid = false;
        }

        /// <summary>
        ///   Compensates the journalled create/property mutations, restoring the graph to its
        ///   pre-transaction state (properties restored, created elements removed) - the rollback the
        ///   transaction manager invokes on a faulting body.
        /// </summary>
        internal void Compensate()
        {
            _f8.RestoreProperties_internal(_propertyUndo);
            for (var i = _createdIds.Count - 1; i >= 0; i--)
            {
                _f8.TryRemoveGraphElement_private(_createdIds[i]);
            }
        }

        private void EnsureValid()
        {
            if (!_valid)
            {
                throw new InvalidOperationException(
                    "The Fallen8 writer context is only valid for the duration of the DelegateTransaction body; it cannot be used afterwards.");
            }
        }

        private static Dictionary<String, Object> Copy(IDictionary<String, Object> properties)
        {
            return properties == null ? null : new Dictionary<String, Object>(properties);
        }

        public VertexModel CreateVertex(UInt32 creationDate, String label, IDictionary<String, Object> properties = null)
        {
            EnsureValid();
            var vertex = _f8.CreateVertex_internal(creationDate, label, Copy(properties));
            _createdIds.Add(vertex.Id);
            return vertex;
        }

        public IReadOnlyList<VertexModel> CreateVertices(IEnumerable<VertexDefinition> definitions)
        {
            EnsureValid();
            var list = definitions?.ToList() ?? new List<VertexDefinition>();
            var created = _f8.CreateVertices_internal(list, out var inputValid);
            if (!inputValid)
            {
                throw new ArgumentException("The vertex definitions were invalid (a null definition).", nameof(definitions));
            }

            foreach (var vertex in created)
            {
                _createdIds.Add(vertex.Id);
            }
            return created;
        }

        public Boolean TryCreateEdge(out EdgeModel edge, Int32 sourceVertexId, String edgePropertyId, Int32 targetVertexId,
            UInt32 creationDate, String label = null, IDictionary<String, Object> properties = null)
        {
            EnsureValid();
            edge = _f8.CreateEdge_internal(sourceVertexId, edgePropertyId, targetVertexId, creationDate, label, Copy(properties));
            if (edge == null)
            {
                return false; // a missing/removed endpoint - no throw, nothing to compensate
            }

            _createdIds.Add(edge.Id);
            return true;
        }

        public void SetProperty(Int32 graphElementId, String propertyId, Object value)
        {
            EnsureValid();
            // Captures the prior value into the journal BEFORE applying, so rollback restores it
            // (reuses the tested transaction-atomicity undo path).
            _f8.SetPropertyWithUndo_internal(graphElementId, propertyId, value, _propertyUndo);
        }

        public void RemoveProperty(Int32 graphElementId, String propertyId)
        {
            EnsureValid();

            // Capture the prior value before removing, so rollback restores it.
            if (_f8.TryGetGraphElement(out var graphElement, graphElementId))
            {
                var hadValue = graphElement.TryGetProperty<Object>(out var priorValue, propertyId);
                _propertyUndo.Add(new PropertyMutationUndo(graphElementId, propertyId, hadValue, priorValue));
            }

            _f8.RemoveProperty_internal(graphElementId, propertyId);
        }

        public Boolean TryRemoveGraphElement(Int32 graphElementId)
        {
            EnsureValid();
            // Not journalled: a removal is not reverted by a rolled-back DelegateTransaction (see the
            // interface remark).
            return _f8.TryRemoveGraphElement_private(graphElementId);
        }
    }
}
