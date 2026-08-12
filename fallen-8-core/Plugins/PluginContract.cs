// MIT License
//
// PluginContract.cs
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
    ///   The exact contract a registered plugin's source must satisfy (feature plugin-registration).
    ///   A category (see <see cref="PluginCategory"/>) selects the applicable contracts; the compile
    ///   bridge validates that the submitted source contains exactly one public type implementing the
    ///   contract's CLR interface. This is a CLOSED enum - an unknown value is rejected at
    ///   registration, so the typed endpoints stay typed (never a catch-all accepting an arbitrary
    ///   interface name). Persisted as its NAME, not its ordinal.
    /// </summary>
    public enum PluginContract
    {
        /// <summary>An <c>IShortestPathAlgorithm</c> (category <see cref="PluginCategory.Algorithm"/>).</summary>
        Path,

        /// <summary>An <c>ISubGraphAlgorithm</c> (category <see cref="PluginCategory.Algorithm"/>).</summary>
        SubGraph,

        /// <summary>An <c>IGraphAnalyticsAlgorithm</c> (category <see cref="PluginCategory.Algorithm"/>).</summary>
        Analytics,

        /// <summary>An <see cref="IGraphFunction"/> (category <see cref="PluginCategory.Function"/>).</summary>
        GraphFunction,

        /// <summary>
        ///   An <c>IIndex</c> (category <see cref="PluginCategory.Index"/>). Reachable only through
        ///   host type registration (<c>Fallen8.RegisterPluginType</c>), which is what lets a host
        ///   create an index where assembly scanning finds nothing.
        /// </summary>
        Index,

        /// <summary>An <c>IService</c> (category <see cref="PluginCategory.Service"/>), host-registered
        /// like <see cref="Index"/>.</summary>
        Service
    }
}
