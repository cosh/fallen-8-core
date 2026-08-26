// MIT License
//
// PluginDurabilityTest.cs
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
using System.IO;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Plugins;
using NoSQL.GraphDB.Core.SubGraph;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Durability tests for the plugin registry (feature plugin-registration, Phase 4): snapshot
    ///   manifest save/load with eager recompile, keep-and-mark-Failed on recompile failure,
    ///   source-only load without a compiler, WAL entries 17/18 with commit-order replay (unanchored),
    ///   and register+remove+register replay.
    /// </summary>
    [TestClass]
    public class PluginDurabilityTest
    {
        private ILoggerFactory _loggerFactory;
        private TempDirectory _temp;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
            _temp = new TempDirectory("f8_plugindur_");
        }

        [TestCleanup]
        public void TestCleanup()
        {
            _temp?.Dispose();
        }

        #region source + helpers

        private const string FunctionSource = @"
using System;
using System.Collections.Generic;
using System.Linq;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Plugin;
using NoSQL.GraphDB.Core.Plugins;

public sealed class NeighboursOfLabel : IGraphFunction
{
    private IFallen8 _graph;
    public string PluginName => ""NeighboursOfLabel"";
    public Type PluginCategory => typeof(IGraphFunction);
    public string Description => ""d"";
    public string Manufacturer => ""t"";
    public void Initialize(IFallen8 fallen8, IDictionary<string, object> parameter) { _graph = fallen8; }
    public void Dispose() { }
    public bool TryInvoke(out GraphFunctionResult result, IDictionary<string, object> parameters)
    { result = GraphFunctionResult.FromElements(_graph.GetAllVertices(), null); return true; }
}";

        private const string PathAlgorithmSource = @"
using System;
using System.Collections.Generic;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.Plugin;

public sealed class MyPath : IShortestPathAlgorithm
{
    public string PluginName => ""MyPath"";
    public Type PluginCategory => typeof(IShortestPathAlgorithm);
    public string Description => ""d"";
    public string Manufacturer => ""t"";
    public void Initialize(IFallen8 fallen8, IDictionary<string, object> parameter) { }
    public void Dispose() { }
    public bool TryCalculateShortestPath(out List<Path> result, ShortestPathDefinition definition)
    { result = new List<Path>(); return true; }
}";

        private string SavePath => Path.Combine(_temp.FullName, "savegame.f8s");
        private string WalPath => Path.Combine(_temp.FullName, "savegame.f8s.wal");

        private Fallen8 NewEngine(bool withCompiler = true)
        {
            var engine = new Fallen8(_loggerFactory);
            if (withCompiler)
            {
                engine.PluginCompiler = new PluginCompiler();
            }
            return engine;
        }

        private Fallen8 NewEngineWithWal()
        {
            return new Fallen8(_loggerFactory, new WriteAheadLogOptions(WalPath),
                new RecipeSubGraphCompiler(), new StoredQueryCompiler(), null, null, new PluginCompiler());
        }

        private PluginsController Controller(Fallen8 engine)
            => new PluginsController(_loggerFactory.CreateLogger<PluginsController>(), engine);

        private void RegisterFunction(Fallen8 engine, string name, string source = FunctionSource)
        {
            var result = Controller(engine).RegisterFunction(new FunctionPluginRegistration { Name = name, SourceCode = source }).Result;
            Assert.AreEqual(201, ((ObjectResult)result).StatusCode, "registration of function '" + name + "' must succeed");
        }

        private void RegisterAlgorithm(Fallen8 engine, string name, string contract, string source)
        {
            var result = Controller(engine).RegisterAlgorithm(
                new AlgorithmPluginRegistration { Name = name, Contract = contract, SourceCode = source }).Result;
            Assert.AreEqual(201, ((ObjectResult)result).StatusCode, "registration of algorithm '" + name + "' must succeed");
        }

        private void DeletePlugin(Fallen8 engine, string name)
        {
            var result = Controller(engine).DeletePlugin(name).Result;
            Assert.AreEqual(204, ((StatusCodeResult)result).StatusCode);
        }

        private static string Save(Fallen8 engine, string path)
        {
            var tx = new SaveTransaction { Path = path, SavePartitions = 1 };
            var info = engine.EnqueueTransaction(tx);
            info.WaitUntilFinished();
            Assert.AreEqual(TransactionState.Finished, info.TransactionState, "the save should finish");
            return tx.ActualPath;
        }

        private static void Load(Fallen8 engine, string path)
        {
            var info = engine.EnqueueTransaction(new LoadTransaction { Path = path });
            info.WaitUntilFinished();
            Assert.AreEqual(TransactionState.Finished, info.TransactionState, "the load should finish");
        }

        #endregion

        [TestMethod]
        public void Snapshot_RoundTrips_FunctionAndAlgorithm_Recompiled()
        {
            var source = NewEngine();
            RegisterFunction(source, "NeighboursOfLabel");
            RegisterAlgorithm(source, "MyPath", "Path", PathAlgorithmSource);
            var actual = Save(source, SavePath);
            source.Dispose();

            var reloaded = NewEngine();
            Load(reloaded, actual);

            Assert.AreEqual(2, reloaded.Plugins.Count);
            Assert.IsTrue(reloaded.Plugins.TryGet(out var fn, "NeighboursOfLabel"));
            Assert.AreEqual(PluginCompileState.Compiled, fn.CompileState);
            Assert.IsTrue(reloaded.Plugins.TryGet(out var algo, "MyPath"));
            Assert.AreEqual(PluginCompileState.Compiled, algo.CompileState);

            // Both resolve/invoke after load.
            Assert.IsTrue(reloaded.TryInvokeGraphFunction(out _, "NeighboursOfLabel", null));
            Assert.IsTrue(reloaded.TryCalculateShortestPath(out _, "MyPath",
                new NoSQL.GraphDB.Core.Algorithms.Path.ShortestPathDefinition { SourceVertexId = 0, DestinationVertexId = 0 }));

            reloaded.Dispose();
        }

        [TestMethod]
        public void Snapshot_WithoutCompiler_LoadsSourceOnly()
        {
            var source = NewEngine();
            RegisterFunction(source, "NeighboursOfLabel");
            var actual = Save(source, SavePath);
            source.Dispose();

            var reloaded = NewEngine(withCompiler: false);
            Load(reloaded, actual);

            Assert.IsTrue(reloaded.Plugins.TryGet(out var fn, "NeighboursOfLabel"));
            Assert.AreEqual(PluginCompileState.SourceOnly, fn.CompileState);
            Assert.IsFalse(reloaded.TryInvokeGraphFunction(out _, "NeighboursOfLabel", null),
                "a source-only function is not runnable");

            reloaded.Dispose();
        }

        [TestMethod]
        public void Snapshot_UncompilableSource_KeptAsFailed_NotDropped()
        {
            // Plant a source-only entry whose source will FAIL a rehydration recompile (bypassing the
            // controller's compile gate, the way an engine upgrade could break a previously-good source).
            var source = NewEngine(withCompiler: false);
            var tx = new RegisterPluginTransaction
            {
                Entry = new PluginEntry(new PluginDefinition
                {
                    Name = "BrokenLater",
                    Category = PluginCategory.Function,
                    Contract = PluginContract.GraphFunction,
                    SourceCode = "public class BrokenLater { this will not compile }",
                    CreatedAt = DateTime.UtcNow
                }, PluginCompileState.SourceOnly, null)
            };
            source.EnqueueTransaction(tx).WaitUntilFinished();
            var actual = Save(source, SavePath);
            source.Dispose();

            var reloaded = NewEngine(); // WITH a compiler -> it tries (and fails) to recompile
            Load(reloaded, actual);

            Assert.IsTrue(reloaded.Plugins.TryGet(out var entry, "BrokenLater"), "the entry must NOT be silently dropped");
            Assert.AreEqual(PluginCompileState.Failed, entry.CompileState);
            Assert.IsNotNull(entry.CompileDiagnostics);

            reloaded.Dispose();
        }

        [TestMethod]
        public void Wal_Unanchored_ReplaysRegistration()
        {
            var engine = NewEngineWithWal();
            RegisterFunction(engine, "NeighboursOfLabel");
            RegisterAlgorithm(engine, "MyPath", "Path", PathAlgorithmSource);
            engine.Dispose(); // no snapshot taken -> the WAL is unanchored

            var recovered = NewEngineWithWal(); // replays the unanchored log during construction
            Assert.AreEqual(2, recovered.Plugins.Count);
            Assert.IsTrue(recovered.Plugins.TryGet(out var fn, "NeighboursOfLabel"));
            Assert.AreEqual(PluginCompileState.Compiled, fn.CompileState);
            Assert.IsTrue(recovered.TryInvokeGraphFunction(out _, "NeighboursOfLabel", null));
            recovered.Dispose();
        }

        [TestMethod]
        public void Wal_RegisterRemoveRegister_ReplaysToOne()
        {
            var engine = NewEngineWithWal();
            RegisterFunction(engine, "NeighboursOfLabel");
            DeletePlugin(engine, "NeighboursOfLabel");
            RegisterFunction(engine, "NeighboursOfLabel");
            engine.Dispose();

            var recovered = NewEngineWithWal();
            Assert.AreEqual(1, recovered.Plugins.Count);
            Assert.IsTrue(recovered.Plugins.TryGet(out _, "NeighboursOfLabel"));
            recovered.Dispose();
        }
    }
}
