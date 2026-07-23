// MIT License
//
// NamespacesController.cs
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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.App.Namespaces;

namespace NoSQL.GraphDB.App.Controllers
{
    /// <summary>
    ///   Namespace CRUD (feature graph-namespaces): list, inspect, create, rename, and drop the
    ///   namespaces of this Fallen-8. Fallen-8-level — these management routes exist once, never
    ///   under <c>/ns/{ns}</c> (the URL scheme's one home is the feature README).
    /// </summary>
    [ApiController]
    [ApiVersion("0.1")]
    [Route("api/v{version:apiVersion}/[controller]")]
    [Fallen8Level]
    public class NamespacesController : ControllerBase
    {
        private readonly Fallen8Namespaces _namespaces;

        public NamespacesController(Fallen8Namespaces namespaces)
        {
            _namespaces = namespaces;
        }

        /// <summary>
        /// Lists all namespaces with their counts and the configured ceiling
        /// </summary>
        /// <returns>The name-ordered namespace list and the maxNamespaces quota</returns>
        /// <response code="200">Returns the namespace list (always includes "default")</response>
        [HttpGet("/ns")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(NamespacesREST), StatusCodes.Status200OK)]
        public NamespacesREST GetAll()
        {
            var namespaces = _namespaces.Snapshot();
            var result = new NamespacesREST
            {
                Namespaces = new System.Collections.Generic.List<NamespaceREST>(namespaces.Count),
                MaxNamespaces = _namespaces.MaxNamespaces
            };
            foreach (var ns in namespaces)
            {
                result.Namespaces.Add(ToRest(ns));
            }

            return result;
        }

        /// <summary>
        /// Gets one namespace
        /// </summary>
        /// <param name="name">The namespace name</param>
        /// <returns>The namespace entry</returns>
        /// <response code="200">Returns the namespace</response>
        /// <response code="404">No namespace with this name exists</response>
        [HttpGet("/ns/{name}")]
        [ProducesResponseType(typeof(NamespaceREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult GetSingle([FromRoute] String name)
        {
            return _namespaces.TryGet(name, out var ns)
                ? Ok(ToRest(ns))
                : NotFoundProblem(name);
        }

        /// <summary>
        /// Creates a new, empty namespace
        /// </summary>
        /// <param name="name">The namespace name, matching ^[a-z0-9-]{1,63}$</param>
        /// <returns>The created namespace entry</returns>
        /// <remarks>
        /// The namespace is immediately ready: it owns a fresh Fallen-8 engine with its own
        /// vertices, edges, indices, subgraphs, stored queries, and change feed. Its routes live
        /// under /ns/{name}/… . The 422 body carries the configured limit as the "maxNamespaces"
        /// extension member.
        /// </remarks>
        /// <response code="201">The namespace was created</response>
        /// <response code="400">The name does not match ^[a-z0-9-]{1,63}$</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="409">A namespace with this name already exists</response>
        /// <response code="422">The configured Fallen8:Namespaces:MaxNamespaces ceiling is reached</response>
        /// <response code="429">The sensitive-endpoint rate limit was exceeded</response>
        [HttpPut("/ns/{name}")]
        [EnableRateLimiting(Fallen8SecurityOptions.SensitiveRateLimitPolicy)]
        [ProducesResponseType(typeof(NamespaceREST), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public IActionResult Create([FromRoute] String name)
        {
            return _namespaces.TryCreate(name, out var ns, out var failure)
                ? Created("/ns/" + ns.Name, ToRest(ns))
                : FailureProblem(name, failure);
        }

        /// <summary>
        /// Renames a namespace
        /// </summary>
        /// <param name="name">The current namespace name</param>
        /// <param name="specification">The new name</param>
        /// <returns>The renamed namespace entry</returns>
        /// <remarks>
        /// Rename is a pure metadata operation: the engine, its data, and its on-disk locations
        /// (keyed by the immutable namespace id) are untouched — only the URL address changes.
        /// The reserved "default" namespace cannot be renamed.
        /// </remarks>
        /// <response code="200">The namespace was renamed</response>
        /// <response code="400">The new name is missing or does not match ^[a-z0-9-]{1,63}$</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="404">No namespace with this name exists</response>
        /// <response code="409">The new name is already in use, or the namespace is "default"</response>
        /// <response code="429">The sensitive-endpoint rate limit was exceeded</response>
        [HttpPatch("/ns/{name}")]
        [EnableRateLimiting(Fallen8SecurityOptions.SensitiveRateLimitPolicy)]
        [Consumes("application/json")]
        [ProducesResponseType(typeof(NamespaceREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public IActionResult Rename([FromRoute] String name, [FromBody] NamespaceRenameSpecification specification)
        {
            if (specification == null || String.IsNullOrEmpty(specification.Name))
            {
                return ProblemResults.Create(StatusCodes.Status400BadRequest, "Invalid namespace name",
                    "A body of the form { \"name\": \"new-name\" } is required.");
            }

            return _namespaces.TryRename(name, specification.Name, out var ns, out var failure)
                ? Ok(ToRest(ns))
                : FailureProblem(name, failure, specification.Name);
        }

        /// <summary>
        /// Drops a namespace irreversibly
        /// </summary>
        /// <param name="name">The namespace name</param>
        /// <remarks>
        /// The namespace's in-memory graph, indices, and stored queries are gone and its live
        /// on-disk state (the write-ahead log) is deleted — there is no undo. Checkpoint files are
        /// NOT deleted: they belong to save-game entries, which remain valid restore points
        /// (delete them via DELETE /savegames/{id}?deleteFiles=true). The reserved "default"
        /// namespace cannot be dropped.
        /// </remarks>
        /// <response code="204">The namespace was dropped</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="404">No namespace with this name exists</response>
        /// <response code="409">The namespace is "default"</response>
        /// <response code="429">The sensitive-endpoint rate limit was exceeded</response>
        [HttpDelete("/ns/{name}")]
        [EnableRateLimiting(Fallen8SecurityOptions.SensitiveRateLimitPolicy)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public IActionResult Drop([FromRoute] String name)
        {
            return _namespaces.TryDrop(name, out var failure)
                ? NoContent()
                : FailureProblem(name, failure);
        }

        #region private helpers

        private static NamespaceREST ToRest(Namespace ns)
        {
            return new NamespaceREST
            {
                Name = ns.Name,
                State = ns.State == NamespaceState.Ready ? "ready" : "creating",
                VertexCount = ns.Engine.VertexCount,
                EdgeCount = ns.Engine.EdgeCount,
                CreatedAt = ns.CreatedAtUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
            };
        }

        private IActionResult NotFoundProblem(String name)
        {
            return ProblemResults.Create(StatusCodes.Status404NotFound, "Namespace not found",
                "No namespace named \"" + name + "\" exists on this Fallen-8.",
                p => p.Extensions["namespace"] = name);
        }

        private IActionResult FailureProblem(String name, NamespaceFailure failure, String newName = null)
        {
            switch (failure)
            {
                case NamespaceFailure.InvalidName:
                    return ProblemResults.Create(StatusCodes.Status400BadRequest, "Invalid namespace name",
                        "\"" + (newName ?? name) + "\" is not a valid namespace name. Names must match ^[a-z0-9-]{1," +
                        Fallen8Namespaces.MaxNameLength + "}$.");
                case NamespaceFailure.Conflict:
                    return ProblemResults.Create(StatusCodes.Status409Conflict, "Namespace name in use",
                        "A namespace named \"" + (newName ?? name) + "\" already exists.");
                case NamespaceFailure.QuotaExceeded:
                    return ProblemResults.Create(StatusCodes.Status422UnprocessableEntity, "Namespace quota exceeded",
                        "This Fallen-8 already holds " + _namespaces.Count + " namespaces; the configured ceiling is " +
                        _namespaces.MaxNamespaces + " (Fallen8:Namespaces:MaxNamespaces).",
                        p => p.Extensions["maxNamespaces"] = _namespaces.MaxNamespaces);
                case NamespaceFailure.Reserved:
                    return ProblemResults.Create(StatusCodes.Status409Conflict, "Reserved namespace",
                        "The \"" + Fallen8Namespaces.DefaultName + "\" namespace is reserved: it aliases the bare " +
                        "(un-prefixed) routes and cannot be renamed or dropped.");
                case NamespaceFailure.NotFound:
                default:
                    return NotFoundProblem(name);
            }
        }

        #endregion
    }
}
