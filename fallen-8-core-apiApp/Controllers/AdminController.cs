// MIT License
//
// AdminController.cs
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
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.App.Interfaces;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.Analytics;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.Helper;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Plugin;
using NoSQL.GraphDB.Core.Serializer;
using NoSQL.GraphDB.Core.Service;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.App.Controllers
{
    [ApiController]
    [Route("api/v{version:apiVersion}/[controller]")]
    [ApiVersion("0.1")]
    public class AdminController : ControllerBase, IRESTService
    {
        #region Data

        /// <summary>
        ///   The internal Fallen-8 instance
        /// </summary>
        private readonly IFallen8 _fallen8;

        /// <summary>
        /// The Fallen-8 save path
        /// </summary>
        private readonly String _savePath;

        /// <summary>
        /// The Fallen-8 save file
        /// </summary>
        private readonly String _saveFile;

        /// <summary>
        /// The optimal number of partitions
        /// </summary>
        private readonly int _optimalNumberOfPartitions;

        private readonly ILogger<AdminController> _logger;

        /// <summary>
        /// The isolated directory uploaded plugin DLLs are written to (never the app's own binary
        /// directory) - feature api-security-boundary.
        /// </summary>
        private readonly String _pluginDirectory;

        /// <summary>
        /// The save-game metadata registry (feature save-games): records every save and auto-registers
        /// an unknown checkpoint on load.
        /// </summary>
        private readonly Services.SaveGameRegistry _saveGames;

        /// <summary>
        /// Whether an API key is configured, reported by /status (see StatusREST.ApiKeyRequired).
        /// </summary>
        private readonly Boolean _apiKeyConfigured;

        #endregion

        public AdminController(ILogger<AdminController> logger, IFallen8 fallen8, IOptions<Fallen8SecurityOptions> security,
            Services.SaveGameRegistry saveGames)
        {
            _logger = logger;

            _fallen8 = fallen8;

            _saveFile = "Temp.f8s";

            string currentAssemblyDirectoryName = AppContext.BaseDirectory;

            _savePath = System.IO.Path.Combine(currentAssemblyDirectoryName, _saveFile);

            _optimalNumberOfPartitions = Convert.ToInt32(Environment.ProcessorCount * 3 / 2);

            var securityOptions = security?.Value ?? new Fallen8SecurityOptions();
            _pluginDirectory = securityOptions.ResolvePluginDirectory();
            _apiKeyConfigured = !String.IsNullOrWhiteSpace(securityOptions.ApiKey);

            _saveGames = saveGames;
        }

        #region IDisposable Members

        public void Dispose()
        {
        }

        #endregion

        /// <summary>
        /// Gets the current status of the Fallen-8 database
        /// </summary>
        /// <returns>Status information including counts, available plugins and memory usage</returns>
        /// <response code="200">Returns the database status information</response>
        [HttpGet("/status")]
        [AllowAnonymous]
        [Produces("application/json")]
        [ProducesResponseType(typeof(StatusREST), StatusCodes.Status200OK)]
        public StatusREST Status()
        {
            var totalBytesOfMemoryUsed = Process.GetCurrentProcess().VirtualMemorySize64;

            var vertexCount = _fallen8.VertexCount;
            var edgeCount = _fallen8.EdgeCount;

            IEnumerable<String> availableIndices;
            PluginFactory.TryGetAvailablePlugins<IIndex>(out availableIndices);

            IEnumerable<String> availablePathAlgos;
            PluginFactory.TryGetAvailablePlugins<IShortestPathAlgorithm>(out availablePathAlgos);

            IEnumerable<String> availableAnalyticsAlgos;
            PluginFactory.TryGetAvailablePlugins<IGraphAnalyticsAlgorithm>(out availableAnalyticsAlgos);

            IEnumerable<String> availableServices;
            PluginFactory.TryGetAvailablePlugins<IService>(out availableServices);

            return new StatusREST
            {
                AvailableIndexPlugins = new List<String>(availableIndices),
                AvailablePathPlugins = new List<String>(availablePathAlgos),
                AvailableAnalyticsPlugins = new List<String>(availableAnalyticsAlgos),
                AvailableServicePlugins = new List<String>(availableServices),
                EdgeCount = edgeCount,
                VertexCount = vertexCount,
                UsedMemory = totalBytesOfMemoryUsed,
                ApiKeyRequired = _apiKeyConfigured,
                Authenticated = HttpContext?.User?.Identity?.IsAuthenticated == true,
            };
        }

        /// <summary>
        /// Trims the database, releasing unused memory
        /// </summary>
        /// <response code="204">Trim operation successfully initiated</response>
        [HttpHead("/trim")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public void Trim()
        {
            TrimTransaction tx = new TrimTransaction();

            _fallen8.EnqueueTransaction(tx);
        }

        /// <summary>
        /// Loads a Fallen-8 database from a saved file
        /// </summary>
        /// <param name="definition">Load specification including file path and service start options</param>
        /// <remarks>
        /// Sample request:
        ///
        ///     PUT /load
        ///     {
        ///        "startServices": true,
        ///        "saveGameLocation": "C:/Fallen8/database.f8s"
        ///     }
        /// </remarks>
        /// <response code="204">Database loaded successfully</response>
        /// <response code="400">Invalid load specification or file not found</response>
        /// <response code="500">The load transaction was rolled back and did not complete</response>
        [HttpPut("/load")]
        [Consumes("application/json")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async System.Threading.Tasks.Task<IActionResult> Load([FromBody] LoadSpecification definition)
        {
            _logger.LogInformation(String.Format("Loading Fallen-8. Start services: {0}", definition.StartServices));

            LoadTransaction tx = new LoadTransaction();
            tx.Path = definition.SaveGameLocation;
            tx.StartServices = definition.StartServices;

            var transactionTask = _fallen8.EnqueueTransaction(tx);
            await transactionTask.Completion;

            // A rolled-back load must not be reported to the client as success (correctness-fixes B6).
            if (transactionTask.TransactionState == TransactionState.RolledBack)
            {
                return StatusCode(StatusCodes.Status500InternalServerError,
                    "The load transaction was rolled back; the database was not loaded.");
            }

            // A checkpoint loaded from an arbitrary path that is not yet in the registry is recorded
            // now (feature save-games FR-7), so the historical record captures manually-loaded saves.
            try
            {
                _saveGames.RegisterImportIfUnknown(_fallen8, definition.SaveGameLocation);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The loaded checkpoint could not be registered in the save-game registry.");
            }

            return NoContent();
        }

        /// <summary>
        /// Saves the current database state to a file
        /// </summary>
        /// <param name="definition">Save specification including file path and partition options (both optional)</param>
        /// <returns>The created save-game registry entry (including its path)</returns>
        /// <remarks>
        /// Sample request:
        ///
        ///     PUT /save
        ///     {
        ///        "saveGameLocation": "C:/Fallen8/database.f8s",
        ///        "savePartitions": 8
        ///     }
        ///
        /// Both parameters are optional. If not provided, defaults to the base directory with filename "Temp.f8s" and optimal partition count.
        /// The save is recorded in the save-game registry (feature save-games); the response is the
        /// created entry, whose "location" field is the path the database was saved to.
        /// </remarks>
        /// <response code="200">Returns the created save-game registry entry</response>
        /// <response code="400">Invalid save specification</response>
        /// <response code="500">The save transaction was rolled back and did not complete</response>
        [HttpPut("/save")]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(SaveGameREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async System.Threading.Tasks.Task<IActionResult> Save([FromBody] SaveSpecification definition)
        {
            // Use provided path or fall back to default
            string savePath = !string.IsNullOrWhiteSpace(definition?.SaveGameLocation)
                ? definition.SaveGameLocation
                : _savePath;

            // Use provided partitions or fall back to optimal
            int savePartitions = definition?.SavePartitions ?? _optimalNumberOfPartitions;

            SaveTransaction saveTx = new SaveTransaction() { Path = savePath, SavePartitions = savePartitions };
            var transactionTask = _fallen8.EnqueueTransaction(saveTx);
            await transactionTask.Completion;

            // A rolled-back save must not be reported to the client as success (correctness-fixes B6).
            if (transactionTask.TransactionState == TransactionState.RolledBack)
            {
                return StatusCode(StatusCodes.Status500InternalServerError,
                    "The save transaction was rolled back; the database was not saved.");
            }

            // Record the successful save in the registry and return the entry (feature save-games FR-4).
            // The checkpoint is already physically written; a registry failure must NOT turn a
            // successful save into a 500. Fall back to a best-effort entry describing the save.
            try
            {
                return Ok(_saveGames.Register(_fallen8, saveTx.ActualPath, "api"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The save to \"{Path}\" succeeded but could not be registered in the save-game registry.", saveTx.ActualPath);
                return Ok(new SaveGameREST
                {
                    SavedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    Trigger = "api",
                    Location = saveTx.ActualPath,
                });
            }
        }

        /// <summary>
        /// Clears all data from the database (resets to empty state)
        /// </summary>
        /// <response code="204">Database successfully cleared</response>
        [HttpHead("/tabularasa")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public void TabulaRasa()
        {
            TabulaRasaTransaction tx = new TabulaRasaTransaction();

            _fallen8.EnqueueTransaction(tx);
        }

        /// <summary>
        /// Gets the total number of vertices in the database
        /// </summary>
        /// <returns>Count of vertices in the database</returns>
        /// <response code="200">Returns the number of vertices</response>
        [HttpGet("/vertex/count")]
        [AllowAnonymous]
        [Produces("application/json")]
        [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
        public int VertexCount()
        {
            return _fallen8.VertexCount;
        }

        /// <summary>
        /// Gets the total number of edges in the database
        /// </summary>
        /// <returns>Count of edges in the database</returns>
        /// <response code="200">Returns the number of edges</response>
        [HttpGet("/edge/count")]
        [AllowAnonymous]
        [Produces("application/json")]
        [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
        public int EdgeCount()
        {
            return _fallen8.EdgeCount;
        }

        /// <summary>
        /// Creates a new service based on the specified plugin
        /// </summary>
        /// <param name="definition">Plugin specification including type, ID and options</param>
        /// <returns>True if service was successfully created, false otherwise</returns>
        /// <response code="200">Returns whether the service was successfully created</response>
        /// <response code="400">Invalid plugin specification</response>
        [HttpPost("/service")]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public bool CreateService([FromBody] PluginSpecification definition)
        {
            IService service;
            return _fallen8.ServiceFactory.TryAddService(out service, definition.PluginType, definition.UniqueId, ServiceHelper.CreatePluginOptions(definition.PluginOptions));
        }

        /// <summary>
        /// Deletes a service with the specified key
        /// </summary>
        /// <param name="key">The unique identifier of the service to delete</param>
        /// <returns>True if the service was successfully deleted, false if it wasn't found</returns>
        /// <response code="200">Returns whether the service was successfully deleted</response>
        [HttpDelete("/service/{key}")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
        public bool DeleteService([FromRoute] string key)
        {
            return _fallen8.ServiceFactory.Services.Remove(key);
        }

        /// <summary>
        /// Uploads and registers a new plugin to the database
        /// </summary>
        /// <param name="dllStream">The plugin DLL binary content as a stream</param>
        /// <response code="204">Plugin successfully uploaded and registered</response>
        /// <response code="400">Invalid plugin data or incompatible plugin</response>
        /// <response code="401">No valid credential was supplied</response>
        /// <response code="403">Dynamic plugin loading is disabled on this server (Fallen8:Security:EnableDynamicPluginLoading)</response>
        /// <response code="413">The uploaded DLL exceeds the plugin size limit</response>
        /// <remarks>
        /// SECURITY: an uploaded plugin is loaded and executed IN-PROCESS WITH FULL TRUST - a trust
        /// boundary, not a sandbox. Requires an authenticated caller AND
        /// Fallen8:Security:EnableDynamicPluginLoading=true. The DLL is written to the configured,
        /// isolated plugin directory, never next to the server binaries.
        /// </remarks>
        [HttpPut("/plugin")]
        [Authorize(Policy = Fallen8SecurityOptions.DynamicPluginPolicy)]
        [EnableRateLimiting(Fallen8SecurityOptions.SensitiveRateLimitPolicy)]
        [RequestSizeLimit(64L * 1024 * 1024)]
        [Consumes("application/octet-stream")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        public IActionResult UploadPlugin([FromBody] Stream dllStream)
        {
            if (dllStream == null)
            {
                return BadRequest("A plugin DLL stream is required.");
            }

            // Write into the isolated plugin directory (created if absent), NOT AppContext.BaseDirectory,
            // so an upload can never plant a DLL next to the server's own binaries. The directory is a
            // registered plugin search directory (see Program.cs), so the plugin is still discovered.
            System.IO.Directory.CreateDirectory(_pluginDirectory);
            var assimilationPath = System.IO.Path.Combine(_pluginDirectory, System.IO.Path.GetRandomFileName() + ".dll");

            try
            {
                PluginFactory.Assimilate(dllStream, assimilationPath);
            }
            catch (Exception ex)
            {
                // An invalid/incompatible DLL is a client error (400), not an unhandled 500 - matching
                // the documented ProducesResponseType(400) (feature api-error-contract E7). Best-effort
                // cleanup of the partially-written file.
                try { if (System.IO.File.Exists(assimilationPath)) System.IO.File.Delete(assimilationPath); } catch { }
                return BadRequest("The uploaded plugin could not be loaded: " + ex.Message);
            }

            return NoContent();
        }

        #region private helper

        // Checkpoint discovery moved to NoSQL.GraphDB.App.Helper.CheckpointDiscovery and is now driven
        // by the hosted DurabilityLifecycleService (feature hosted-durability-lifecycle). The former
        // private FindLatestFallen8 was dead code (never called) and has been removed.

        #endregion

        #region not implemented

        [NonAction]
        public void Save(SerializationWriter writer)
        {
        }

        [NonAction]
        public void Load(SerializationReader reader, IFallen8 fallen8)
        {
        }

        [NonAction]
        public void Shutdown()
        {
        }

        #endregion
    }
}
