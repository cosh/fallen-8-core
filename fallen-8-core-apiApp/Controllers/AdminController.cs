using Microsoft.AspNetCore.Mvc;
using Microsoft.CSharp;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.App.Interfaces;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.Helper;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Index.Fulltext;
using NoSQL.GraphDB.Core.Index.Spatial;
using NoSQL.GraphDB.Core.Log;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Plugin;
using NoSQL.GraphDB.Core.Service;
using NoSQL.GraphDB.Serializer;
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;

namespace NoSQL.GraphDB.App.Controllers
{
    [ApiController]
    [Route("[controller]")]
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
            _fallen8.Trim();
        }

        [HttpGet("/load/{startServices}")]
        [Produces("application/json")]
        public void Load([FromQuery] Boolean startServices)
        {
            Logger.LogInfo(String.Format("Loading Fallen-8. Start services: {0}", startServices));
            _fallen8.Load(FindLatestFallen8(), startServices);
        }

        [HttpHead("/save")]
        public void Save()
        {
            _fallen8.Save(_savePath, _optimalNumberOfPartitions);
        }

        [HttpHead("/tabularasa")]
        public void TabulaRasa()
        {
            _fallen8.TabulaRasa();
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
        public bool CreateService([FromBody]PluginSpecification definition)
        {
            IService service;
            return _fallen8.ServiceFactory.TryAddService(out service, definition.PluginType, definition.UniqueId, ServiceHelper.CreatePluginOptions(definition.PluginOptions));
        }

        [HttpDelete("/service/{key}")]
        public bool DeleteService([FromQuery] string key)
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
            Logger.LogInfo("Trying to find the latest Fallen-8 savegame");
            string currentAssemblyDirectoryName = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            Logger.LogInfo(String.Format("Save directory: {0}", currentAssemblyDirectoryName));

            var versions = Directory.EnumerateFiles(currentAssemblyDirectoryName,
                                               _saveFile + Constants.VersionSeparator + "*")
                                               .ToList();

            if (versions.Count > 0)
            {
                Logger.LogInfo(String.Format("There are multiple Fallen-8 savegames."));

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

                Logger.LogInfo(String.Format("The latest revivision is from {0}", DateTime.FromBinary(Convert.ToInt64(latestRevision))));

                return fileToPathMapper.First(_ => _.Key.Contains(latestRevision)).Value;
            }

            var lookupPath = System.IO.Path.Combine(currentAssemblyDirectoryName, _saveFile);
            Logger.LogInfo(String.Format("Trying to find a savegame here: {0}", lookupPath));

            if (System.IO.File.Exists(lookupPath))
            {
                Logger.LogInfo(String.Format("There is a savegame here: {0}", lookupPath));
                return lookupPath;
            }

            Logger.LogInfo(String.Format("There were no Fallen-8 savegames.", versions.Count));

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
