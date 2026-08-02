// MIT License
//
// DocumentBindingREST.cs
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

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   The semantic layer's index binding (feature semantic-layer FR-7): the state the Studio
    ///   "State" panel reads to decide whether to offer "create the required indexes". The layer
    ///   never creates indices implicitly - ingestion answers 428 until <see cref="Ready"/>.
    /// </summary>
    public sealed class DocumentBindingREST
    {
        /// <summary>True when every required index exists and is the right shape: ingestion will
        /// be accepted (subject to the other gates).</summary>
        public Boolean Ready
        {
            get; set;
        }

        /// <summary>The vector index over the chunk embeddings (kNN side of fused search).</summary>
        public DocumentBindingRoleREST Vector
        {
            get; set;
        }

        /// <summary>The fulltext index over chunk text (lexical side of fused search).</summary>
        public DocumentBindingRoleREST Fulltext
        {
            get; set;
        }

        /// <summary>The dictionary index that deduplicates Entity vertices (one per (type,
        /// normalized) per namespace).</summary>
        public DocumentBindingRoleREST Entity
        {
            get; set;
        }
    }

    /// <summary>One index role in the binding: its id, whether the current configuration requires
    /// it, whether it exists, whether it is usable (right shape), and a short human note.</summary>
    public sealed class DocumentBindingRoleREST
    {
        /// <summary><c>vector</c>, <c>fulltext</c> or <c>entity</c>.</summary>
        public String Role
        {
            get; set;
        }

        /// <summary>The configured index id this role binds.</summary>
        public String IndexId
        {
            get; set;
        }

        /// <summary>Whether the current configuration needs this index (embeddings on for the
        /// vector role, NLP on for the entity role, the fulltext role whenever it is enabled).</summary>
        public Boolean Required
        {
            get; set;
        }

        /// <summary>Whether an index with this id exists at all.</summary>
        public Boolean Exists
        {
            get; set;
        }

        /// <summary>Whether the index exists AND is the right shape for this role.</summary>
        public Boolean Ready
        {
            get; set;
        }

        /// <summary>A short note: the binding detail when ready, or the shape conflict when not.</summary>
        public String Detail
        {
            get; set;
        }
    }
}
