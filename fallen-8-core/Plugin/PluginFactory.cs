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
    ///   is warned at its own call site at build time instead of finding out at runtime. A name that
    ///   resolves to nothing is a clean not-found: <see cref="TryFindPlugin{T}" /> returns
    ///   <c>false</c>. A partially trimmed or malformed assembly is NOT that benign - the guards here
    ///   are narrow (only <c>FileLoadException</c> around the load, only <c>TypeLoadException</c>
    ///   around the activation, with <c>GetExportedTypes</c> and <c>GetInterfaces</c> unguarded), so a
    ///   missing dependency, an exported type whose base type or interface is gone, or a removed
    ///   constructor throws OUT of discovery - and not every caller guards the lookup (index creation,
    ///   the subgraph algorithm load and the engine's cached-plugin resolve do not).
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
            "Plugin discovery scans and loads assemblies and activates types resolved from string names, so a trimmer cannot keep them. Reference the plugin type directly instead - for path finding, the typed TryCalculateShortestPath<T> overload.";

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
        /// </summary>
        private static volatile IReadOnlyList<Type> _candidateTypes;

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
        ///   Returns the memoized candidate types, discovering them once under a lock on first use
        ///   (finding P5). On a discovery failure the cache stays <c>null</c> so the next call retries,
        ///   matching the old per-call behaviour.
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
                return GetCandidateTypesLocked();
            }
        }

        /// <summary>
        ///   The lock-free core of <see cref="GetCandidateTypes" />: discovers the candidate set on
        ///   first use and publishes it. The caller MUST already hold <see cref="_discoveryLock" />.
        ///   Exists so <see cref="GetNameMap{T}" /> can discover candidates and build the derived name
        ///   map under a SINGLE lock acquisition (finding M1), with no re-entrancy.
        /// </summary>
        [RequiresUnreferencedCode(DiscoveryIsNotTrimSafe)]
        private static IReadOnlyList<Type> GetCandidateTypesLocked()
        {
            return _candidateTypes ??= DiscoverCandidateTypes();
        }

        /// <summary>
        ///   The one-time, expensive discovery: enumerate every DLL in the base directory,
        ///   <c>Assembly.Load</c> each and collect its exported, structurally-eligible types (public,
        ///   non-abstract classes with a parameterless constructor). The interface/category filters
        ///   are applied later, per query, in <see cref="GetAllTypes{T}" />.
        /// </summary>
        [RequiresUnreferencedCode(DiscoveryIsNotTrimSafe)]
        private static IReadOnlyList<Type> DiscoverCandidateTypes()
        {
            var result = new List<Type>();

            // Scan the base directory only. Runtime plugins are no longer external assemblies dropped
            // into an extra directory (that upload path was removed - feature plugin-registration);
            // they live as source in the per-namespace registry. The base-directory scan still
            // discovers the BUILT-IN plugins compiled into the shipped assemblies.
            var baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
            foreach (var file in Directory.EnumerateFiles(baseDirectory, "*.dll"))
            {
                result.AddRange(ProcessAFile(file));
            }

            return result;
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

                // Capture the candidate set and build the map under the SAME lock, then store it.
                var map = BuildNameMap<T>(GetCandidateTypesLocked());
                _nameMaps[typeof(T)] = map;
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
        /// name, tolerating a component whose <c>FullName</c> throws).</summary>
        [RequiresUnreferencedCode(DiscoveryIsNotTrimSafe)]
        private static Boolean IsInterfaceOf(Type interfaceType, Type type)
        {
            var interestingInterface = interfaceType.FullName;

            return type.GetInterfaces().Any(i =>
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
        ///   Activate the specified currentPluginType.
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
            catch (TypeLoadException)
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
            catch (FileLoadException)
            {
                yield break;
            }

            var types = assembly.GetExportedTypes();

            foreach (var aType in types)
            {
                if (!aType.IsClass || aType.IsAbstract)
                {
                    continue;
                }

                if (!aType.IsPublic)
                {
                    continue;
                }

                if (aType.GetConstructor(Type.EmptyTypes) == null)
                {
                    continue;
                }

                yield return aType;
            }
        }

        #endregion
    }
}
