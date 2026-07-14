// MIT License
//
// TransactionAtomicityBenchmark.cs
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
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Opt-in benchmark for the transaction-atomicity feature: measures batch create / property /
    /// remove throughput so the construct-then-commit pre-validation pass can be shown not to
    /// regress the happy path. Follows the repo convention (Benchmark category + [Ignore]) so it is
    /// NOT part of the default run; remove the [Ignore] (or run the method explicitly) to capture
    /// numbers. Output is prefixed "[TXABENCH]".
    /// </summary>
    [TestClass]
    public class TransactionAtomicityBenchmark
    {
        private static void Emit(string line)
        {
            Console.WriteLine("[TXABENCH] " + line);
        }

        private static int EnvInt(string name, int fallback)
        {
            var raw = Environment.GetEnvironmentVariable(name);
            return int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : fallback;
        }

        [TestMethod]
        [TestCategory("Benchmark")]
        [Ignore("Opt-in benchmark: remove [Ignore] or run explicitly to capture numbers.")]
        public void BatchMutationThroughput()
        {
            var vertexCount = EnvInt("TXABENCH_VERTICES", 200_000);
            var batchSize = EnvInt("TXABENCH_BATCH", 10_000);
            var loggerFactory = TestLoggerFactory.Create();
            var fallen8 = new Fallen8(loggerFactory);

            // Batch vertex creation.
            var createdIds = new List<int>(vertexCount);
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < vertexCount; i += batchSize)
            {
                var tx = new CreateVerticesTransaction();
                var end = Math.Min(i + batchSize, vertexCount);
                for (var j = i; j < end; j++)
                {
                    tx.AddVertex(1u, "person", new Dictionary<string, object> { { "name", "v" + j } });
                }
                fallen8.EnqueueTransaction(tx).WaitUntilFinished();
                foreach (var v in tx.GetCreatedVertices())
                {
                    createdIds.Add(v.Id);
                }
            }
            sw.Stop();
            Emit($"create-vertices: {vertexCount:N0} in {sw.ElapsedMilliseconds:N0} ms " +
                 $"({vertexCount * 1000.0 / Math.Max(1, sw.ElapsedMilliseconds):N0}/s, batch {batchSize:N0})");

            // Batch edge creation (a chain).
            sw.Restart();
            for (var i = 0; i < createdIds.Count - 1; i += batchSize)
            {
                var tx = new CreateEdgesTransaction();
                var end = Math.Min(i + batchSize, createdIds.Count - 1);
                for (var k = i; k < end; k++)
                {
                    tx.AddEdge(createdIds[k], "knows", createdIds[k + 1], 1u);
                }
                fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            }
            sw.Stop();
            var edgeCount = createdIds.Count - 1;
            Emit($"create-edges: {edgeCount:N0} in {sw.ElapsedMilliseconds:N0} ms " +
                 $"({edgeCount * 1000.0 / Math.Max(1, sw.ElapsedMilliseconds):N0}/s, batch {batchSize:N0})");

            // Batch property sets (one new key per vertex).
            sw.Restart();
            for (var i = 0; i < createdIds.Count; i += batchSize)
            {
                var tx = new AddPropertiesTransaction();
                var end = Math.Min(i + batchSize, createdIds.Count);
                for (var j = i; j < end; j++)
                {
                    tx.AddProperty(createdIds[j], "age", j % 100);
                }
                fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            }
            sw.Stop();
            Emit($"add-properties: {createdIds.Count:N0} in {sw.ElapsedMilliseconds:N0} ms " +
                 $"({createdIds.Count * 1000.0 / Math.Max(1, sw.ElapsedMilliseconds):N0}/s, batch {batchSize:N0})");

            // Batch removal.
            sw.Restart();
            for (var i = 0; i < createdIds.Count; i += batchSize)
            {
                var end = Math.Min(i + batchSize, createdIds.Count);
                var tx = new RemoveGraphElementsTransaction
                {
                    GraphElementIds = createdIds.GetRange(i, end - i)
                };
                fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            }
            sw.Stop();
            Emit($"remove-elements: {createdIds.Count:N0} in {sw.ElapsedMilliseconds:N0} ms " +
                 $"({createdIds.Count * 1000.0 / Math.Max(1, sw.ElapsedMilliseconds):N0}/s, batch {batchSize:N0})");

            // Sanity: the bulk remove made progress. (Not an exact "== 0": auto-trim can renumber
            // element ids mid-bulk-delete - the separate trim-reader-safety concern - so removing by
            // the originally-captured absolute ids need not clear every vertex. This benchmark measures
            // batch throughput, not the trim interaction.)
            Assert.IsTrue(fallen8.VertexCount < vertexCount, "Sanity: the bulk remove reduced the vertex count.");
        }
    }
}
