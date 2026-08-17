// MIT License
//
// NamespacesController.cs
//
// Copyright (c) 2011-2026 Henning Rauch
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
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.App.Namespaces;
using NoSQL.GraphDB.App.Services;

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
        private readonly NamespaceLoader _loader;

        public NamespacesController(Fallen8Namespaces namespaces, NamespaceLoader loader)
        {
            _namespaces = namespaces;
            _loader = loader;
        }

        /// <summary>
        /// Lists all namespaces with their counts and the configured ceiling
        /// </summary>
        /// <returns>The name-ordered namespace list and the maxNamespaces quota</returns>
        /// <remarks>
        /// A namespace that is cataloged but NOT loaded in this process is listed too, with state
        /// "notLoaded" and absent (null) vertex/edge counts (feature namespace-startup-load) - the
        /// list is the inventory, not the residency filter.
        /// </remarks>
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
                : NamespaceProblems.NotFound(name);
        }

        /// <summary>
        /// Creates a new, empty namespace
        /// </summary>
        /// <param name="name">The namespace name (permissive: up to 63 chars, any case/spaces/Unicode; not "."/".." or containing "/" "\" or control chars — it is a URL path segment)</param>
        /// <returns>The created namespace entry</returns>
        /// <remarks>
        /// The namespace is immediately ready: it owns a fresh Fallen-8 engine with its own
        /// vertices, edges, indices, subgraphs, stored queries, and change feed. Its routes live
        /// under /ns/{name}/… . The 422 body carries the configured limit as the "maxNamespaces"
        /// extension member.
        /// </remarks>
        /// <response code="201">The namespace was created</response>
        /// <response code="400">The name is empty/whitespace-padded, too long, "."/"..", or contains "/", "\", or a control character</response>
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
        /// Updates a namespace: rename it and/or set its plugin-registration or startup-load override
        /// </summary>
        /// <param name="name">The current namespace name</param>
        /// <param name="specification">The update: an optional new name and/or either override</param>
        /// <returns>The updated namespace entry</returns>
        /// <remarks>
        /// Rename is a pure metadata operation: the engine, its data, and its on-disk locations
        /// (keyed by the immutable namespace id) are untouched - only the URL address changes. The
        /// reserved "default" namespace cannot be RENAMED, but its plugin-registration override CAN
        /// be set. Both overrides take "enabled"/"disabled"/"inherit" ("inherit" clears them):
        /// "pluginRegistration" overrides Fallen8:Security:EnableDynamicPluginLoading for this
        /// namespace (feature plugin-registration), and "loadOnStartup" overrides
        /// Fallen8:Namespaces:LoadOnStartup, i.e. whether the NEXT boot loads this namespace at all
        /// (feature namespace-startup-load) - it takes effect on restart and never loads or unloads
        /// the running process's engine. The reserved "default" namespace is always loaded and
        /// refuses "loadOnStartup" with 409. The whole update is applied atomically: every field is
        /// validated first, then all of them are persisted by one catalog write. Supply at least one
        /// field.
        /// </remarks>
        /// <response code="200">The namespace was updated</response>
        /// <response code="400">No field supplied, an invalid new name, or an unrecognized "pluginRegistration"/"loadOnStartup" value</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="404">No namespace with this name exists</response>
        /// <response code="409">The new name is already in use, or a rename or "loadOnStartup" of the reserved "default" namespace</response>
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
        public IActionResult Update([FromRoute] String name, [FromBody] NamespaceUpdateSpecification specification)
        {
            if (specification == null)
            {
                return InvalidUpdate();
            }

            // Parse EVERY tri-state before any mutation: the rename is committed to the catalog and
            // survives a restart, so a 400 raised after it had already renamed the namespace would
            // contradict "rejected, nothing changed" (audit defect B31). A new field must join both
            // this block and the "supply at least one" guard - which it does structurally here,
            // because the guard reads NamespaceUpdate.IsEmpty over the very object built from them.
            var update = new NamespaceUpdate { NewName = specification.Name };

            if (specification.PluginRegistration != null)
            {
                if (!TryParseTriState(specification.PluginRegistration, out var pluginRegistration))
                {
                    return InvalidTriState("pluginRegistration");
                }

                update.PluginRegistrationSupplied = true;
                update.PluginRegistrationEnabled = pluginRegistration;
            }

            if (specification.LoadOnStartup != null)
            {
                if (!TryParseTriState(specification.LoadOnStartup, out var loadOnStartup))
                {
                    return InvalidTriState("loadOnStartup");
                }

                update.LoadOnStartupSupplied = true;
                update.LoadOnStartupEnabled = loadOnStartup;
            }

            if (update.IsEmpty)
            {
                return InvalidUpdate();
            }

            // ONE atomic update: a single catalog write persists every field, so a failing write can
            // no longer leave a rename applied while the response says the request was rejected.
            return _namespaces.TryUpdate(name, update, out var ns, out var failure)
                ? Ok(ToRest(ns))
                : FailureProblem(name, failure, specification.Name);
        }

        private IActionResult InvalidUpdate()
        {
            return ProblemResults.Create(StatusCodes.Status400BadRequest, "Invalid namespace update",
                "Supply a new \"name\" and/or a \"pluginRegistration\" and/or a \"loadOnStartup\" of " +
                "\"enabled\"/\"disabled\"/\"inherit\".");
        }

        private IActionResult InvalidTriState(String field)
        {
            return ProblemResults.Create(StatusCodes.Status400BadRequest, "Invalid " + field,
                "Expected \"enabled\", \"disabled\", or \"inherit\".");
        }

        /// <summary>Maps a tri-state body value to an override (true/false/null=inherit). Both
        /// overrides share one vocabulary, so they share one parser.</summary>
        private static bool TryParseTriState(String raw, out bool? enabled)
        {
            switch (raw)
            {
                case "enabled": enabled = true; return true;
                case "disabled": enabled = false; return true;
                case "inherit": enabled = null; return true;
                default: enabled = null; return false;
            }
        }

        /// <summary>
        /// Loads a cataloged namespace into the running process
        /// </summary>
        /// <param name="name">The namespace name</param>
        /// <returns>The activated namespace, and whether this call is what loaded it</returns>
        /// <remarks>
        /// The way back from an exclusion without a restart (feature namespace-startup-load): the
        /// namespace's engine is constructed, its newest registered save game is restored and its
        /// write-ahead-log tail replayed on top, and only then does it start serving requests - so a
        /// failed restore leaves it exactly as not-loaded as it was, and no request ever sees a
        /// half-loaded graph.
        ///
        /// Idempotent: activating a namespace that is already loaded is a 200 with
        /// "activated": false, never a conflict.
        ///
        /// It does NOT change the persisted startup-load policy, because the two answer different
        /// questions: this call answers for the running process, the policy answers for the next
        /// boot, which still honours it. To make it stick, PATCH /ns/{name} with "loadOnStartup":
        /// "enabled" as well.
        ///
        /// Named "activate" because /ns/{name}/load already means "restore a checkpoint into a
        /// namespace".
        /// </remarks>
        /// <response code="200">The namespace is loaded (whether by this call or already)</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="404">No namespace with this name exists</response>
        /// <response code="409">It has checkpoint files no registered save game contains; the body names how to adopt them</response>
        /// <response code="429">The sensitive-endpoint rate limit was exceeded</response>
        /// <response code="500">Its checkpoint could not be restored; it stays not loaded and its files are untouched</response>
        [HttpPost("/ns/{name}/activate")]
        [EnableRateLimiting(Fallen8SecurityOptions.SensitiveRateLimitPolicy)]
        [Produces("application/json")]
        [ProducesResponseType(typeof(NamespaceActivationREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> Activate([FromRoute] String name)
        {
            var activation = await _loader.ActivateAsync(name);
            switch (activation.Outcome)
            {
                case NamespaceActivationOutcome.Activated:
                case NamespaceActivationOutcome.AlreadyLoaded:
                    return Ok(new NamespaceActivationREST
                    {
                        Namespace = ToRest(activation.Namespace),
                        Activated = activation.Outcome == NamespaceActivationOutcome.Activated,
                        Detail = activation.Detail
                    });

                case NamespaceActivationOutcome.UnregisteredCheckpoints:
                    // 409, and NOT a 500 or a 200: nothing is broken and nothing failed - the server
                    // is refusing to publish an empty engine over checkpoint files no registry entry
                    // claims, which conflicts with the state of the namespace on disk. The detail is
                    // the loader's, so the situation and the way to adopt those files read the same
                    // here as in the boot log.
                    return ProblemResults.Create(StatusCodes.Status409Conflict,
                        "Namespace has unregistered checkpoints", activation.Detail,
                        p =>
                        {
                            p.Extensions["namespace"] = name;
                            p.Extensions["namespaceState"] = Namespace.WireName(NamespaceState.NotLoaded);
                        });

                case NamespaceActivationOutcome.LoadFailed:
                    // 500, not 503: the namespace is reachable and the request was understood, but its
                    // own durability state is broken and no retry fixes it until an operator acts. The
                    // detail is the loader's, so this failure reads the same as the boot's abort does
                    // in the log.
                    return ProblemResults.Create(StatusCodes.Status500InternalServerError,
                        "Namespace activation failed", activation.Detail,
                        p =>
                        {
                            p.Extensions["namespace"] = name;
                            p.Extensions["namespaceState"] = Namespace.WireName(NamespaceState.NotLoaded);
                        });

                case NamespaceActivationOutcome.NotFound:
                default:
                    return NamespaceProblems.NotFound(name);
            }
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

        /// <summary>
        ///   Projects a namespace onto the wire WITHOUT assuming an engine (feature
        ///   namespace-startup-load): a not-loaded namespace reports state <c>notLoaded</c> with
        ///   absent counts, so the list keeps listing it. Reading <c>ns.Engine</c> here would answer
        ///   503 for the WHOLE list because of one excluded namespace, which is the same recover-state
        ///   trap as hiding it.
        /// </summary>
        private static NamespaceREST ToRest(Namespace ns)
        {
            var loaded = ns.TryGetEngine(out var engine);
            return new NamespaceREST
            {
                Name = ns.Name,
                State = Namespace.WireName(ns.EffectiveState),
                VertexCount = loaded ? engine.VertexCount : (Int32?)null,
                EdgeCount = loaded ? engine.EdgeCount : (Int32?)null,
                CreatedAt = ns.CreatedAtUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                PluginRegistrationEnabled = ns.PluginRegistrationEnabled,
                LoadOnStartupEnabled = ns.LoadOnStartupEnabled
            };
        }

        private IActionResult FailureProblem(String name, NamespaceFailure failure, String newName = null)
        {
            switch (failure)
            {
                case NamespaceFailure.InvalidName:
                    return ProblemResults.Create(StatusCodes.Status400BadRequest, "Invalid namespace name",
                        "\"" + (newName ?? name) + "\" is not a valid namespace name. A name may be up to " +
                        Fallen8Namespaces.MaxNameLength + " characters and may not be \".\"/\"..\", have leading or " +
                        "trailing whitespace, or contain \"/\", \"\\\", or control characters.");
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
                        "(un-prefixed) routes and cannot be renamed, dropped, or excluded from the startup load.");
                case NamespaceFailure.NotFound:
                default:
                    return NamespaceProblems.NotFound(name);
            }
        }

        #endregion
    }
}
