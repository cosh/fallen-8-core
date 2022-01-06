﻿using System;
using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.App.Controllers.Benchmark;
using NoSQL.GraphDB.App.Interfaces;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Serializer;

namespace NoSQL.GraphDB.App.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class BenchmarkController : ControllerBase, IRESTService
    {
        #region Data

        /// <summary>
        ///   The internal Fallen-8 instance
        /// </summary>
        private readonly Fallen8 _fallen8;

        /// <summary>
        /// The intro provider.
        /// </summary>
        private readonly ScaleFreeNetwork _introProvider;

        private readonly ILogger<BenchmarkController> _logger;

        #endregion

        public BenchmarkController(ILogger<BenchmarkController> logger, Fallen8 fallen8)
        {
            _logger = logger;

            _fallen8 = fallen8;

            _introProvider = new ScaleFreeNetwork(fallen8);
        }

        [HttpGet("/generate")]
        [Produces("application/json")]
        public string CreateGraph([FromQuery] string nodeCount, [FromQuery] string edgeCount)
        {

            var sw = Stopwatch.StartNew();

            _introProvider.CreateScaleFreeNetwork(Convert.ToInt32(nodeCount), Convert.ToInt32(edgeCount));

            sw.Stop();

            _fallen8.Trim();

            return String.Format("It took {0}ms to create a Fallen-8 graph with {1} nodes and {2} edges per node.", sw.Elapsed.TotalMilliseconds, nodeCount, edgeCount);
        }

        [HttpGet("/benchmark")]
        [Produces("application/json")]
        public string Bench([FromQuery] string iterations)
        {
            return _introProvider.Bench(Convert.ToInt32(iterations));
        }

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

        #region IDisposable Members

        public void Dispose()
        {
        }

        #endregion
    }
}
