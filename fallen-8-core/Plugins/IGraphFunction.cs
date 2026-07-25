// MIT License
//
// IGraphFunction.cs
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
using NoSQL.GraphDB.Core.Plugin;

namespace NoSQL.GraphDB.Core.Plugins
{
    /// <summary>
    ///   The contract for a stored graph function (feature plugin-registration) - a user-authored
    ///   graph procedure, analogous to a stored function/procedure in other databases: authored once
    ///   as C# source, stored per namespace, and invoked by name whenever needed.
    /// </summary>
    /// <remarks>
    ///   <para>A graph function reaches the graph through the <see cref="IFallen8"/> it captures in
    ///   <see cref="IPlugin.Initialize"/> (as algorithm plugins do), so inside <see cref="TryInvoke"/>
    ///   it can do a full scan (<c>GetAllVertices</c>/<c>GetAllEdges</c>/<c>GetAllGraphElements</c>) or
    ///   an index query, driven by the call-time <c>parameters</c> bag. It returns a
    ///   <see cref="GraphFunctionResult"/> that REFERENCES existing elements (no deep copy).</para>
    ///   <para>Graph functions are READ-ONLY in v1: they read the graph and return a projection, they
    ///   do not mutate. A write-capable "stored procedure" would have to run on the single writer
    ///   thread through a transaction - a materially different lifecycle - and is a deliberate later
    ///   track. There are no built-in graph functions; the contract exists purely for user-authored
    ///   procedures resolved from the per-namespace plugin registry.</para>
    /// </remarks>
    public interface IGraphFunction : IPlugin
    {
        /// <summary>
        ///   Runs the function against the captured graph.
        /// </summary>
        /// <param name="result">
        ///   On success, the selected vertices/edges (a view of existing elements); otherwise null.
        /// </param>
        /// <param name="parameters">
        ///   The call-time parameter bag (may be null/empty). Values are supplied by the invoker; the
        ///   function interprets them.
        /// </param>
        /// <returns>
        ///   <c>true</c> if the function produced a result; <c>false</c> on an expected failure (e.g. a
        ///   missing/invalid parameter), following the engine's <c>Try*(out result, ...)</c> pattern.
        /// </returns>
        Boolean TryInvoke(out GraphFunctionResult result, IDictionary<String, Object> parameters);
    }
}
