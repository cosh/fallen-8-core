// MIT License
//
// Fallen8NamespacesOptions.cs
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
        ///   <c>default</c>. A cap, not a target: each namespace owns a Fallen-8 engine with a
        ///   dedicated writer thread, an open write-ahead log (durable mode), and metric
        ///   instruments, so realistic fleets are dozens to hundreds. Default 10000.
        /// </summary>
        public Int32 MaxNamespaces { get; set; } = 10000;
    }
}
