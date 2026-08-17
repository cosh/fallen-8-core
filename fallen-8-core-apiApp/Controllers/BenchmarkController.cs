// MIT License
//
// BenchmarkController.cs
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Namespaces;
using NoSQL.GraphDB.App.Controllers.Benchmark;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.App.Interfaces;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Serializer;

namespace NoSQL.GraphDB.App.Controllers
{
    [ApiController]
    [Route("api/v{version:apiVersion}/[controller]")]
    [ApiVersion("0.1")]
    [NamespaceRequired]
    public class BenchmarkController : ControllerBase, IRESTService
    {
        #region Data

        /// <summary>
        ///   Timed iterations used when the request names none. Clamped to
        ///   <see cref="Fallen8SecurityOptions.BenchmarkMaxIterations"/> so a request that asks for
        ///   nothing can never breach the configured ceiling.
        /// </summary>
        private const Int32 DefaultIterations = 1000;

        /// <summary>
        ///   The internal Fallen-8 instance
        /// </summary>
        private readonly IFallen8 _fallen8;

        /// <summary>
        /// The intro provider.
        /// </summary>
        private readonly ScaleFreeNetwork _introProvider;

        private readonly ILogger<BenchmarkController> _logger;

        private readonly Fallen8SecurityOptions _securityOptions;

        #endregion

        public BenchmarkController(ILogger<BenchmarkController> logger, IFallen8 fallen8,
            IOptions<Fallen8SecurityOptions> securityOptions)
        {
            _logger = logger;

            _fallen8 = fallen8;

            _securityOptions = securityOptions.Value;

            _introProvider = new ScaleFreeNetwork(fallen8);
        }

        /// <summary>
        /// Generates a random benchmark graph on top of the current one
        /// </summary>
        /// <param name="nodeCount">Vertices to create (default 200)</param>
        /// <param name="edgeCount">Out-edges added per vertex (default 5)</param>
        /// <param name="distribution">Edge-target distribution: "uniform" (default) or
        /// "preferential" (Barabási–Albert-style attachment — heavy-tailed in-degrees, so
        /// PageRank/degree analytics at scale show real hubs)</param>
        /// <remarks>
        /// Writes into the ADDRESSED namespace, and names it in the response. This is one of the
        /// two operations with no bare-URL alias to "default" (feature graph-namespaces): it grows
        /// exactly one graph, so a URL that names no namespace is a 400 rather than a silent write
        /// into "default". The generated vertices are unlabeled and the edges carry edge property
        /// "A". A convenience for conjuring a graph to measure - GET /ns/{ns}/benchmark follows
        /// every out-edge regardless of edge-property-id, so it benchmarks any loaded graph, not
        /// only generated ones.
        /// </remarks>
        /// <response code="200">What was created, how long it took, and the resulting totals</response>
        /// <response code="400">A non-numeric or negative count, an unknown distribution, or a bare
        /// URL naming no namespace</response>
        [HttpGet("/generate")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(GraphGenerationResultREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<GraphGenerationResultREST>> CreateGraph([FromQuery] string nodeCount, [FromQuery] string edgeCount, [FromQuery] string distribution = null)
        {
            var nodes = 200;
            if (!String.IsNullOrWhiteSpace(nodeCount) && (!Int32.TryParse(nodeCount, out nodes) || nodes < 0))
            {
                return ProblemResults.BadRequest(String.Format("'{0}' is not a valid node count.", nodeCount));
            }

            var edgesPerVertex = 5;
            if (!String.IsNullOrWhiteSpace(edgeCount) && (!Int32.TryParse(edgeCount, out edgesPerVertex) || edgesPerVertex < 0))
            {
                return ProblemResults.BadRequest(String.Format("'{0}' is not a valid edge count.", edgeCount));
            }

            bool preferential;
            if (String.IsNullOrWhiteSpace(distribution)
                || String.Equals(distribution, ScaleFreeNetwork.UniformDistribution, StringComparison.OrdinalIgnoreCase))
            {
                preferential = false;
            }
            else if (String.Equals(distribution, ScaleFreeNetwork.PreferentialDistribution, StringComparison.OrdinalIgnoreCase))
            {
                preferential = true;
            }
            else
            {
                return ProblemResults.BadRequest(String.Format("'{0}' is not a valid distribution (expected {1} or {2}).",
                    distribution, ScaleFreeNetwork.UniformDistribution, ScaleFreeNetwork.PreferentialDistribution));
            }

            var result = await _introProvider.CreateScaleFreeNetworkAsync(nodes, edgesPerVertex, preferential);

            // The graph builder knows only a graph; the namespace is an addressing concept, so the
            // controller stamps it. [NamespaceRequired] guarantees the route named one, which is why
            // this reads the route value instead of falling back to the default namespace.
            result.Namespace = HttpContext?.Request.RouteValues[
                NamespaceRouteConvention.RouteParameterName] as String;

            return result;
        }

        /// <summary>
        /// Runs the edge-traversal benchmark and returns structured statistics
        /// </summary>
        /// <param name="iterations">Number of timed iterations (default 1000, at most
        /// Fallen8:Security:BenchmarkMaxIterations; the default is clamped to that ceiling)</param>
        /// <returns>Per-iteration TPS statistics (average, median, standard deviation)</returns>
        /// <remarks>
        /// Traverses the ADDRESSED namespace, and like GET /ns/{ns}/generate it has no bare-URL
        /// alias to "default" (feature graph-namespaces): measuring a graph the caller did not name
        /// would report the wrong graph's throughput as if it were theirs. It follows every outgoing
        /// edge of every vertex regardless of edge-property-id, so it works on any loaded graph and
        /// reports edges traversed per second (not query latency).
        /// </remarks>
        /// <response code="200">The benchmark statistics</response>
        /// <response code="400">Empty graph, non-positive or non-numeric iteration count, a count
        /// above Fallen8:Security:BenchmarkMaxIterations, or a bare URL naming no namespace</response>
        [HttpGet("/benchmark")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(BenchmarkResultREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public ActionResult<BenchmarkResultREST> Bench([FromQuery] string iterations)
        {
            int iterationCount;
            if (String.IsNullOrWhiteSpace(iterations))
            {
                // Clamped like the analytics default budget, so a request that names NO count can
                // never fail the ceiling check for a value it never sent.
                iterationCount = Math.Min(DefaultIterations, _securityOptions.BenchmarkMaxIterations);
            }
            else if (!Int32.TryParse(iterations, out iterationCount))
            {
                return ProblemResults.BadRequest(String.Format("'{0}' is not a valid iteration count.", iterations));
            }

            if (iterationCount > _securityOptions.BenchmarkMaxIterations)
            {
                return ProblemResults.BadRequest(String.Format(
                    "iterations must be at most {0} (Fallen8:Security:BenchmarkMaxIterations).",
                    _securityOptions.BenchmarkMaxIterations));
            }

            if (!_introProvider.TryBench(out var result, out var message, iterationCount))
            {
                return ProblemResults.BadRequest(message);
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
