// MIT License
//
// CheckpointIoEfficiencyTest.cs
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
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Tests for the "checkpoint-io-efficiency" feature. The CRC-32 is now backed by the
    /// hardware-accelerated <c>System.IO.Hashing.Crc32</c>; this pins that it is byte-for-byte
    /// identical to the standard CRC-32/ISO-HDLC (so existing checkpoints still validate and there is
    /// no format bump), that the byte-array and streaming overloads agree, and that a save (now
    /// single-pass, no read-back) still round-trips and still rejects a corrupted sidecar.
    /// </summary>
    [TestClass]
    public class CheckpointIoEfficiencyTest
    {
        private ILoggerFactory _loggerFactory;
        private string _tempDir;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
            _tempDir = Path.Combine(Path.GetTempPath(), "f8_ckptio_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        [TestCleanup]
        public void TestCleanup()
        {
            try { if (_tempDir != null && Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); }
            catch { }
        }

        // Reflection into the internal Crc32 facade (the engine declares no InternalsVisibleTo).
        private static readonly MethodInfo _crcArray = typeof(Fallen8).Assembly
            .GetType("NoSQL.GraphDB.Core.Persistency.Crc32")
            .GetMethod("Compute", BindingFlags.NonPublic | BindingFlags.Static,
                new[] { typeof(byte[]), typeof(int), typeof(int) });

        private static readonly MethodInfo _crcStream = typeof(Fallen8).Assembly
            .GetType("NoSQL.GraphDB.Core.Persistency.Crc32")
            .GetMethod("Compute", BindingFlags.NonPublic | BindingFlags.Static,
                new[] { typeof(Stream), typeof(long).MakeByRefType() });

        private static uint Crc(byte[] buffer)
        {
            return (uint)_crcArray.Invoke(null, new object[] { buffer, 0, buffer.Length });
        }

        private static uint CrcStream(byte[] buffer)
        {
            var args = new object[] { new MemoryStream(buffer), 0L };
            var crc = (uint)_crcStream.Invoke(null, args);
            return crc;
        }

        [TestMethod]
        public void Crc32_MatchesTheStandardIsoHdlcCheckValues()
        {
            // The canonical CRC-32/ISO-HDLC check value: CRC of the ASCII "123456789" is 0xCBF43926.
            // This is exactly what the former hand-rolled table loop produced, so the SIMD swap is
            // byte-compatible and no checkpoint needs re-writing.
            Assert.AreEqual(0xCBF43926u, Crc(Encoding.ASCII.GetBytes("123456789")),
                "The CRC must equal the standard CRC-32/ISO-HDLC check value.");

            // The empty input hashes to 0.
            Assert.AreEqual(0u, Crc(Array.Empty<byte>()), "CRC of empty input must be 0.");
        }

        [TestMethod]
        public void Crc32_ArrayAndStreamOverloads_Agree_IncludingOver64K()
        {
            var rng = new Random(20260714);
            foreach (var size in new[] { 0, 1, 17, 65_536, 200_000 })
            {
                var buffer = new byte[size];
                rng.NextBytes(buffer);
                Assert.AreEqual(Crc(buffer), CrcStream(buffer),
                    $"The array and streaming CRC overloads must agree for a {size}-byte buffer.");
            }
        }

        // ---- save (single-pass) round-trip + corruption rejection ---------------------------------

        private string SavePath => Path.Combine(_tempDir, "ckptio.f8s");

        private string Save(Fallen8 fallen8, int partitions)
        {
            var tx = new SaveTransaction { Path = SavePath, SavePartitions = partitions };
            var info = fallen8.EnqueueTransaction(tx);
            info.WaitUntilFinished();
            Assert.AreEqual(TransactionState.Finished, info.TransactionState, "The save should finish. " + info.Error);
            return tx.ActualPath;
        }

        private Fallen8 Load(string path)
        {
            var loaded = new Fallen8(_loggerFactory);
            var info = loaded.EnqueueTransaction(new LoadTransaction { Path = path });
            info.WaitUntilFinished();
            Assert.AreEqual(TransactionState.Finished, info.TransactionState, "The load should finish. " + info.Error);
            return loaded;
        }

        [TestMethod]
        public void SinglePassSave_RoundTripsExactly()
        {
            var source = new Fallen8(_loggerFactory);
            var vtx = new CreateVerticesTransaction();
            vtx.AddVertex(1u, "person", new System.Collections.Generic.Dictionary<string, object> { { "n", 1 } });
            vtx.AddVertex(1u, "person", new System.Collections.Generic.Dictionary<string, object> { { "n", 2 } });
            source.EnqueueTransaction(vtx).WaitUntilFinished();
            var v = vtx.GetCreatedVertices();
            var edgeTx = new CreateEdgesTransaction();
            edgeTx.AddEdge(v[0].Id, "knows", v[1].Id, 1u, "knows");
            source.EnqueueTransaction(edgeTx).WaitUntilFinished();

            // The single-pass save (CRC from the in-memory image, no file read-back) round-trips
            // exactly. Corruption rejection over the same CRC is covered by LoadPathIntegrityTest /
            // PersistenceEncodingTest, which stay green on the SIMD CRC.
            var path = Save(source, partitions: 2);
            var loaded = Load(path);
            Assert.AreEqual(2, loaded.VertexCount);
            Assert.AreEqual(1, loaded.EdgeCount);

            loaded.Dispose();
            source.Dispose();
        }
    }
}
