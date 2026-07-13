// MIT License
//
// AdjacencyMemoryBenchmark.cs
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
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// A self-contained GC/allocation harness for the adjacency-flattening theme. It measures the
    /// STEADY-STATE retained bytes attributable to a vertex's <b>adjacency</b> - the number the
    /// swap from two per-vertex <c>ImmutableDictionary&lt;string, ImmutableList&lt;EdgeModel&gt;&gt;</c>
    /// to copy-on-write <c>Dictionary&lt;string, EdgeModel[]&gt;</c> moves - on a large synthetic
    /// graph, using <see cref="GC.GetTotalMemory(bool)"/> (retained, after a forced blocking
    /// collection).
    ///
    /// The per-edge figure is the retained delta of adding E edges on top of a fixed vertex set
    /// (it includes the shared <c>EdgeModel</c> body, but the ONLY part that changes between the
    /// before/after representations is the two adjacency entries + their container overhead, so the
    /// before/after delta is the adjacency win). The per-vertex figure isolates the empty
    /// adjacency-container overhead by contrasting single-group vertices with edge-free vertices.
    ///
    /// It is <see cref="TestCategory"/> "Benchmark"-gated and <see cref="IgnoreAttribute"/>-marked
    /// so it does NOT run in the normal suite (the numbers depend on machine + GC and are captured
    /// out-of-band, exactly like <c>MemoryFootprintBenchmark</c>/<c>StorageBenchmark</c>). To
    /// capture a run, temporarily remove the [Ignore] (or run the method directly):
    ///   dotnet test --filter "FullyQualifiedName~AdjacencyMemoryBenchmark"
    /// Set F8_ADJ_SCALE (double, default 1.0) to resize the workload and F8_ADJ_LABEL to tag the
    /// run (use "before"/"after" to capture a comparison). Results are printed ("[F8ADJ]") and
    /// appended to a file so a captured console is not required.
    /// </summary>
    [TestClass]
    public class AdjacencyMemoryBenchmark
    {
        private static double Scale
        {
            get
            {
                var raw = Environment.GetEnvironmentVariable("F8_ADJ_SCALE");
                double parsed;
                if (!string.IsNullOrWhiteSpace(raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) && parsed > 0)
                {
                    return parsed;
                }
                return 1.0;
            }
        }

        private static string Label => Environment.GetEnvironmentVariable("F8_ADJ_LABEL") ?? "unlabeled";

        private static string ResultsPath => Path.Combine(Path.GetTempPath(), "fallen8-adjacency-memory.txt");

        private static long LiveBytes()
        {
            // Force a full, blocking, compacting collection so the reading reflects retained
            // (reachable) memory rather than not-yet-collected garbage.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            return GC.GetTotalMemory(true);
        }

        [TestMethod]
        [TestCategory("Benchmark")]
        [Ignore("Benchmark harness; run explicitly, e.g. remove [Ignore] or run the method directly. Not part of the default suite.")]
        public void Adjacency_RetainedFootprint_Benchmark()
        {
            double scale = Scale;
            int vertexCount = (int)(200_000 * scale);
            int edgeCount = (int)(400_000 * scale);

            var report = new StringBuilder();
            void Emit(string metric, double value, string unit)
            {
                var line = string.Format(CultureInfo.InvariantCulture,
                    "[F8ADJ] label={0,-8} metric={1,-34} value={2,14:0.000} unit={3}",
                    Label, metric, value, unit);
                Console.WriteLine(line);
                report.AppendLine(line);
            }

            var loggerFactory = TestLoggerFactory.Create();

            // ---- Property-less vertices, no edges: the empty-adjacency baseline. ----
            var graph = new Fallen8(loggerFactory);
            long baseline = LiveBytes();
            {
                var tx = new CreateVerticesTransaction();
                for (var i = 0; i < vertexCount; i++)
                {
                    tx.AddVertex(1u, "person");
                }
                graph.EnqueueTransaction(tx).WaitUntilFinished();
            }
            long afterVertices = LiveBytes();
            Assert.AreEqual(vertexCount, graph.VertexCount);

            double vertexRetained = afterVertices - baseline;
            Emit("bytes_per_vertex_no_edges", vertexRetained / vertexCount, "B");

            // ---- Add edges on top of the fixed vertex set. The retained delta / edge is the
            //      per-edge cost whose adjacency portion the flattening removes. ----
            {
                var rng = new Random(4242);
                var tx = new CreateEdgesTransaction();
                for (var j = 0; j < edgeCount; j++)
                {
                    tx.AddEdge(rng.Next(0, vertexCount), "friend", rng.Next(0, vertexCount), 1u, "knows");
                }
                graph.EnqueueTransaction(tx).WaitUntilFinished();
            }
            long afterEdges = LiveBytes();
            Assert.AreEqual(edgeCount, graph.EdgeCount);

            double edgeRetained = afterEdges - afterVertices;
            Emit("bytes_per_edge_incl_adjacency", edgeRetained / edgeCount, "B");
            Emit("total_graph_retained_mb", (afterEdges - baseline) / 1048576.0, "MB");
            GC.KeepAlive(graph);
            graph.Dispose();

            var header = string.Format(CultureInfo.InvariantCulture,
                "==== fallen8 adjacency memory  label={0}  scale={1}  vertices={2}  edges={3}  utc={4:o} ====",
                Label, scale.ToString(CultureInfo.InvariantCulture), vertexCount, edgeCount, DateTime.UtcNow);
            try
            {
                File.AppendAllText(ResultsPath, header + Environment.NewLine + report + Environment.NewLine);
                Console.WriteLine("[F8ADJ] results appended to " + ResultsPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[F8ADJ] could not write results file: " + ex.Message);
            }
        }
    }
}
