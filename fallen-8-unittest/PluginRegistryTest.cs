// MIT License
//
// PluginRegistryTest.cs
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
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Plugins;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Engine-level tests for the plugin registry (feature plugin-registration): name validation,
    ///   the transactional register/remove path with structured failure reasons, duplicate + quota
    ///   handling, snapshot isolation, and the entry invariant. These exercise the registry through a
    ///   real in-memory <see cref="Fallen8"/> and the transactions (no Roslyn - the artifact is a stub
    ///   type, since the registry does not validate the artifact; that is the compiler's job).
    /// </summary>
    [TestClass]
    public class PluginRegistryTest
    {
        private Fallen8 _fallen8;

        [TestInitialize]
        public void TestInitialize()
        {
            _fallen8 = new Fallen8(TestLoggerFactory.Create());
        }

        [TestCleanup]
        public void TestCleanup()
        {
            _fallen8.Dispose();
        }

        #region helpers

        private static PluginDefinition Definition(string name)
        {
            return new PluginDefinition
            {
                Name = name,
                Category = PluginCategory.Algorithm,
                Contract = PluginContract.Path,
                SourceCode = "// stub",
                Description = "test",
                CreatedAt = DateTime.UtcNow
            };
        }

        private static PluginEntry CompiledEntry(string name)
        {
            // The registry does not inspect the artifact type; a stub type stands in for a compiled
            // plugin type at this level.
            return new PluginEntry(Definition(name), PluginCompileState.Compiled, typeof(object));
        }

        private void Register(PluginEntry entry, bool expectSuccess = true)
        {
            var info = _fallen8.EnqueueTransaction(new RegisterPluginTransaction { Entry = entry });
            info.WaitUntilFinished();
            Assert.AreEqual(expectSuccess ? TransactionState.Finished : TransactionState.RolledBack,
                info.TransactionState);
        }

        #endregion

        [TestMethod]
        public void IsValidName_AcceptsAndRejects()
        {
            Assert.IsTrue(PluginRegistry.IsValidName("My-Algo_1"));
            Assert.IsTrue(PluginRegistry.IsValidName("a"));
            Assert.IsFalse(PluginRegistry.IsValidName(null));
            Assert.IsFalse(PluginRegistry.IsValidName(""));
            Assert.IsFalse(PluginRegistry.IsValidName("has space"));
            Assert.IsFalse(PluginRegistry.IsValidName("dots.not.allowed"));
            Assert.IsFalse(PluginRegistry.IsValidName("slash/no"));
            Assert.IsFalse(PluginRegistry.IsValidName(new string('x', PluginRegistry.MaxNameLength + 1)));
        }

        [TestMethod]
        public void CompiledEntry_RequiresArtifact()
        {
            Assert.ThrowsException<ArgumentException>(
                () => new PluginEntry(Definition("x"), PluginCompileState.Compiled, null));

            // Failed / SourceOnly carry no artifact and no exception.
            var failed = new PluginEntry(Definition("x"), PluginCompileState.Failed, null, "boom");
            Assert.AreEqual(PluginCompileState.Failed, failed.CompileState);
            Assert.AreEqual("boom", failed.CompileDiagnostics);
            Assert.IsNull(failed.Artifact);
        }

        [TestMethod]
        public void Register_Then_Get_And_List()
        {
            Register(CompiledEntry("algoA"));

            Assert.IsTrue(_fallen8.Plugins.TryGet(out var entry, "algoA"));
            Assert.AreEqual("algoA", entry.Definition.Name);
            Assert.AreEqual(PluginCategory.Algorithm, entry.Definition.Category);
            Assert.AreEqual(1, _fallen8.Plugins.Count);
            Assert.AreEqual(1, _fallen8.Plugins.GetAll().Count);
        }

        [TestMethod]
        public void Register_DuplicateName_RollsBackWithConflict()
        {
            Register(CompiledEntry("dupe"));

            var info = _fallen8.EnqueueTransaction(new RegisterPluginTransaction { Entry = CompiledEntry("dupe") });
            info.WaitUntilFinished();

            Assert.AreEqual(TransactionState.RolledBack, info.TransactionState);
            Assert.AreEqual(TransactionFailureReason.Conflict, info.FailureReason);
            Assert.AreEqual(1, _fallen8.Plugins.Count);
        }

        [TestMethod]
        public void Register_BeyondQuota_RollsBackWithQuotaExceeded()
        {
            _fallen8.Plugins.MaxCount = 2;
            Register(CompiledEntry("p1"));
            Register(CompiledEntry("p2"));

            var info = _fallen8.EnqueueTransaction(new RegisterPluginTransaction { Entry = CompiledEntry("p3") });
            info.WaitUntilFinished();

            Assert.AreEqual(TransactionState.RolledBack, info.TransactionState);
            Assert.AreEqual(TransactionFailureReason.QuotaExceeded, info.FailureReason);
            Assert.AreEqual(2, _fallen8.Plugins.Count);
        }

        [TestMethod]
        public void NamesForContract_ReturnsOnlyCompiledEntriesOfThatContract()
        {
            // The union surface (feature plugin-registration §4.4): a status/analytics "available"
            // list unions these names with the built-ins. Only Compiled entries of the contract count.
            Register(CompiledEntry("PathA"));
            Register(CompiledEntry("PathB"));

            // A Failed entry of the same contract must be excluded (it cannot be invoked).
            var failed = new PluginEntry(Definition("PathFailed"), PluginCompileState.Failed, null, "boom");
            var info = _fallen8.EnqueueTransaction(new RegisterPluginTransaction { Entry = failed });
            info.WaitUntilFinished();
            Assert.AreEqual(TransactionState.Finished, info.TransactionState);

            var pathNames = new List<string>(_fallen8.Plugins.NamesForContract(PluginContract.Path));
            CollectionAssert.AreEquivalent(new[] { "PathA", "PathB" }, pathNames);
            Assert.AreEqual(0, _fallen8.Plugins.NamesForContract(PluginContract.Analytics).Count);
        }

        [TestMethod]
        public void Remove_Then_Absent()
        {
            Register(CompiledEntry("gone"));
            Assert.IsTrue(_fallen8.Plugins.TryGet(out _, "gone"));

            var info = _fallen8.EnqueueTransaction(new RemovePluginTransaction { Name = "gone" });
            info.WaitUntilFinished();

            Assert.AreEqual(TransactionState.Finished, info.TransactionState);
            Assert.IsFalse(_fallen8.Plugins.TryGet(out _, "gone"));
            Assert.AreEqual(0, _fallen8.Plugins.Count);
        }

        [TestMethod]
        public void Remove_Missing_RollsBackWithNotFound()
        {
            var info = _fallen8.EnqueueTransaction(new RemovePluginTransaction { Name = "nope" });
            info.WaitUntilFinished();

            Assert.AreEqual(TransactionState.RolledBack, info.TransactionState);
            Assert.AreEqual(TransactionFailureReason.NotFound, info.FailureReason);
        }

        [TestMethod]
        public void GetAll_SnapshotIsIsolatedFromLaterMutation()
        {
            Register(CompiledEntry("s1"));
            var snapshot = _fallen8.Plugins.GetAll();
            Assert.AreEqual(1, snapshot.Count);

            Register(CompiledEntry("s2"));

            // The previously returned list is a point-in-time copy, unaffected by the later register.
            Assert.AreEqual(1, snapshot.Count);
            Assert.AreEqual(2, _fallen8.Plugins.Count);
        }
    }
}
