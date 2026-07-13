// MIT License
//
// PluginFactory.cs
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
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace NoSQL.GraphDB.Core.Plugin
{
    /// <summary>
    ///   Fallen8 plugin factory.
    /// </summary>
    public static class PluginFactory
    {
        /// <summary>
        /// A TypeEvaluator delegate
        /// </summary>
        /// <param name="type">The to be evaluated type</param>
        /// <returns>True = OK otherwise not</returns>
        public delegate bool TypeEvaluator(Type type);

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
        ///   discovered and is reset to <c>null</c> by <see cref="Assimilate" /> when a new plugin
        ///   assembly is dropped in, so a freshly assimilated plugin is picked up. The interface/
        ///   category filters stay per-query in <see cref="GetAllTypes{T}" /> (they are cheap
        ///   reflection over the cached list, no I/O).
        /// </summary>
        private static volatile IReadOnlyList<Type> _candidateTypes;

        /// <summary>
        ///   Per-category (keyed by the requested plugin interface type) memoized
        ///   <see cref="FrozenDictionary{TKey,TValue}" /> mapping a plugin's <c>PluginName</c> to its
        ///   CLR type, so <see cref="TryFindPlugin{T}" /> resolves a plugin by name in O(1) instead of
        ///   activating candidates one by one until a name matches (finding P5). Cleared alongside
        ///   <see cref="_candidateTypes" /> on <see cref="Assimilate" />.
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
        ///   Tries to find a class.
        /// </summary>
        /// <returns> <c>true</c> if something was found; otherwise, <c>false</c> . </returns>
        /// <param name='result'> Result. </param>
        /// <param name='evaluator'> A type evaluator delegate </param>
        /// <typeparam name='T'> The interface type of the plugin. </typeparam>
        public static Boolean TryFind<T>(out T result, TypeEvaluator evaluator)
        {
            foreach (var aPluginTypeOfT in GetAllTypes<T>(false).Where(_ => evaluator(_)))
            {
                var aPluginInstance = Activator.CreateInstance(aPluginTypeOfT);
                if (aPluginInstance != null)
                {
                    result = (T)aPluginInstance;
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
        public static Boolean TryGetAvailablePlugins<T>(out IEnumerable<String> result)
        {
            result = (from aPluginTypeOfT in GetAllTypes<T>()
                      select Activate<IPlugin>(aPluginTypeOfT)
                      into aPluginInstance
                      where aPluginInstance != null
                      select aPluginInstance.PluginName);
            return result.Any();
        }

        /// <summary>
        /// Assimilate the specified dllStream.
        /// </summary>
        /// <param name='dllStream'>
        /// Dll stream.
        /// </param>
        /// <param name='path'>
        /// The path where the dll should be assimilated.
        /// </param>
        public static String Assimilate(Stream dllStream, String path = null)
        {
            var assimilationPath = path ?? Path.Combine(AppContext.BaseDirectory, Path.GetRandomFileName() + ".dll");

            using (var dllFileStream = File.Create(assimilationPath, 1024))
            {
                dllStream.CopyTo(dllFileStream);
            }

            // A new plugin assembly is now on disk, so the memoized discovery (and every derived
            // name->type map) is stale and must be rebuilt on the next lookup (finding P5).
            InvalidateDiscoveryCache();

            return assimilationPath;
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
        private static IEnumerable<Type> GetAllTypes<T>(Boolean checkForIPlugin = true)
        {
            foreach (var candidate in GetCandidateTypes())
            {
                if (checkForIPlugin && !IsInterfaceOf<IPlugin>(candidate))
                {
                    continue;
                }

                if (!IsInterfaceOf<T>(candidate))
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
        private static IReadOnlyList<Type> GetCandidateTypes()
        {
            var cached = _candidateTypes;
            if (cached != null)
            {
                return cached;
            }

            lock (_discoveryLock)
            {
                return _candidateTypes ??= DiscoverCandidateTypes();
            }
        }

        /// <summary>
        ///   The one-time, expensive discovery: enumerate every DLL in the base directory,
        ///   <c>Assembly.Load</c> each and collect its exported, structurally-eligible types (public,
        ///   non-abstract classes with a parameterless constructor). The interface/category filters
        ///   are applied later, per query, in <see cref="GetAllTypes{T}" />.
        /// </summary>
        private static IReadOnlyList<Type> DiscoverCandidateTypes()
        {
            var result = new List<Type>();

            string currentAssemblyDirectoryName = AppContext.BaseDirectory;

            var files = Directory.EnumerateFiles(currentAssemblyDirectoryName, "*.dll");

            foreach (var file in files)
            {
                result.AddRange(ProcessAFile(file));
            }

            return result;
        }

        /// <summary>
        ///   Builds (and memoizes) the <c>PluginName</c> -> CLR type map for a plugin category, by
        ///   activating each candidate once to read its name. First type wins for a duplicated name,
        ///   matching the old first-match linear scan. An activation that throws is skipped so a
        ///   single malformed plugin cannot break name resolution for the whole category.
        /// </summary>
        private static FrozenDictionary<String, Type> GetNameMap<T>()
            where T : class, IPlugin
        {
            return _nameMaps.GetOrAdd(typeof(T), _ => BuildNameMap<T>());
        }

        private static FrozenDictionary<String, Type> BuildNameMap<T>()
            where T : class, IPlugin
        {
            var map = new Dictionary<String, Type>(StringComparer.Ordinal);

            foreach (var aPluginTypeOfT in GetAllTypes<T>())
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
        private static Boolean IsInterfaceOf<T>(Type type)
        {
            var interestingInterface = typeof(T).FullName;

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
        ///   Activate the specified currentPluginType.
        /// </summary>
        /// <param name='currentPluginType'> Current plugin type. </param>
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
