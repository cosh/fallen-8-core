// MIT License
//
// NamespaceREST.cs
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
using NoSQL.GraphDB.App.Configuration;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   One namespace as the REST surface reports it (feature graph-namespaces). No memory
    ///   figure by design: engines share one GC heap, so a per-namespace byte count would be
    ///   fiction (spec §5.3).
    /// </summary>
    public sealed class NamespaceREST
    {
        /// <summary>The unique, URL-addressable name.</summary>
        public String Name { get; set; }

        /// <summary>
        ///   Lifecycle state: <c>ready</c>, <c>creating</c> (reserved for future async creation), or
        ///   <c>notLoaded</c> - cataloged, but with no engine in this process (feature
        ///   namespace-startup-load). A <c>notLoaded</c> namespace still appears here BY DESIGN:
        ///   hiding it reaches the Studio recover state by absence, whose primary action recreates it
        ///   empty.
        /// </summary>
        public String State { get; set; }

        /// <summary>
        ///   The namespace's vertex count, or <c>null</c> when it is <c>notLoaded</c> and this
        ///   process therefore has no count to report. Never <c>0</c> in that case: the Studio shell
        ///   branches on <c>vertexCount === 0</c> to replay the first-run walkthrough, so a zero
        ///   would greet an operator with "get started" over a namespace that holds data, and a
        ///   reconciling writer would read "healthy and empty" and delete on that basis.
        /// </summary>
        public Int32? VertexCount { get; set; }

        /// <summary>The namespace's edge count, or <c>null</c> when it is <c>notLoaded</c>
        /// (see <see cref="VertexCount"/> for why absent rather than zero).</summary>
        public Int32? EdgeCount { get; set; }

        /// <summary>When the namespace was created (UTC, ISO 8601).</summary>
        public String CreatedAt { get; set; }

        /// <summary>
        ///   This namespace's plugin-registration override (feature plugin-registration):
        ///   <c>true</c>/<c>false</c> when set explicitly, <c>null</c> when it inherits the global
        ///   <c>Fallen8:Security:EnableDynamicPluginLoading</c> default.
        /// </summary>
        public Boolean? PluginRegistrationEnabled { get; set; }

        /// <summary>
        ///   This namespace's startup-load override (feature namespace-startup-load):
        ///   <c>true</c>/<c>false</c> when set explicitly, <c>null</c> when it inherits the global
        ///   <c>Fallen8:Namespaces:LoadOnStartup</c> default. It describes the NEXT boot, so it is
        ///   independent of <see cref="State"/> - a loaded namespace can carry <c>false</c>, and a
        ///   <c>notLoaded</c> one can carry <c>true</c> (it was excluded by
        ///   <c>Fallen8:Namespaces:StartupLoadMode</c> instead). The reserved <c>default</c> namespace
        ///   always reports <c>true</c>: it aliases every bare URL and cannot be excluded.
        /// </summary>
        public Boolean? LoadOnStartupEnabled { get; set; }
    }

    /// <summary>The namespace list with its configured ceiling.</summary>
    public sealed class NamespacesREST
    {
        /// <summary>All namespaces, name-ordered (always includes <c>default</c>).</summary>
        public List<NamespaceREST> Namespaces { get; set; }

        /// <summary>The configured <c>Fallen8:Namespaces:MaxNamespaces</c> ceiling.</summary>
        public Int32 MaxNamespaces { get; set; }

        /// <summary>
        ///   The instance-wide startup-load default (<c>Fallen8:Namespaces:LoadOnStartup</c>) this boot
        ///   ran with, which is what a namespace set to <c>inherit</c> resolves to.
        ///
        ///   <para>Published UNCOMPOSED, i.e. without <see cref="StartupLoadMode"/> folded in. Composing
        ///   them would report <c>true</c> under mode <c>All</c> whatever the default actually is, and an
        ///   operator who then saved <c>skip</c> would see the value bounce straight back to <c>true</c>
        ///   and conclude the control was broken. The two fields are reported separately so a client can
        ///   say what is really happening: the default is <c>skip</c> AND the mode is overriding it.</para>
        /// </summary>
        public Boolean LoadOnStartupDefault { get; set; }

        /// <summary>
        ///   The startup-load mode this boot ran with: <c>catalog</c> (honour each namespace's own
        ///   preference), <c>all</c> (load every catalogued namespace regardless) or <c>defaultOnly</c>.
        ///   Both <c>all</c> and <c>defaultOnly</c> SHORT-CIRCUIT the per-namespace preference, so a
        ///   namespace showing <c>skip</c> can still have been loaded; a client that renders the policy
        ///   has to disclose that rather than show a preference the boot ignored.
        ///
        ///   <para>A string, not the enum: this application installs no string-enum converter, so a bare
        ///   enum would publish 0, 1 or 2 and the meaning would live only in this assembly.</para>
        /// </summary>
        public String StartupLoadMode { get; set; }

        /// <summary>
        ///   The wire spelling of a startup-load mode, camelCase like every other published value.
        ///
        ///   <para>An out-of-range value maps to <c>catalog</c> rather than throwing, and the choice is
        ///   about behaviour, not leniency: the configuration binder happily binds a numeric string like
        ///   <c>5</c> into the enum, the boot loop's own switch treats every unknown mode as the
        ///   per-namespace (catalog) branch, and a projection that threw here would turn a configuration
        ///   the boot tolerated into a 500 on every namespace listing.</para>
        /// </summary>
        public static String WireStartupLoadMode(NamespaceStartupLoadMode mode)
        {
            return Enum.IsDefined(mode) ? WireEnum.Camel(mode) : "catalog";
        }
    }

    /// <summary>
    ///   The result of one <c>POST /ns/{name}/activate</c> (feature namespace-startup-load §4.8).
    ///   Its own type rather than a bare <see cref="NamespaceREST"/>, because the answer is a report
    ///   about an OPERATION - "did this call load it, and what came back" - and a namespace entry has
    ///   no place to say that. Putting <see cref="Activated"/> on <see cref="NamespaceREST"/> instead
    ///   would ship a meaningless field on every entry of <c>GET /ns</c>.
    /// </summary>
    public sealed class NamespaceActivationREST
    {
        /// <summary>The namespace as it stands after the call (state <c>ready</c>, real counts).</summary>
        public NamespaceREST Namespace { get; set; }

        /// <summary>
        ///   Whether THIS call loaded the namespace. <c>false</c> means it was already loaded in this
        ///   process and nothing was restored - a success, since activation is idempotent, and the
        ///   only way a caller can tell the two apart.
        /// </summary>
        public Boolean Activated { get; set; }

        /// <summary>
        ///   What happened, in the operator's words: which save game was restored (and whether its
        ///   write-ahead-log tail was replayed on top), or that the namespace was already loaded, or
        ///   that no registered save game contains it.
        /// </summary>
        public String Detail { get; set; }
    }

    /// <summary>
    ///   Request body for updating a namespace via <c>PATCH /ns/{name}</c>: rename it and/or set its
    ///   plugin-registration or startup-load override. Every field is optional; supply at least one.
    /// </summary>
    public sealed class NamespaceUpdateSpecification
    {
        /// <summary>
        ///   Optional new namespace name (rename). Permissive: 1-63 characters of any case, spaces,
        ///   punctuation, or Unicode; only empty/whitespace-only, leading/trailing whitespace,
        ///   "." / "..", "/", "\", and control characters are rejected. Case-sensitive. Omit to leave
        ///   the name unchanged.
        /// </summary>
        public String Name { get; set; }

        /// <summary>
        ///   Optional plugin-registration override (feature plugin-registration):
        ///   <c>"enabled"</c> | <c>"disabled"</c> | <c>"inherit"</c>. Omit (or null) to leave it
        ///   unchanged; <c>"inherit"</c> clears the override so this namespace follows the global
        ///   <c>Fallen8:Security:EnableDynamicPluginLoading</c> default.
        /// </summary>
        public String PluginRegistration { get; set; }

        /// <summary>
        ///   Optional startup-load override (feature namespace-startup-load):
        ///   <c>"enabled"</c> | <c>"disabled"</c> | <c>"inherit"</c>, the same tri-state vocabulary as
        ///   <see cref="PluginRegistration"/>. Omit (or null) to leave it unchanged;
        ///   <c>"inherit"</c> clears the override so this namespace follows the global
        ///   <c>Fallen8:Namespaces:LoadOnStartup</c> default. It takes effect on the next restart -
        ///   it never loads or unloads the namespace in the running process.
        /// </summary>
        public String LoadOnStartup { get; set; }
    }
}
