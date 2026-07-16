// MIT License
//
// StoredQueryResolver.cs
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
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.Algorithms.SubGraph;
using NoSQL.GraphDB.Core.StoredQueries;

namespace NoSQL.GraphDB.App.Helper
{
    /// <summary>
    ///   Resolves a stored-query reference on an invocation request (feature
    ///   stored-query-library) into the entry's PINNED compiled artifact. Resolution captures the
    ///   artifact reference ONCE: a concurrent removal either wins before resolution (404) or the
    ///   invocation completes against the captured artifact - never a torn state (the delegates
    ///   keep the collectible AssemblyLoadContext alive until the invocation returns).
    /// </summary>
    internal static class StoredQueryResolver
    {
        /// <summary>
        ///   Resolves a stored <see cref="StoredQueryKind.Path"/> query to its pinned
        ///   <see cref="IPathTraverser"/>. Returns null on success; otherwise the error result:
        ///   404 unknown name (the message names the stored query, disambiguating it from the
        ///   endpoint's vertex 404s), 400 kind mismatch, 409 a non-invocable compile state (the
        ///   body carries the stored diagnostics).
        /// </summary>
        internal static IActionResult TryResolvePathTraverser(IFallen8 fallen8, String name, out IPathTraverser traverser)
        {
            traverser = null;

            var error = Resolve(fallen8, name, StoredQueryKind.Path, out var entry);
            if (error != null)
            {
                return error;
            }

            traverser = (IPathTraverser)entry.Artifact;
            return null;
        }

        /// <summary>
        ///   Resolves a stored <see cref="StoredQueryKind.SubGraph"/> query to its pinned
        ///   template <see cref="SubGraphDefinition"/> AND the stored template block (needed to
        ///   materialize the created subgraph's self-contained recipe). Returns null on success;
        ///   otherwise the error result (404 / 400 / 409 as above).
        /// </summary>
        internal static IActionResult TryResolveSubGraphTemplate(IFallen8 fallen8, String name,
            out SubGraphDefinition template, out StoredSubGraphQueryBlock templateBlock)
        {
            template = null;
            templateBlock = null;

            var error = Resolve(fallen8, name, StoredQueryKind.SubGraph, out var entry);
            if (error != null)
            {
                return error;
            }

            try
            {
                templateBlock = JsonSerializer.Deserialize(entry.Definition.SpecificationJson,
                    AppJsonContext.Default.StoredSubGraphQueryBlock);
            }
            catch (Exception)
            {
                templateBlock = null;
            }

            if (templateBlock == null)
            {
                // The stored document was serialized by the registration endpoint, so this is an
                // internal invariant breach, not a client error.
                return new ObjectResult(String.Format(
                    "The stored specification of stored query '{0}' could not be read.", name))
                { StatusCode = StatusCodes.Status500InternalServerError };
            }

            template = (SubGraphDefinition)entry.Artifact;
            return null;
        }

        private static IActionResult Resolve(IFallen8 fallen8, String name, StoredQueryKind expectedKind,
            out StoredQueryEntry entry)
        {
            if (!fallen8.StoredQueries.TryGet(out entry, name))
            {
                return new NotFoundObjectResult(String.Format("No stored query named '{0}'.", name));
            }

            if (entry.Definition.Kind != expectedKind)
            {
                return new BadRequestObjectResult(String.Format(
                    "Stored query '{0}' is of kind '{1}'; this endpoint requires kind '{2}'.",
                    name, entry.Definition.Kind, expectedKind));
            }

            if (entry.CompileState != StoredQueryCompileState.Compiled)
            {
                var message = entry.CompileState == StoredQueryCompileState.Failed
                    ? String.Format(
                        "Stored query '{0}' failed to recompile on load and is not invocable. Delete and re-register it. Diagnostics: {1}",
                        name, entry.CompileDiagnostics)
                    : String.Format(
                        "Stored query '{0}' was loaded without a compiler and is not invocable.", name);

                return new ConflictObjectResult(message);
            }

            return null;
        }
    }
}
