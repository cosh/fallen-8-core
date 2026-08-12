// MIT License
//
// PluginFactory.cs
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
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Core.Plugins;

namespace NoSQL.GraphDB.Core.Plugin
{
    /// <summary>
    ///   Fallen8 plugin factory.
    ///
    ///   <para>
    ///   NOT TRIM-SAFE, by nature: discovery enumerates the DLLs in the base directory,
    ///   <c>Assembly.Load</c>s them, reads their exported types and activates a type resolved from a
    ///   STRING name. None of that is statically analyzable, so a trimmer cannot know which plugin
    ///   types to keep. Every member that takes part carries
    ///   <see cref="RequiresUnreferencedCodeAttribute" /> with
    ///   <see cref="DiscoveryIsNotTrimSafe" /> - THE home for this explanation - so a trimming consumer
    ///   is warned at its own call site at build time instead of finding out at runtime.
    ///   </para>
    ///
    ///   <para>
    ///   THE DEGRADATION CONTRACT, relied on by every caller that resolves a name without guarding the
    ///   lookup: a broken DEPLOYMENT never becomes a throw at the caller, it yields fewer candidates.
    ///   An unreadable base directory, an assembly that will not load, an assembly whose exported types
    ///   cannot be read, a type whose interface list or constructor cannot be resolved, and a
    ///   constructor that is gone are each SKIPPED, so a partially trimmed or otherwise broken
    ///   deployment resolves a name to a clean not-found - <see cref="TryFindPlugin{T}" /> returns
    ///   <c>false</c> - exactly as an unknown name does.
    ///   </para>
    ///
    ///   <para>
    ///   A skip is not LOGGED here (this type is static and holds no logger), but it is not silent
    ///   either: the one skip an operator cannot diagnose from the outside - a base-directory
    ///   enumeration that failed as a whole, so discovery contributed NOTHING - is recorded
    ///   (<see cref="TryGetDiscoveryDegradation" />) and reported by the resolving factory, which does
    ///   have a logger, at the moment it matters: the not-found (<see cref="LogPluginNotFound" />).
    ///   A per-FILE skip stays unreported - it is one candidate fewer out of a file list that was read,
    ///   and its ordinary cause (a native or otherwise unloadable dll sitting next to the managed ones)
    ///   is neither news nor a fault.
    ///   </para>
    /// </summary>
    public static class PluginFactory
    {
        /// <summary>The single trim-requirement message for every discovery member (see the type
        /// remarks). Named types resolved from scanned assemblies cannot be kept by a trimmer.
        /// <para>PUBLIC so implementers of the annotated interface members outside this assembly can
        /// carry the SAME message instead of a hand-copied paraphrase; the engine declares no
        /// <c>InternalsVisibleTo</c> by decision, so public is the available way to share it.</para></summary>
        public const String DiscoveryIsNotTrimSafe =
            "Plugin discovery scans and loads assemblies and activates types resolved from string names, so a trimmer cannot keep them. Register the plugin type instead - Fallen8.RegisterPluginType<T>() makes it resolvable BY NAME with no scanning, in any host - or reference it directly, as the typed TryCalculateShortestPath<T> overload does for path finding.";

        #region discovery memoization (finding P5)

        /// <summary>
        ///   Guards the one-time discovery of <see cref="_candidateTypes" /> and its invalidation.
        /// </summary>
        private static readonly object _discoveryLock = new object();

        /// <summary>
        ///   The memoized set of structurally-eligible plugin candidate types across every loadable
        ///   assembly in the base directory: public, non-abstract classes with a parameterless
        ///   constructor (finding P5). The expensive part - enumerating the DLLs, <c>Assembly.Load</c>
        ///   on each and <c>GetExportedTypes</c> - was previously repeated on EVERY index/service/
        ///   save/load/path op; it now runs once and is reused. It is <c>null</c> until first
        ///   discovered and is reset to <c>null</c> by <see cref="InvalidateDiscoveryCache" /> to force
        ///   a rediscovery. The interface/category filters stay per-query in
        ///   <see cref="GetAllTypes{T}" /> (they are cheap reflection over the cached list, no I/O).
        ///
        ///   <para>ONLY a COMPLETE enumeration is ever stored here: see
        ///   <see cref="GetCandidateTypesLocked" /> for what "complete" means and why a partial one is
        ///   returned but not memoized.</para>
        /// </summary>
        private static volatile IReadOnlyList<Type> _candidateTypes;

        /// <summary>
        ///   The recorded reason the LAST base-directory enumeration failed as a whole (exception type
        ///   and message), or <c>null</c> when the last one completed. Written under
        ///   <see cref="_discoveryLock" /> by every discovery attempt, so it always describes the most
        ///   recent one; read lock-free through <see cref="TryGetDiscoveryDegradation" /> by a caller
        ///   that has a logger. It exists because this type must not turn a broken deployment into a
        ///   throw and cannot log one itself.
        /// </summary>
        private static volatile String _discoveryDegradation;

        /// <summary>
        ///   Per-category (keyed by the requested plugin interface type) memoized
        ///   <see cref="FrozenDictionary{TKey,TValue}" /> mapping a plugin's <c>PluginName</c> to its
        ///   CLR type, so <see cref="TryFindPlugin{T}" /> resolves a plugin by name in O(1) instead of
        ///   activating candidates one by one until a name matches (finding P5). Cleared alongside
        ///   <see cref="_candidateTypes" /> on <see cref="InvalidateDiscoveryCache" />.
        /// </summary>
        private static readonly ConcurrentDictionary<Type, FrozenDictionary<String, Type>> _nameMaps
            = new ConcurrentDictionary<Type, FrozenDictionary<String, Type>>();

        #endregion

        /// <summary>
        ///   Tries to find a plugin.
        /// </summary>
        /// <returns> <c>true</c> if something was found; otherwise, <c>false</c> . </returns>
        /// <param name='result'> Result. </param>
        /// <param name='name'> The unique name of the pluginN. </param>
        /// <typeparam name='T'> The interface type of the plugin. </typeparam>
        [RequiresUnreferencedCode(DiscoveryIsNotTrimSafe)]
        public static Boolean TryFindPlugin<T>(out T result, String name)
            where T : class, IPlugin
        {
            // Resolve by name through the memoized per-category name->type map (finding P5) instead of
            // activating every candidate one by one until a PluginName matches. The map stores the
            // TYPE, so a fresh instance is still activated per call, exactly as before.
            var nameMap = GetNameMap<T>();

            Type pluginType;
            if (name != null && nameMap.TryGetValue(name, out pluginType))
            {
                var aPluginInstance = Activate<T>(pluginType);
                if (aPluginInstance != null)
                {
                    result = aPluginInstance;
                    return true;
                }
            }

            result = default(T);
            return false;
        }

        /// <summary>
        ///   Tries to get available plugin descriptions.
        /// </summary>
        /// <returns> <c>true</c> if something was found; otherwise, <c>false</c> . </returns>
        /// <param name='result'> Result. </param>
        /// <typeparam name='T'> The interface type of the plugin. </typeparam>
        [RequiresUnreferencedCode(DiscoveryIsNotTrimSafe)]
        public static Boolean TryGetAvailablePluginsWithDescriptions<T>(out Dictionary<String, String> result)
        {
            result = (from aPluginTypeOfT in GetAllTypes<T>()
                      select Activate<IPlugin>(aPluginTypeOfT)
                      into aPluginInstance
                      where aPluginInstance != null
                      select aPluginInstance).ToDictionary(key => key.PluginName, GenerateDescription);
            return result.Any();
        }

        /// <summary>
        ///   Tries to get available plugin descriptions.
        /// </summary>
        /// <returns> <c>true</c> if something was found; otherwise, <c>false</c> . </returns>
        /// <param name='result'> Result. </param>
        /// <typeparam name='T'> The interface type of the plugin. </typeparam>
        [RequiresUnreferencedCode(DiscoveryIsNotTrimSafe)]
        public static Boolean TryGetAvailablePlugins<T>(out IEnumerable<String> result)
        {
            result = (from aPluginTypeOfT in GetAllTypes<T>()
                      select Activate<IPlugin>(aPluginTypeOfT)
                      into aPluginInstance
                      where aPluginInstance != null
                      select aPluginInstance.PluginName);
            return result.Any();
        }

        #region private helper

        /// <summary>
        ///   Generates the description for a plugin
        /// </summary>
        /// <param name="aPluginInstance"> A plugin instance </param>
        /// <returns> </returns>
        private static string GenerateDescription(IPlugin aPluginInstance)
        {
            var sb = new StringBuilder();

            sb.AppendLine(String.Format("NAME: {0}", aPluginInstance.PluginName));
            sb.AppendLine(String.Format("  *DESCRIPTION: {0}", aPluginInstance.Description));
            sb.AppendLine(String.Format("  *MANUFACTURER: {0}", aPluginInstance.Manufacturer));
            sb.AppendLine(String.Format("  *TYPE: {0}", aPluginInstance.GetType().FullName));
            sb.AppendLine(String.Format("  *CATEGORY: {0}", aPluginInstance.PluginCategory.FullName));

            return sb.ToString();
        }

        /// <summary>
        ///   Gets all plugin types of the requested category. The expensive assembly discovery is
        ///   memoized once (see <see cref="GetCandidateTypes" />); this only applies the cheap,
        ///   I/O-free interface/category filters over the cached candidate list, preserving both the
        ///   exact set and the discovery order the old per-call scan produced.
        /// </summary>
        /// <returns> The all types. </returns>
        /// <typeparam name='T'> The type of the plugin. </typeparam>
        [RequiresUnreferencedCode(DiscoveryIsNotTrimSafe)]
        private static IEnumerable<Type> GetAllTypes<T>(Boolean checkForIPlugin = true)
        {
            return FilterTypes<T>(GetCandidateTypes(), checkForIPlugin);
        }

        /// <summary>
        ///   Applies the cheap, I/O-free interface/category filters to a supplied candidate list,
        ///   preserving discovery order. Split out from <see cref="GetCandidateTypes" /> so the
        ///   name-map build can filter a candidate set captured ONCE under <see cref="_discoveryLock" />
        ///   (finding M1) without re-entering that lock.
        /// </summary>
        [RequiresUnreferencedCode(DiscoveryIsNotTrimSafe)]
        private static IEnumerable<Type> FilterTypes<T>(IEnumerable<Type> candidates, Boolean checkForIPlugin = true)
        {
            return FilterTypes(typeof(T), candidates, checkForIPlugin);
        }

        /// <summary>
        ///   The non-generic core of <see cref="FilterTypes{T}" />: filters candidates to those
        ///   implementing <paramref name="contractType" /> (and <see cref="IPlugin" /> unless
        ///   <paramref name="checkForIPlugin" /> is false), preserving discovery order. Used by
        ///   <see cref="AvailableBuiltInNames" />, which knows the contract interface only as a
        ///   runtime <see cref="Type" /> (from <see cref="ContractInterface" />).
        /// </summary>
        [RequiresUnreferencedCode(DiscoveryIsNotTrimSafe)]
        private static IEnumerable<Type> FilterTypes(Type contractType, IEnumerable<Type> candidates, Boolean checkForIPlugin = true)
        {
            foreach (var candidate in candidates)
            {
                if (checkForIPlugin && !IsInterfaceOf(typeof(IPlugin), candidate))
                {
                    continue;
                }

                if (!IsInterfaceOf(contractType, candidate))
                {
                    continue;
                }

                yield return candidate;
            }
        }

        /// <summary>
        ///   Returns the candidate types, discovering them once under a lock on first use (finding P5).
        ///   A discovery whose base-directory enumeration failed as a whole is served to THIS caller but
        ///   not cached, so the next call retries - see <see cref="GetCandidateTypesLocked" /> for why.
        /// </summary>
        [RequiresUnreferencedCode(DiscoveryIsNotTrimSafe)]
        private static IReadOnlyList<Type> GetCandidateTypes()
        {
            var cached = _candidateTypes;
            if (cached != null)
            {
                return cached;
            }

            lock (_discoveryLock)
            {
                return GetCandidateTypesLocked(out _);
            }
        }

        /// <summary>
        ///   The lock-free core of <see cref="GetCandidateTypes" />: discovers the candidate set on
        ///   first use and publishes it. The caller MUST already hold <see cref="_discoveryLock" />.
        ///   Exists so <see cref="GetNameMap{T}" /> can discover candidates and build the derived name
        ///   map under a SINGLE lock acquisition (finding M1), with no re-entrancy.
        ///
        ///   <para>MEMOIZATION IS FOR A COMPLETE ENUMERATION ONLY, and
        ///   <paramref name="complete" /> reports which kind the caller got, because everything derived
        ///   from a partial set (<see cref="_nameMaps" />) must not be cached either. "Complete" means
        ///   the base-directory FILE LIST was read to the end; the per-file skips of the degradation
        ///   contract are complete-compatible, since a skipped file is a verdict on a file that WAS
        ///   seen and its ordinary cause (an unloadable dll among the managed ones) is permanent. A
        ///   directory-level failure is different in kind: the candidate set is not smaller, it is
        ///   unknown. Memoizing it would turn one transient failure - the directory changing under a
        ///   lazy enumeration, a hold on the path - into a plugin set that stays wrong for the life of
        ///   the process, with the plugins that do exist never resolving again. Retrying instead costs
        ///   one enumeration attempt per discovery call, which is exactly what every call did before
        ///   the memoization existed, and is bounded in a host where the failure is permanent (a
        ///   browser, where there is no directory at all) because a registered type resolves through
        ///   the registry without ever asking discovery.</para>
        /// </summary>
        [RequiresUnreferencedCode(DiscoveryIsNotTrimSafe)]
        private static IReadOnlyList<Type> GetCandidateTypesLocked(out Boolean complete)
        {
            var cached = _candidateTypes;
            if (cached != null)
            {
                complete = true;
                return cached;
            }

            var discovered = DiscoverCandidateTypes(out complete);

            if (complete)
            {
                _candidateTypes = discovered;
            }

            return discovered;
        }

        /// <summary>
        ///   The one-time, expensive discovery: enumerate every DLL in the base directory,
        ///   <c>Assembly.Load</c> each and collect its exported, structurally-eligible types (public,
        ///   non-abstract classes with a parameterless constructor). The interface/category filters
        ///   are applied later, per query, in <see cref="GetAllTypes{T}" />.
        ///
        ///   <para><paramref name="complete" /> is false when the enumeration itself failed, which is
        ///   what makes the result unfit for memoization (see <see cref="GetCandidateTypesLocked" />).
        ///   The caller MUST hold <see cref="_discoveryLock" />: this also writes
        ///   <see cref="_discoveryDegradation" />.</para>
        /// </summary>
        [RequiresUnreferencedCode(DiscoveryIsNotTrimSafe)]
        private static IReadOnlyList<Type> DiscoverCandidateTypes(out Boolean complete)
        {
            var result = new List<Type>();
            complete = true;

            // Scan the base directory only. Runtime plugins are no longer external assemblies dropped
            // into an extra directory (that upload path was removed - feature plugin-registration);
            // they live as source in the per-namespace registry. The base-directory scan still
            // discovers the BUILT-IN plugins compiled into the shipped assemblies.
            try
            {
                var baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
                foreach (var file in Directory.EnumerateFiles(baseDirectory, "*.dll"))
                {
                    result.AddRange(ProcessAFile(file));
                }

                _discoveryDegradation = null;
            }
            catch (Exception ex) when (IsDeploymentFailure(ex))
            {
                // The DIRECTORY side of the degradation contract: a base directory that does not exist,
                // cannot be listed, or is not a filesystem path at all (browser WebAssembly) yields no
                // candidates instead of a fault. An unusable single FILE never reaches here - it is
                // skipped inside ProcessAFile - so whatever was already collected stays collected and
                // is returned to this caller; it is the CACHING of it that is refused above.
                complete = false;
                _discoveryDegradation = ex.GetType().Name + ": " + ex.Message;
            }

            return result;
        }

        /// <summary>
        ///   Whether the most recent base-directory enumeration failed as a whole, and with what - the
        ///   fact behind <see cref="LogPluginNotFound" />, exposed on its own so a host that wants to
        ///   surface the state of its deployment (rather than wait for a not-found) can read it.
        ///   <c>false</c> means the last enumeration was read to the end; per-file skips are not
        ///   reported here (see the type remarks).
        /// </summary>
        public static Boolean TryGetDiscoveryDegradation(out String reason)
        {
            reason = _discoveryDegradation;
            return reason != null;
        }

        /// <summary>
        ///   THE diagnostic for a plugin name that resolved through neither the per-namespace registry
        ///   nor discovery: it names the degradation when discovery could not read the base directory at
        ///   all, so an operator is told that a "not found" may be a broken deployment rather than a
        ///   wrong name. One home for the message because both resolving factories need exactly it, and
        ///   the caller supplies the logger because this type is static and has none.
        /// </summary>
        /// <param name="logger">The resolving factory's logger.</param>
        /// <param name="family">The plugin family, for the message: <c>"index"</c>, <c>"service"</c>.</param>
        /// <param name="pluginName">The name that resolved nowhere.</param>
        public static void LogPluginNotFound(ILogger logger, String family, String pluginName)
        {
            if (logger == null)
            {
                return;
            }

            if (TryGetDiscoveryDegradation(out var reason))
            {
                logger.LogError(
                    "Could not find {Family} plugin with name \"{PluginName}\". Plugin discovery could not read the base directory ({Reason}), so it contributed no candidates at all and a name it would otherwise resolve looks unknown; register the type with Fallen8.RegisterPluginType<T>() to resolve it without discovery.",
                    family, pluginName, reason);
                return;
            }

            logger.LogError("Could not find {Family} plugin with name \"{PluginName}\".", family, pluginName);
        }

        /// <summary>
        ///   Whether an exception is a broken-DEPLOYMENT failure, which the degradation contract (see
        ///   the type remarks) turns into "one candidate fewer": a file, assembly, type or member that
        ///   cannot be read or resolved. THE one home for that set, so every guard in this type skips
        ///   exactly the same failures.
        ///
        ///   <para>A plugin's OWN exception is deliberately not in the set: a constructor that throws
        ///   surfaces as <see cref="System.Reflection.TargetInvocationException" /> from
        ///   <c>Activator.CreateInstance</c>, so it is never mistaken for a deployment problem.</para>
        /// </summary>
        private static Boolean IsDeploymentFailure(Exception ex)
        {
            return ex is IOException                    // unreadable path, incl. FileNotFound/FileLoad
                || ex is UnauthorizedAccessException
                || ex is BadImageFormatException        // not a managed assembly
                || ex is ReflectionTypeLoadException    // the exported type list cannot be read
                || ex is TypeLoadException              // a base type, interface or member is gone
                || ex is MissingMemberException         // the constructor is gone (Arg_NoDefCTor)
                || ex is NotSupportedException
                || ex is ArgumentException;             // a path or assembly name the runtime rejects
        }

        /// <summary>
        ///   The ACTIVATION subset of <see cref="IsDeploymentFailure" />: everything in that set except
        ///   <see cref="ArgumentException" /> and <see cref="NotSupportedException" />. Expressed as an
        ///   exclusion so the set itself keeps one home.
        ///
        ///   <para>Those two mean something different at <c>Activator.CreateInstance</c> than they mean
        ///   at a path or an assembly name: not a deployment the trimmer took apart, but a type that
        ///   cannot be constructed at all (an open generic, a non-runtime type) - a bug in the plugin or
        ///   in the call. Swallowing them would answer "no such plugin" to a question that had nothing
        ///   to do with the name, and the bug would never be diagnosable. Discovery cannot walk into
        ///   this: <see cref="IsEligibleCandidate" /> only offers types that CAN be constructed.</para>
        /// </summary>
        private static Boolean IsActivationDeploymentFailure(Exception ex)
        {
            return IsDeploymentFailure(ex) && ex is not ArgumentException && ex is not NotSupportedException;
        }

        /// <summary>
        ///   Returns the memoized <c>PluginName</c> -> CLR type map for a plugin category, building it
        ///   on first use. The build AND its store run under <see cref="_discoveryLock" /> - the same
        ///   lock that guards candidate discovery and <see cref="InvalidateDiscoveryCache" /> (finding
        ///   M1). Serializing them means a concurrent <see cref="Assimilate" /> invalidation can never
        ///   be overtaken by the store of a map built from the pre-invalidation candidate set: the
        ///   invalidation either completes before this acquisition (so the rebuild rediscovers the
        ///   fresh set) or after it releases (so its <c>Clear</c> drops this map and the next lookup
        ///   rebuilds). The candidate set is captured once here and passed into the build, so the build
        ///   never re-enters the lock.
        ///
        ///   <para>A map built from an INCOMPLETE candidate set is returned but not stored, for the same
        ///   reason the candidate set itself is not (see <see cref="GetCandidateTypesLocked" />):
        ///   storing it would make a transient directory failure permanent through the derived cache
        ///   even though the set behind it was refused.</para>
        /// </summary>
        [RequiresUnreferencedCode(DiscoveryIsNotTrimSafe)]
        private static FrozenDictionary<String, Type> GetNameMap<T>()
            where T : class, IPlugin
        {
            // Fast path: an already-built map. ConcurrentDictionary reads need no lock; only the
            // build/store below - and invalidation - are serialized on _discoveryLock.
            if (_nameMaps.TryGetValue(typeof(T), out var cached))
            {
                return cached;
            }

            lock (_discoveryLock)
            {
                // Re-check under the lock: another thread may have built it while we waited.
                if (_nameMaps.TryGetValue(typeof(T), out cached))
                {
                    return cached;
                }

                // Capture the candidate set and build the map under the SAME lock, then store it -
                // unless the set was partial, in which case the map is served and forgotten.
                var map = BuildNameMap<T>(GetCandidateTypesLocked(out var complete));

                if (complete)
                {
                    _nameMaps[typeof(T)] = map;
                }

                return map;
            }
        }

        /// <summary>
        ///   Builds the <c>PluginName</c> -> CLR type map for a plugin category from a supplied
        ///   candidate set, by activating each candidate once to read its name. First type wins for a
        ///   duplicated name, matching the old first-match linear scan. An activation that throws is
        ///   skipped so a single malformed plugin cannot break name resolution for the whole category.
        /// </summary>
        [RequiresUnreferencedCode(DiscoveryIsNotTrimSafe)]
        private static FrozenDictionary<String, Type> BuildNameMap<T>(IReadOnlyList<Type> candidates)
            where T : class, IPlugin
        {
            var map = new Dictionary<String, Type>(StringComparer.Ordinal);

            foreach (var aPluginTypeOfT in FilterTypes<T>(candidates))
            {
                T instance;
                try
                {
                    instance = Activate<T>(aPluginTypeOfT);
                }
                catch (Exception)
                {
                    continue;
                }

                if (instance == null)
                {
                    continue;
                }

                var pluginName = instance.PluginName;
                if (pluginName != null && !map.ContainsKey(pluginName))
                {
                    map[pluginName] = aPluginTypeOfT;
                }
            }

            return map.ToFrozenDictionary(StringComparer.Ordinal);
        }

        /// <summary>
        ///   Drops the memoized discovery and all derived name maps, forcing a re-scan on next use.
        ///   Retained diagnostic/test hook: it has no runtime caller since the DLL-drop path
        ///   (<c>Assimilate</c>) was removed with the plugin-upload endpoint (feature
        ///   plugin-registration) - built-ins never change at runtime - but it is the primitive the
        ///   memoization's invalidate → fresh-name-map-rebuild invariant (finding M1) is pinned against
        ///   in <c>EnginePerformanceTest</c>.
        /// </summary>
        private static void InvalidateDiscoveryCache()
        {
            lock (_discoveryLock)
            {
                _candidateTypes = null;
                _nameMaps.Clear();
                _discoveryDegradation = null;
            }
        }

        /// <summary>
        ///   Determines whether a type is interface of the specified type.
        /// </summary>
        /// <returns> <c>true</c> if this instance is interface of the specified type; otherwise, <c>false</c> . </returns>
        /// <param name='type'> Type. </param>
        /// <typeparam name='T'> The interface type. </typeparam>
        [RequiresUnreferencedCode(DiscoveryIsNotTrimSafe)]
        private static Boolean IsInterfaceOf<T>(Type type)
        {
            return IsInterfaceOf(typeof(T), type);
        }

        /// <summary>The non-generic core of <see cref="IsInterfaceOf{T}" />: whether
        /// <paramref name="type" /> implements <paramref name="interfaceType" /> (matched by full
        /// name, tolerating a component whose <c>FullName</c> throws, and answering false for a type
        /// whose interface list cannot be resolved at all - degradation contract).</summary>
        [RequiresUnreferencedCode(DiscoveryIsNotTrimSafe)]
        private static Boolean IsInterfaceOf(Type interfaceType, Type type)
        {
            var interestingInterface = interfaceType.FullName;

            Type[] implemented;

            try
            {
                implemented = type.GetInterfaces();
            }
            catch (Exception ex) when (IsDeploymentFailure(ex))
            {
                return false;
            }

            return implemented.Any(i =>
            {
                String fullNameOfInterface = null;

                try
                {
                    fullNameOfInterface = i.FullName;
                }
                catch (Exception)
                {
                }

                return fullNameOfInterface != null && fullNameOfInterface.Equals(interestingInterface);
            });
        }

        /// <summary>
        ///   THE contract-to-CLR-interface map (consolidation-audit CA-13): the single home that
        ///   resolves a <see cref="PluginContract" /> to the engine interface a plugin of that
        ///   contract implements, or <c>null</c> for an unknown contract. Every site that needs the
        ///   built-in set for a contract - the plugin compiler's contract check, the
        ///   register-time built-in-name collision guard, the <c>/status</c> discovery union, and
        ///   the subgraph plugin list - resolves through here (directly for the Type, or via
        ///   <see cref="AvailableBuiltInNames" /> for the names), so adding a contract updates one
        ///   switch instead of four.
        /// </summary>
        public static Type ContractInterface(PluginContract contract)
        {
            switch (contract)
            {
                case PluginContract.Path:
                    return typeof(NoSQL.GraphDB.Core.Algorithms.Path.IShortestPathAlgorithm);
                case PluginContract.SubGraph:
                    return typeof(NoSQL.GraphDB.Core.Algorithms.SubGraph.ISubGraphAlgorithm);
                case PluginContract.Analytics:
                    return typeof(NoSQL.GraphDB.Core.Algorithms.Analytics.IGraphAnalyticsAlgorithm);
                case PluginContract.GraphFunction:
                    return typeof(IGraphFunction);
                case PluginContract.Index:
                    return typeof(NoSQL.GraphDB.Core.Index.IIndex);
                case PluginContract.Service:
                    return typeof(NoSQL.GraphDB.Core.Service.IService);
                default:
                    return null;
            }
        }

        /// <summary>
        ///   The built-in plugin names for a contract, discovered by reflection exactly as
        ///   <see cref="TryGetAvailablePlugins{T}" /> does but resolving the interface at runtime
        ///   through <see cref="ContractInterface" /> (consolidation-audit CA-13). Same candidate
        ///   list and filter as the generic path, so the set and order are identical. An unknown
        ///   contract (or one with no built-in implementation, e.g. GraphFunction) yields empty.
        /// </summary>
        [RequiresUnreferencedCode(DiscoveryIsNotTrimSafe)]
        public static IEnumerable<String> AvailableBuiltInNames(PluginContract contract)
        {
            var contractType = ContractInterface(contract);
            if (contractType == null)
            {
                return Enumerable.Empty<String>();
            }

            return (from candidate in FilterTypes(contractType, GetCandidateTypes())
                    select Activate<IPlugin>(candidate)
                    into instance
                    where instance != null
                    select instance.PluginName);
        }

        /// <summary>
        ///   THE UNION RULE for every "what plugins can I name here?" surface: the discovered built-ins
        ///   (<see cref="AvailableBuiltInNames" />) followed by the addressed namespace's registered
        ///   plugins of that contract (<see cref="PluginRegistry.NamesForContract" />), de-duplicated,
        ///   built-in order first. A registered plugin must be DISCOVERABLE, not merely
        ///   invocable-by-name (feature plugin-registration §4.4), and a host-registered type is often
        ///   the only way a name resolves at all - in a browser or trimmed host discovery contributes
        ///   nothing, so a list of built-ins alone would advertise an empty set while every registered
        ///   name works.
        ///
        ///   <para>De-duplication is not cosmetic: a registered plugin may deliberately SHADOW a
        ///   built-in of the same name (resolution is registry-first), and one name must then appear
        ///   once.</para>
        /// </summary>
        /// <param name="contract">The plugin contract to list.</param>
        /// <param name="registry">
        ///   The addressed namespace's registry, or <c>null</c> for built-ins only - which is what a
        ///   caller reading a disposed engine's factory gets, so it is answered rather than thrown at.
        /// </param>
        [RequiresUnreferencedCode(DiscoveryIsNotTrimSafe)]
        public static IEnumerable<String> AvailablePluginNames(PluginContract contract, PluginRegistry registry)
        {
            IEnumerable<String> result = AvailableBuiltInNames(contract);

            if (registry != null)
            {
                result = result.Concat(registry.NamesForContract(contract)).Distinct(StringComparer.Ordinal);
            }

            return result.ToList();
        }

        /// <summary>
        ///   Activate the specified currentPluginType. A broken deployment yields no instance
        ///   (<see cref="IsActivationDeploymentFailure" /> - which is deliberately narrower than the
        ///   set the file/type-reading guards use); anything else, including the plugin constructor's
        ///   own exception, propagates.
        /// </summary>
        /// <param name='currentPluginType'> Current plugin type. </param>
        [RequiresUnreferencedCode(DiscoveryIsNotTrimSafe)]
        internal static T Activate<T>(Type currentPluginType)
            where T : class
        {
            object instance;

            try
            {
                instance = Activator.CreateInstance(currentPluginType, false);
            }
            catch (Exception ex) when (IsActivationDeploymentFailure(ex))
            {
                return default(T);
            }

            return instance as T;
        }

        /// <summary>
        /// Loads one assembly and yields its exported, structurally-eligible candidate types
        /// (public, non-abstract classes with a parameterless constructor). The category/interface
        /// filters are applied later in <see cref="GetAllTypes{T}" />.
        /// </summary>
        /// <param name="file">The interesting file</param>
        /// <returns>Enumerable of candidate types</returns>
        [RequiresUnreferencedCode(DiscoveryIsNotTrimSafe)]
        private static IEnumerable<Type> ProcessAFile(string file)
        {
            Assembly assembly;

            try
            {
                AssemblyName assemblyName = new AssemblyName(Path.GetFileNameWithoutExtension(file));
                assembly = Assembly.Load(assemblyName);
            }
            catch (Exception ex) when (IsDeploymentFailure(ex))
            {
                yield break;
            }

            Type[] types;

            try
            {
                types = assembly.GetExportedTypes();
            }
            catch (Exception ex) when (IsDeploymentFailure(ex))
            {
                // An exported type whose base type or interface was trimmed out (or whose defining
                // dependency is missing) makes the WHOLE list unreadable, so this assembly contributes
                // no candidates rather than failing the scan (degradation contract).
                yield break;
            }

            foreach (var aType in types)
            {
                if (IsEligibleCandidate(aType))
                {
                    yield return aType;
                }
            }
        }

        /// <summary>
        ///   The structural candidate filter: a public, non-abstract, CLOSED class with a parameterless
        ///   constructor - i.e. a type <see cref="Activate{T}" /> can actually construct. A type whose
        ///   constructor list cannot be resolved is not a candidate (degradation contract). Split out of
        ///   <see cref="ProcessAFile" /> because a guard cannot wrap a <c>yield return</c>.
        /// </summary>
        [RequiresUnreferencedCode(DiscoveryIsNotTrimSafe)]
        private static Boolean IsEligibleCandidate(Type candidate)
        {
            // ContainsGenericParameters: a generic type DEFINITION passes every check below (it is a
            // public class with a parameterless constructor) but no instance of it can exist without
            // type arguments, and a plugin resolved by name has nowhere to get them. Rejecting it here
            // is what keeps "a bad call" out of Activate, where it is no longer swallowed
            // (see IsActivationDeploymentFailure).
            if (!candidate.IsClass || candidate.IsAbstract || !candidate.IsPublic || candidate.ContainsGenericParameters)
            {
                return false;
            }

            try
            {
                return candidate.GetConstructor(Type.EmptyTypes) != null;
            }
            catch (Exception ex) when (IsDeploymentFailure(ex))
            {
                return false;
            }
        }

        #endregion
    }
}
