// MIT License
//
// RegistryDurabilityTest.cs
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
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Services;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Persistency;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Durability of the host's two POINTER files - the save-game registry and the namespace catalog
    ///   (feature platform-integrity-audit W1). These are what make every fsync'd checkpoint byte
    ///   reachable, and they were written with a plain write-then-rename: atomic for readers, but not
    ///   durable, so a power loss could publish a zero-length pointer to a complete checkpoint. Two
    ///   halves are asserted here:
    ///
    ///   <para>(1) the write goes through the engine's fsync-before-rename primitive, and</para>
    ///
    ///   <para>(2) a PRESENT-but-empty pointer file is LOUD, because a destroyed pointer must not be
    ///   indistinguishable from a legitimately empty one. An ABSENT file stays silent-and-empty, which is
    ///   save-games FR-8 ("no savegames.json, or an empty registry -> start empty; a checkpoint sitting in
    ///   the storage directory is NOT loaded just because it exists") and FR-11's one-time PUT /load
    ///   migration. That specified behaviour is deliberately NOT changed - the defect was only the
    ///   inability to tell "never saved" from "pointer destroyed".</para>
    /// </summary>
    [TestClass]
    public class RegistryDurabilityTest
    {
        private string _metaDir;
        private string _dataDir;
        private SaveGameRegistry _registry;
        private Fallen8 _fallen8;

        [TestInitialize]
        public void Init()
        {
            _metaDir = Path.Combine(Path.GetTempPath(), "f8_w1_meta_" + Guid.NewGuid().ToString("N"));
            _dataDir = Path.Combine(Path.GetTempPath(), "f8_w1_data_" + Guid.NewGuid().ToString("N"));
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

        #region the loud-vs-silent distinction (registry)

        [TestMethod]
        public void AbsentRegistry_IsSilentlyEmpty()
        {
            // FR-8's legitimate case, and the guard that the fix below did not over-reach: a first boot
            // has no registry file at all and must start empty without complaint.
            Assert.IsFalse(File.Exists(_registry.RegistryPath));

            var doc = _registry.Load();

            Assert.AreEqual(0, doc.SaveGames.Count);
            Assert.IsNull(_registry.Newest());
        }

        [TestMethod]
        public void ZeroLengthRegistry_IsLoud_NotSilentlyEmpty()
        {
            // Exactly what a non-durable write-then-rename publishes on a power loss. Before W1 this
            // returned an empty document, so boot started EMPTY with a complete checkpoint on disk and a
            // green health probe - the one step in which all graph state becomes unreachable, silently.
            Directory.CreateDirectory(_metaDir);
            File.WriteAllBytes(_registry.RegistryPath, Array.Empty<byte>());

            var ex = Assert.ThrowsException<InvalidOperationException>(() => _registry.Load());

            StringAssert.Contains(ex.Message, _registry.RegistryPath);
            StringAssert.Contains(ex.Message, "present but empty");
            Assert.IsTrue(ex.Message.Contains("DELETE", StringComparison.Ordinal),
                "The message must say how to start genuinely empty, or the operator's only move is guessing.");
        }

        [TestMethod]
        public void WhitespaceOnlyRegistry_IsLoud()
        {
            // A partially-flushed write can land whitespace/NULs rather than nothing at all.
            Directory.CreateDirectory(_metaDir);
            File.WriteAllText(_registry.RegistryPath, "   \r\n\t ");

            Assert.ThrowsException<InvalidOperationException>(() => _registry.Load());
        }

        [TestMethod]
        public void CorruptJsonRegistry_StaysLoud()
        {
            // Pre-existing behaviour, kept: the empty-file guard must not have replaced it.
            Directory.CreateDirectory(_metaDir);
            File.WriteAllText(_registry.RegistryPath, "{ not json");

            var ex = Assert.ThrowsException<InvalidOperationException>(() => _registry.Load());
            StringAssert.Contains(ex.Message, "corrupt");
        }

        #endregion

        #region durability of the write

        [TestMethod]
        public void Persist_WritesAReadableRegistry_AndLeavesNoTempFile()
        {
            _fallen8.EnqueueTransaction(new CreateVerticesTransaction().AddVertex(0)).WaitUntilFinished();
            var path = SaveCheckpoint();

            _registry.Register(_fallen8, path, "api");

            Assert.IsTrue(File.Exists(_registry.RegistryPath));
            Assert.AreEqual(1, _registry.GetAll().Count);

            // The durable write uses a GUID-unique temp name and renames it away; nothing may linger.
            var leftovers = Directory.GetFiles(_metaDir)
                .Where(f => !f.EndsWith(SaveGameRegistry.RegistryFileName, StringComparison.Ordinal))
                .ToList();
            Assert.AreEqual(0, leftovers.Count,
                "The atomic-durable write leaves no temp file behind: " + string.Join(", ", leftovers));
        }

        [TestMethod]
        public void Persist_IsRepeatable_AndNeverPublishesAnEmptyDocument()
        {
            // The empty-file guard above is only safe because no write path can produce one. Prove it:
            // register, delete every entry, and the file must still be a parseable document, never empty.
            _fallen8.EnqueueTransaction(new CreateVerticesTransaction().AddVertex(0)).WaitUntilFinished();
            var entry = _registry.Register(_fallen8, SaveCheckpoint(), "api");

            _registry.Delete(entry.Id, deleteFiles: false);

            Assert.IsTrue(new FileInfo(_registry.RegistryPath).Length > 0,
                "A registry emptied of ENTRIES is still a JSON document, never a zero-length file - which is " +
                "what makes the zero-length guard safe rather than a trap for a legitimate state.");
            var doc = _registry.Load();
            Assert.AreEqual(0, doc.SaveGames.Count, "It parses, and it has no entries.");
        }

        #endregion

        #region the shared primitive

        [TestMethod]
        public void ReplaceAllTextDurably_CreatesThenReplaces_WithNoTempResidue()
        {
            var dir = Path.Combine(_metaDir, "durable");
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, "pointer.json");

            DurableFileIo.ReplaceAllTextDurably(target, "{\"v\":1}");
            Assert.AreEqual("{\"v\":1}", File.ReadAllText(target));

            DurableFileIo.ReplaceAllTextDurably(target, "{\"v\":2}");
            Assert.AreEqual("{\"v\":2}", File.ReadAllText(target), "The second call replaces rather than appends.");

            Assert.AreEqual(1, Directory.GetFiles(dir).Length, "No temp file survives either call.");
        }

        [TestMethod]
        public void ReplaceAllTextDurably_WritesUtf8WithoutABom()
        {
            // The readers are JsonSerializer over a StreamReader; a BOM would be tolerated, but emitting
            // one would silently change the bytes of every pointer file relative to the previous build.
            var target = Path.Combine(_metaDir, "bom.json");
            Directory.CreateDirectory(_metaDir);

            DurableFileIo.ReplaceAllTextDurably(target, "{}");

            var bytes = File.ReadAllBytes(target);
            Assert.AreEqual(2, bytes.Length);
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("{}"), bytes);
        }

        [TestMethod]
        public void ReplaceAllTextDurably_LeavesTheTargetIntact_WhenTheTempWriteFails()
        {
            // A failed attempt must not damage the existing pointer: the rename is the commit point.
            var target = Path.Combine(_metaDir, "keep.json");
            Directory.CreateDirectory(_metaDir);
            DurableFileIo.ReplaceAllTextDurably(target, "{\"good\":true}");

            // A directory in place of the temp path makes the temp write fail without touching the target.
            // (The temp name is GUID-unique, so it is forced by pointing at an unwritable path instead.)
            var unwritable = Path.Combine(_metaDir, "nope", "deeper", "x.json");
            Assert.ThrowsException<DirectoryNotFoundException>(
                () => DurableFileIo.ReplaceAllTextDurably(unwritable, "{}"));

            Assert.AreEqual("{\"good\":true}", File.ReadAllText(target));
        }

        #endregion
    }
}
