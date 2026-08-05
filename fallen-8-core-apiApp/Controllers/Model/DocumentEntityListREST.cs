// MIT License
//
// DocumentEntityListREST.cs
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

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   The entity network the corpus mentions (feature semantic-layer FR-6): deduplicated
    ///   Entity vertices ranked by how often chunks mention them. Backs the MCP <c>entities</c>
    ///   op and the Studio "Entities" view. A page (bounded); <see cref="Total"/> is the full
    ///   count so the caller knows whether more exist.
    /// </summary>
    public sealed class DocumentEntityListREST
    {
        /// <summary>The entities, most-mentioned first (ties by text, then id).</summary>
        public List<DocumentEntityREST> Entities
        {
            get; set;
        }

        /// <summary>The total number of entities matching the filter, before the page cap.</summary>
        public Int32 Total
        {
            get; set;
        }
    }

    /// <summary>One deduplicated entity: the vertex id (a valid graph seed), its surface text,
    /// its type, and how many chunks mention it.</summary>
    public sealed class DocumentEntityREST
    {
        /// <summary>The Entity vertex id - usable directly as a /path or /subgraph seed.</summary>
        public Int32 Id
        {
            get; set;
        }

        /// <summary>The entity's surface form (the first one seen).</summary>
        public String Text
        {
            get; set;
        }

        /// <summary>The entity type: the label the NLP sidecar emitted, e.g. PERSON, ORG or GPE.</summary>
        public String Type
        {
            get; set;
        }

        /// <summary>How many chunks mention this entity (incoming <c>mentions</c> edges).</summary>
        public Int32 MentionCount
        {
            get; set;
        }
    }
}
