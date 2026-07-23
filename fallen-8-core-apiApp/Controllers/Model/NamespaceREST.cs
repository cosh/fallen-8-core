// MIT License
//
// NamespaceREST.cs
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

        /// <summary>Lifecycle state: <c>ready</c> or <c>creating</c> (always <c>ready</c> in v1).</summary>
        public String State { get; set; }

        /// <summary>The namespace's vertex count.</summary>
        public Int32 VertexCount { get; set; }

        /// <summary>The namespace's edge count.</summary>
        public Int32 EdgeCount { get; set; }

        /// <summary>When the namespace was created (UTC, ISO 8601).</summary>
        public String CreatedAt { get; set; }
    }

    /// <summary>The namespace list with its configured ceiling.</summary>
    public sealed class NamespacesREST
    {
        /// <summary>All namespaces, name-ordered (always includes <c>default</c>).</summary>
        public List<NamespaceREST> Namespaces { get; set; }

        /// <summary>The configured <c>Fallen8:Namespaces:MaxNamespaces</c> ceiling.</summary>
        public Int32 MaxNamespaces { get; set; }
    }

    /// <summary>Request body for renaming a namespace.</summary>
    public sealed class NamespaceRenameSpecification
    {
        /// <summary>The new name (<c>^[a-z0-9-]{1,63}$</c>).</summary>
        public String Name { get; set; }
    }
}
