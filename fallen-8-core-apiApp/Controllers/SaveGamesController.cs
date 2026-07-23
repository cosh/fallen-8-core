// MIT License
//
// SaveGamesController.cs
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
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Linq;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.App.Namespaces;
using NoSQL.GraphDB.App.Services;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.App.Controllers
{
    /// <summary>
    ///   The save-game (checkpoint) registry over REST (feature save-games): list, inspect, load and
    ///   delete registered checkpoints.
    /// </summary>
    [ApiController]
    [Route("api/v{version:apiVersion}/[controller]")]
    [ApiVersion("0.1")]
    [Fallen8Level]
    public class SaveGamesController : ControllerBase
    {
        private readonly Fallen8Namespaces _namespaces;
        private readonly SaveGameRegistry _registry;
        private readonly ILogger<SaveGamesController> _logger;

        public SaveGamesController(Fallen8Namespaces namespaces, SaveGameRegistry registry, ILogger<SaveGamesController> logger)
        {
            _namespaces = namespaces;
            _registry = registry;
            _logger = logger;
        }

        /// <summary>
        /// Lists all registered save games, newest first
        /// </summary>
        /// <returns>The registered save games with their KPIs and file facts</returns>
        /// <response code="200">The registered save games</response>
        [HttpGet("/savegames")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(List<SaveGameREST>), StatusCodes.Status200OK)]
        public ActionResult<List<SaveGameREST>> List()
        {
            return _registry.GetAll();
        }

        /// <summary>
        /// Gets a single registered save game by id
        /// </summary>
        /// <param name="id">The save-game id</param>
        /// <returns>The save game, or 204 when unknown</returns>
        /// <response code="200">The save game</response>
        /// <response code="204">No save game with that id</response>
        [HttpGet("/savegames/{id}")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(SaveGameREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult<SaveGameREST> Get([FromRoute] String id)
        {
            var entry = _registry.GetById(id);
            if (entry == null)
            {
                return NoContent();
            }
            return entry;
        }

        /// <summary>
        /// Restores a registered save game's namespaces, replacing their in-memory graphs
        /// </summary>
        /// <param name="id">The save-game id</param>
        /// <param name="waitForCompletion">Wait for the load transactions to finish before responding</param>
        /// <param name="namespaceName">Restore only this namespace out of the entry (404 when the entry does not contain it)</param>
        /// <returns>The loaded save game's summary</returns>
        /// <remarks>
        /// Restores exactly the namespaces the entry contains (feature graph-namespaces): a dropped
        /// namespace is recreated, an existing one has its content replaced, and namespaces the
        /// entry does NOT contain are left untouched. Pre-namespace (v1) entries restore into
        /// "default". With ?namespace={name}, only that one namespace is restored.
        /// </remarks>
        /// <response code="200">Loaded (waited); returns the save game</response>
        /// <response code="202">Load accepted (not waited)</response>
        /// <response code="404">No save game with that id, or the entry does not contain the requested namespace</response>
        /// <response code="500">A load transaction was rolled back, or a dropped namespace could not be recreated</response>
        [HttpPut("/savegames/{id}/load")]
        [ProducesResponseType(typeof(SaveGameREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> Load([FromRoute] String id, [FromQuery] bool waitForCompletion = false,
            [FromQuery(Name = "namespace")] String namespaceName = null)
        {
            var entry = _registry.GetById(id);
            if (entry == null)
            {
                return NotFound(String.Format("No save game with id '{0}'.", id));
            }

            var members = SaveGameRegistry.EffectiveNamespaces(entry);
            if (namespaceName != null)
            {
                var member = members.FirstOrDefault(m => m.Name == namespaceName);
                if (member == null)
                {
                    return ProblemResults.Create(StatusCodes.Status404NotFound, "Namespace not in save game",
                        "Save game \"" + id + "\" does not contain namespace \"" + namespaceName + "\"; it contains: " +
                        String.Join(", ", members.Select(m => m.Name)) + ".");
                }
                members = new List<SaveGameNamespaceREST> { member };
            }

            // Validate EVERY member's checkpoint file BEFORE touching anything: the engine's Load
            // treats a missing file as a no-op success (never a rollback), so without this check a
            // gutted entry would answer 200, recreate dropped namespaces permanently, and load
            // nothing - the REST twin of the boot path's FR-9 loud failure.
            foreach (var member in members)
            {
                if (!System.IO.File.Exists(member.Location))
                {
                    return ProblemResults.Create(StatusCodes.Status500InternalServerError, "Save game files missing",
                        "Save game \"" + id + "\" points at \"" + member.Location + "\" for namespace \"" +
                        member.Name + "\", which does not exist; nothing was restored. Restore the files, or " +
                        "remove the entry (DELETE /savegames/" + id + ").");
                }
            }

            // Resolve/recreate ALL target namespaces before enqueuing ANY load, so a failure here
            // leaves every graph untouched. Namespaces the entry does not contain are never touched.
            var targets = new List<(SaveGameNamespaceREST Member, Namespace Target, Boolean Recreated)>();
            foreach (var member in members)
            {
                var recreated = false;
                if (!_namespaces.TryGet(member.Name, out var target))
                {
                    if (!_namespaces.TryCreate(member.Name, out target, out var failure))
                    {
                        return ProblemResults.Create(StatusCodes.Status500InternalServerError, "Namespace restore failed",
                            "The dropped namespace \"" + member.Name + "\" could not be recreated (" + failure + "); " +
                            "nothing was restored.");
                    }
                    recreated = true;
                }
                targets.Add((member, target, recreated));
            }

            var loads = new List<(String Name, TransactionInformation Info)>();
            foreach (var (member, target, _) in targets)
            {
                var tx = new LoadTransaction { Path = member.Location, StartServices = true };
                loads.Add((member.Name, target.Engine.EnqueueTransaction(tx)));
            }

            if (!waitForCompletion)
            {
                return Accepted();
            }

            // Await EVERY load before answering - an early error return would report a state that
            // is still changing behind it.
            var rolledBack = new List<String>();
            foreach (var load in loads)
            {
                await load.Info.Completion;
                if (load.Info.TransactionState == TransactionState.RolledBack)
                {
                    rolledBack.Add(load.Name);
                }
            }

            if (rolledBack.Count > 0)
            {
                return ProblemResults.Create(StatusCodes.Status500InternalServerError, "Restore incomplete",
                    "The load transaction rolled back for: " + String.Join(", ", rolledBack) + ".",
                    p => p.Extensions["failedNamespaces"] = rolledBack);
            }

            // A RECREATED namespace has a fresh immutable id, so the entry that just restored it
            // no longer matches it at boot; register the restored checkpoint under the new id to
            // keep the boot chain intact (an unclean restart must not serve it empty).
            foreach (var (member, target, recreated) in targets)
            {
                if (recreated)
                {
                    try
                    {
                        _registry.RegisterImportIfUnknown(target.Name, target.Id, target.Engine, member.Location);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Restored namespace \"{Namespace}\" could not be re-registered under its new id.",
                            target.Name);
                    }
                }
            }

            return Ok(entry);
        }

        /// <summary>
        /// Removes a save game from the registry, optionally deleting its files
        /// </summary>
        /// <param name="id">The save-game id</param>
        /// <param name="deleteFiles">When true, also delete the checkpoint files on disk</param>
        /// <response code="204">Removed</response>
        /// <response code="404">No save game with that id</response>
        [HttpDelete("/savegames/{id}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult Delete([FromRoute] String id, [FromQuery] bool deleteFiles = false)
        {
            return _registry.Delete(id, deleteFiles)
                ? NoContent()
                : NotFound(String.Format("No save game with id '{0}'.", id));
        }
    }
}
