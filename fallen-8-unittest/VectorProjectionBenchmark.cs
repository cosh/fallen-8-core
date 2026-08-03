// MIT License
//
// VectorProjectionBenchmark.cs
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
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Index.Vector;
using NoSQL.GraphDB.Core.Model;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Opt-in micro-timing for the vector-index write path - <see cref="VectorIndex.AddOrUpdate"/>
    ///   replace-in-place, which runs the rankability predicate (dimension, finite, cosine
    ///   zero-norm) on every call. Its purpose is the hot-path before/after guard for
    ///   consolidation-audit CA-5 (single-homing that predicate as <c>VectorRankability.Classify</c>):
    ///   capture ms/call and bytes/call, apply the change, re-capture, and revert if it regresses.
    ///   Cosine is fixed because it is the worst case (the only metric that exercises the zero-norm
    ///   branch); the vectors are all finite and non-zero-norm so every call reaches
    ///   <c>AddOrUpdateCore</c> and the two runs measure identical work.
    ///
    ///   <para>Benchmark-gated and <see cref="IgnoreAttribute"/>-marked like every benchmark here,
    ///   so it is not part of the default suite. Capture a run in Release by temporarily removing
    ///   the [Ignore]. Tag runs with <c>F8_VECTOR_LABEL</c> ("before"/"after"); results are appended
    ///   to <see cref="ResultsPath"/> so they survive the test host's stdout capture.</para>
    /// </summary>
    [TestClass]
    public class VectorProjectionBenchmark
    {
        private const int Dimension = 384;
        private const int VectorCount = 50_000;
        private const int Reps = 21;
        private const int Seed = 4242;

        private static string ResultsPath => Path.Combine(Path.GetTempPath(), "fallen8-vector-projection-benchmark.txt");
        private static string Label => Environment.GetEnvironmentVariable("F8_VECTOR_LABEL") ?? "unlabeled";

        private static void Emit(string line)
        {
            Console.WriteLine("[VPBENCH] " + line);
        }

        [TestMethod]
        [TestCategory("Benchmark")]
        [Ignore("Benchmark harness; opt-in. Not part of the default suite.")]
        public void VectorIndex_AddOrUpdate_RankabilityHotPath()
        {
            using var fallen8 = new Fallen8(TestLoggerFactory.Create());
            var index = new VectorIndex();
            index.Initialize(fallen8, new Dictionary<string, object>
            {
                { "dimension", Dimension },
                { "metric", "Cosine" }
            });

            // Fixed-seed, all-finite, non-zero-norm vectors so every AddOrUpdate is rankable and
            // reaches AddOrUpdateCore: the two runs traverse identical work.
            var random = new Random(Seed);
            var vectors = new float[VectorCount][];
            var elements = new VertexModel[VectorCount];
            for (var i = 0; i < VectorCount; i++)
            {
                var vector = new float[Dimension];
                for (var j = 0; j < Dimension; j++)
                {
                    vector[j] = (float)(random.NextDouble() * 2 - 1);
                }
                vectors[i] = vector;
                elements[i] = new VertexModel(i, 0u);
            }

            // Warm up: the first insert of each element grows the slab and JITs the path; the timed
            // reps below are all replace-in-place (no grow, no allocation).
            for (var i = 0; i < VectorCount; i++)
            {
                index.AddOrUpdate(vectors[i], elements[i]);
            }

            var samples = new double[Reps];
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (var r = 0; r < Reps; r++)
            {
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < VectorCount; i++)
                {
                    index.AddOrUpdate(vectors[i], elements[i]);
                }
                sw.Stop();
                samples[r] = sw.Elapsed.TotalMilliseconds;
            }
            var bytesPerCall = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / (double)(Reps * (long)VectorCount);

            Array.Sort(samples);
            var median = samples[Reps / 2];
            var min = samples[0];

            var header = String.Format(
                "== vector projection benchmark label={0} dimension={1} vectors={2} reps={3} ==",
                Label, Dimension, VectorCount, Reps);
            var line = String.Format(
                "AddOrUpdate      median={0,8:F3} ms/pass  min={1,8:F3} ms/pass  ns/call={2,8:F1}  bytes/call={3,8:F2}",
                median, min, min / VectorCount * 1_000_000, bytesPerCall);
            Emit(header);
            Emit(line);
            File.AppendAllText(ResultsPath, header + Environment.NewLine + line + Environment.NewLine);
            Emit("results appended to " + ResultsPath);
        }
    }
}
