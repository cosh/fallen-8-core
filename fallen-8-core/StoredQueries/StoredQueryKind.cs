// MIT License
//
// StoredQueryKind.cs
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

namespace NoSQL.GraphDB.Core.StoredQueries
{
    /// <summary>
    ///   The kind of a stored query (feature stored-query-library). Exactly two kinds exist,
    ///   matching the two artifacts the engine actually compiles and executes as a unit: a whole
    ///   path filter/cost set (one <c>IPathTraverser</c>) and a whole subgraph pattern template
    ///   (one <c>SubGraphDefinition</c>). Per-fragment kinds are deliberately not modelled - no
    ///   endpoint executes a lone fragment (see the feature spec's non-goals).
    /// </summary>
    public enum StoredQueryKind
    {
        /// <summary>A path filter/cost set, compiled into a single <c>IPathTraverser</c>.</summary>
        Path,

        /// <summary>A subgraph pattern template, compiled into a <c>SubGraphDefinition</c>.</summary>
        SubGraph
    }
}
