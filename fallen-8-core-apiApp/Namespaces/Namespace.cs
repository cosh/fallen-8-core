// MIT License
//
// Namespace.cs
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
using NoSQL.GraphDB.Core;

namespace NoSQL.GraphDB.App.Namespaces
{
    /// <summary>
    ///   One namespace inside a Fallen-8: a named, isolated graph owning exactly one Fallen-8
    ///   engine (feature graph-namespaces; terminology is spec §1). The name is the mutable
    ///   address key (rename is a metadata operation); the id is immutable and collection-assigned,
    ///   and is what on-disk locations and metric tags are keyed by, so user-supplied names never
    ///   become filesystem paths or tag values.
    /// </summary>
    public sealed class Namespace
    {
        internal Namespace(String name, String id, Fallen8 engine, DateTime createdAtUtc)
        {
            Name = name;
            Id = id;
            Engine = engine;
            CreatedAtUtc = createdAtUtc;
        }

        /// <summary>The unique, URL-addressable name (<c>^[a-z0-9-]{1,63}$</c>); changed by rename.</summary>
        public String Name { get; internal set; }

        /// <summary>The immutable collection-assigned id (e.g. <c>ns-20260723-101502-3f2a</c>).</summary>
        public String Id { get; }

        /// <summary>The Fallen-8 engine that holds this namespace's graph.</summary>
        public Fallen8 Engine { get; }

        /// <summary>When the namespace was created (UTC).</summary>
        public DateTime CreatedAtUtc { get; }

        /// <summary>
        ///   Lifecycle state. Creation is synchronous in v1, so this is always
        ///   <see cref="NamespaceState.Ready"/>; the enum exists so a future async provisioning
        ///   path is not a breaking contract change.
        /// </summary>
        public NamespaceState State { get; internal set; } = NamespaceState.Ready;

        /// <summary>
        ///   Set exactly once, under the collection's dispose gate, when the engine is disposed —
        ///   a drop and the collection's own disposal can both reach an engine, and
        ///   <c>Fallen8.Dispose</c> is not idempotent.
        /// </summary>
        internal Boolean EngineDisposed { get; set; }
    }

    /// <summary>Lifecycle state of a <see cref="Namespace"/>.</summary>
    public enum NamespaceState
    {
        /// <summary>The namespace serves requests.</summary>
        Ready,

        /// <summary>The namespace is being provisioned (reserved for future async creation).</summary>
        Creating
    }
}
