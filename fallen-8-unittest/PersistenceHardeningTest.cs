// MIT License
//
// PersistenceHardeningTest.cs
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
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.SubGraph;
using NoSQL.GraphDB.Core.Expression;
using NoSQL.GraphDB.Core.Helper;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Serializer;
using NoSQL.GraphDB.Core.SubGraph;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Covers Stage A of the persistence-hardening theme: id-space sizing after a remove without
    /// Trim (C1), the self-describing magic + version envelope with clean-reject (C4), atomic
    /// writes with a completion manifest + per-file integrity (C2), corruption safety on the load
    /// path (C5), the single subgraph-recipe manifest (C6), symmetric OtherType framing (C7) and the
    /// UTC/monotonic version suffix (C8).
    /// </summary>
    [TestClass]
    public class PersistenceHardeningTest
    {
        private ILoggerFactory _loggerFactory;
        private string _tempDir;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
            _tempDir = Path.Combine(Path.GetTempPath(), "f8_persist_hardening_" + Guid.NewGuid().ToString("N"));
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

        #region helpers

        private string SavePath => Path.Combine(_tempDir, "savegame.f8s");

        private string Save(Fallen8 fallen8, int partitions = 1)
        {
            var tx = new SaveTransaction { Path = SavePath, SavePartitions = partitions };
            var info = fallen8.EnqueueTransaction(tx);
            info.WaitUntilFinished();
            Assert.AreEqual(TransactionState.Finished, info.TransactionState, "The save should finish.");
            Assert.IsFalse(String.IsNullOrEmpty(tx.ActualPath), "The save should report a path.");
            return tx.ActualPath;
        }

        private static (TransactionState State, Exception Error) Load(Fallen8 fallen8, string path)
        {
            var tx = new LoadTransaction { Path = path };
            var info = fallen8.EnqueueTransaction(tx);
            info.WaitUntilFinished();
            return (info.TransactionState, info.Error);
        }

        private static VertexModel[] AddVertices(Fallen8 fallen8, params (string Label, string Name)[] specs)
        {
            var tx = new CreateVerticesTransaction();
            foreach (var spec in specs)
            {
                tx.AddVertex(1u, spec.Label, new Dictionary<string, object> { { "name", spec.Name } });
            }
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.GetCreatedVertices().ToArray();
        }

        private static int CountWithName(Fallen8 fallen8, string name)
        {
            List<AGraphElementModel> hits;
            fallen8.GraphScan(out hits, "name", name, BinaryOperator.Equals);
            return hits.Count;
        }

        private string FindSingleSidecar(string marker)
        {
            return Directory.GetFiles(_tempDir)
                .Where(f => Path.GetFileName(f).Contains(marker))
                .Single(f => !f.Contains(Constants.TempSaveSuffix));
        }

        #endregion

        #region C1 - id-space sizing after a remove without Trim

        [TestMethod]
        public void C1_SaveAfterRemovingLowId_WithoutTrim_RoundTrips()
        {
            var source = new Fallen8(_loggerFactory);
            var vertices = AddVertices(source,
                ("person", "Alice"),   // id 0
                ("person", "Bob"),     // id 1
                ("person", "Carol"),   // id 2
                ("person", "Dave"));   // id 3

            // An edge gets the next id (4) - a HIGH-id survivor once a low id is removed.
            var edgeTx = new CreateEdgesTransaction();
            edgeTx.AddEdge(vertices[2].Id, "knows", vertices[3].Id, 1u, "knows");
            source.EnqueueTransaction(edgeTx).WaitUntilFinished();

            Assert.AreEqual(4, source.VertexCount);
            Assert.AreEqual(1, source.EdgeCount);

            // Remove a LOW id (Alice, id 0) and do NOT Trim: the survivors now have an id gap and the
            // highest surviving id (the edge, 4) is >= the live count (4). Sizing the loaded array by
            // the live count used to make Load's graphElements[id] = ... throw (finding C1).
            source.EnqueueTransaction(new RemoveGraphElementTransaction { GraphElementId = vertices[0].Id }).WaitUntilFinished();
            Assert.AreEqual(3, source.VertexCount);
            Assert.AreEqual(1, source.EdgeCount);

            var actualPath = Save(source); // NOT trimmed

            var loaded = new Fallen8(_loggerFactory);
            var (state, error) = Load(loaded, actualPath);

            Assert.AreEqual(TransactionState.Finished, state,
                "A save taken after removing a low id (without Trim) must load; instead: " + error);
            Assert.AreEqual(3, loaded.VertexCount, "All three surviving vertices round-trip.");
            Assert.AreEqual(1, loaded.EdgeCount, "The high-id surviving edge round-trips.");
            Assert.AreEqual(0, CountWithName(loaded, "Alice"), "The removed vertex is gone.");
            Assert.AreEqual(1, CountWithName(loaded, "Bob"));
            Assert.AreEqual(1, CountWithName(loaded, "Carol"));
            Assert.AreEqual(1, CountWithName(loaded, "Dave"));
        }

        #endregion

        #region C4 - magic + version (clean-reject) and header integrity

        [TestMethod]
        public void C4_FileWithoutMagic_IsCleanlyRejected_LeavesGraphUnchanged_WorkerSurvives()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            AddVertices(fallen8, ("person", "Alice"), ("person", "Bob"));
            Assert.AreEqual(2, fallen8.VertexCount);

            // A pre-existing/unversioned or foreign file: it does not carry the format magic. This is
            // exactly what an old (pre-hardening) save file looks like - and it must be REJECTED loudly.
            var foreign = Path.Combine(_tempDir, "foreign.bin");
            File.WriteAllBytes(foreign, Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray());

            var (state, error) = Load(fallen8, foreign);

            Assert.AreEqual(TransactionState.RolledBack, state, "A file without the magic must be rejected.");
            Assert.IsInstanceOfType(error, typeof(InvalidDataException), "The rejection must be a clear format error.");
            Assert.AreEqual(2, fallen8.VertexCount, "A rejected load must leave the graph unchanged (clean rollback).");

            // The single writer thread survived the faulting load: a later transaction still runs.
            AddVertices(fallen8, ("person", "Carol"));
            Assert.AreEqual(3, fallen8.VertexCount, "The transaction writer survived the rejected load.");
        }

        [TestMethod]
        public void C4_CorruptHeaderByte_FailsCrcAndIsRejected()
        {
            var source = new Fallen8(_loggerFactory);
            AddVertices(source, ("person", "Alice"), ("person", "Bob"), ("person", "Carol"));
            var actualPath = Save(source);

            // Flip a byte in the middle of the header (inside the CRC-protected region). Any accidental
            // bit-rot in the header must be caught rather than misparsed (finding C4).
            var bytes = File.ReadAllBytes(actualPath);
            bytes[bytes.Length / 2] ^= 0xFF;
            File.WriteAllBytes(actualPath, bytes);

            var loaded = new Fallen8(_loggerFactory);
            var (state, error) = Load(loaded, actualPath);

            Assert.AreEqual(TransactionState.RolledBack, state, "A corrupt header must be rejected.");
            Assert.IsInstanceOfType(error, typeof(InvalidDataException));
            Assert.AreEqual(0, loaded.VertexCount);
        }

        #endregion

        #region C2 - atomicity: completion manifest + per-file integrity

        [TestMethod]
        public void C2_TruncatedMandatorySidecar_IsRejected_AndPriorGraphIsPreserved()
        {
            var source = new Fallen8(_loggerFactory);
            AddVertices(source, ("person", "Alice"), ("person", "Bob"), ("person", "Carol"));
            var actualPath = Save(source, partitions: 1);

            // Simulate a crash mid-write / bit-rot: truncate the graph-element bunch the manifest
            // references. Load must catch the size/CRC mismatch and refuse the save (finding C2).
            var bunch = FindSingleSidecar(Constants.GraphElementsSaveString);
            using (var fs = new FileStream(bunch, FileMode.Open, FileAccess.Write))
            {
                fs.SetLength(Math.Max(0, fs.Length - 16));
            }

            var loaded = new Fallen8(_loggerFactory);
            AddVertices(loaded, ("keep", "Keeper")); // prior state that a rejected load must preserve

            var (state, error) = Load(loaded, actualPath);

            Assert.AreEqual(TransactionState.RolledBack, state, "A truncated mandatory sidecar must reject the load.");
            Assert.IsInstanceOfType(error, typeof(InvalidDataException));
            Assert.AreEqual(1, loaded.VertexCount, "The prior graph must survive the rejected load.");
            Assert.AreEqual(1, CountWithName(loaded, "Keeper"));

            // Worker still alive after the fatal-sidecar rollback.
            AddVertices(loaded, ("person", "Later"));
            Assert.AreEqual(2, loaded.VertexCount);
        }

        [TestMethod]
        public void C2_MissingMandatorySidecar_IsRejected()
        {
            var source = new Fallen8(_loggerFactory);
            AddVertices(source, ("person", "Alice"), ("person", "Bob"));
            var actualPath = Save(source, partitions: 1);

            // A crash between writing the header and (all of) its sidecars would leave the header
            // referencing a missing file. Load must not accept such a save.
            File.Delete(FindSingleSidecar(Constants.GraphElementsSaveString));

            var loaded = new Fallen8(_loggerFactory);
            var (state, error) = Load(loaded, actualPath);

            Assert.AreEqual(TransactionState.RolledBack, state, "A missing mandatory sidecar must reject the load.");
            Assert.IsInstanceOfType(error, typeof(InvalidDataException));
            Assert.AreEqual(0, loaded.VertexCount);
        }

        #endregion

        #region C5 - corruption safety (no huge allocation, no crash)

        [TestMethod]
        public void C5_TinyGarbageFile_IsRejectedWithoutCrash()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            AddVertices(fallen8, ("person", "Alice"));

            var tiny = Path.Combine(_tempDir, "tiny.bin");
            File.WriteAllBytes(tiny, new byte[] { 1, 2, 3 });

            var (state, _) = Load(fallen8, tiny);

            Assert.AreEqual(TransactionState.RolledBack, state, "A tiny garbage file must be rejected.");
            Assert.AreEqual(1, fallen8.VertexCount, "The graph is unchanged.");
        }

        [TestMethod]
        public void C5_HugeStringLengthPrefix_ThrowsBeforeAllocating()
        {
            // A single string, then tamper its 4-byte length prefix to claim ~2 GB. The reader must
            // reject it against the bytes remaining rather than doing new byte[Int32.MaxValue].
            var writer = new SerializationWriter(new MemoryStream());
            writer.Write("hello");
            var data = writer.ToArray();

            // Layout: [12-byte writer header][Int32 payload-length][UTF-8 payload]. Offset 12 is the
            // string length prefix.
            BitConverter.GetBytes(Int32.MaxValue).CopyTo(data, 12);

            var reader = new SerializationReader(new MemoryStream(data));
            Assert.ThrowsException<InvalidDataException>(() => reader.ReadString());
        }

        [TestMethod]
        public void C5_NegativeStringLengthPrefix_Throws()
        {
            var writer = new SerializationWriter(new MemoryStream());
            writer.Write("hello");
            var data = writer.ToArray();

            BitConverter.GetBytes(-1).CopyTo(data, 12);

            var reader = new SerializationReader(new MemoryStream(data));
            Assert.ThrowsException<InvalidDataException>(() => reader.ReadString());
        }

        #endregion

        #region C6 - single subgraph-recipe manifest (no stale rehydration)

        [TestMethod]
        public void C6_StaleOldSchemeRecipeFile_IsIgnoredByManifestDrivenLoad()
        {
            var source = CreateGraphWithSubgraphs("people");
            var actualPath = Save(source, partitions: 1);

            // The new scheme writes exactly ONE manifest and NO per-recipe _subgraph_N files.
            Assert.IsTrue(File.Exists(actualPath + Constants.SubGraphManifestString),
                "A single subgraph-recipe manifest must be written.");
            Assert.IsFalse(File.Exists(actualPath + Constants.SubGraphSaveString + "0"),
                "No per-recipe _subgraph_N files should be written by the new scheme.");

            // Plant a stale old-scheme recipe file, as a larger earlier save would have left behind.
            // The former directory-scan load globbed these; the manifest-driven load must ignore it.
            Assert.IsTrue(source.SubGraphFactory.TryGetSubGraph(out SubGraphResult people, "people"));
            var ghost = new SubGraphRecipe
            {
                Name = "ghost",
                SubGraphId = Guid.NewGuid(),
                AlgorithmPluginName = people.Recipe.AlgorithmPluginName,
                SourceFallen8Id = people.Recipe.SourceFallen8Id,
                SpecificationJson = people.Recipe.SpecificationJson
            };
            File.WriteAllText(actualPath + Constants.SubGraphSaveString + "0", JsonSerializer.Serialize(ghost));

            var loaded = new Fallen8(_loggerFactory) { SubGraphRecipeCompiler = new RecipeSubGraphCompiler() };
            var (state, error) = Load(loaded, actualPath);

            Assert.AreEqual(TransactionState.Finished, state, "Load should succeed; instead: " + error);
            Assert.IsTrue(loaded.SubGraphFactory.TryGetSubGraph(out _, "people"),
                "The real recipe must rehydrate from the manifest.");
            Assert.IsFalse(loaded.SubGraphFactory.TryGetSubGraph(out _, "ghost"),
                "A stale _subgraph_N file must NOT be rehydrated (no directory scan).");
        }

        [TestMethod]
        public void C6_SaveWithFewerRecipes_LoadsExactlyThatManysubgraphs()
        {
            // Save with two recipes -> both rehydrate.
            var twoRecipeGraph = CreateGraphWithSubgraphs("people", "persons");
            var twoPath = Save(twoRecipeGraph, partitions: 1);

            var loadedTwo = new Fallen8(_loggerFactory) { SubGraphRecipeCompiler = new RecipeSubGraphCompiler() };
            Load(loadedTwo, twoPath);
            Assert.IsTrue(loadedTwo.SubGraphFactory.TryGetSubGraph(out _, "people"));
            Assert.IsTrue(loadedTwo.SubGraphFactory.TryGetSubGraph(out _, "persons"));

            // A separate save with a single recipe -> exactly one rehydrates; the manifest reflects
            // exactly the recipes of THAT save, never a stale carry-over.
            var oneRecipeGraph = CreateGraphWithSubgraphs("persons");
            var onePath = Save(oneRecipeGraph, partitions: 1);

            var loadedOne = new Fallen8(_loggerFactory) { SubGraphRecipeCompiler = new RecipeSubGraphCompiler() };
            Load(loadedOne, onePath);
            Assert.IsTrue(loadedOne.SubGraphFactory.TryGetSubGraph(out _, "persons"));
            Assert.IsFalse(loadedOne.SubGraphFactory.TryGetSubGraph(out _, "people"),
                "A save with fewer recipes must not carry a stale recipe into the load.");
        }

        #endregion

        #region C7 - symmetric OtherType framing

        [TestMethod]
        public void C7_ComplexValue_RoundTrips_AndKeepsStreamFraming()
        {
            var writer = new SerializationWriter(new MemoryStream());

            // A Dictionary is neither a primitive nor an array/ArrayList, so WriteObject routes it
            // through the JSON OtherType fallback.
            var complex = new Dictionary<string, object> { { "a", 1 }, { "b", "two" } };
            writer.WriteObject(complex);

            // Values written AFTER the complex one prove the framing is symmetric: the previous reader
            // handed the raw stream to the JSON deserializer, which over-read and misframed everything
            // that followed (finding C7).
            writer.WriteObject("sentinel");
            writer.WriteObject(42);

            var data = writer.ToArray();
            var reader = new SerializationReader(new MemoryStream(data));

            var readComplex = reader.ReadObject();
            Assert.IsInstanceOfType(readComplex, typeof(JsonElement), "A complex value comes back as a JSON element.");
            var element = (JsonElement)readComplex;
            Assert.AreEqual(1, element.GetProperty("a").GetInt32());
            Assert.AreEqual("two", element.GetProperty("b").GetString());

            Assert.AreEqual("sentinel", reader.ReadObject(), "The value after the complex one must still be framed correctly.");
            Assert.AreEqual(42, reader.ReadObject());
        }

        #endregion

        #region C8 - UTC + monotonic version suffix

        [TestMethod]
        public void C8_VersionSuffix_IsUtcMonotonicUnique_AndEveryVersionLoads()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            AddVertices(fallen8, ("person", "Alice"));

            var first = new SaveTransaction { Path = SavePath, SavePartitions = 1 };
            fallen8.EnqueueTransaction(first).WaitUntilFinished();
            var second = new SaveTransaction { Path = SavePath, SavePartitions = 1 };
            fallen8.EnqueueTransaction(second).WaitUntilFinished();
            var third = new SaveTransaction { Path = SavePath, SavePartitions = 1 };
            fallen8.EnqueueTransaction(third).WaitUntilFinished();

            Assert.AreEqual(SavePath, first.ActualPath, "The first save uses the base path.");
            Assert.AreNotEqual(first.ActualPath, second.ActualPath, "A second save must not overwrite the first.");
            Assert.AreNotEqual(second.ActualPath, third.ActualPath);

            var stamp2 = long.Parse(second.ActualPath.Split(Constants.VersionSeparator).Last());
            var stamp3 = long.Parse(third.ActualPath.Split(Constants.VersionSeparator).Last());
            Assert.IsTrue(stamp3 > stamp2, "Version stamps must be monotonically increasing.");
            Assert.AreEqual(DateTimeKind.Utc, DateTime.FromBinary(stamp2).Kind, "The stamp must encode a UTC time.");

            foreach (var path in new[] { first.ActualPath, second.ActualPath, third.ActualPath })
            {
                var loaded = new Fallen8(_loggerFactory);
                var (state, error) = Load(loaded, path);
                Assert.AreEqual(TransactionState.Finished, state, "Every versioned save must load; instead: " + error);
                Assert.AreEqual(1, loaded.VertexCount);
            }
        }

        #endregion

        #region CRC-32 sanity

        [TestMethod]
        public void Crc32_MatchesKnownCheckVector()
        {
            // The integrity check is a hand-rolled CRC-32; pin it to the standard CRC-32/ISO-HDLC
            // check value for "123456789" so it can never silently drift to a non-standard variant.
            var crc32Type = typeof(Fallen8).Assembly.GetType("NoSQL.GraphDB.Core.Persistency.Crc32", throwOnError: true);
            var compute = crc32Type.GetMethod("Compute", BindingFlags.Static | BindingFlags.NonPublic,
                null, new[] { typeof(byte[]), typeof(int), typeof(int) }, null);
            Assert.IsNotNull(compute, "Crc32.Compute(byte[],int,int) should exist.");

            var data = Encoding.ASCII.GetBytes("123456789");
            var crc = (uint)compute.Invoke(null, new object[] { data, 0, data.Length });
            Assert.AreEqual(0xCBF43926u, crc);
        }

        #endregion

        #region subgraph test fixtures

        private Fallen8 CreateGraphWithSubgraphs(params string[] subgraphNames)
        {
            var fallen8 = new Fallen8(_loggerFactory) { SubGraphRecipeCompiler = new RecipeSubGraphCompiler() };

            var verticesTx = new CreateVerticesTransaction();
            verticesTx.AddVertex(1u, "person", new Dictionary<string, object> { { "name", "Alice" } });
            verticesTx.AddVertex(1u, "person", new Dictionary<string, object> { { "name", "Bob" } });
            verticesTx.AddVertex(1u, "company", new Dictionary<string, object> { { "name", "TechCorp" } });
            fallen8.EnqueueTransaction(verticesTx).WaitUntilFinished();
            var v = verticesTx.GetCreatedVertices();

            var edgesTx = new CreateEdgesTransaction();
            edgesTx.AddEdge(v[0].Id, "knows", v[1].Id, 1u, "knows");
            fallen8.EnqueueTransaction(edgesTx).WaitUntilFinished();

            var controller = new SubGraphController(TestLoggerFactory.Create().CreateLogger<SubGraphController>(), fallen8);
            foreach (var name in subgraphNames)
            {
                _ = controller.CreateSubGraph(AllPersons(name)).Result;
            }

            return fallen8;
        }

        private static SubGraphSpecification AllPersons(string name)
        {
            return new SubGraphSpecification
            {
                Name = name,
                Patterns = new List<PatternSpecification>
                {
                    new PatternSpecification { Type = "Vertex", PatternName = "p", GraphElementFilter = "return (ge) => ge.Label == \"person\";" }
                }
            };
        }

        #endregion
    }
}
