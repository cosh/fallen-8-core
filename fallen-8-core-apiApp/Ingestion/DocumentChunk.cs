// MIT License
//
// DocumentChunk.cs
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

namespace NoSQL.GraphDB.App.Ingestion
{
    /// <summary>
    ///   One chunk as produced by <see cref="DocumentChunker"/> - the in-pipeline shape that
    ///   becomes a Chunk vertex (spec unstructured-ingestion FR-4).
    /// </summary>
    public sealed class DocumentChunk
    {
        /// <summary>Chunk kind of a prose chunk.</summary>
        public const String TextKind = "text";

        /// <summary>Chunk kind of an intact (or row-windowed) table.</summary>
        public const String TableKind = "table";

        public String Text
        {
            get; set;
        }

        /// <summary>Document position, 0-based.</summary>
        public Int32 Order
        {
            get; set;
        }

        /// <summary><see cref="TextKind"/> or <see cref="TableKind"/>.</summary>
        public String Kind { get; set; } = TextKind;

        /// <summary>The heading trail ("A &gt; B"), null when none is known.</summary>
        public String HeadingPath
        {
            get; set;
        }

        public Int32? PageFrom
        {
            get; set;
        }

        public Int32? PageTo
        {
            get; set;
        }

        /// <summary>Extracted identifier tokens, capped and sorted (FR-6).</summary>
        public List<String> Identifiers { get; set; } = new List<String>();
    }
}
