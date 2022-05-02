// MIT License
//
// AdminController.cs
//
// Copyright (c) 2021 Henning Rauch
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


        [HttpGet("/status")]
        [Produces("application/json")]
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

        [HttpHead("/trim")]
        public void Trim()
        {
            TrimTransaction tx = new TrimTransaction();

            _fallen8.EnqueueTransaction(tx);
        }

        [HttpPut("/load/{startServices}")]
        public void Load([FromRoute] Boolean startServices)
        {
            _logger.LogInformation(String.Format("Loading Fallen-8. Start services: {0}", startServices));

            LoadTransaction tx = new LoadTransaction();

            _fallen8.EnqueueTransaction(tx);
        }

        [HttpHead("/save")]
        public void Save()
        {
            _fallen8.Save(_savePath, _optimalNumberOfPartitions);
        }

        [HttpHead("/tabularasa")]
        public void TabulaRasa()
        {
            TabulaRasaTransaction tx = new TabulaRasaTransaction();

            _fallen8.EnqueueTransaction(tx);
        }

        [HttpGet("/vertex/count")]
        public int VertexCount()
        {
            return _fallen8.VertexCount;
        }

        [HttpGet("/edge/count")]
        public int EdgeCount()
        {
            return _fallen8.EdgeCount;
        }

        [HttpPost("/service")]
        [Consumes("application/json")]
        public bool CreateService([FromBody] PluginSpecification definition)
        {
            IService service;
            return _fallen8.ServiceFactory.TryAddService(out service, definition.PluginType, definition.UniqueId, ServiceHelper.CreatePluginOptions(definition.PluginOptions));
        }

        [HttpDelete("/service/{key}")]
        public bool DeleteService([FromRoute] string key)
        {
            return _fallen8.ServiceFactory.Services.Remove(key);
        }

        [HttpPut("/plugin")]
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
