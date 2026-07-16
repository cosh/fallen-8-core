// MIT License
//
// IStoredQueryCompiler.cs
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
    ///   Compiles a <see cref="StoredQueryDefinition"/>'s opaque specification into its executable
    ///   artifact: an <c>IPathTraverser</c> for <see cref="StoredQueryKind.Path"/>, a
    ///   <c>SubGraphDefinition</c> for <see cref="StoredQueryKind.SubGraph"/>.
    /// </summary>
    /// <remarks>
    ///   The engine cannot compile a definition on its own because the specification text is
    ///   interpreted by a higher layer (the REST API compiles C# fragments with Roslyn) - the
    ///   same bridge pattern as <see cref="SubGraph.ISubGraphRecipeCompiler"/>. An implementation
    ///   is registered on the graph via <c>IFallen8.StoredQueryCompiler</c>; without one,
    ///   rehydrated definitions load as source-only
    ///   (<see cref="StoredQueryCompileState.SourceOnly"/>).
    /// </remarks>
    public interface IStoredQueryCompiler
    {
        /// <summary>
        ///   Attempts to compile a stored query definition into its executable artifact.
        /// </summary>
        /// <param name="definition">The definition to compile.</param>
        /// <param name="artifact">
        ///   The compiled artifact (an <c>IPathTraverser</c> or a <c>SubGraphDefinition</c>,
        ///   matching <see cref="StoredQueryDefinition.Kind"/>), or null on failure.
        /// </param>
        /// <param name="error">A human-readable error (compiler diagnostics) on failure; otherwise null.</param>
        /// <returns><c>true</c> if an artifact was produced; otherwise <c>false</c>.</returns>
        bool TryCompile(StoredQueryDefinition definition, out Object artifact, out String error);
    }
}
