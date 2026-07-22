// MIT License
//
// BenchmarkController.cs
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
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.App.Controllers.Benchmark;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Interfaces;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Serializer;

namespace NoSQL.GraphDB.App.Controllers
{
    [ApiController]
    [Route("api/v{version:apiVersion}/[controller]")]
    [ApiVersion("0.1")]
    public class BenchmarkController : ControllerBase, IRESTService
    {
        #region Data

        /// <summary>
        ///   The internal Fallen-8 instance
        /// </summary>
        private readonly IFallen8 _fallen8;

        /// <summary>
        /// The intro provider.
        /// </summary>
        private readonly ScaleFreeNetwork _introProvider;

        private readonly ILogger<BenchmarkController> _logger;

        #endregion

        public BenchmarkController(ILogger<BenchmarkController> logger, IFallen8 fallen8)
        {
            _logger = logger;

            _fallen8 = fallen8;

            _introProvider = new ScaleFreeNetwork(fallen8);
        }

        [HttpGet("/generate")]
        [Produces("application/json")]
        public async Task<string> CreateGraph([FromQuery] string nodeCount, [FromQuery] string edgeCount)
        {

            var sw = Stopwatch.StartNew();

            await _introProvider.CreateScaleFreeNetworkAsync(Convert.ToInt32(nodeCount), Convert.ToInt32(edgeCount));

            sw.Stop();

            //_fallen8.Trim();

            return String.Format("It took {0}ms to create a Fallen-8 graph with {1} nodes and {2} edges per node.", sw.Elapsed.TotalMilliseconds, nodeCount, edgeCount);
        }

        /// <summary>
        /// Runs the edge-traversal benchmark and returns structured statistics
        /// </summary>
        /// <param name="iterations">Number of timed iterations (default 1000)</param>
        /// <returns>Per-iteration TPS statistics (average, median, standard deviation)</returns>
        /// <response code="200">The benchmark statistics</response>
        /// <response code="400">Empty graph, non-positive or non-numeric iteration count</response>
        [HttpGet("/benchmark")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(BenchmarkResultREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public ActionResult<BenchmarkResultREST> Bench([FromQuery] string iterations)
        {
            int iterationCount;
            if (String.IsNullOrWhiteSpace(iterations))
            {
                iterationCount = 1000;
            }
            else if (!Int32.TryParse(iterations, out iterationCount))
            {
                return BadRequest(String.Format("'{0}' is not a valid iteration count.", iterations));
            }

            if (!_introProvider.TryBench(out var result, out var message, iterationCount))
            {
                return BadRequest(message);
            }

            return result;
        }

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

        #region IDisposable Members

        public void Dispose()
        {
        }

        #endregion
    }
}
