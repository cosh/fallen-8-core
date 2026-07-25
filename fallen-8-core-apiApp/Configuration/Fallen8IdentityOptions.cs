// MIT License
//
// Fallen8IdentityOptions.cs
//
// Copyright (c) 2026 Henning Rauch
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
    ///   Fleet identity, bound from the <c>Fallen8:Identity</c> section (feature
    ///   fleet-observability). A Fallen-8 process declares the tenant it belongs to and its own
    ///   instance identity; both are stamped as OpenTelemetry resource attributes on every
    ///   metric, trace, and log the process emits, so a central consumer can separate the fleet.
    ///
    ///   <para>Everything is optional: unset values auto-fill (see <see cref="Fallen8Identity"/>)
    ///   so observability works with zero config, and an operator sets real ids/names to get
    ///   meaningful fleet labels. Unlike graph content, these are operator-controlled,
    ///   bounded-cardinality identifiers, so they are the sole exception the narrowed tag-hygiene
    ///   invariant allows onto telemetry.</para>
    /// </summary>
    public sealed class Fallen8IdentityOptions
    {
        /// <summary>The configuration section this binds from.</summary>
        public const String SectionName = "Fallen8:Identity";

        /// <summary>The tenant this instance belongs to (<c>Fallen8:Identity:Tenant</c>).</summary>
        public IdentityRef Tenant { get; set; } = new IdentityRef();

        /// <summary>This Fallen-8 instance's own identity (<c>Fallen8:Identity:Instance</c>).</summary>
        public IdentityRef Instance { get; set; } = new IdentityRef();

        /// <summary>An id + name pair. Both are optional; see <see cref="Fallen8Identity"/> for
        /// how they default.</summary>
        public sealed class IdentityRef
        {
            /// <summary>The stable machine identifier (a GUID or any opaque token).</summary>
            public String Id { get; set; }

            /// <summary>The human-readable display name; defaults to the id when unset.</summary>
            public String Name { get; set; }
        }
    }
}
