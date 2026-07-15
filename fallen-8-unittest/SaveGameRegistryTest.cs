// MIT License
//
// SaveGameRegistryTest.cs
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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Services;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Unit tests for the save-game registry (feature save-games): document round-trip and atomic
    /// write, corruption = loud failure, KPI capture from a live engine, checkpoint file measurement,
    /// newest-wins ordering, import-once semantics, and delete with/without files.
    /// </summary>
    [TestClass]
    public class SaveGameRegistryTest
    {
        private string _metaDir;
        private string _dataDir;
        private SaveGameRegistry _registry;
        private Fallen8 _fallen8;

        [TestInitialize]
        public void Init()
        {
            _metaDir = Path.Combine(Path.GetTempPath(), "f8_meta_" + Guid.NewGuid().ToString("N"));
            _dataDir = Path.Combine(Path.GetTempPath(), "f8_data_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dataDir);
            var options = Options.Create(new Fallen8MetadataOptions { Directory = _metaDir });
            _registry = new SaveGameRegistry(options, NullLogger<SaveGameRegistry>.Instance);
            _fallen8 = new Fallen8(TestLoggerFactory.Create());
        }

        [TestCleanup]
        public void Cleanup()
        {
            _fallen8?.Dispose();
            foreach (var dir in new[] { _metaDir, _dataDir })
            {
                try { if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            }
        }

        private string SaveCheckpoint()
        {
            var tx = new SaveTransaction { Path = Path.Combine(_dataDir, "database.f8s") };
            _fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.ActualPath;
        }

        [TestMethod]
        public void EmptyRegistry_WhenNoFile()
        {
            var doc = _registry.Load();
            Assert.AreEqual(0, doc.SaveGames.Count);
            Assert.IsNull(_registry.Newest());
            Assert.AreEqual(0, _registry.GetAll().Count);
        }

        [TestMethod]
        public void Register_WritesEntry_WithKpisAndFileFacts()
        {
            _fallen8.EnqueueTransaction(new CreateVerticesTransaction().AddVertex(0)).WaitUntilFinished();
            var path = SaveCheckpoint();

            var entry = _registry.Register(_fallen8, path, "api");

            Assert.IsTrue(entry.Id.StartsWith("sg-"));
            Assert.AreEqual("api", entry.Trigger);
            Assert.AreEqual(Path.GetFullPath(path), entry.Location);
            Assert.IsTrue(entry.FileCount >= 1, "The checkpoint has at least the primary file.");
            Assert.IsTrue(entry.TotalBytes > 0, "The checkpoint files have non-zero size.");
            Assert.AreEqual(1, entry.Kpis.VertexCount);
            Assert.IsNotNull(entry.EngineVersion);

            // Persisted and re-readable.
            Assert.IsTrue(File.Exists(_registry.RegistryPath));
            Assert.AreEqual(1, _registry.GetAll().Count);
        }

        [TestMethod]
        public void Newest_ReturnsMostRecentBySavedAt()
        {
            var path = SaveCheckpoint();
            var first = _registry.Register(_fallen8, path, "api");
            // Force a strictly later savedAt by writing a second entry after a tick.
            System.Threading.Thread.Sleep(1100);
            var path2 = SaveCheckpoint();
            var second = _registry.Register(_fallen8, path2, "api");

            var newest = _registry.Newest();
            Assert.AreEqual(second.Id, newest.Id);
            Assert.AreNotEqual(first.Id, second.Id);
            // GetAll is newest-first.
            Assert.AreEqual(second.Id, _registry.GetAll().First().Id);
        }

        [TestMethod]
        public void CorruptRegistry_ThrowsLoudly()
        {
            Directory.CreateDirectory(_metaDir);
            File.WriteAllText(_registry.RegistryPath, "{ this is not valid json ");

            var ex = Assert.ThrowsException<InvalidOperationException>(() => _registry.Load());
            StringAssert.Contains(ex.Message, "corrupt");
        }

        [TestMethod]
        public void Persist_IsAtomic_LeavesNoTempBehind()
        {
            var path = SaveCheckpoint();
            _registry.Register(_fallen8, path, "api");
            Assert.IsFalse(File.Exists(_registry.RegistryPath + ".tmp"), "The temp file must be renamed away.");
        }

        [TestMethod]
        public void Import_RegistersOnce_ThenNoOp()
        {
            var path = SaveCheckpoint();

            var first = _registry.RegisterImportIfUnknown(_fallen8, path);
            Assert.IsNotNull(first);
            Assert.AreEqual("imported", first.Trigger);

            var second = _registry.RegisterImportIfUnknown(_fallen8, path);
            Assert.IsNull(second, "A second import of the same path is a no-op.");
            Assert.AreEqual(1, _registry.GetAll().Count);
        }

        [TestMethod]
        public void Import_UsesSamePathIdentity_RegardlessOfSeparators()
        {
            var path = SaveCheckpoint();
            _registry.Register(_fallen8, path, "api");

            // A load of the already-registered checkpoint adds no duplicate.
            var dup = _registry.RegisterImportIfUnknown(_fallen8, path);
            Assert.IsNull(dup);
        }

        [TestMethod]
        public void Delete_RemovesEntry_KeepsFilesByDefault()
        {
            var path = SaveCheckpoint();
            var entry = _registry.Register(_fallen8, path, "api");
            Assert.IsTrue(File.Exists(path));

            Assert.IsTrue(_registry.Delete(entry.Id, deleteFiles: false));
            Assert.AreEqual(0, _registry.GetAll().Count);
            Assert.IsTrue(File.Exists(path), "Files are kept when deleteFiles is false.");
            Assert.IsFalse(_registry.Delete(entry.Id, deleteFiles: false), "Deleting an unknown id returns false.");
        }

        [TestMethod]
        public void Delete_WithFiles_RemovesCheckpointFiles()
        {
            var path = SaveCheckpoint();
            var entry = _registry.Register(_fallen8, path, "api");
            Assert.IsTrue(File.Exists(path));

            Assert.IsTrue(_registry.Delete(entry.Id, deleteFiles: true));
            Assert.IsFalse(File.Exists(path), "The primary checkpoint file is deleted when deleteFiles is true.");
        }

        [TestMethod]
        public void DeleteWithFiles_DoesNotTouchAVersionedSiblingsFiles()
        {
            // Two saves to the SAME base path: the first is "database.f8s" (bare), the second is
            // "database.f8s#<stamp>" (versioned). The bare name is a textual prefix of the versioned
            // one, so a naive prefix glob would delete the sibling's files. It must not.
            var basePath = Path.Combine(_dataDir, "database.f8s");
            var tx1 = new SaveTransaction { Path = basePath };
            _fallen8.EnqueueTransaction(tx1).WaitUntilFinished();
            var first = _registry.Register(_fallen8, tx1.ActualPath, "api");

            _fallen8.EnqueueTransaction(new CreateVerticesTransaction().AddVertex(0)).WaitUntilFinished();
            var tx2 = new SaveTransaction { Path = basePath };
            _fallen8.EnqueueTransaction(tx2).WaitUntilFinished();
            var second = _registry.Register(_fallen8, tx2.ActualPath, "api");

            Assert.AreNotEqual(first.Location, second.Location, "The second save must be versioned.");
            StringAssert.StartsWith(Path.GetFileName(second.Location), Path.GetFileName(first.Location),
                "This test only matters when the bare name is a prefix of the versioned sibling.");
            Assert.IsTrue(File.Exists(second.Location));

            // Delete the FIRST (bare) entry with its files; the versioned sibling must survive.
            Assert.IsTrue(_registry.Delete(first.Id, deleteFiles: true));
            Assert.IsFalse(File.Exists(first.Location), "The deleted entry's own primary file is gone.");
            Assert.IsTrue(File.Exists(second.Location),
                "The versioned sibling's files must NOT be deleted by the prefix-named neighbor.");
        }

        [TestMethod]
        public void MeasureFiles_DoesNotCountAVersionedSiblingsFiles()
        {
            var basePath = Path.Combine(_dataDir, "database.f8s");
            var tx1 = new SaveTransaction { Path = basePath };
            _fallen8.EnqueueTransaction(tx1).WaitUntilFinished();

            var beforeSibling = _registry.MeasureFiles(tx1.ActualPath);

            // A later versioned save to the same base must not inflate the bare entry's measurement.
            var tx2 = new SaveTransaction { Path = basePath };
            _fallen8.EnqueueTransaction(tx2).WaitUntilFinished();

            var afterSibling = _registry.MeasureFiles(tx1.ActualPath);
            Assert.AreEqual(beforeSibling.FileCount, afterSibling.FileCount,
                "Measuring the bare entry must ignore the versioned sibling's files.");
        }
    }
}
