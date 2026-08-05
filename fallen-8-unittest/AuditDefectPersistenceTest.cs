// MIT License
//
// AuditDefectPersistenceTest.cs
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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Services;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Serializer;
using NoSQL.GraphDB.Core.Service;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Regression tests for the audit's persistence-side admin defects: PUT /load must reject a
    /// checkpoint path that does not exist instead of answering 204 and recording a phantom "newest"
    /// save game (which aborted the next startup), and DELETE /service/{key} must stop the service
    /// it drops instead of leaving it running with no handle left to reach it.
    /// </summary>
    [TestClass]
    public class AuditDefectPersistenceTest
    {
        private String _metaDir;
        private String _dataDir;
        private SaveGameRegistry _registry;
        private ILoggerFactory _loggerFactory;
        private Fallen8 _fallen8;

        [TestInitialize]
        public void Init()
        {
            _metaDir = Path.Combine(Path.GetTempPath(), "f8_audit_meta_" + Guid.NewGuid().ToString("N"));
            _dataDir = Path.Combine(Path.GetTempPath(), "f8_audit_data_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dataDir);

            _registry = new SaveGameRegistry(
                Options.Create(new Fallen8MetadataOptions { Directory = _metaDir }),
                NullLogger<SaveGameRegistry>.Instance);

            _loggerFactory = TestLoggerFactory.Create();
            _fallen8 = new Fallen8(_loggerFactory);

            StoppableTestService.StopCalls = 0;
            ThrowingStopTestService.StopCalls = 0;
        }

        [TestCleanup]
        public void Cleanup()
        {
            _fallen8?.Dispose();
            _loggerFactory?.Dispose();
            foreach (var dir in new[] { _metaDir, _dataDir })
            {
                // A leftover temp directory must never fail a test.
                try { if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            }
        }

        private AdminController NewController()
        {
            return new AdminController(_loggerFactory.CreateLogger<AdminController>(), _fallen8, null, _registry);
        }

        /// <summary>Writes a real checkpoint of the current engine state and returns its path.</summary>
        private String SaveCheckpoint()
        {
            var tx = new SaveTransaction { Path = Path.Combine(_dataDir, "database.f8s") };
            _fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            return tx.ActualPath;
        }

        #region PUT /load pre-flight (audit B39)

        [TestMethod]
        public async Task Load_WithNonExistentPath_Returns400_AndRegistersNoSaveGame()
        {
            var missing = Path.Combine(_dataDir, "does-not-exist.f8s");
            Assert.IsFalse(File.Exists(missing), "arrange: the path must really be absent");

            var result = await NewController().Load(new LoadSpecification { SaveGameLocation = missing });

            ProblemAssert.AssertProblem(result, StatusCodes.Status400BadRequest, "does-not-exist.f8s");
            Assert.AreEqual(0, _registry.GetAll().Count,
                "a rejected load must not record a save-game entry");
            Assert.IsNull(_registry.Newest(),
                "a phantom newest entry is what aborted the next startup");
        }

        [TestMethod]
        public async Task Load_WithNonExistentPath_LeavesTheRealNewestCheckpointIntact()
        {
            // The poisoning scenario: a real checkpoint is registered, then a typo'd load used to
            // insert a UtcNow-stamped phantom that outranked it as "newest" for the namespace.
            var real = _registry.Register(_fallen8, SaveCheckpoint(), "api");

            var result = await NewController().Load(new LoadSpecification
            {
                SaveGameLocation = Path.Combine(_dataDir, "typo.f8s"),
            });

            ProblemAssert.AssertProblem(result, StatusCodes.Status400BadRequest);
            Assert.AreEqual(1, _registry.GetAll().Count);
            Assert.AreEqual(real.Id, _registry.Newest().Id,
                "the real checkpoint must stay the newest entry");
        }

        [TestMethod]
        public async Task Load_WithBlankPath_Returns400_AndRegistersNoSaveGame()
        {
            var controller = NewController();

            foreach (var blank in new[] { null, "", "   " })
            {
                var result = await controller.Load(new LoadSpecification { SaveGameLocation = blank });

                ProblemAssert.AssertProblem(result, StatusCodes.Status400BadRequest,
                    "save game location is required");
                Assert.AreEqual(0, _registry.GetAll().Count,
                    "a blank location must not record a save-game entry");
            }
        }

        [TestMethod]
        public async Task Load_WithDirectoryInsteadOfFile_Returns400()
        {
            // File.Exists is false for a directory, exactly as the engine's own pre-condition is.
            var result = await NewController().Load(new LoadSpecification { SaveGameLocation = _dataDir });

            ProblemAssert.AssertProblem(result, StatusCodes.Status400BadRequest);
            Assert.AreEqual(0, _registry.GetAll().Count);
        }

        [TestMethod]
        public async Task Load_WithExistingCheckpoint_Returns204_AndRegistersTheImport()
        {
            // The happy path the pre-flight must not break: an unregistered checkpoint on disk loads
            // and is recorded as an import (feature save-games FR-7).
            _fallen8.EnqueueTransaction(new CreateVerticesTransaction().AddVertex(0)).WaitUntilFinished();
            var checkpoint = SaveCheckpoint();
            Assert.IsTrue(File.Exists(checkpoint), "arrange: the checkpoint must exist");

            var result = await NewController().Load(new LoadSpecification
            {
                SaveGameLocation = checkpoint,
                StartServices = false,
            });

            Assert.IsInstanceOfType(result, typeof(NoContentResult), "a successful load answers 204");

            var entries = _registry.GetAll();
            Assert.AreEqual(1, entries.Count);
            Assert.AreEqual(Path.GetFullPath(checkpoint), entries[0].Location);
            Assert.AreEqual("imported", entries[0].Trigger);
        }

        #endregion

        #region DELETE /service/{key} (audit B43)

        private static PluginSpecification ServiceSpec(String uniqueId, String pluginName)
        {
            return new PluginSpecification
            {
                UniqueId = uniqueId,
                PluginType = pluginName,
                PluginOptions = new Dictionary<String, PropertySpecification>(),
            };
        }

        [TestMethod]
        public void DeleteService_StopsTheServiceBeforeDroppingIt()
        {
            var controller = NewController();
            Assert.IsTrue(controller.CreateService(ServiceSpec("svc", StoppableTestService.TestPluginName)),
                "arrange: the test service plugin must be discoverable and addable");
            Assert.IsTrue(_fallen8.ServiceFactory.Services.ContainsKey("svc"));
            Assert.AreEqual(0, StoppableTestService.StopCalls, "arrange: nothing stopped yet");

            var deleted = controller.DeleteService("svc");

            Assert.IsTrue(deleted, "the registered service is deleted");
            Assert.AreEqual(1, StoppableTestService.StopCalls,
                "the dropped service must be stopped: the factory held the only handle to it");
            Assert.AreEqual(0, _fallen8.ServiceFactory.Services.Count);
        }

        [TestMethod]
        public void DeleteService_WithUnknownKey_ReturnsFalse_AndStopsNothing()
        {
            var controller = NewController();
            Assert.IsTrue(controller.CreateService(ServiceSpec("svc", StoppableTestService.TestPluginName)));

            Assert.IsFalse(controller.DeleteService("nope"), "an unknown key deletes nothing");
            Assert.AreEqual(0, StoppableTestService.StopCalls,
                "an unknown key must not stop the services that ARE registered");
            Assert.IsTrue(_fallen8.ServiceFactory.Services.ContainsKey("svc"));

            // Deleting the same key twice: the second call finds nothing and stops nothing again.
            Assert.IsTrue(controller.DeleteService("svc"));
            Assert.IsFalse(controller.DeleteService("svc"));
            Assert.AreEqual(1, StoppableTestService.StopCalls);
        }

        [TestMethod]
        public void DeleteService_WhenTryStopThrows_StillRemovesTheService()
        {
            // A misbehaving plugin must not turn the documented 200-with-bool into a 500.
            var controller = NewController();
            Assert.IsTrue(controller.CreateService(ServiceSpec("bad", ThrowingStopTestService.TestPluginName)));

            var deleted = controller.DeleteService("bad");

            Assert.IsTrue(deleted, "a throwing TryStop must not prevent the removal");
            Assert.AreEqual(1, ThrowingStopTestService.StopCalls, "the stop was attempted");
            Assert.AreEqual(0, _fallen8.ServiceFactory.Services.Count);
        }

        #endregion
    }

    /// <summary>
    /// A minimal service double that counts how often it was stopped. It is a top-level public type
    /// with a public parameterless constructor so <c>PluginFactory</c> discovers it by plugin name
    /// (nested types report <c>IsNestedPublic</c>, not <c>IsPublic</c>, so they are skipped) - only
    /// then can the admin endpoints add it by name. Everything else is an inert no-op.
    /// </summary>
    /// <remarks>
    /// Consequence of being globally discoverable: this double is enumerated by
    /// <c>PluginFactory.TryGetAvailablePlugins&lt;IService&gt;()</c> during test runs, so any FUTURE
    /// test asserting an exact set or count of available SERVICE plugins must filter the test
    /// doubles out (e.g. by <see cref="Manufacturer"/> == "fallen-8 tests"). The same note on the
    /// index side lives on <see cref="ThrowingOnLoadIndex"/>.
    /// </remarks>
    public sealed class StoppableTestService : IService
    {
        public const String TestPluginName = "StoppableTestService";

        /// <summary>How often any instance was asked to stop; reset by the test's initializer.</summary>
        public static Int32 StopCalls;

        public String PluginName => TestPluginName;
        public Type PluginCategory => typeof(IService);
        public String Description => "A test service that records its stop calls.";
        public String Manufacturer => "fallen-8 tests";

        public DateTime StartTime => DateTime.MinValue;
        public Boolean IsRunning
        {
            get; private set;
        }

        public IDictionary<String, String> Metadata => new Dictionary<String, String>();

        public void Initialize(IFallen8 fallen8, IDictionary<String, Object> parameter)
        {
        }

        public void Save(SerializationWriter writer)
        {
        }

        public void Load(SerializationReader reader, IFallen8 fallen8)
        {
        }

        public Boolean TryStop()
        {
            Interlocked.Increment(ref StopCalls);
            IsRunning = false;
            return true;
        }

        public Boolean TryStart()
        {
            IsRunning = true;
            return true;
        }

        public void OnServiceRestart()
        {
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// A service double whose <see cref="TryStop"/> always throws, proving the delete endpoint
    /// contains a misbehaving plugin instead of failing the request. Discoverable for the same
    /// reason as <see cref="StoppableTestService"/> (see its remarks).
    /// </summary>
    public sealed class ThrowingStopTestService : IService
    {
        public const String TestPluginName = "ThrowingStopTestService";

        /// <summary>How often any instance was asked to stop; reset by the test's initializer.</summary>
        public static Int32 StopCalls;

        public String PluginName => TestPluginName;
        public Type PluginCategory => typeof(IService);
        public String Description => "A test service whose TryStop throws.";
        public String Manufacturer => "fallen-8 tests";

        public DateTime StartTime => DateTime.MinValue;
        public Boolean IsRunning => false;
        public IDictionary<String, String> Metadata => new Dictionary<String, String>();

        public void Initialize(IFallen8 fallen8, IDictionary<String, Object> parameter)
        {
        }

        public void Save(SerializationWriter writer)
        {
        }

        public void Load(SerializationReader reader, IFallen8 fallen8)
        {
        }

        public Boolean TryStop()
        {
            Interlocked.Increment(ref StopCalls);
            throw new InvalidOperationException("This service deliberately fails to stop.");
        }

        public Boolean TryStart()
        {
            return true;
        }

        public void OnServiceRestart()
        {
        }

        public void Dispose()
        {
        }
    }
}
