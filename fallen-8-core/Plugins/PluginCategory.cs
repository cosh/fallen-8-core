// MIT License
//
// PluginCategory.cs
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

namespace NoSQL.GraphDB.Core.Plugins
{
    /// <summary>
    ///   The top-level category of a runtime-registered plugin (feature plugin-registration). The
    ///   set is CLOSED and defined by maintainers - it grows only when a maintainer adds a category
    ///   in code (a new contract + validation + typed REST endpoint), never at runtime. A user
    ///   registers an INSTANCE of a category as validated C# source; the category decides which
    ///   contract that source must satisfy and how it is invoked.
    ///
    ///   <para>Persisted as its NAME (see the manifest/WAL serialization) so the on-disk contract
    ///   does not depend on enum member order.</para>
    /// </summary>
    public enum PluginCategory
    {
        /// <summary>
        ///   A runtime-registered algorithm plugin (an <c>IShortestPathAlgorithm</c>,
        ///   <c>ISubGraphAlgorithm</c> or <c>IGraphAnalyticsAlgorithm</c>, selected by
        ///   <see cref="PluginContract"/>). Invoked transparently by name through the existing
        ///   path/subgraph/analytics endpoints.
        /// </summary>
        Algorithm,

        /// <summary>
        ///   A runtime-registered graph function (an <see cref="IGraphFunction"/>): a stored graph
        ///   procedure authored in source, invoked by name with a parameter bag, returning a view of
        ///   existing vertices/edges. There are no built-in graph functions.
        /// </summary>
        Function,

        /// <summary>
        ///   An index plugin (an <c>IIndex</c>), created by name through <c>IndexFactory</c>. Unlike the
        ///   two categories above it has no source form and no registration ENDPOINT: the way into the
        ///   registry is a host registering the TYPE (<c>Fallen8.RegisterPluginType</c>, see
        ///   <see cref="PluginContract.Index"/>), which produces an entry carrying no source.
        ///
        ///   <para>Whether such an entry is persisted is not decided by this category: it is decided
        ///   entry by entry by <see cref="PluginEntry.IsPersistable"/> - the one home for that rule -
        ///   which asks for source and finds none. Nothing here forbids an Index-category entry that
        ///   HAS source; were one ever registered, it would be persisted like any other.</para>
        /// </summary>
        Index,

        /// <summary>A service plugin (an <c>IService</c>), added by name through <c>ServiceFactory</c>;
        /// reached the same way as <see cref="Index"/>, by host type registration.</summary>
        Service
    }
}
