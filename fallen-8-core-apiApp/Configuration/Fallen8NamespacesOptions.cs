// MIT License
//
// Fallen8NamespacesOptions.cs
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

namespace NoSQL.GraphDB.App.Configuration
{
    /// <summary>
    ///   Namespace configuration, bound from the <c>Fallen8:Namespaces</c> section (feature
    ///   graph-namespaces).
    /// </summary>
    public sealed class Fallen8NamespacesOptions
    {
        /// <summary>The configuration section this binds from.</summary>
        public const String SectionName = "Fallen8:Namespaces";

        /// <summary>
        ///   The namespace ceiling, counting every namespace including the reserved
        ///   <c>default</c>. A cap, not a target: each LOADED namespace owns a Fallen-8 engine with
        ///   a dedicated writer thread, its retained graph heap, and metric instruments, so
        ///   realistic fleets are dozens to hundreds. Default 10000.
        ///   <para>It used to say "an open write-ahead log (durable mode)" here; that was wrong and
        ///   overstated the per-namespace cost. Every append opens, fsyncs and closes the log
        ///   (<c>fallen-8-core/Persistency/WriteAheadLog.cs</c>), so no namespace holds a persistent
        ///   file handle - which is also why <see cref="LoadOnStartup"/> saves retained heap and
        ///   sequential load latency, and close to nothing else.</para>
        /// </summary>
        public Int32 MaxNamespaces { get; set; } = 10000;

        /// <summary>
        ///   What a cataloged namespace with no explicit policy inherits (feature
        ///   namespace-startup-load, spec §4.2): whether this boot constructs an engine for it and
        ///   restores its newest checkpoint. Default true - a Fallen-8 loads everything it holds
        ///   unless an operator says otherwise. Per-namespace overrides live on the catalog entry
        ///   (<c>loadOnStartupEnabled</c>, settable through <c>PATCH /ns/{name}</c>); the reserved
        ///   <c>default</c> namespace is always loaded and this key does not apply to it.
        /// </summary>
        public Boolean LoadOnStartup { get; set; } = true;

        /// <summary>
        ///   The operator escape hatch from the persisted selection (feature
        ///   namespace-startup-load, spec §4.2). It is a MODE and never a name list: names are
        ///   mutable while ids are the on-disk key, so a configured name list silently changes
        ///   meaning after a rename. It exists because the catalog is the only inventory and a
        ///   malformed catalog aborts the process, so without a config-side override an operator who
        ///   excluded the wrong namespace would have to hand-edit that one file.
        /// </summary>
        public NamespaceStartupLoadMode StartupLoadMode { get; set; } = NamespaceStartupLoadMode.Catalog;
    }

    /// <summary>How a boot picks the namespaces it loads (see <see cref="Fallen8NamespacesOptions.StartupLoadMode"/>).</summary>
    public enum NamespaceStartupLoadMode
    {
        /// <summary>Honour each catalog entry's <c>loadOnStartupEnabled</c>, falling back to
        /// <see cref="Fallen8NamespacesOptions.LoadOnStartup"/>. The default.</summary>
        Catalog,

        /// <summary>Load every cataloged namespace, ignoring every exclusion - the cold-boot lever
        /// back from a wrong exclusion, without hand-editing the catalog.</summary>
        All,

        /// <summary>Load nothing but the reserved <c>default</c> namespace, for when the selection
        /// itself is what is broken.</summary>
        DefaultOnly
    }
}
