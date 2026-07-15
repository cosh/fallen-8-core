// MIT License
//
// HostedDurabilityLifecycleTest.cs
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
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Integration tests for the hosted durability lifecycle (feature hosted-durability-lifecycle):
    /// the hosted API loads the latest checkpoint on boot, saves on a clean shutdown, and recovers
    /// committed transactions via WAL replay after a crash. Data is created directly against the
    /// hosted IFallen8 singleton (the same instance the controllers use), so these exercise the
    /// load-on-start / save-on-stop wiring rather than HTTP DTO plumbing.
    /// </summary>
    [TestClass]
    public class HostedDurabilityLifecycleTest
    {
        private string _storageDir;
        private string _metaDir;

        [TestInitialize]
        public void TestInitialize()
        {
            _storageDir = Path.Combine(Path.GetTempPath(), "f8_hosted_" + Guid.NewGuid().ToString("N"));
            _metaDir = Path.Combine(Path.GetTempPath(), "f8_hosted_meta_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_storageDir);
        }

        [TestCleanup]
        public void TestCleanup()
        {
            foreach (var dir in new[] { _storageDir, _metaDir })
            {
                try
                {
                    if (dir != null && Directory.Exists(dir))
                    {
                        Directory.Delete(dir, true);
                    }
                }
                catch
                {
                    // best-effort cleanup
                }
            }
        }

        private sealed class DurabilityFactory : WebApplicationFactory<Program>
        {
            private readonly IReadOnlyDictionary<string, string> _settings;

            public DurabilityFactory(IReadOnlyDictionary<string, string> settings)
            {
                _settings = settings;
            }

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                foreach (var kv in _settings)
                {
                    builder.UseSetting(kv.Key, kv.Value);
                }
            }
        }

        private DurabilityFactory NewHost(bool volatileMode = false, bool saveOnShutdown = true)
        {
            return new DurabilityFactory(new Dictionary<string, string>
            {
                ["Fallen8:Durability:StorageDirectory"] = _storageDir,
                ["Fallen8:Durability:Volatile"] = volatileMode ? "true" : "false",
                ["Fallen8:Durability:SaveOnShutdown"] = saveOnShutdown ? "true" : "false",
                // Isolate the save-game registry per test (as storage is isolated), so parallel
                // test classes never collide on the default bin/metadata/savegames.json.
                ["Fallen8:Metadata:Directory"] = _metaDir,
            });
        }

        /// <summary>Starts the host (StartAsync runs) and returns the hosted engine singleton.</summary>
        private static IFallen8 Engine(DurabilityFactory factory)
        {
            // Accessing Services boots the host - StartAsync (load-on-boot) runs synchronously here.
            return factory.Services.GetRequiredService<IFallen8>();
        }

        private static int[] AddVertices(IFallen8 engine, params (string Label, int Age)[] specs)
        {
            var tx = new CreateVerticesTransaction();
            foreach (var s in specs)
            {
                tx.AddVertex(1u, s.Label, new Dictionary<string, object> { { "age", s.Age } });
            }
            engine.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.GetCreatedVertices().Select(v => v.Id).ToArray();
        }

        [TestMethod]
        public void CleanRestart_DurableMode_DataSurvives()
        {
            int v0Id, v1Id;
            using (var host1 = NewHost(volatileMode: false, saveOnShutdown: true))
            {
                var engine = Engine(host1);
                var ids = AddVertices(engine, ("person", 30), ("person", 42));
                v0Id = ids[0];
                v1Id = ids[1];
                var edgeTx = new CreateEdgesTransaction();
                edgeTx.AddEdge(v0Id, "knows", v1Id, 1u);
                engine.EnqueueTransaction(edgeTx).WaitUntilFinished();

                Assert.AreEqual(2, engine.VertexCount);
                Assert.AreEqual(1, engine.EdgeCount);
                // Dispose (below) triggers the clean-shutdown save.
            }

            using (var host2 = NewHost(volatileMode: false, saveOnShutdown: true))
            {
                var engine = Engine(host2);
                Assert.AreEqual(2, engine.VertexCount, "Vertices must survive a clean restart in durable mode.");
                Assert.AreEqual(1, engine.EdgeCount, "The edge must survive a clean restart.");

                Assert.IsTrue(engine.TryGetVertex(out var v0, v0Id), "The saved vertex must be present after restart.");
                Assert.IsTrue(v0.TryGetProperty(out int age, "age"));
                Assert.AreEqual(30, age, "The vertex property must survive the restart.");
                Assert.IsTrue(v0.TryGetOutEdge(out var outEdges, "knows"), "The edge adjacency must survive.");
                Assert.AreEqual(1, outEdges.Count(e => e.TargetVertex.Id == v1Id));
            }
        }

        [TestMethod]
        public void Crash_NoShutdownSave_RecoversViaWalReplay()
        {
            int vId;
            using (var host1 = NewHost(volatileMode: false, saveOnShutdown: false))
            {
                var engine = Engine(host1);
                var ids = AddVertices(engine, ("survivor", 7));
                vId = ids[0];
                Assert.AreEqual(1, engine.VertexCount);
                // saveOnShutdown=false: dispose simulates a crash - no snapshot is written, but the
                // per-commit WAL already fsync'd the create.
            }

            using (var host2 = NewHost(volatileMode: false, saveOnShutdown: false))
            {
                var engine = Engine(host2);
                Assert.AreEqual(1, engine.VertexCount,
                    "A committed transaction must be recovered by WAL replay after a crash (no shutdown save).");
                Assert.IsTrue(engine.TryGetVertex(out var v, vId));
                Assert.IsTrue(v.TryGetProperty(out int age, "age"));
                Assert.AreEqual(7, age);
            }
        }

        [TestMethod]
        public void EmptyStorage_StartsCleanAndUsable()
        {
            using var host = NewHost(volatileMode: false, saveOnShutdown: true);
            var engine = Engine(host);

            Assert.AreEqual(0, engine.VertexCount, "An empty storage directory starts with an empty graph.");
            Assert.AreEqual(0, engine.EdgeCount);

            // ...and the engine is usable.
            var ids = AddVertices(engine, ("fresh", 1));
            Assert.AreEqual(1, engine.VertexCount);
            Assert.IsTrue(engine.TryGetVertex(out _, ids[0]));
        }

        [TestMethod]
        public void VolatileMode_DataIsLostOnRestart()
        {
            using (var host1 = NewHost(volatileMode: true))
            {
                var engine = Engine(host1);
                AddVertices(engine, ("ephemeral", 99));
                Assert.AreEqual(1, engine.VertexCount);
            }

            using (var host2 = NewHost(volatileMode: true))
            {
                var engine = Engine(host2);
                Assert.AreEqual(0, engine.VertexCount,
                    "In volatile mode a restart loses data by explicit choice; nothing is loaded.");
            }

            // No checkpoint or WAL should have been written in volatile mode.
            Assert.AreEqual(0, Directory.GetFiles(_storageDir).Length,
                "Volatile mode must not write any checkpoint or WAL files.");
        }
    }
}
