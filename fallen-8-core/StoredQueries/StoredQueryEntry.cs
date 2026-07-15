// MIT License
//
// StoredQueryEntry.cs
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

namespace NoSQL.GraphDB.Core.StoredQueries
{
    /// <summary>
    ///   A registered stored query: its immutable <see cref="Definition"/> plus the compile
    ///   outcome. The compiled <see cref="Artifact"/> is STRONGLY referenced for the entry's
    ///   registered lifetime - deliberately not the 60-second sliding expiry of the inline compile
    ///   caches, because a stored query is long-lived by definition. Deleting the entry drops the
    ///   reference so the artifact's collectible <c>AssemblyLoadContext</c> can unload once
    ///   in-flight invocations finish (an invocation captures this reference once at resolution,
    ///   so a concurrent removal either wins before resolution or the invocation completes against
    ///   the old artifact - never a torn state).
    /// </summary>
    /// <remarks>
    ///   Entries are immutable after construction; a state change (e.g. a rehydration outcome) is
    ///   expressed by registering a NEW entry, which keeps the library's lock-free snapshot reads
    ///   trivially safe.
    /// </remarks>
    public sealed class StoredQueryEntry
    {
        /// <summary>The stored definition (name, kind, source, metadata).</summary>
        public StoredQueryDefinition Definition
        {
            get;
        }

        /// <summary>The compile state of this entry.</summary>
        public StoredQueryCompileState CompileState
        {
            get;
        }

        /// <summary>
        ///   The pinned compiled artifact: an <c>IPathTraverser</c> (<see cref="StoredQueryKind.Path"/>)
        ///   or a <c>SubGraphDefinition</c> (<see cref="StoredQueryKind.SubGraph"/>). Null unless
        ///   <see cref="CompileState"/> is <see cref="StoredQueryCompileState.Compiled"/>.
        /// </summary>
        public Object Artifact
        {
            get;
        }

        /// <summary>
        ///   The compiler diagnostics of a failed rehydration recompile. Null unless
        ///   <see cref="CompileState"/> is <see cref="StoredQueryCompileState.Failed"/>.
        /// </summary>
        public String CompileDiagnostics
        {
            get;
        }

        /// <summary>
        ///   Creates an entry. Invariants: a <see cref="StoredQueryCompileState.Compiled"/> entry
        ///   carries a non-null artifact; the other states carry none.
        /// </summary>
        public StoredQueryEntry(StoredQueryDefinition definition, StoredQueryCompileState compileState,
            Object artifact, String compileDiagnostics = null)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            if (compileState == StoredQueryCompileState.Compiled && artifact == null)
            {
                throw new ArgumentException("A Compiled entry requires a non-null artifact.", nameof(artifact));
            }

            Definition = definition;
            CompileState = compileState;
            Artifact = compileState == StoredQueryCompileState.Compiled ? artifact : null;
            CompileDiagnostics = compileState == StoredQueryCompileState.Failed ? compileDiagnostics : null;
        }
    }
}
