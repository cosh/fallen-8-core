// MIT License
//
// IPluginCompiler.cs
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
    ///   Compiles a <see cref="PluginDefinition"/>'s C# source into its executable artifact: the CLR
    ///   <see cref="Type"/> of the single public class that implements the definition's contract
    ///   interface (feature plugin-registration).
    /// </summary>
    /// <remarks>
    ///   The engine cannot compile a definition on its own because the source is compiled by a higher
    ///   layer (the REST API compiles C# with Roslyn) - the same bridge pattern as
    ///   <c>IStoredQueryCompiler</c> and <c>ISubGraphRecipeCompiler</c>. An implementation is
    ///   registered on the graph via <c>IFallen8.PluginCompiler</c>; without one, rehydrated
    ///   definitions load as source-only (<see cref="PluginCompileState.SourceOnly"/>). The compiler
    ///   both compiles and validates the contract (exactly one implementing public type, activatable,
    ///   with the expected <c>PluginName</c>); a validation failure is returned as an error, not a
    ///   throw.
    /// </remarks>
    public interface IPluginCompiler
    {
        /// <summary>
        ///   Attempts to compile a plugin definition into its executable artifact type.
        /// </summary>
        /// <param name="definition">The definition to compile.</param>
        /// <param name="artifact">
        ///   The compiled plugin type (implementing the contract's interface), or null on failure.
        /// </param>
        /// <param name="error">A human-readable error (compiler/contract diagnostics) on failure; otherwise null.</param>
        /// <returns><c>true</c> if an artifact type was produced; otherwise <c>false</c>.</returns>
        bool TryCompile(PluginDefinition definition, out Type artifact, out String error);
    }
}
