// MIT License
//
// SubGraphQuota.cs
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

namespace NoSQL.GraphDB.Core.SubGraph
{
    /// <summary>
    /// Resource ceilings enforced by <see cref="SubGraphFactory"/> when creating subgraphs,
    /// to bound the memory a caller can consume by materializing many or large subgraphs.
    /// </summary>
    /// <remarks>
    /// The defaults are generous but finite (finding M6): they never get in the way of ordinary
    /// embedded/trusted use, yet they bound the memory a runaway or hostile caller can consume by
    /// materializing unboundedly many or unboundedly large subgraphs (the previous
    /// <see cref="Int32.MaxValue"/> defaults were effectively unlimited). A hosting layer that
    /// exposes subgraph creation to untrusted callers should still set tighter, use-case-specific
    /// values; a trusted embedder that genuinely wants no ceiling can set the limits back to
    /// <see cref="Int32.MaxValue"/>.
    /// </remarks>
    public sealed class SubGraphQuota
    {
        /// <summary>The default <see cref="MaxSubGraphCount"/>: generous for real use, but bounded.</summary>
        public const int DefaultMaxSubGraphCount = 1024;

        /// <summary>The default <see cref="MaxElementsPerSubGraph"/>: up to a large-graph scale.</summary>
        public const int DefaultMaxElementsPerSubGraph = 10_000_000;

        /// <summary>The default <see cref="MaxTotalElements"/> summed across all subgraphs.</summary>
        public const int DefaultMaxTotalElements = 25_000_000;

        /// <summary>
        /// Maximum number of registered subgraphs. Creation is rejected when this many
        /// subgraphs already exist. Defaults to <see cref="DefaultMaxSubGraphCount"/>.
        /// </summary>
        public int MaxSubGraphCount
        {
            get; set;
        } = DefaultMaxSubGraphCount;

        /// <summary>
        /// Maximum number of materialized elements (vertices + edges) in a single subgraph.
        /// A subgraph that would exceed this is rejected and not registered. Defaults to
        /// <see cref="DefaultMaxElementsPerSubGraph"/>.
        /// </summary>
        public int MaxElementsPerSubGraph
        {
            get; set;
        } = DefaultMaxElementsPerSubGraph;

        /// <summary>
        /// Maximum number of materialized elements (vertices + edges) summed across all
        /// registered subgraphs. Defaults to <see cref="DefaultMaxTotalElements"/>.
        /// </summary>
        public int MaxTotalElements
        {
            get; set;
        } = DefaultMaxTotalElements;
    }
}
