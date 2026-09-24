// MIT License
//
// Namespace.cs
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
using System.Threading;
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
            _engine = engine;
            CreatedAtUtc = createdAtUtc;
        }

        /// <summary>
        ///   Absent when this namespace is cataloged but not resident in this process (feature
        ///   namespace-startup-load). Residency is a property of the ENTRY, never of collection
        ///   membership: the catalog writer rebuilds its whole document from the collection it is
        ///   handed, so a namespace that left the collection would have its catalog entry erased,
        ///   its data directory stranded unreachable, and its name freed to be re-minted under a
        ///   second id over real data.
        ///   <para>Assigned after construction by runtime activation only (see
        ///   <see cref="AttachEngine"/>), which is also why every read of it goes through
        ///   <see cref="Volatile"/>.</para>
        /// </summary>
        private Fallen8 _engine;

        /// <summary>The unique, URL-addressable name (permissive; see <see cref="Fallen8Namespaces.IsValidName"/>); changed by rename.</summary>
        public String Name { get; internal set; }

        /// <summary>The immutable collection-assigned id (e.g. <c>ns-20260723-101502-3f2a</c>).</summary>
        public String Id { get; }

        /// <summary>
        ///   The Fallen-8 engine that holds this namespace's graph.
        ///   <para>THROWS <see cref="NamespaceNotLoadedException"/> when the namespace is cataloged
        ///   but not loaded in this process, rather than returning null. This repo has no
        ///   nullable-reference analysis (no <c>&lt;Nullable&gt;</c> in Directory.Build.props or any
        ///   csproj), so the compiler cannot point at the sites that must branch - which makes a
        ///   throw the only fail-safe default. A site the sweep missed then fails diagnosably, and
        ///   inside the shutdown save's per-namespace catch it means "skip", so a not-loaded
        ///   namespace is never enqueued for a save that would overwrite its checkpoint with an
        ///   empty graph and truncate its write-ahead log (spec §5). A null-returning property
        ///   would reach the same skip by NullReferenceException, which is neither diagnosable nor
        ///   mappable to a problem body.</para>
        ///   <para>Branch with <see cref="IsLoaded"/> or <see cref="TryGetEngine"/>.</para>
        /// </summary>
        public Fallen8 Engine =>
            Volatile.Read(ref _engine) ?? throw new NamespaceNotLoadedException(Name);

        /// <summary>Whether this namespace's engine is resident in this process.</summary>
        public Boolean IsLoaded => Volatile.Read(ref _engine) != null;

        /// <summary>
        ///   The engine if this namespace is loaded. The accessor for every site that must treat a
        ///   not-loaded namespace as a normal, expected condition rather than an error.
        /// </summary>
        public Boolean TryGetEngine(out Fallen8 engine)
        {
            engine = Volatile.Read(ref _engine);
            return engine != null;
        }

        /// <summary>
        ///   Publishes the engine a runtime activation built for this namespace (feature
        ///   namespace-startup-load §4.8). Called ONLY from
        ///   <see cref="Fallen8Namespaces.ActivateAsync"/>, under that namespace's load gate, and only
        ///   after the engine is fully constructed AND its checkpoint restored - so no request ever
        ///   observes a half-loaded graph, and a failed restore leaves this entry exactly as
        ///   not-loaded as it was.
        ///   <para>The volatile write is what pairs with the volatile reads above: the readers hold
        ///   no lock, so without a release/acquire pair a reader could observe the reference before
        ///   the engine state it points at.</para>
        /// </summary>
        internal void AttachEngine(Fallen8 engine)
        {
            Volatile.Write(ref _engine, engine);
        }

        /// <summary>When the namespace was created (UTC).</summary>
        public DateTime CreatedAtUtc { get; }

        /// <summary>
        ///   Provisioning state. Creation is synchronous in v1, so this is always
        ///   <see cref="NamespaceState.Ready"/>; the enum exists so a future async provisioning
        ///   path is not a breaking contract change. Residency is NOT stored here - read
        ///   <see cref="EffectiveState"/>.
        /// </summary>
        public NamespaceState State { get; internal set; } = NamespaceState.Ready;

        /// <summary>
        ///   The state the REST surface reports: <see cref="State"/> unless this namespace is not
        ///   resident, in which case <see cref="NamespaceState.NotLoaded"/>. Derived rather than
        ///   stored, because a residency field beside <see cref="IsLoaded"/> would be two facts that
        ///   can disagree.
        /// </summary>
        public NamespaceState EffectiveState => IsLoaded ? State : NamespaceState.NotLoaded;

        /// <summary>
        ///   The wire spelling of a state. The one home for these strings: the namespace list, the
        ///   status probe and the 503 refusal body all name the same state, and a client branches on
        ///   that name.
        /// </summary>
        public static String WireName(NamespaceState state)
        {
            switch (state)
            {
                case NamespaceState.Creating: return "creating";
                case NamespaceState.NotLoaded: return "notLoaded";
                case NamespaceState.Ready:
                default: return "ready";
            }
        }

        /// <summary>
        ///   A namespace name as a URL PATH SEGMENT. The one home for that encoding, because names
        ///   are deliberately permissive while the addressable form is not: a space, "#", "?", "%"
        ///   or a non-ASCII character is a valid name (<see cref="Fallen8Namespaces.IsValidName"/>)
        ///   and must be percent-encoded to survive a URL. Every message that prints a URL for an
        ///   operator to copy runs the name through here, or its instructions are wrong for exactly
        ///   the names that need the help; the quoted human-readable name stays unencoded.
        /// </summary>
        public static String UrlSegment(String name)
        {
            return Uri.EscapeDataString(name);
        }

        /// <summary>
        ///   This namespace's plugin-registration override (feature plugin-registration). Null ⇒
        ///   inherit the global <c>Fallen8:Security:EnableDynamicPluginLoading</c> default; true/false
        ///   force plugin registration on/off for this namespace. Read by the authorization gate and
        ///   persisted on the namespace catalog entry (the default namespace's override lives on the
        ///   catalog document).
        /// </summary>
        public Boolean? PluginRegistrationEnabled { get; internal set; }

        /// <summary>
        ///   This namespace's startup-load override (feature namespace-startup-load). Null ⇒ inherit
        ///   the global <c>Fallen8:Namespaces:LoadOnStartup</c> default; true/false force a boot to
        ///   load/skip this namespace. Persisted on the catalog entry, and read only at boot - it
        ///   describes the NEXT process, never this one, so it is deliberately independent of
        ///   <see cref="IsLoaded"/> (an operator can exclude a namespace that is loaded right now).
        ///   The reserved <c>default</c> namespace holds a fixed true: it aliases every bare URL, so
        ///   it is always loaded and cannot be overridden (spec §4.9).
        /// </summary>
        public Boolean? LoadOnStartupEnabled { get; internal set; }

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
        Creating,

        /// <summary>
        ///   The namespace is cataloged and addressable for management, but has no engine in this
        ///   process, so it serves no data request (feature namespace-startup-load). Its data on
        ///   disk is untouched - which is why this is a third state rather than absence from the
        ///   list, and why the refusal is 503 rather than 404 (see <c>NamespaceProblems.NotLoaded</c>).
        /// </summary>
        NotLoaded
    }
}
