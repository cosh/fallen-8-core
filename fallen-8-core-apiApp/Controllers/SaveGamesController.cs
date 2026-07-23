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
using NoSQL.GraphDB.App.Namespaces;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Services;
using NoSQL.GraphDB.Core;
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
        private readonly IFallen8 _fallen8;
        private readonly SaveGameRegistry _registry;
        private readonly ILogger<SaveGamesController> _logger;

        public SaveGamesController(IFallen8 fallen8, SaveGameRegistry registry, ILogger<SaveGamesController> logger)
        {
            _fallen8 = fallen8;
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
        /// Loads a registered save game, replacing the current in-memory graph
        /// </summary>
        /// <param name="id">The save-game id</param>
        /// <param name="waitForCompletion">Wait for the load transaction to finish before responding</param>
        /// <returns>The loaded save game's summary</returns>
        /// <response code="200">Loaded (waited); returns the save game</response>
        /// <response code="202">Load accepted (not waited)</response>
        /// <response code="404">No save game with that id</response>
        /// <response code="500">The load transaction was rolled back</response>
        [HttpPut("/savegames/{id}/load")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(SaveGameREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status202Accepted)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> Load([FromRoute] String id, [FromQuery] bool waitForCompletion = false)
        {
            var entry = _registry.GetById(id);
            if (entry == null)
            {
                return NotFound(String.Format("No save game with id '{0}'.", id));
            }

            var tx = new LoadTransaction { Path = entry.Location, StartServices = true };
            var task = _fallen8.EnqueueTransaction(tx);

            if (!waitForCompletion)
            {
                return Accepted();
            }

            await task.Completion;
            if (task.TransactionState == TransactionState.RolledBack)
            {
                return StatusCode(StatusCodes.Status500InternalServerError,
                    "The load transaction was rolled back; the save game was not loaded.");
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
