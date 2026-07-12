// MIT License
//
// MemoryFootprintBenchmark.cs
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
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// A self-contained GC/allocation harness for the memory-footprint theme (findings M1-M4).
    /// It measures the STEADY-STATE retained bytes per element (the number M1's property-store
    /// compaction and M2's string interning move) and the transient allocation of a bulk insert
    /// (the number M3's transaction-release moves), on a large synthetic property graph, using
    /// <see cref="GC.GetTotalMemory(bool)"/> (retained, after a forced blocking collection) and
    /// <see cref="GC.GetTotalAllocatedBytes(bool)"/> (cumulative allocations).
    ///
    /// It is <see cref="TestCategory"/> "Benchmark"-gated so it does NOT run in the normal suite
    /// (the numbers depend on machine + GC and are captured out-of-band, exactly like
    /// <c>StorageBenchmark</c>). Run it with:
    ///   dotnet test --filter "FullyQualifiedName~MemoryFootprintBenchmark"
    /// Set F8_MEM_SCALE (double, default 1.0) to resize the workload and F8_MEM_LABEL to tag the
    /// run (use "before"/"after" to capture a comparison). Results are printed ("[F8MEM]") and
    /// appended to a file so a captured console is not required.
    /// </summary>
    [TestClass]
    public class MemoryFootprintBenchmark
    {
        private static double Scale
        {
            get
            {
                var raw = Environment.GetEnvironmentVariable("F8_MEM_SCALE");
                double parsed;
                if (!string.IsNullOrWhiteSpace(raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) && parsed > 0)
                {
                    return parsed;
                }
                return 1.0;
            }
        }

        private static string Label => Environment.GetEnvironmentVariable("F8_MEM_LABEL") ?? "unlabeled";

        private static string ResultsPath => Path.Combine(Path.GetTempPath(), "fallen8-memory-footprint.txt");

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
        public void PropertyGraph_RetainedFootprint_Benchmark()
        {
            double scale = Scale;
            int vertexCount = (int)(200_000 * scale);
            int edgeCount = (int)(200_000 * scale);

            var report = new StringBuilder();
            void Emit(string metric, double value, string unit)
            {
                var line = string.Format(CultureInfo.InvariantCulture,
                    "[F8MEM] label={0,-8} metric={1,-32} value={2,14:0.000} unit={3}",
                    Label, metric, value, unit);
                Console.WriteLine(line);
                report.AppendLine(line);
            }

            var loggerFactory = TestLoggerFactory.Create();

            // ---- Vertices WITH a handful of mixed-typed properties (the M1/M2 target). Keys and
            //      label repeat across every vertex (interning target); values vary. ----
            var withProps = new Fallen8(loggerFactory);
            long baseline = LiveBytes();
            long allocBefore = GC.GetTotalAllocatedBytes(true);
            {
                var tx = new CreateVerticesTransaction();
                for (var i = 0; i < vertexCount; i++)
                {
                    tx.AddVertex(1u, "person", new Dictionary<string, object>
                    {
                        { "name", "person-" + i },
                        { "age", i % 120 },
                        { "active", (i & 1) == 0 },
                        { "score", i * 0.5 },
                    });
                }
                withProps.EnqueueTransaction(tx).WaitUntilFinished();
            }
            long allocAfterVertices = GC.GetTotalAllocatedBytes(true);
            long afterVertices = LiveBytes();
            Assert.AreEqual(vertexCount, withProps.VertexCount);

            double vertexRetained = afterVertices - baseline;
            Emit("vertices_with_4props_retained_mb", vertexRetained / 1048576.0, "MB");
            Emit("bytes_per_vertex_with_4props", vertexRetained / vertexCount, "B");
            Emit("alloc_bulk_insert_vertices_mb", (allocAfterVertices - allocBefore) / 1048576.0, "MB");

            // ---- Edges (each with one property) on top of the same graph. ----
            long allocBeforeEdges = GC.GetTotalAllocatedBytes(true);
            {
                var rng = new Random(4242);
                var tx = new CreateEdgesTransaction();
                for (var j = 0; j < edgeCount; j++)
                {
                    tx.AddEdge(rng.Next(0, vertexCount), "friend", rng.Next(0, vertexCount), 1u, "knows",
                        new Dictionary<string, object> { { "weight", j % 7 } });
                }
                withProps.EnqueueTransaction(tx).WaitUntilFinished();
            }
            long allocAfterEdges = GC.GetTotalAllocatedBytes(true);
            long afterEdges = LiveBytes();
            Assert.AreEqual(edgeCount, withProps.EdgeCount);

            double edgeRetained = afterEdges - afterVertices;
            Emit("edges_with_1prop_retained_mb", edgeRetained / 1048576.0, "MB");
            Emit("bytes_per_edge_with_1prop", edgeRetained / edgeCount, "B");
            Emit("alloc_bulk_insert_edges_mb", (allocAfterEdges - allocBeforeEdges) / 1048576.0, "MB");

            double totalRetained = afterEdges - baseline;
            Emit("total_graph_retained_mb", totalRetained / 1048576.0, "MB");
            GC.KeepAlive(withProps);
            withProps.Dispose();

            // ---- Property-less vertices, to isolate the property-container overhead. ----
            var noProps = new Fallen8(loggerFactory);
            long baseline2 = LiveBytes();
            {
                var tx = new CreateVerticesTransaction();
                for (var i = 0; i < vertexCount; i++)
                {
                    tx.AddVertex(1u, "person");
                }
                noProps.EnqueueTransaction(tx).WaitUntilFinished();
            }
            long afterNoProps = LiveBytes();
            Assert.AreEqual(vertexCount, noProps.VertexCount);
            double noPropRetained = afterNoProps - baseline2;
            Emit("bytes_per_vertex_no_props", noPropRetained / vertexCount, "B");
            Emit("bytes_per_vertex_props_overhead", (vertexRetained - noPropRetained) / vertexCount, "B");
            GC.KeepAlive(noProps);
            noProps.Dispose();

            var header = string.Format(CultureInfo.InvariantCulture,
                "==== fallen8 memory footprint  label={0}  scale={1}  vertices={2}  edges={3}  utc={4:o} ====",
                Label, scale.ToString(CultureInfo.InvariantCulture), vertexCount, edgeCount, DateTime.UtcNow);
            try
            {
                File.AppendAllText(ResultsPath, header + Environment.NewLine + report + Environment.NewLine);
                Console.WriteLine("[F8MEM] results appended to " + ResultsPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[F8MEM] could not write results file: " + ex.Message);
            }
        }
    }
}
