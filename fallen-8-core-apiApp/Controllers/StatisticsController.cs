// MIT License
//
// StatisticsController.cs
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
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;

namespace NoSQL.GraphDB.App.Controllers
{
    /// <summary>
    ///   The graph-shape snapshot (feature observability): counts, label cardinalities, degree
    ///   distribution, property-key cardinalities, index inventory and free memory numbers -
    ///   each stat with an honest, bounded cost.
    /// </summary>
    [ApiController]
    [Route("api/v{version:apiVersion}/[controller]")]
    [ApiVersion("0.1")]
    public class StatisticsController : ControllerBase
    {
        private readonly IFallen8 _fallen8;
        private readonly Fallen8ObservabilityOptions _options;

        public StatisticsController(ILogger<StatisticsController> logger, IFallen8 fallen8,
            IOptions<Fallen8ObservabilityOptions> options = null,
            Embedding.Fallen8EmbeddingProvider embeddingProvider = null)
        {
            _fallen8 = fallen8;
            _options = options?.Value ?? new Fallen8ObservabilityOptions();
            _embeddingProvider = embeddingProvider;
        }

        /// <summary>The embedding provider whose identity is surfaced (feature
        /// embedding-provider); null under direct unit construction.</summary>
        private readonly Embedding.Fallen8EmbeddingProvider _embeddingProvider;

        /// <summary>
        /// Returns a graph-shape snapshot: counts, cardinalities, degrees, indices, memory
        /// </summary>
        /// <returns>The statistics snapshot</returns>
        /// <remarks>
        /// The result is an ADVISORY snapshot, not transactionally consistent: reads are
        /// lock-free over the volatile element snapshot, so a write committed during the pass
        /// may or may not appear.
        ///
        /// COST (honest): counts and memory are O(1); labels and property keys are one pass
        /// over the element snapshot; degrees are O(1) adjacency-count reads per vertex. When
        /// V+E exceeds the configured element budget (Fallen8:Observability:StatisticsElementBudget,
        /// default 1,000,000) the pass samples with a uniform stride and the response says so:
        /// sampled=true, sampleStride, per-name counts as counted IN THE SAMPLE (multiply by
        /// the stride to extrapolate), distinctTotal = distinct within the sample (sampling
        /// honestly undercounts distinct values). Degree percentiles from a strided sample are
        /// statistically sound. Memory numbers never force a GC.
        ///
        /// This endpoint exposes SCHEMA-SHAPED data (label names, property keys, index names),
        /// so it sits behind the normal API-key policy - unlike /metrics, whose inventory is
        /// aggregate numbers only - and under the sensitive rate limiter so a misconfigured
        /// scrape loop cannot turn an O(V+E) pass into a DoS.
        /// </remarks>
        /// <response code="200">The snapshot</response>
        /// <response code="429">Rate limited (the sensitive fixed-window limiter)</response>
        [HttpGet("/statistics")]
        [Produces("application/json")]
        [EnableRateLimiting(Fallen8SecurityOptions.SensitiveRateLimitPolicy)]
        [ProducesResponseType(typeof(GraphStatisticsREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public GraphStatisticsREST GetStatistics()
        {
            var stopwatch = Stopwatch.StartNew();

            var vertices = _fallen8.GetAllVertices();
            var edges = _fallen8.GetAllEdges();

            // Uniform stride so at most ElementBudget elements are touched; 1 = exact.
            var totalElements = (Int64)vertices.Count + edges.Count;
            var budget = _options.StatisticsElementBudget;
            var stride = totalElements <= budget
                ? 1
                : (Int32)((totalElements + budget - 1) / budget);

            var vertexLabels = new Dictionary<String, Int64>(StringComparer.Ordinal);
            var edgeLabels = new Dictionary<String, Int64>(StringComparer.Ordinal);
            var propertyKeys = new Dictionary<String, Int64>(StringComparer.Ordinal);
            var inDegrees = new List<Int64>(Math.Min(vertices.Count, budget));
            var outDegrees = new List<Int64>(Math.Min(vertices.Count, budget));
            var totalDegrees = new List<Int64>(Math.Min(vertices.Count, budget));

            for (var i = 0; i < vertices.Count; i += stride)
            {
                var vertex = vertices[i];
                Tally(vertexLabels, vertex.Label);
                TallyPropertyKeys(propertyKeys, vertex);

                var inDegree = (Int64)vertex.GetInDegree();
                var outDegree = (Int64)vertex.GetOutDegree();
                inDegrees.Add(inDegree);
                outDegrees.Add(outDegree);
                totalDegrees.Add(inDegree + outDegree);
            }

            for (var i = 0; i < edges.Count; i += stride)
            {
                var edge = edges[i];
                Tally(edgeLabels, edge.Label);
                TallyPropertyKeys(propertyKeys, edge);
            }

            var indices = new List<IndexStatsREST>();
            var factory = _fallen8.IndexFactory;
            if (factory != null)
            {
                // A read-locked snapshot: enumerating the live dictionary would race a
                // concurrent index create/delete on another request thread.
                foreach (var pair in factory.GetNamedIndicesSnapshot())
                {
                    indices.Add(new IndexStatsREST
                    {
                        Name = pair.Key,
                        Type = pair.Value.PluginName,
                        Keys = pair.Value.CountOfKeys(),
                        Values = pair.Value.CountOfValues()
                    });
                }
            }

            var gcInfo = GC.GetGCMemoryInfo();
            var memory = new MemoryStatsREST
            {
                ProcessWorkingSetBytes = Environment.WorkingSet,
                GcHeapBytes = GC.GetTotalMemory(false),
                GcLastHeapSizeBytes = gcInfo.HeapSizeBytes,
                GcFragmentedBytes = gcInfo.FragmentedBytes
            };

            stopwatch.Stop();

            return new GraphStatisticsREST
            {
                VertexCount = _fallen8.VertexCount,
                EdgeCount = _fallen8.EdgeCount,
                VertexLabels = ToCardinality(vertexLabels, _options.StatisticsTopN),
                EdgeLabels = ToCardinality(edgeLabels, _options.StatisticsTopN),
                InDegree = ToDegreeStats(inDegrees),
                OutDegree = ToDegreeStats(outDegrees),
                TotalDegree = ToDegreeStats(totalDegrees),
                PropertyKeys = ToCardinality(propertyKeys, _options.StatisticsTopN),
                Indices = indices,
                Memory = memory,
                ComputedInMs = stopwatch.Elapsed.TotalMilliseconds,
                Sampled = stride > 1,
                SampleStride = stride,
                // Config/state reads only - surfacing the identity must never trigger the
                // provider's lazy model load (feature embedding-provider FR-9).
                Embedding = _embeddingProvider == null ? null : new EmbeddingProviderStatsREST
                {
                    Enabled = _embeddingProvider.IsEnabled,
                    Backend = _embeddingProvider.Backend,
                    ModelName = _embeddingProvider.Identity.Name,
                    ModelVersion = _embeddingProvider.Identity.Version,
                    Dimension = _embeddingProvider.Identity.Dimension,
                    IntendedMetric = _embeddingProvider.Identity.IntendedMetric.ToString(),
                    Loaded = _embeddingProvider.IsLoaded
                }
            };
        }

        private static void Tally(Dictionary<String, Int64> counts, String label)
        {
            var key = label ?? "";
            counts[key] = counts.TryGetValue(key, out var current) ? current + 1 : 1;
        }

        private static void TallyPropertyKeys(Dictionary<String, Int64> counts, AGraphElementModel element)
        {
            if (element.GetPropertyCount() == 0)
            {
                return;
            }

            foreach (var pair in element.GetAllProperties())
            {
                counts[pair.Key] = counts.TryGetValue(pair.Key, out var current) ? current + 1 : 1;
            }
        }

        private static CardinalityStatsREST ToCardinality(Dictionary<String, Int64> counts, Int32 topN)
        {
            return new CardinalityStatsREST
            {
                DistinctTotal = counts.Count,
                Top = counts
                    .OrderByDescending(p => p.Value)
                    .ThenBy(p => p.Key, StringComparer.Ordinal)
                    .Take(topN)
                    .Select(p => new NamedCountREST { Name = p.Key, Count = p.Value })
                    .ToList()
            };
        }

        private static DegreeStatsREST ToDegreeStats(List<Int64> samples)
        {
            if (samples.Count == 0)
            {
                return new DegreeStatsREST();
            }

            samples.Sort();
            var sum = 0d;
            for (var i = 0; i < samples.Count; i++)
            {
                sum += samples[i];
            }

            return new DegreeStatsREST
            {
                Min = samples[0],
                Max = samples[samples.Count - 1],
                Mean = sum / samples.Count,
                P50 = Percentile(samples, 0.50),
                P90 = Percentile(samples, 0.90),
                P99 = Percentile(samples, 0.99)
            };
        }

        /// <summary>Nearest-rank percentile over a SORTED sample.</summary>
        private static Int64 Percentile(List<Int64> sorted, Double p)
        {
            var rank = (Int32)Math.Ceiling(p * sorted.Count);
            var index = Math.Clamp(rank - 1, 0, sorted.Count - 1);
            return sorted[index];
        }
    }
}
