// MIT License
//
// PluginEntry.cs
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

namespace NoSQL.GraphDB.Core.Plugins
{
    /// <summary>
    ///   A registered plugin: its immutable <see cref="Definition"/> plus the compile outcome. The
    ///   compiled <see cref="Artifact"/> is the plugin's CLR <see cref="Type"/> (loaded in a
    ///   collectible <c>AssemblyLoadContext</c>), STRONGLY referenced for the entry's registered
    ///   lifetime - deliberately not a sliding-expiry cache, because a registered plugin is long-lived
    ///   by definition. Deleting the entry drops the reference so the type's collectible load context
    ///   can unload once in-flight activations/invocations finish (a resolution captures the type once,
    ///   so a concurrent removal either wins before resolution or the invocation completes against the
    ///   captured type - never a torn state).
    /// </summary>
    /// <remarks>
    ///   Entries are immutable after construction; a state change (e.g. a rehydration outcome) is
    ///   expressed by registering a NEW entry, which keeps the registry's lock-free snapshot reads
    ///   trivially safe - the exact discipline of <c>StoredQueryEntry</c>.
    /// </remarks>
    public sealed class PluginEntry
    {
        /// <summary>The stored definition (name, category, contract, source, metadata).</summary>
        public PluginDefinition Definition
        {
            get;
        }

        /// <summary>The compile state of this entry.</summary>
        public PluginCompileState CompileState
        {
            get;
        }

        /// <summary>
        ///   The pinned compiled artifact: the plugin's CLR <see cref="Type"/> (a public,
        ///   non-abstract class implementing the contract's interface). Null unless
        ///   <see cref="CompileState"/> is <see cref="PluginCompileState.Compiled"/>. Kept as
        ///   <see cref="Type"/> rather than a materialized instance because algorithm plugins are
        ///   activated fresh per resolution (matching <c>PluginFactory</c>); holding the type keeps the
        ///   collectible load context alive, and dropping it on delete lets the context unload.
        /// </summary>
        public Type Artifact
        {
            get;
        }

        /// <summary>
        ///   The compiler diagnostics of a failed rehydration recompile. Null unless
        ///   <see cref="CompileState"/> is <see cref="PluginCompileState.Failed"/>.
        /// </summary>
        public String CompileDiagnostics
        {
            get;
        }

        /// <summary>
        ///   Creates an entry. Invariants: a <see cref="PluginCompileState.Compiled"/> entry carries a
        ///   non-null artifact type; the other states carry none.
        /// </summary>
        public PluginEntry(PluginDefinition definition, PluginCompileState compileState,
            Type artifact, String compileDiagnostics = null)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            if (compileState == PluginCompileState.Compiled && artifact == null)
            {
                throw new ArgumentException("A Compiled entry requires a non-null artifact type.", nameof(artifact));
            }

            Definition = definition;
            CompileState = compileState;
            Artifact = compileState == PluginCompileState.Compiled ? artifact : null;
            CompileDiagnostics = compileState == PluginCompileState.Failed ? compileDiagnostics : null;
        }
    }
}
