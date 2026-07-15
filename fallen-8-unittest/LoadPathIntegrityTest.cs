// MIT License
//
// LoadPathIntegrityTest.cs
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
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Serializer;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Pins the load-path-integrity fixes (features/done/load-path-integrity/):
    /// L1 - cross-bunch edge rehydration is exactly correct under the parallel load (no torn/lost/
    /// duplicated fix-up); L2 - an oversized checkpoint segment fails the save loudly instead of
    /// silently writing a wrapped length that is unloadable; L3 - an absurd manifest count is
    /// rejected before the large preallocation.
    /// </summary>
    [TestClass]
    public class LoadPathIntegrityTest
    {
        private ILoggerFactory _loggerFactory;
        private string _tempDir;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
            _tempDir = Path.Combine(Path.GetTempPath(), "f8_loadpath_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        [TestCleanup]
        public void TestCleanup()
        {
            try
            {
                if (_tempDir != null && Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }

        #region L1 - cross-bunch edge rehydration race

        [TestMethod]
        public void Load_CrossBunchEdges_RehydratesAdjacencyExactly()
        {
            RunCrossBunchLoad(vertexCount: 20_000, iterations: 8);
        }

        [TestMethod]
        [TestCategory("Benchmark")]
        [Ignore("Opt-in load-race stress; not part of the default suite.")]
        public void Load_CrossBunchEdges_Heavy()
        {
            RunCrossBunchLoad(vertexCount: 200_000, iterations: 40);
        }

        /// <summary>
        /// Builds a graph whose edges systematically span save partitions (so most edges have their
        /// two endpoints in different bunches, driving the concurrent cross-bunch fix-up), saves and
        /// loads it repeatedly, and asserts the rehydrated adjacency is exactly what was saved:
        /// every out/in edge present exactly once, correct endpoints, counts intact.
        /// </summary>
        private void RunCrossBunchLoad(int vertexCount, int iterations)
        {
            const string edgePropertyId = "e";
            var stride = vertexCount / 2; // endpoints ~half the id space apart => different bunches

            var source = new Fallen8(_loggerFactory);
            var vtx = new CreateVerticesTransaction();
            for (var i = 0; i < vertexCount; i++)
            {
                vtx.AddVertex(1u, "n");
            }
            source.EnqueueTransaction(vtx).WaitUntilFinished();
            var vertexIds = vtx.GetCreatedVertices().Select(v => v.Id).ToArray();

            var edg = new CreateEdgesTransaction();
            // Expected adjacency: (sourceId, targetId) pairs, one edge each.
            var expected = new List<(int Source, int Target)>(vertexCount);
            for (var i = 0; i < vertexCount; i++)
            {
                var s = vertexIds[i];
                var t = vertexIds[(i + stride) % vertexCount];
                edg.AddEdge(s, edgePropertyId, t, 1u);
                expected.Add((s, t));
            }
            source.EnqueueTransaction(edg).WaitUntilFinished();

            Assert.AreEqual(vertexCount, source.VertexCount);
            Assert.AreEqual(vertexCount, source.EdgeCount);

            for (var iter = 0; iter < iterations; iter++)
            {
                var savePath = Path.Combine(_tempDir, "cb_" + iter + ".f8s");
                var saveTx = new SaveTransaction { Path = savePath };
                source.EnqueueTransaction(saveTx).WaitUntilFinished();

                var loaded = new Fallen8(_loggerFactory);
                var loadInfo = loaded.EnqueueTransaction(new LoadTransaction { Path = saveTx.ActualPath });
                loadInfo.WaitUntilFinished();
                Assert.AreEqual(TransactionState.Finished, loadInfo.TransactionState,
                    "Iteration " + iter + ": the load should finish.");

                Assert.AreEqual(vertexCount, loaded.VertexCount, "Iteration " + iter + ": vertex count.");
                Assert.AreEqual(vertexCount, loaded.EdgeCount, "Iteration " + iter + ": edge count.");

                var totalOut = 0;
                var totalIn = 0;
                foreach (var v in loaded.GetAllVertices())
                {
                    if (v.TryGetOutEdge(out var outEdges, edgePropertyId))
                    {
                        totalOut += outEdges.Count;
                        // No duplicate edge id within a bucket.
                        Assert.AreEqual(outEdges.Count, outEdges.Select(e => e.Id).Distinct().Count(),
                            "Iteration " + iter + ": duplicate out-edge in a bucket (torn/double-added fix-up).");
                    }
                    if (v.TryGetInEdge(out var inEdges, edgePropertyId))
                    {
                        totalIn += inEdges.Count;
                        Assert.AreEqual(inEdges.Count, inEdges.Select(e => e.Id).Distinct().Count(),
                            "Iteration " + iter + ": duplicate in-edge in a bucket (torn/double-added fix-up).");
                    }
                }

                // Every edge is present exactly once on each side - no losses, no duplicates.
                Assert.AreEqual(vertexCount, totalOut, "Iteration " + iter + ": total out-edges (lost/duplicated fix-up).");
                Assert.AreEqual(vertexCount, totalIn, "Iteration " + iter + ": total in-edges (lost/duplicated fix-up).");

                // Spot-check exact endpoints on a sample so a "right count, wrong wiring" load is caught.
                var step = Math.Max(1, vertexCount / 500);
                for (var i = 0; i < expected.Count; i += step)
                {
                    var (s, t) = expected[i];
                    Assert.IsTrue(loaded.TryGetVertex(out var sv, s));
                    Assert.IsTrue(sv.TryGetOutEdge(out var outs, edgePropertyId), "source " + s + " missing out bucket.");
                    Assert.AreEqual(1, outs.Count(e => e.TargetVertex.Id == t),
                        "Iteration " + iter + ": edge " + s + "->" + t + " must appear exactly once in the source's OutEdges.");

                    Assert.IsTrue(loaded.TryGetVertex(out var tv, t));
                    Assert.IsTrue(tv.TryGetInEdge(out var ins, edgePropertyId), "target " + t + " missing in bucket.");
                    Assert.AreEqual(1, ins.Count(e => e.SourceVertex.Id == s),
                        "Iteration " + iter + ": edge " + s + "->" + t + " must appear exactly once in the target's InEdges.");
                }

                File.Delete(saveTx.ActualPath);
                foreach (var sidecar in Directory.GetFiles(_tempDir, "cb_" + iter + ".f8s*"))
                {
                    File.Delete(sidecar);
                }
            }
        }

        #endregion

        #region Save into a non-existent directory

        [TestMethod]
        public void Save_ToPathInNonexistentDirectory_CreatesItAndRoundTrips()
        {
            // A save to a path whose directory does not exist (e.g. a fresh "C:/Fallen8/database.f8s")
            // must create the directory and succeed, not throw DirectoryNotFoundException from the
            // first bunch-sidecar write. Uses multiple partitions to exercise the sidecar path.
            var missingDir = Path.Combine(_tempDir, "does", "not", "exist", "yet");
            var savePath = Path.Combine(missingDir, "database.f8s");
            Assert.IsFalse(Directory.Exists(missingDir), "Precondition: the target directory does not exist.");

            string actualPath;
            using (var source = new Fallen8(_loggerFactory))
            {
                var create = new CreateVerticesTransaction();
                create.AddVertex(1u, "person", new Dictionary<string, object> { { "name", "Alice" } });
                source.EnqueueTransaction(create).WaitUntilFinished();
                var createdId = create.GetCreatedVertices().Single().Id;

                var saveTx = new SaveTransaction { Path = savePath, SavePartitions = 8 };
                var saveInfo = source.EnqueueTransaction(saveTx);
                saveInfo.WaitUntilFinished();

                Assert.AreEqual(TransactionState.Finished, saveInfo.TransactionState,
                    "A save into a non-existent directory must succeed (the directory is created), not roll back.");
                Assert.IsTrue(Directory.Exists(missingDir), "The save must have created the target directory.");
                actualPath = saveTx.ActualPath;
                _ = createdId;
            }

            using (var loaded = new Fallen8(_loggerFactory))
            {
                var loadInfo = loaded.EnqueueTransaction(new LoadTransaction { Path = actualPath });
                loadInfo.WaitUntilFinished();
                Assert.AreEqual(TransactionState.Finished, loadInfo.TransactionState, "The saved graph must load back.");
                Assert.AreEqual(1, loaded.VertexCount);
                Assert.IsTrue(loaded.TryGetVertex(out var v, 0) && v.TryGetProperty(out string name, "name") && name == "Alice");
            }
        }

        #endregion

        #region L2 - oversized segment must fail the save, not write a wrapped length

        [TestMethod]
        public void UpdateHeader_SegmentOverTwoGigabytes_FailsLoudlyInsteadOfWrappingTheLength()
        {
            // A genuine >2 GB write is impractical in a unit test, so drive the public UpdateHeader
            // over a seekable stream that reports a Position past Int32.MaxValue. Before the fix the
            // unchecked (int) cast wraps to a negative length (silently unloadable); after the fix it
            // throws at save time so no broken checkpoint is committed.
            using (var stream = new PositionOverridingStream())
            {
                var writer = new SerializationWriter(stream, allowUpdateHeader: false);
                stream.ReportedPosition = (long)int.MaxValue + 4096L;

                Assert.ThrowsException<InvalidDataException>(() => writer.UpdateHeader(),
                    "A checkpoint segment exceeding the 2 GB per-file limit must throw at save time, not write a wrapped length.");
            }
        }

        [TestMethod]
        public void UpdateHeader_NormalSizedSegment_StillReturnsLength()
        {
            // Guards the overflow check against being over-eager: an ordinary segment still works.
            using (var mem = new MemoryStream())
            {
                var writer = new SerializationWriter(mem, allowUpdateHeader: true);
                writer.Write(12345);
                writer.Write("hello");
                var length = writer.UpdateHeader();
                Assert.IsTrue(length > 0, "A normal segment reports its length as before.");
            }
        }

        #endregion

        #region L3 - manifest count must be bounded before allocation

        [TestMethod]
        public void ReadManifestList_AbsurdCount_RejectedBeforeAllocating()
        {
            // Craft a header slice whose (first) manifest declares a huge entry count but carries no
            // entries. The read must reject it with InvalidDataException at the count check, before
            // the new List<>(count) preallocation - not attempt a multi-GB allocation.
            byte[] headerRegion;
            using (var mem = new MemoryStream())
            {
                var writer = new SerializationWriter(mem, allowUpdateHeader: true);
                writer.Write(Guid.NewGuid());   // currentGuId
                writer.Write(0);                // idSpaceSize
                writer.Write(int.MaxValue);     // bogus bunch-manifest count, then NO entries
                writer.UpdateHeader();
                writer.Flush();
                headerRegion = mem.ToArray();
            }

            var reader = new SerializationReader(headerRegion);
            reader.ReadGuid();
            reader.ReadInt32();

            // PersistencyFactory is internal, so resolve the type through the engine assembly.
            var persistencyFactoryType = typeof(Fallen8).Assembly
                .GetType("NoSQL.GraphDB.Core.Persistency.PersistencyFactory");
            Assert.IsNotNull(persistencyFactoryType, "PersistencyFactory type should resolve for the guard test.");
            var method = persistencyFactoryType.GetMethod("ReadManifestList",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "ReadManifestList should be present for the guard test.");

            var ex = Assert.ThrowsException<TargetInvocationException>(() => method.Invoke(null, new object[] { reader }));
            Assert.IsInstanceOfType(ex.InnerException, typeof(InvalidDataException),
                "An absurd manifest count must be rejected as InvalidDataException before allocating.");
        }

        #endregion

        #region helpers

        /// <summary>
        /// A minimal seekable, writable stream whose reported Position can be overridden to a value
        /// past Int32.MaxValue, so a test can drive SerializationWriter.UpdateHeader's length
        /// computation across the 2 GB boundary without allocating anything.
        /// </summary>
        private sealed class PositionOverridingStream : Stream
        {
            private long _position;

            /// <summary>When set, overrides the position reported to callers.</summary>
            public long? ReportedPosition { get; set; }

            public override bool CanRead => false;
            public override bool CanSeek => true;
            public override bool CanWrite => true;
            public override long Length => ReportedPosition ?? _position;

            public override long Position
            {
                get => ReportedPosition ?? _position;
                set => _position = value;
            }

            public override void Write(byte[] buffer, int offset, int count) => _position += count;
            public override long Seek(long offset, SeekOrigin origin)
            {
                _position = origin == SeekOrigin.Begin ? offset
                    : origin == SeekOrigin.Current ? _position + offset
                    : (ReportedPosition ?? _position) + offset;
                return _position;
            }

            public override void Flush() { }
            public override void SetLength(long value) { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        #endregion
    }
}
