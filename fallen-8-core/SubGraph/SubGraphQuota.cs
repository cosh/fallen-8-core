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
    /// Every limit defaults to <see cref="Int32.MaxValue"/> (effectively unlimited) so
    /// embedded/trusted use is unaffected. A hosting layer that exposes subgraph creation to
    /// untrusted callers should set conservative values.
    /// </remarks>
    public sealed class SubGraphQuota
    {
        /// <summary>
        /// Maximum number of registered subgraphs. Creation is rejected when this many
        /// subgraphs already exist.
        /// </summary>
        public int MaxSubGraphCount
        {
            get; set;
        } = Int32.MaxValue;

        /// <summary>
        /// Maximum number of materialized elements (vertices + edges) in a single subgraph.
        /// A subgraph that would exceed this is rejected and not registered.
        /// </summary>
        public int MaxElementsPerSubGraph
        {
            get; set;
        } = Int32.MaxValue;

        /// <summary>
        /// Maximum number of materialized elements (vertices + edges) summed across all
        /// registered subgraphs.
        /// </summary>
        public int MaxTotalElements
        {
            get; set;
        } = Int32.MaxValue;
    }
}
