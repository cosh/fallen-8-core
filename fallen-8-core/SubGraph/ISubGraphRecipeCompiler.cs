// MIT License
//
// ISubGraphRecipeCompiler.cs
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
using NoSQL.GraphDB.Core.Algorithms.SubGraph;

namespace NoSQL.GraphDB.Core.SubGraph
{
    /// <summary>
    /// Turns a persisted <see cref="SubGraphRecipe"/> back into an executable
    /// <see cref="SubGraphDefinition"/>.
    /// </summary>
    /// <remarks>
    /// The engine cannot compile a recipe on its own because the specification text is
    /// interpreted by a higher layer (for example the REST API compiles C# filter fragments
    /// with Roslyn). An implementation is registered on the graph via
    /// <c>Fallen8.SubGraphRecipeCompiler</c>; if none is registered, persisted subgraphs are
    /// skipped on load.
    /// </remarks>
    public interface ISubGraphRecipeCompiler
    {
        /// <summary>
        /// Attempts to compile a recipe into a definition.
        /// </summary>
        /// <param name="recipe">The recipe to compile.</param>
        /// <param name="definition">The resulting definition, or null on failure.</param>
        /// <param name="error">A human-readable error when compilation fails; otherwise null.</param>
        /// <returns><c>true</c> if a definition was produced; otherwise <c>false</c>.</returns>
        bool TryCompile(SubGraphRecipe recipe, out SubGraphDefinition definition, out String error);
    }
}
