// MIT License
//
// ConfigOverridesTest.cs
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
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Configuration;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// The stored-overrides configuration layer (feature writable-instance-config phase 2).
    ///
    /// Two properties carry this layer, and both are tested here rather than assumed. It must OUTRANK
    /// appsettings.json, because that file ships much of the writable set at its code defaults and a
    /// layer underneath would be dead on most of the feature. It must NEVER outrank an environment
    /// variable or the command line, because the shipped compose file declares roughly two dozen
    /// Fallen8__ variables and the docs tell operators to set them by hand.
    ///
    /// The second property cannot be had by source ordering: this source is appended LAST, so ordering
    /// alone would make it beat everything. It is per-key arbitration that holds the line, which is why
    /// the arbitration tests below assert the effective value through a real configuration root instead
    /// of inspecting the provider.
    /// </summary>
    [TestClass]
    public class ConfigOverridesTest
    {
        private String _directory;

        [TestInitialize]
        public void CreateDirectory()
        {
            _directory = Path.Combine(Path.GetTempPath(), "f8-overrides-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        [TestCleanup]
        public void RemoveDirectory()
        {
            try
            {
                if (_directory != null && Directory.Exists(_directory))
                {
                    Directory.Delete(_directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }

        #region helpers

        private void WriteOverrides(String json)
        {
            File.WriteAllText(Path.Combine(_directory, Fallen8ConfigOverridesSource.FileName), json);
        }

        private void WriteOverrides(params (String Key, String Value)[] settings)
        {
            var body = String.Join(",", settings.Select(s => "\"" + s.Key + "\": \"" + s.Value + "\""));
            WriteOverrides("{ \"version\": 1, \"settings\": { " + body + " } }");
        }

        /// <summary>
        ///   Builds a configuration root shaped like the real app's: appsettings-like values first, then
        ///   any environment or command-line declarations, then the overrides source appended LAST, the
        ///   way Program.cs adds it.
        /// </summary>
        private (IConfigurationRoot Root, Fallen8ConfigOverridesSource Source) Build(
            IDictionary<String, String> appSettings = null,
            IDictionary<String, String> environment = null,
            String[] commandLine = null,
            String metadataDirectory = null)
        {
            var builder = new ConfigurationBuilder();
            builder.AddInMemoryCollection(appSettings ?? new Dictionary<String, String>());

            if (environment != null)
            {
                foreach (var pair in environment)
                {
                    Environment.SetEnvironmentVariable(pair.Key, pair.Value);
                }

                builder.AddEnvironmentVariables();
            }

            if (commandLine != null)
            {
                builder.AddCommandLine(commandLine);
            }

            var partial = builder.Build();
            var source = Fallen8ConfigOverridesSource.Resolve(partial,
                metadataDirectory ?? _directory);
            if (source != null)
            {
                builder.Add(source);
            }

            return (builder.Build(), source);
        }

        private static void ClearEnvironment(params String[] names)
        {
            foreach (var name in names)
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        #endregion

        #region the landmine: never guess a path

        /// <summary>
        ///   THE test that protects the whole suite. Fallen8MetadataOptions.ResolveDirectory falls back
        ///   to a metadata folder under AppContext.BaseDirectory, which under the unit suite is the one
        ///   shared test output directory. If this source ever adopted that fallback, an overrides file
        ///   sitting there would be appended last and therefore outrank the settings that dozens of test
        ///   hosts inject, for the rest of the run and for every later run on the machine. So an
        ///   instance that was never told where its metadata lives must keep NO overrides at all.
        /// </summary>
        [TestMethod]
        public void WithNoConfiguredMetadataDirectory_ThereIsNoOverridesLayerAtAll()
        {
            var configuration = new ConfigurationBuilder().Build();

            foreach (var unset in new[] { null, "", "   " })
            {
                Assert.IsNull(Fallen8ConfigOverridesSource.Resolve(configuration, unset),
                    "an unconfigured metadata directory must yield no overrides layer, never a guessed path");
            }
        }

        /// <summary>
        ///   The same rule from the other side: the layer must never resolve to anything under the test
        ///   output directory, which is what the forbidden fallback would produce.
        /// </summary>
        [TestMethod]
        public void TheOverridesPath_IsNeverUnderTheApplicationBaseDirectory()
        {
            var (_, source) = Build();

            Assert.IsNotNull(source, "an explicitly configured directory does yield a layer");
            Assert.AreEqual(Path.Combine(_directory, Fallen8ConfigOverridesSource.FileName), source.State.Path);
            Assert.IsFalse(source.State.Path.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase),
                "the overrides file must never land in the shared test output directory");
        }

        #endregion

        #region arbitration

        [TestMethod]
        public void AStoredOverride_BeatsAppSettings()
        {
            WriteOverrides(("Fallen8:Plugins:MaxCount", "128"));

            var (root, source) = Build(appSettings: new Dictionary<String, String>
            {
                ["Fallen8:Plugins:MaxCount"] = "64"
            });

            Assert.AreEqual("128", root["Fallen8:Plugins:MaxCount"],
                "appsettings ships the writable set at its defaults, so a layer that lost to it would be dead");
            Assert.AreEqual(1, source.State.Applied.Count);
            Assert.AreEqual(0, source.State.Shadowed.Count);
        }

        [TestMethod]
        public void AStoredOverride_LosesToAnEnvironmentVariable_AndSaysSo()
        {
            const String Variable = "Fallen8__Plugins__MaxCount";
            try
            {
                WriteOverrides(("Fallen8:Plugins:MaxCount", "128"));

                var (root, source) = Build(environment: new Dictionary<String, String>
                {
                    [Variable] = "256"
                });

                Assert.AreEqual("256", root["Fallen8:Plugins:MaxCount"],
                    "the environment is the operator's own declaration and outranks stored configuration");
                CollectionAssert.Contains(source.State.Shadowed.ToList(), "Fallen8:Plugins:MaxCount",
                    "a stored value that lost must be reported, never silently dropped");
                Assert.AreEqual(0, source.State.Applied.Count);
            }
            finally
            {
                ClearEnvironment(Variable);
            }
        }

        /// <summary>
        ///   Compose writes <c>Fallen8__Security__ApiKey=${F8_API_KEY:-}</c>, so on a default environment
        ///   the variable exists and is EMPTY. An empty declaration is still a declaration: treating
        ///   "unset" as "the operator has no opinion" would let a stored value quietly win over a
        ///   variable the operator deliberately left blank.
        /// </summary>
        [TestMethod]
        public void AnEmptyEnvironmentDeclaration_StillOutranksAStoredOverride()
        {
            const String Variable = "Fallen8__Chat__Ollama__Model";
            try
            {
                WriteOverrides(("Fallen8:Chat:Ollama:Model", "stored-model"));

                var (root, source) = Build(environment: new Dictionary<String, String>
                {
                    [Variable] = String.Empty
                });

                Assert.AreEqual(String.Empty, root["Fallen8:Chat:Ollama:Model"]);
                CollectionAssert.Contains(source.State.Shadowed.ToList(), "Fallen8:Chat:Ollama:Model");
            }
            finally
            {
                ClearEnvironment(Variable);
            }
        }

        /// <summary>
        ///   WebApplicationFactory delivers every UseSetting value as a command-line argument, so this is
        ///   the case that keeps a stored override from overwriting what a test host asked for.
        /// </summary>
        [TestMethod]
        public void AStoredOverride_LosesToTheCommandLine()
        {
            WriteOverrides(("Fallen8:Plugins:MaxCount", "128"));

            var (root, source) = Build(commandLine: new[] { "--Fallen8:Plugins:MaxCount=512" });

            Assert.AreEqual("512", root["Fallen8:Plugins:MaxCount"]);
            CollectionAssert.Contains(source.State.Shadowed.ToList(), "Fallen8:Plugins:MaxCount");
        }

        /// <summary>
        ///   A host declares intermediate section keys as empty strings (WebApplicationFactory really
        ///   passes <c>--Fallen8=</c> and <c>--Fallen8:Metadata=</c>), so arbitration must probe the exact
        ///   leaf key. A prefix-shaped probe would conclude that everything under Fallen8 was declared
        ///   and this layer would contribute nothing, anywhere, ever.
        /// </summary>
        [TestMethod]
        public void AParentSectionDeclaration_DoesNotSuppressALeafOverride()
        {
            WriteOverrides(("Fallen8:Plugins:MaxCount", "128"));

            var (root, source) = Build(commandLine: new[] { "--Fallen8=", "--Fallen8:Plugins=" });

            Assert.AreEqual("128", root["Fallen8:Plugins:MaxCount"],
                "arbitration probes leaf keys, so a declared parent section must not suppress the leaf");
            Assert.AreEqual(0, source.State.Shadowed.Count);
        }

        #endregion

        #region what the layer refuses to carry

        /// <summary>
        ///   The layer is bounded by the catalog's writable set. The write route cannot produce any other
        ///   key, so a never-writable key in the file was hand-edited in, and honouring it would turn a
        ///   text file into a way around every rule in spec section 4.7.
        /// </summary>
        [TestMethod]
        public void AHandEditedNeverWritableKey_IsRefusedAndReported()
        {
            WriteOverrides(
                ("Fallen8:Security:ApiKey", "smuggled"),
                ("Fallen8:Durability:StorageDirectory", "/somewhere/else"),
                ("Fallen8:Plugins:MaxCount", "128"));

            var (root, source) = Build();

            Assert.IsNull(root["Fallen8:Security:ApiKey"],
                "the overrides layer must never be a way to set an API key");
            Assert.IsNull(root["Fallen8:Durability:StorageDirectory"]);
            Assert.AreEqual("128", root["Fallen8:Plugins:MaxCount"], "the writable key in the same file still applies");

            CollectionAssert.AreEquivalent(
                new[] { "Fallen8:Durability:StorageDirectory", "Fallen8:Security:ApiKey" },
                source.State.Ignored.ToList());
        }

        [TestMethod]
        public void AnUncataloguedKey_IsRefused()
        {
            WriteOverrides(("Fallen8:NoSuch:Key", "value"), ("Logging:LogLevel:Default", "Trace"));

            var (root, source) = Build();

            Assert.IsNull(root["Fallen8:NoSuch:Key"]);
            Assert.IsNull(root["Logging:LogLevel:Default"]);
            Assert.AreEqual(2, source.State.Ignored.Count);
        }

        #endregion

        #region a broken file must not stop a database from starting

        /// <summary>
        ///   A preferences file is not a data pointer. The save-game registry and the namespace catalog
        ///   both throw when their document is corrupt, and that is right for them: each is the sole
        ///   authority for what exists. This file only carries operator preferences, and a provider that
        ///   threw would do so during configuration build, before the logging pipeline exists, leaving
        ///   the instance unbootable with no way to fix it over REST. So a broken file is reported and
        ///   ignored.
        /// </summary>
        [TestMethod]
        public void ACorruptOverridesFile_IsReportedAndIgnoredRatherThanFatal()
        {
            WriteOverrides("{ this is not json");

            var (root, source) = Build(appSettings: new Dictionary<String, String>
            {
                ["Fallen8:Plugins:MaxCount"] = "64"
            });

            Assert.AreEqual("64", root["Fallen8:Plugins:MaxCount"], "the layer below still applies");
            Assert.IsNotNull(source.State.LoadError, "the failure is recorded so boot can report it");
            Assert.AreEqual(0, source.State.Applied.Count);
        }

        [TestMethod]
        public void AFutureFormatVersion_IsRefusedRatherThanGuessedAt()
        {
            WriteOverrides("{ \"version\": 99, \"settings\": { \"Fallen8:Plugins:MaxCount\": \"128\" } }");

            var (root, source) = Build();

            Assert.IsNull(root["Fallen8:Plugins:MaxCount"]);
            Assert.IsNotNull(source.State.LoadError);
            StringAssert.Contains(source.State.LoadError, "99");
        }

        [TestMethod]
        public void AMissingFile_IsTheNormalCaseAndCarriesNoError()
        {
            var (root, source) = Build();

            Assert.IsNull(source.State.LoadError);
            Assert.AreEqual(0, source.State.Applied.Count);
            Assert.IsNull(root["Fallen8:Plugins:MaxCount"]);
        }

        /// <summary>
        ///   An operator editing this file by hand will write <c>128</c> and <c>true</c>, not
        ///   <c>"128"</c>, so a scalar of any JSON type is read verbatim. Configuration is text; the
        ///   binder does the converting.
        /// </summary>
        [TestMethod]
        public void HandWrittenScalars_AreReadVerbatim()
        {
            WriteOverrides("{ \"version\": 1, \"settings\": { "
                + "\"Fallen8:Plugins:MaxCount\": 128, "
                + "\"Fallen8:ChangeFeed:Enabled\": false, "
                + "\"Fallen8:Observability:TracingSamplingRatio\": 0.25, "
                + "\"Fallen8:Nlp:Endpoint\": { \"not\": \"a scalar\" } } }");

            var (root, _) = Build();

            Assert.AreEqual("128", root["Fallen8:Plugins:MaxCount"]);
            Assert.AreEqual("false", root["Fallen8:ChangeFeed:Enabled"]);
            Assert.AreEqual("0.25", root["Fallen8:Observability:TracingSamplingRatio"]);
            Assert.IsNull(root["Fallen8:Nlp:Endpoint"], "an object is not a configuration value");
        }

        #endregion

        #region the read model

        /// <summary>
        ///   Every source the surface can report, resolved against a root shaped like the real one. The
        ///   appsettings case uses a genuine JSON file rather than an in-memory collection, because the
        ///   classification keys off the provider type and an in-memory stand-in would prove nothing.
        /// </summary>
        [TestMethod]
        public void TheReadModel_NamesTheLayerEachValueCameFrom()
        {
            const String Variable = "Fallen8__Analytics__MaxTimeBudgetSeconds";
            try
            {
                WriteOverrides(("Fallen8:Plugins:MaxCount", "128"));

                var settingsFile = Path.Combine(_directory, "appsettings.json");
                File.WriteAllText(settingsFile,
                    "{ \"Fallen8\": { \"BulkIO\": { \"ImportBatchSize\": 500 } } }");

                Environment.SetEnvironmentVariable(Variable, "600");

                var builder = new ConfigurationBuilder();
                builder.AddJsonFile(settingsFile, optional: false);
                builder.AddEnvironmentVariables();
                // An in-process host setting, which is how a test host injects a value.
                builder.AddInMemoryCollection(new Dictionary<String, String>
                {
                    ["Fallen8:Ingestion:MaxPages"] = "7"
                });

                var source = Fallen8ConfigOverridesSource.Resolve(builder.Build(), _directory);
                builder.Add(source);
                var root = builder.Build();

                var model = new Fallen8ConfigOverrides(root, source);

                Assert.AreEqual(Fallen8SettingSource.Override, model.SourceOf("Fallen8:Plugins:MaxCount"));
                Assert.AreEqual(Fallen8SettingSource.Environment, model.SourceOf("Fallen8:Analytics:MaxTimeBudgetSeconds"));
                Assert.AreEqual(Fallen8SettingSource.AppSettings, model.SourceOf("Fallen8:BulkIO:ImportBatchSize"));
                Assert.AreEqual(Fallen8SettingSource.Host, model.SourceOf("Fallen8:Ingestion:MaxPages"),
                    "an in-process host setting is not an authority, because a write can beat it");
                Assert.AreEqual(Fallen8SettingSource.Default, model.SourceOf("Fallen8:StoredQueries:MaxCount"),
                    "a key no layer sets reports the options class's own default");
            }
            finally
            {
                ClearEnvironment(Variable);
            }
        }

        /// <summary>
        ///   The read-only rule the editor applies must match what a write would actually do: exactly the
        ///   two authority sources refuse a write, and every other source accepts one. A mismatch here
        ///   would either show a dead field or claim a locked row is editable.
        /// </summary>
        [TestMethod]
        public void OnlyTheTwoAuthoritySources_MeanAWriteCannotWin()
        {
            const String Variable = "Fallen8__Plugins__MaxCount";
            try
            {
                WriteOverrides(("Fallen8:Plugins:MaxCount", "128"), ("Fallen8:Ingestion:MaxPages", "9"));

                var builder = new ConfigurationBuilder();
                builder.AddInMemoryCollection(new Dictionary<String, String>
                {
                    ["Fallen8:Ingestion:MaxPages"] = "7"
                });
                Environment.SetEnvironmentVariable(Variable, "256");
                builder.AddEnvironmentVariables();

                var source = Fallen8ConfigOverridesSource.Resolve(builder.Build(), _directory);
                builder.Add(source);
                var root = builder.Build();
                var model = new Fallen8ConfigOverrides(root, source);

                // Environment: authority. The stored value lost, and the source says so.
                Assert.AreEqual(Fallen8SettingSource.Environment, model.SourceOf("Fallen8:Plugins:MaxCount"));
                Assert.AreEqual("256", root["Fallen8:Plugins:MaxCount"]);

                // Host: not authority. The stored value won, so the row must not read as locked.
                Assert.AreEqual(Fallen8SettingSource.Override, model.SourceOf("Fallen8:Ingestion:MaxPages"));
                Assert.AreEqual("9", root["Fallen8:Ingestion:MaxPages"]);
            }
            finally
            {
                ClearEnvironment(Variable);
            }
        }

        /// <summary>
        ///   The pending set is derived by comparing against a boot snapshot, so a value that changes
        ///   AFTER the snapshot is pending and a value that was already there at boot is not. This is
        ///   what makes the signal survive a page reload and clear on restart with no stored state.
        /// </summary>
        [TestMethod]
        public void PendingRestart_IsDerivedFromTheBootSnapshot()
        {
            // A restart-tier key on purpose: a live key applies immediately and is therefore never
            // pending, which the live-tier tests assert separately.
            const String Key = "Fallen8:Ingestion:MaxPages";

            WriteOverrides((Key, "128"));
            var (root, source) = Build();

            var model = new Fallen8ConfigOverrides(root, source);

            Assert.AreEqual(0, model.PendingRestart().Count,
                "a value already in force at boot is not pending; the process is using it");
            Assert.AreEqual("128", model.BootValue(Key));

            // A later write, as PATCH /config will do: rewrite the file and reload the root.
            WriteOverrides((Key, "256"));
            root.Reload();

            var pending = model.PendingRestart();
            Assert.AreEqual(1, pending.Count);
            Assert.AreEqual(Key, pending[0].Key);
            Assert.AreEqual("128", model.BootValue(Key), "the snapshot does not move");
            Assert.AreEqual("256", model.CurrentValue(Key));
            Assert.IsTrue(model.IsRestartPending(pending[0]));
        }

        [TestMethod]
        public void ANeverWritableKey_IsNeverPending()
        {
            var (root, source) = Build(appSettings: new Dictionary<String, String>
            {
                ["Fallen8:Security:ApiKey"] = "before"
            });
            var model = new Fallen8ConfigOverrides(root, source);

            Assert.IsTrue(Fallen8SettingCatalog.TryGet("Fallen8:Security:ApiKey", out var entry));
            Assert.IsFalse(model.IsRestartPending(entry),
                "a key that cannot be written can never be waiting for a restart");
        }

        [TestMethod]
        public void TheEnvironmentSpelling_IsWhatAnOperatorHasToRemove()
        {
            Assert.AreEqual("Fallen8__Plugins__MaxCount",
                Fallen8ConfigOverrides.EnvironmentSpelling("Fallen8:Plugins:MaxCount"));
        }

        /// <summary>
        ///   A shadowed key's stored value survives an unrelated write. It contributes nothing while the
        ///   environment outranks it, but it is operator intent waiting for that variable to be removed,
        ///   and a rewrite that rebuilt the file from the applied set alone would silently delete it.
        /// </summary>
        [TestMethod]
        public void AWrite_PreservesTheStoredValueOfAShadowedKey()
        {
            const String Variable = "Fallen8__Chat__TimeoutSeconds";
            try
            {
                WriteOverrides(("Fallen8:Chat:TimeoutSeconds", "60"));
                Environment.SetEnvironmentVariable(Variable, "30");

                var (root, source) = Build(environment: new Dictionary<String, String>
                {
                    [Variable] = "30"
                });
                var model = new Fallen8ConfigOverrides(root, source);
                CollectionAssert.Contains(source.State.Shadowed.ToList(), "Fallen8:Chat:TimeoutSeconds",
                    "precondition: the stored key is shadowed by the environment");

                // An unrelated write rewrites the whole file.
                model.Write(new Dictionary<String, String> { ["Fallen8:Ingestion:MaxPages"] = "250" });

                var file = File.ReadAllText(Path.Combine(_directory, Fallen8ConfigOverridesSource.FileName));
                StringAssert.Contains(file, "Fallen8:Chat:TimeoutSeconds",
                    "the shadowed key is still in the file");
                StringAssert.Contains(file, "60", "with the value the operator stored");
                StringAssert.Contains(file, "Fallen8:Ingestion:MaxPages", "beside the new write");
            }
            finally
            {
                ClearEnvironment(Variable);
            }
        }

        /// <summary>
        ///   An unreadable file refuses writes at the model level too, not only at the route: a rewrite
        ///   starts from what the file holds, and an empty starting set would replace everything the
        ///   unreadable file still contains.
        /// </summary>
        [TestMethod]
        public void AWrite_WhileTheFileIsUnreadable_ThrowsInsteadOfReplacingIt()
        {
            WriteOverrides("{ not json");
            var (root, source) = Build();
            var model = new Fallen8ConfigOverrides(root, source);

            Assert.IsNotNull(source.State.LoadError, "precondition: the load failed");
            Assert.ThrowsException<InvalidOperationException>(() =>
                model.Write(new Dictionary<String, String> { ["Fallen8:Ingestion:MaxPages"] = "250" }));

            Assert.AreEqual("{ not json",
                File.ReadAllText(Path.Combine(_directory, Fallen8ConfigOverridesSource.FileName)),
                "the file is untouched");
        }

        /// <summary>
        ///   A live key whose apply failed is reported as restart-pending by the read surface: the
        ///   stored value is real, but the process is not using it, and a row that kept calling itself
        ///   live would be the wrong-"this-applied" claim this feature exists to remove.
        /// </summary>
        [TestMethod]
        public void ALiveKeyWhoseApplyFailed_IsPublishedAsRestartPending()
        {
            Assert.IsTrue(Fallen8SettingCatalog.TryGet("Fallen8:ChangeFeed:MaxSubscribers", out var live));

            var healthy = NoSQL.GraphDB.App.Controllers.Model.SettingREST.From(live, overrides: null);
            Assert.AreEqual("live", healthy.Tier);
            Assert.AreEqual("liveForNewWork", healthy.ApplyMode);
            Assert.IsFalse(healthy.RestartPending);

            var failed = NoSQL.GraphDB.App.Controllers.Model.SettingREST.From(live, overrides: null,
                effectiveValues: null, applyFailure: "the delegate threw");
            Assert.AreEqual("restart", failed.ApplyMode, "the promise is downgraded to what is true");
            Assert.IsTrue(failed.RestartPending, "and the row reports that a restart is what applies it");
        }

        #endregion
    }
}
