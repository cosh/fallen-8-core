// MIT License
//
// CheckpointDiscoveryTest.cs
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
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.Core.Helper;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Unit tests for the checkpoint discovery helper (feature hosted-durability-lifecycle) - the
    /// logic relocated from the dead AdminController.FindLatestFallen8: newest-by-stamp selection,
    /// the un-versioned fallback, and sidecar/temp exclusion.
    /// </summary>
    [TestClass]
    public class CheckpointDiscoveryTest
    {
        private string _dir;
        private const string BaseName = "Temp.f8s";

        [TestInitialize]
        public void TestInitialize()
        {
            _dir = Path.Combine(Path.GetTempPath(), "f8_discovery_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TestCleanup]
        public void TestCleanup()
        {
            try
            {
                if (_dir != null && Directory.Exists(_dir))
                {
                    Directory.Delete(_dir, true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }

        private string VersionedHeader(DateTime utc)
        {
            var name = BaseName + Constants.VersionSeparator + utc.ToBinary().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var full = Path.Combine(_dir, name);
            File.WriteAllText(full, "header");
            return full;
        }

        [TestMethod]
        public void NoFiles_ReturnsFalse()
        {
            Assert.IsFalse(CheckpointDiscovery.TryFindLatestCheckpoint(_dir, BaseName, out var path));
            Assert.IsNull(path);
        }

        [TestMethod]
        public void MissingDirectory_ReturnsFalse()
        {
            var missing = Path.Combine(_dir, "does-not-exist");
            Assert.IsFalse(CheckpointDiscovery.TryFindLatestCheckpoint(missing, BaseName, out var path));
            Assert.IsNull(path);
        }

        [TestMethod]
        public void UnversionedBaseFile_IsFound()
        {
            var full = Path.Combine(_dir, BaseName);
            File.WriteAllText(full, "header");

            Assert.IsTrue(CheckpointDiscovery.TryFindLatestCheckpoint(_dir, BaseName, out var path));
            Assert.AreEqual(full, path);
        }

        [TestMethod]
        public void MultipleVersions_PicksNewestByStamp()
        {
            var older = VersionedHeader(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var newer = VersionedHeader(new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc));

            Assert.IsTrue(CheckpointDiscovery.TryFindLatestCheckpoint(_dir, BaseName, out var path));
            Assert.AreEqual(newer, path, "The newest header by version stamp must be chosen.");
            Assert.AreNotEqual(older, path);
        }

        [TestMethod]
        public void SidecarAndTempFiles_AreExcluded()
        {
            // A versioned header plus its sidecars and an in-progress temp file for the same version.
            var stamp = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc).ToBinary()
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            var header = Path.Combine(_dir, BaseName + Constants.VersionSeparator + stamp);
            File.WriteAllText(header, "header");
            File.WriteAllText(Path.Combine(_dir, BaseName + Constants.VersionSeparator + stamp + Constants.GraphElementsSaveString + "0"), "x");
            File.WriteAllText(Path.Combine(_dir, BaseName + Constants.VersionSeparator + stamp + Constants.IndexSaveString + "0"), "x");
            File.WriteAllText(Path.Combine(_dir, BaseName + Constants.VersionSeparator + stamp + Constants.SubGraphManifestString), "x");
            File.WriteAllText(Path.Combine(_dir, BaseName + Constants.VersionSeparator + stamp + Constants.TempSaveSuffix), "x");

            Assert.IsTrue(CheckpointDiscovery.TryFindLatestCheckpoint(_dir, BaseName, out var path));
            Assert.AreEqual(header, path, "Only the main header (not a sidecar/temp) may be selected.");
        }
    }
}
