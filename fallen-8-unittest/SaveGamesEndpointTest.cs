// MIT License
//
// SaveGamesEndpointTest.cs
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
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Pipeline tests for the save-game registry (feature save-games) through the real ASP.NET
    /// pipeline: PUT /save returns and registers an entry, GET /savegames lists it, a foreign load
    /// auto-registers once, delete removes it, and a fresh host with an empty registry starts empty
    /// even with a checkpoint on disk (registry-driven boot).
    /// </summary>
    [TestClass]
    public class SaveGamesEndpointTest
    {
        private string _storageDir;
        private string _metaDir;

        [TestInitialize]
        public void Init()
        {
            _storageDir = Path.Combine(Path.GetTempPath(), "f8sg_store_" + Guid.NewGuid().ToString("N"));
            _metaDir = Path.Combine(Path.GetTempPath(), "f8sg_meta_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_storageDir);
        }

        [TestCleanup]
        public void Cleanup()
        {
            foreach (var dir in new[] { _storageDir, _metaDir })
            {
                try { if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            }
        }

        private sealed class Factory : WebApplicationFactory<Program>
        {
            private readonly string _storage;
            private readonly string _meta;
            private readonly bool _volatile;

            public Factory(string storage, string meta, bool isVolatile)
            {
                _storage = storage;
                _meta = meta;
                _volatile = isVolatile;
            }

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseSetting("Fallen8:Durability:Volatile", _volatile ? "true" : "false");
                builder.UseSetting("Fallen8:Durability:StorageDirectory", _storage);
                builder.UseSetting("Fallen8:Durability:SaveOnShutdown", "false");
                builder.UseSetting("Fallen8:Metadata:Directory", _meta);
            }
        }

        private sealed class SaveGameDto
        {
            public string Id
            {
                get; set;
            }
            public string Trigger
            {
                get; set;
            }
            public string Location
            {
                get; set;
            }
            public int FileCount
            {
                get; set;
            }
            public long TotalBytes
            {
                get; set;
            }
        }

        private static readonly JsonSerializerOptions _json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        [TestMethod]
        public async Task Save_RegistersEntry_ListedAndFetchable()
        {
            using var factory = new Factory(_storageDir, _metaDir, isVolatile: false);
            using var client = factory.CreateClient();

            // Create a vertex so the checkpoint is non-trivial.
            var v = await client.PutAsync("/vertex?waitForCompletion=true",
                new StringContent("{\"label\":\"person\",\"creationDate\":0}", Encoding.UTF8, "application/json"));
            Assert.AreEqual(HttpStatusCode.Accepted, v.StatusCode);

            var savePath = Path.Combine(_storageDir, "database.f8s");
            var save = await client.PutAsync("/save?waitForCompletion=true",
                new StringContent("{\"saveGameLocation\":\"" + savePath.Replace("\\", "\\\\") + "\"}", Encoding.UTF8, "application/json"));
            Assert.AreEqual(HttpStatusCode.OK, save.StatusCode);
            var entry = JsonSerializer.Deserialize<SaveGameDto>(await save.Content.ReadAsStringAsync(), _json);
            Assert.IsTrue(entry.Id.StartsWith("sg-"));
            Assert.AreEqual("api", entry.Trigger);
            Assert.IsTrue(entry.FileCount >= 1);
            Assert.IsTrue(entry.TotalBytes > 0);

            var list = JsonSerializer.Deserialize<List<SaveGameDto>>(await client.GetStringAsync("/savegames"), _json);
            Assert.AreEqual(1, list.Count);
            Assert.AreEqual(entry.Id, list[0].Id);

            var one = await client.GetAsync("/savegames/" + entry.Id);
            Assert.AreEqual(HttpStatusCode.OK, one.StatusCode);

            var missing = await client.GetAsync("/savegames/sg-does-not-exist");
            Assert.AreEqual(HttpStatusCode.NoContent, missing.StatusCode);
        }

        [TestMethod]
        public async Task Load_AutoRegistersUnknownCheckpoint_Once()
        {
            // First host: create + save a checkpoint, capture its path.
            string checkpointPath;
            using (var factory = new Factory(_storageDir, _metaDir, isVolatile: false))
            using (var client = factory.CreateClient())
            {
                await client.PutAsync("/vertex?waitForCompletion=true",
                    new StringContent("{\"label\":\"person\",\"creationDate\":0}", Encoding.UTF8, "application/json"));
                var save = await client.PutAsync("/save?waitForCompletion=true",
                    new StringContent("{\"saveGameLocation\":\"" + Path.Combine(_storageDir, "database.f8s").Replace("\\", "\\\\") + "\"}",
                        Encoding.UTF8, "application/json"));
                var entry = JsonSerializer.Deserialize<SaveGameDto>(await save.Content.ReadAsStringAsync(), _json);
                checkpointPath = entry.Location;
            }

            // Fresh host with an EMPTY metadata dir: loading the checkpoint auto-registers it (FR-7).
            var freshMeta = Path.Combine(Path.GetTempPath(), "f8sg_meta2_" + Guid.NewGuid().ToString("N"));
            try
            {
                using var factory = new Factory(_storageDir, freshMeta, isVolatile: false);
                using var client = factory.CreateClient();

                Assert.AreEqual(0, JsonSerializer.Deserialize<List<SaveGameDto>>(
                    await client.GetStringAsync("/savegames"), _json).Count);

                var body = "{\"startServices\":true,\"saveGameLocation\":\"" + checkpointPath.Replace("\\", "\\\\") + "\"}";
                var load = await client.PutAsync("/load?waitForCompletion=true",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                Assert.AreEqual(HttpStatusCode.NoContent, load.StatusCode);

                var afterOne = JsonSerializer.Deserialize<List<SaveGameDto>>(await client.GetStringAsync("/savegames"), _json);
                Assert.AreEqual(1, afterOne.Count);
                Assert.AreEqual("imported", afterOne[0].Trigger);

                // Loading again adds no duplicate.
                await client.PutAsync("/load?waitForCompletion=true",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                var afterTwo = JsonSerializer.Deserialize<List<SaveGameDto>>(await client.GetStringAsync("/savegames"), _json);
                Assert.AreEqual(1, afterTwo.Count);
            }
            finally
            {
                try { if (Directory.Exists(freshMeta)) Directory.Delete(freshMeta, true); } catch { }
            }
        }

        [TestMethod]
        public async Task Delete_RemovesEntry()
        {
            using var factory = new Factory(_storageDir, _metaDir, isVolatile: false);
            using var client = factory.CreateClient();

            await client.PutAsync("/vertex?waitForCompletion=true",
                new StringContent("{\"label\":\"person\",\"creationDate\":0}", Encoding.UTF8, "application/json"));
            var save = await client.PutAsync("/save?waitForCompletion=true",
                new StringContent("{\"saveGameLocation\":\"" + Path.Combine(_storageDir, "database.f8s").Replace("\\", "\\\\") + "\"}",
                    Encoding.UTF8, "application/json"));
            var entry = JsonSerializer.Deserialize<SaveGameDto>(await save.Content.ReadAsStringAsync(), _json);

            var del = await client.DeleteAsync("/savegames/" + entry.Id);
            Assert.AreEqual(HttpStatusCode.NoContent, del.StatusCode);
            Assert.AreEqual(0, JsonSerializer.Deserialize<List<SaveGameDto>>(
                await client.GetStringAsync("/savegames"), _json).Count);

            var delMissing = await client.DeleteAsync("/savegames/" + entry.Id);
            Assert.AreEqual(HttpStatusCode.NotFound, delMissing.StatusCode);
        }

        [TestMethod]
        public async Task Startup_EmptyRegistry_StartsEmpty_EvenWithCheckpointOnDisk()
        {
            // Host A: save a graph with a vertex, leaving checkpoint files in _storageDir.
            using (var a = new Factory(_storageDir, _metaDir, isVolatile: false))
            using (var ca = a.CreateClient())
            {
                await ca.PutAsync("/vertex?waitForCompletion=true",
                    new StringContent("{\"label\":\"person\",\"creationDate\":0}", Encoding.UTF8, "application/json"));
                await ca.PutAsync("/save?waitForCompletion=true",
                    new StringContent("{\"saveGameLocation\":\"" + Path.Combine(_storageDir, "database.f8s").Replace("\\", "\\\\") + "\"}",
                        Encoding.UTF8, "application/json"));
            }
            Assert.IsTrue(Directory.GetFiles(_storageDir).Length > 0, "Checkpoint files exist on disk.");

            // Host B: SAME storage dir, but a FRESH empty metadata dir -> registry-driven boot must
            // start EMPTY, not auto-load the orphan checkpoint (FR-8).
            var freshMeta = Path.Combine(Path.GetTempPath(), "f8sg_meta3_" + Guid.NewGuid().ToString("N"));
            try
            {
                using var b = new Factory(_storageDir, freshMeta, isVolatile: false);
                using var cb = b.CreateClient();
                var count = await cb.GetStringAsync("/vertex/count");
                Assert.AreEqual("0", count.Trim(), "An empty registry starts empty despite a checkpoint on disk.");
            }
            finally
            {
                try { if (Directory.Exists(freshMeta)) Directory.Delete(freshMeta, true); } catch { }
            }
        }

        [TestMethod]
        public async Task Startup_WithRegistry_LoadsNewest()
        {
            // Host A: create 1 vertex, save (registered). Registry + checkpoint now describe 1 vertex.
            using (var a = new Factory(_storageDir, _metaDir, isVolatile: false))
            using (var ca = a.CreateClient())
            {
                await ca.PutAsync("/vertex?waitForCompletion=true",
                    new StringContent("{\"label\":\"person\",\"creationDate\":0}", Encoding.UTF8, "application/json"));
                await ca.PutAsync("/save?waitForCompletion=true",
                    new StringContent("{\"saveGameLocation\":\"" + Path.Combine(_storageDir, "database.f8s").Replace("\\", "\\\\") + "\"}",
                        Encoding.UTF8, "application/json"));
            }

            // Host B: SAME storage + metadata dir -> boots from the newest registered save game.
            using var b = new Factory(_storageDir, _metaDir, isVolatile: false);
            using var cb = b.CreateClient();
            var count = await cb.GetStringAsync("/vertex/count");
            Assert.AreEqual("1", count.Trim(), "The newest registered save game is loaded on boot.");
        }
    }
}
