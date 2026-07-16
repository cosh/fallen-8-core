// MIT License
//
// VectorIndexBenchmark.cs
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
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core.Index.Vector;
using NoSQL.GraphDB.Core.Model;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Opt-in latency benchmark for the brute-force vector scan (feature vector-index).
    ///   Not part of the regular suite: remove [Ignore] and run with
    ///   dotnet test --filter "TestCategory=Benchmark" to reproduce the numbers in the
    ///   feature README. The measured latency at 100k x 384 dims is the evidence for the
    ///   spec's ANN revisit trigger.
    /// </summary>
    [TestClass]
    public class VectorIndexBenchmark
    {
        [TestMethod]
        [TestCategory("Benchmark")]
        [Ignore("Opt-in benchmark; remove [Ignore] and run with --filter TestCategory=Benchmark")]
        public void BruteForceScan_100kVectors_384Dims()
        {
            const int count = 100_000;
            const int dims = 384;
            const int k = 10;
            const int queries = 50;

            var fallen8 = new Core.Fallen8(TestLoggerFactory.Create());
            var index = new VectorIndex();
            index.Initialize(fallen8, new System.Collections.Generic.Dictionary<string, object>
            {
                { "dimension", dims },
                { "metric", "Cosine" }
            });

            var random = new Random(42);
            var vector = new float[dims];
            for (var i = 0; i < count; i++)
            {
                for (var j = 0; j < dims; j++)
                {
                    vector[j] = (float)(random.NextDouble() * 2 - 1);
                }
                index.AddOrUpdate((float[])vector.Clone(), new VertexModel(i, 0u));
            }

            var query = new float[dims];
            for (var j = 0; j < dims; j++)
            {
                query[j] = (float)(random.NextDouble() * 2 - 1);
            }

            // Warm-up (JIT + slab into cache), then measure.
            Assert.IsTrue(index.TryNearestNeighbors(out var warmup, query, k));
            Assert.AreEqual(k, warmup.Entries.Count);

            var stopwatch = Stopwatch.StartNew();
            for (var q = 0; q < queries; q++)
            {
                index.TryNearestNeighbors(out _, query, k);
            }
            stopwatch.Stop();

            var perQueryMs = stopwatch.Elapsed.TotalMilliseconds / queries;
            var slabBytes = (long)count * dims * sizeof(float);
            Console.WriteLine($"Brute-force kNN over {count:N0} x {dims} dims (Cosine, k={k}): " +
                              $"{perQueryMs:F2} ms/query ({queries} queries, total {stopwatch.Elapsed.TotalMilliseconds:F0} ms); " +
                              $"vector slab {slabBytes / (1024.0 * 1024.0):F0} MiB");

            // Generous bound - a failure here means the scan regressed by an order of
            // magnitude, not that the machine was busy. Measured ~21 ms/query (2026-07).
            Assert.IsTrue(perQueryMs < 500, $"brute-force scan regressed: {perQueryMs:F2} ms/query");
        }
    }
}
