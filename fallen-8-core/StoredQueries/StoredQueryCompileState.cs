// MIT License
//
// StoredQueryCompileState.cs
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
    ///   The compile state of a registered stored query. Registration only ever produces
    ///   <see cref="Compiled"/> entries (a compile failure rejects the registration); the other
    ///   states arise when a persisted definition is rehydrated on load or write-ahead-log replay.
    /// </summary>
    public enum StoredQueryCompileState
    {
        /// <summary>The artifact compiled and is pinned for the entry's registered lifetime.</summary>
        Compiled,

        /// <summary>
        ///   Rehydration recompiled the persisted source and FAILED (for example after an engine
        ///   upgrade changed the model API). The entry is kept - loudly, with its diagnostics -
        ///   so an operator can inspect (get), then delete and re-register; invoking it fails.
        ///   Silent disappearance of an operator-registered query would be data loss.
        /// </summary>
        Failed,

        /// <summary>
        ///   The definition was loaded but no <see cref="IStoredQueryCompiler"/> is registered on
        ///   the graph (embedded engine use without the API layer). There is no invocation surface
        ///   without a hosting layer, so the entry is source-only until one recompiles it.
        /// </summary>
        SourceOnly
    }
}
