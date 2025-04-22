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
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.App.Interfaces;
using NoSQL.GraphDB.Core;
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
        private readonly Fallen8 _fallen8;

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
        private readonly UInt32 _optimalNumberOfPartitions;

        private readonly ILogger<AdminController> _logger;

        #endregion

        public AdminController(ILogger<AdminController> logger, Fallen8 fallen8)
        {
            _logger = logger;

            _fallen8 = fallen8;

            _saveFile = "Temp.f8s";

            string currentAssemblyDirectoryName = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            _savePath = currentAssemblyDirectoryName + System.IO.Path.DirectorySeparatorChar + _saveFile;

            _optimalNumberOfPartitions = Convert.ToUInt32(Environment.ProcessorCount * 3 / 2);
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

            IEnumerable<String> availableServices;
            PluginFactory.TryGetAvailablePlugins<IService>(out availableServices);

            return new StatusREST
            {
                AvailableIndexPlugins = new List<String>(availableIndices),
                AvailablePathPlugins = new List<String>(availablePathAlgos),
                AvailableServicePlugins = new List<String>(availableServices),
                EdgeCount = edgeCount,
                VertexCount = vertexCount,
                UsedMemory = totalBytesOfMemoryUsed,
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
        [HttpPut("/load")]
        [Consumes("application/json")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public void Load([FromBody] LoadSpecification definition)
        {
            _logger.LogInformation(String.Format("Loading Fallen-8. Start services: {0}", definition.StartServices));

            LoadTransaction tx = new LoadTransaction();
            tx.Path = definition.SaveGameLocation;
            tx.StartServices = definition.StartServices;

            _fallen8.EnqueueTransaction(tx).WaitUntilFinished();
        }

        /// <summary>
        /// Saves the current database state to a file
        /// </summary>
        /// <returns>The path where the database was saved</returns>
        /// <response code="200">Returns the path where the database was saved</response>
        [HttpGet("/save")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
        public String Save()
        {
            SaveTransaction saveTx = new SaveTransaction() { Path = _savePath, SavePartitions = _optimalNumberOfPartitions };
            _fallen8.EnqueueTransaction(saveTx).WaitUntilFinished();

            return saveTx.ActualPath;
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
        [HttpPut("/plugin")]
        [Consumes("application/octet-stream")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public void UploadPlugin([FromBody] Stream dllStream)
        {
            PluginFactory.Assimilate(dllStream);
        }

        #region private helper

        /// <summary>
        /// Searches for the latest fallen-8
        /// </summary>
        /// <returns></returns>
        private string FindLatestFallen8()
        {
            _logger.LogInformation("Trying to find the latest Fallen-8 savegame");
            string currentAssemblyDirectoryName = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            _logger.LogInformation(String.Format("Save directory: {0}", currentAssemblyDirectoryName));

            var versions = Directory.EnumerateFiles(currentAssemblyDirectoryName,
                                               _saveFile + Constants.VersionSeparator + "*")
                                               .ToList();

            if (versions.Count > 0)
            {
                _logger.LogInformation(String.Format("There are multiple Fallen-8 savegames."));

                var fileToPathMapper = versions
                    .Select(path => path.Split(System.IO.Path.DirectorySeparatorChar))
                    .Where(_ => !_.Last().Contains(Constants.GraphElementsSaveString))
                    .Where(_ => !_.Last().Contains(Constants.IndexSaveString))
                    .Where(_ => !_.Last().Contains(Constants.ServiceSaveString))
                    .ToDictionary(key => key.Last(), value => value.Aggregate((a, b) => a + System.IO.Path.DirectorySeparatorChar + b));

                var latestRevision = fileToPathMapper
                    .Select(file => file.Key.Split(Constants.VersionSeparator)[1])
                    .Select(revisionString => DateTime.FromBinary(Convert.ToInt64(revisionString)))
                    .OrderByDescending(revision => revision)
                    .First()
                    .ToBinary()
                    .ToString(CultureInfo.InvariantCulture);

                _logger.LogInformation(String.Format("The latest revivision is from {0}", DateTime.FromBinary(Convert.ToInt64(latestRevision))));

                return fileToPathMapper.First(_ => _.Key.Contains(latestRevision)).Value;
            }

            var lookupPath = System.IO.Path.Combine(currentAssemblyDirectoryName, _saveFile);
            _logger.LogInformation(String.Format("Trying to find a savegame here: {0}", lookupPath));

            if (System.IO.File.Exists(lookupPath))
            {
                _logger.LogInformation(String.Format("There is a savegame here: {0}", lookupPath));
                return lookupPath;
            }

            _logger.LogInformation(String.Format("There were no Fallen-8 savegames.", versions.Count));

            return null;
        }

        #endregion

        #region not implemented

        [NonAction]
        public void Save(SerializationWriter writer)
        {
        }

        [NonAction]
        public void Load(SerializationReader reader, Fallen8 fallen8)
        {
        }

        [NonAction]
        public void Shutdown()
        {
        }

        #endregion
    }
}
