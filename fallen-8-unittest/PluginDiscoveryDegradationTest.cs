// MIT License
//
// PluginDiscoveryDegradationTest.cs
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
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Plugin;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Pins the DEGRADATION CONTRACT documented on <see cref="PluginFactory"/>: a broken deployment
    ///   makes discovery yield fewer candidates, never throw at a caller. It is load-bearing rather than
    ///   nice-to-have, because it is the whole justification for the trim suppressions in
    ///   <c>IndexFactory</c> / <c>ServiceFactory</c> (feature host-plugin-registration): those callers
    ///   resolve a plugin name WITHOUT guarding the lookup, and they may do that only because a
    ///   deployment that a trimmer took apart resolves to a clean not-found.
    ///
    ///   <para>These tests reach into PROCESS-WIDE state - the memoized discovery caches, a file in
    ///   <see cref="AppContext.BaseDirectory"/>, and (for the retry pin) the base-directory data slot
    ///   itself - because that is where the contract lives; there is no per-instance seam to inject.
    ///   Every test restores what it changed in a <c>finally</c> and invalidates the caches on the way
    ///   out, so the next test rediscovers from a clean state. What they cannot survive is running
    ///   CONCURRENTLY with a test that resolves a plugin by name, hence
    ///   <see cref="DoNotParallelizeAttribute"/> on the class: the suite is sequential today (no
    ///   <c>[Parallelize]</c> anywhere), and this attribute is what keeps these tests correct if that
    ///   ever changes, instead of leaving them silently dependent on it.</para>
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class PluginDiscoveryDegradationTest
    {
        #region reflected seams

        /// <summary>Reflects the internal activation helper (the engine declares no
        /// <c>InternalsVisibleTo</c>, so this suite reflects rather than widening visibility).</summary>
        private static MethodInfo ActivateMethod(Type contract)
        {
            return typeof(PluginFactory)
                .GetMethod("Activate", BindingFlags.NonPublic | BindingFlags.Static)
                .MakeGenericMethod(contract);
        }

        private static object Activate(Type contract, Type candidate)
        {
            try
            {
                return ActivateMethod(contract).Invoke(null, new object[] { candidate });
            }
            catch (TargetInvocationException ex)
            {
                throw new AssertFailedException(
                    "Activating a candidate whose parameterless constructor is missing must degrade to no " +
                    "instance, not throw: a trimmer removing that constructor is the exact deployment the " +
                    "contract covers. Got " + ex.InnerException, ex.InnerException);
            }
        }

        /// <summary>The exception <c>Activate</c> let through, unwrapped from the reflection wrapper.</summary>
        private static Exception ActivationFault(Type contract, Type candidate)
        {
            try
            {
                ActivateMethod(contract).Invoke(null, new object[] { candidate });
                return null;
            }
            catch (TargetInvocationException ex)
            {
                return ex.InnerException;
            }
        }

        private static bool IsEligibleCandidate(Type candidate)
        {
            return (bool)typeof(PluginFactory)
                .GetMethod("IsEligibleCandidate", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { candidate });
        }

        private static IReadOnlyList<Type> ProcessAFile(string file)
        {
            var types = (IEnumerable<Type>)typeof(PluginFactory)
                .GetMethod("ProcessAFile", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { file });
            return types.ToList();
        }

        private static void InvalidateDiscoveryCache()
        {
            typeof(PluginFactory)
                .GetMethod("InvalidateDiscoveryCache", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, null);
        }

        /// <summary>The memoized candidate list, or null while nothing is memoized.</summary>
        private static object MemoizedCandidates()
        {
            return typeof(PluginFactory)
                .GetField("_candidateTypes", BindingFlags.NonPublic | BindingFlags.Static)
                .GetValue(null);
        }

        private const string BaseDirectoryKey = "APP_CONTEXT_BASE_DIRECTORY";

        #endregion

        [TestMethod]
        public void Activation_OfACandidateWithoutAParameterlessConstructor_YieldsNoInstance()
        {
            Assert.IsNull(Activate(typeof(IPlugin), typeof(ConstructorlessPlugin)),
                "no instance, and no exception escaping to the caller");
        }

        [TestMethod]
        public void Activation_OfATypeThatCannotBeConstructedAtAll_Throws_RatherThanLookingLikeANotFound()
        {
            // The boundary of the contract: "a broken deployment yields one candidate fewer" covers a
            // file, assembly, type or member that cannot be READ. An open generic type is none of those
            // - it is a type no instance of can exist, i.e. a bug in the plugin or in the call - and
            // Activator reports it as ArgumentException. Swallowing that would answer "no such plugin"
            // to a question that had nothing to do with the name, leaving the bug undiagnosable.
            var fault = ActivationFault(typeof(object), typeof(OpenGenericCandidate<>));

            Assert.IsInstanceOfType(fault, typeof(ArgumentException),
                "an uninstantiable type must surface as the ArgumentException Activator raises, not as a null instance");
        }

        [TestMethod]
        public void Discovery_NeverOffersATypeThatCannotBeConstructed()
        {
            // The other half of the decision above: because Activate no longer swallows "cannot be
            // constructed", the structural filter must not hand it such a type. An open generic class
            // passes every other structural check (public, non-abstract, parameterless constructor).
            Assert.IsTrue(IsEligibleCandidate(typeof(ClosedCandidate)),
                "the control: an ordinary public class with a parameterless constructor IS a candidate");
            Assert.IsFalse(IsEligibleCandidate(typeof(OpenGenericCandidate<>)),
                "a generic type DEFINITION cannot be instantiated by name, so discovery must not offer it");
        }

        [TestMethod]
        public void Discovery_SkipsADllTheRuntimeCannotLoad_AndStillResolvesTheBuiltIns()
        {
            // The unloadable-dll half of the contract, exercised the only way it can be without a
            // trimmer: an extra .dll in the base directory that the runtime refuses to load. It is
            // refused because Assembly.Load resolves by NAME (this file is in no assembly resolution
            // list), not because its bytes are malformed - the skip is the same either way, and this is
            // the one the test can actually produce.
            var bogus = Path.Combine(AppContext.BaseDirectory, "f8-not-an-assembly-" + Guid.NewGuid().ToString("N") + ".dll");
            File.WriteAllText(bogus, "this is not an assembly");

            try
            {
                // The file-level seam directly: it yields no candidates and does not throw.
                CollectionAssert.AreEqual(Array.Empty<Type>(), ProcessAFile(bogus).ToArray(),
                    "a dll the runtime will not load contributes no candidates instead of failing the scan");

                // And through the whole scan, which now has that file in its enumeration. Discovery is
                // memoized process-wide, so the cache is reset around the file's lifetime - the same
                // reflected hook EnginePerformanceTest uses for the invalidate/rebuild pin.
                InvalidateDiscoveryCache();

                Assert.IsTrue(PluginFactory.TryFindPlugin<IIndex>(out var index, "DictionaryIndex"),
                    "one unloadable file must not stop the scan from resolving the assemblies that DO load");
                Assert.AreEqual("DictionaryIndex", index.PluginName);
                Assert.IsFalse(PluginFactory.TryFindPlugin<IIndex>(out _, "NoSuchIndexPlugin"),
                    "and an unknown name is still the same clean not-found");

                Assert.IsFalse(PluginFactory.TryGetDiscoveryDegradation(out _),
                    "a per-FILE skip is not a degraded discovery: the file list was read to the end");
            }
            finally
            {
                File.Delete(bogus);
                InvalidateDiscoveryCache();
            }
        }

        [TestMethod]
        public void Discovery_WhoseDirectoryCannotBeRead_IsRecorded_AndIsNeverMemoized()
        {
            // The DIRECTORY half of the contract, and the memoization rule that goes with it. A whole
            // enumeration that failed is a fact about one moment (a hold on the path, the directory
            // changing under a lazy enumeration, no filesystem at all), so it must NOT be cached as if
            // it were the deployment's plugin set: caching it would make every plugin that does exist
            // unresolvable for the life of the process, with no way back.
            var original = AppContext.GetData(BaseDirectoryKey);
            var realBaseDirectory = AppContext.BaseDirectory;
            var missing = Path.Combine(Path.GetTempPath(), "f8-no-such-basedir-" + Guid.NewGuid().ToString("N"))
                          + Path.DirectorySeparatorChar;

            try
            {
                InvalidateDiscoveryCache();
                AppContext.SetData(BaseDirectoryKey, missing);
                Assert.AreEqual(missing, AppContext.BaseDirectory,
                    "the test needs the base directory to be redirectable; without that it cannot reach this arm");

                Assert.IsFalse(PluginFactory.TryFindPlugin<IIndex>(out _, "DictionaryIndex"),
                    "an unreadable base directory degrades to a clean not-found instead of throwing");
                Assert.IsNull(MemoizedCandidates(),
                    "a discovery that never got the file list must not be memoized as the candidate set");
                Assert.IsTrue(PluginFactory.TryGetDiscoveryDegradation(out var reason),
                    "it is recorded instead of being silent, so a caller with a logger can report it");
                StringAssert.Contains(reason, "DirectoryNotFound",
                    "the record names the failure, which is what makes it worth logging");

                // The directory is readable again - WITHOUT invalidating anything. The next call must
                // rediscover, both for the candidate set and for the derived per-contract name map.
                AppContext.SetData(BaseDirectoryKey, realBaseDirectory);

                Assert.IsTrue(PluginFactory.TryFindPlugin<IIndex>(out var index, "DictionaryIndex"),
                    "the next call retries: a transient directory failure must not poison discovery for the process");
                Assert.AreEqual("DictionaryIndex", index.PluginName);
                Assert.IsFalse(PluginFactory.TryGetDiscoveryDegradation(out _),
                    "and the record clears once an enumeration completes, so it never reports stale bad news");
            }
            finally
            {
                AppContext.SetData(BaseDirectoryKey, original);
                InvalidateDiscoveryCache();
            }
        }

        [TestMethod]
        public void ADegradedDiscovery_IsReportedByTheFactoryThatResolvedTheName()
        {
            // The honest half of "a skip is not logged": the static factory cannot log, so the caller
            // that CAN reports it, at the not-found. Without this an operator staring at "could not find
            // index plugin DictionaryIndex" has no way to tell a typo from a deployment whose plugin
            // directory was unreadable at that moment.
            var original = AppContext.GetData(BaseDirectoryKey);
            var missing = Path.Combine(Path.GetTempPath(), "f8-no-such-basedir-" + Guid.NewGuid().ToString("N"))
                          + Path.DirectorySeparatorChar;
            var sink = new TestLogSink();
            var engine = new Fallen8(sink.CreateFactory());

            try
            {
                InvalidateDiscoveryCache();
                AppContext.SetData(BaseDirectoryKey, missing);

                Assert.IsFalse(engine.IndexFactory.TryCreateIndex(out _, "idx", "DictionaryIndex"),
                    "nothing is registered and nothing is discoverable, so a built-in name resolves nowhere");

                Assert.IsTrue(sink.Contains(Microsoft.Extensions.Logging.LogLevel.Error,
                        "DictionaryIndex", "could not read the base directory"),
                    "the not-found names the degradation instead of blaming the name");
            }
            finally
            {
                AppContext.SetData(BaseDirectoryKey, original);
                InvalidateDiscoveryCache();
                engine.Dispose();
            }
        }
    }

    /// <summary>Stands in for a type whose parameterless constructor a trimmer removed: there is only
    /// one constructor and it takes an argument.</summary>
    internal sealed class ConstructorlessPlugin : IPlugin
    {
        public ConstructorlessPlugin(int unused)
        {
            GC.KeepAlive(unused);
        }

        public string PluginName => "Constructorless";
        public Type PluginCategory => typeof(IPlugin);
        public string Description => "d";
        public string Manufacturer => "test";
        public void Initialize(IFallen8 fallen8, IDictionary<string, object> parameter) { }
        public void Dispose() { }
    }

    /// <summary>A public, non-abstract class with a parameterless constructor: the shape the structural
    /// candidate filter accepts. Deliberately NOT an <see cref="IPlugin"/> - it is here to be measured
    /// against <see cref="OpenGenericCandidate{T}"/>, not to be discovered as a plugin.</summary>
    public sealed class ClosedCandidate
    {
    }

    /// <summary>The same shape, but a generic type DEFINITION: structurally eligible on every other
    /// count and impossible to instantiate.</summary>
    public sealed class OpenGenericCandidate<T>
    {
    }
}
