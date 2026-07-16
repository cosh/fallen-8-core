// MIT License
//
// VectorSearchResult.cs
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
using NoSQL.GraphDB.Core.Model;

namespace NoSQL.GraphDB.Core.Index.Vector
{
    /// <summary>
    ///   One kNN hit: the element and its RAW score under the index's metric.
    /// </summary>
    public readonly struct VectorSearchEntry
    {
        public readonly AGraphElementModel Element;
        public readonly Single Score;

        public VectorSearchEntry(AGraphElementModel element, Single score)
        {
            Element = element;
            Score = score;
        }
    }

    /// <summary>
    ///   The result of a k-nearest-neighbour query (feature vector-index): best-first entries
    ///   plus the metric and its direction, so a caller can never misread an L2 distance as a
    ///   similarity (mirrors <c>FulltextSearchResult</c>'s elements-plus-relevance shape).
    /// </summary>
    public sealed class VectorSearchResult
    {
        /// <summary>The index's metric.</summary>
        public VectorDistanceMetric Metric
        {
            get;
        }

        /// <summary>Whether a HIGHER score is better (true for Cosine/DotProduct, false for L2).</summary>
        public Boolean HigherIsBetter => Metric != VectorDistanceMetric.L2;

        /// <summary>The hits, best first; ties broken by ascending element id.</summary>
        public IReadOnlyList<VectorSearchEntry> Entries
        {
            get;
        }

        public VectorSearchResult(VectorDistanceMetric metric, IReadOnlyList<VectorSearchEntry> entries)
        {
            Metric = metric;
            Entries = entries;
        }
    }

    /// <summary>The element category a kNN query is constrained to.</summary>
    public enum VectorSearchElementKind : byte
    {
        Any = 0,
        Vertex = 1,
        Edge = 2
    }

    /// <summary>
    ///   Optional declarative constraints on a kNN query, applied BEFORE scoring - the returned
    ///   k are k MATCHING elements, not k results minus casualties.
    /// </summary>
    public sealed class VectorSearchConstraint
    {
        /// <summary>The element kind to match. Default: any.</summary>
        public VectorSearchElementKind Kind { get; set; } = VectorSearchElementKind.Any;

        /// <summary>An exact (ordinal) label to match; null matches any label. An unlabeled
        /// element never matches a non-null label.</summary>
        public String Label
        {
            get; set;
        }
    }
}
