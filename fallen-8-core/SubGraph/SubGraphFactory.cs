// MIT License
//
// SubGraphFactory.cs
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
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Core.Algorithms.SubGraph;
using NoSQL.GraphDB.Core.Cache;
using NoSQL.GraphDB.Core.Plugin;

namespace NoSQL.GraphDB.Core.SubGraph
{
    /// <summary>
    /// Factory for creating and managing subgraphs.
    /// </summary>
    public sealed class SubGraphFactory
    {
        #region Data

        /// <summary>
        /// The created subgraphs.
        /// </summary>
        private readonly ConcurrentDictionary<String, SubGraphResult> _subGraphsByName;

        private readonly ConcurrentDictionary<Guid, SubGraphResult> _subGraphsById;

        private readonly ConcurrentDictionary<Guid, Guid> _subGraphDependencies;

        /// <summary>
        /// The logger
        /// </summary>
        private readonly ILogger<SubGraphFactory> _logger;

        /// <summary>
        /// The Fallen-8 instance
        /// </summary>
        private readonly Fallen8 _fallen8;

        /// <summary>
        /// The plugin cache
        /// </summary>
        private readonly PluginCache _pluginCache;

        /// <summary>
        /// Resource ceilings enforced at creation time. Generous but bounded by default (M6).
        /// </summary>
        private SubGraphQuota _quota = new SubGraphQuota();

        #endregion

        #region constructor

        /// <summary>
        /// Initializes a new instance of the SubGraphFactory class.
        /// </summary>
        /// <param name="myF8">The Fallen-8 instance.</param>
        /// <param name="logger">The logger instance.</param>
        /// <param name="pluginCache">The plugin cache instance.</param>
        public SubGraphFactory(Fallen8 myF8, ILogger<SubGraphFactory> logger, PluginCache pluginCache)
        {
            _subGraphsByName = new ConcurrentDictionary<String, SubGraphResult>();
            _subGraphsById = new ConcurrentDictionary<Guid, SubGraphResult>();
            _subGraphDependencies = new ConcurrentDictionary<Guid, Guid>();
            _logger = logger;
            _fallen8 = myF8;
            _pluginCache = pluginCache;
        }

        #endregion

        #region public methods

        /// <summary>
        /// Gets or sets the resource ceilings enforced when creating subgraphs. Setting null
        /// resets to the default quota. Defaults to a generous-but-bounded quota (M6) so ordinary
        /// embedded/trusted use is unaffected while a runaway caller cannot exhaust memory.
        /// </summary>
        public SubGraphQuota Quota
        {
            get { return _quota; }
            set { _quota = value ?? new SubGraphQuota(); }
        }

        /// <summary>
        /// The number of currently registered subgraphs.
        /// </summary>
        public int SubGraphCount
        {
            get { return _subGraphsByName.Count; }
        }

        /// <summary>
        /// The total number of materialized elements (vertices + edges) across all
        /// registered subgraphs.
        /// </summary>
        private int CurrentTotalElements()
        {
            int total = 0;
            foreach (var result in _subGraphsById.Values)
            {
                if (result.SubGraph != null)
                {
                    total += result.SubGraph.VertexCount + result.SubGraph.EdgeCount;
                }
            }
            return total;
        }

        /// <summary>
        /// Gets the available subgraph algorithm plugins.
        /// </summary>
        /// <returns>The available subgraph algorithm plugins.</returns>
        public IEnumerable<String> GetAvailableSubGraphPlugins()
        {
            IEnumerable<String> result;

            PluginFactory.TryGetAvailablePlugins<ISubGraphAlgorithm>(out result);

            return result;
        }

        /// <summary>
        /// Tries to create a subgraph.
        /// </summary>
        /// <param name="subGraph">The created subgraph result.</param>
        /// <param name="subGraphName">The name for the subgraph.</param>
        /// <param name="definition">The subgraph definition.</param>
        /// <param name="algorithmTypeName">The subgraph algorithm plugin name (as reported by the plugin's PluginName). Defaults to the breadth-first search algorithm.</param>
        /// <param name="parameter">Parameter for the algorithm. Default is null.</param>
        /// <returns><c>true</c> if the subgraph was created; otherwise, <c>false</c>.</returns>
        public bool TryCreateSubGraph(out SubGraphResult subGraph, string subGraphName, SubGraphDefinition definition,
                                      string algorithmTypeName = BreathFirstSearchSubgraphAlgorithm.AlgorithmPluginName,
                                      IDictionary<string, object> parameter = null)
        {
            return TryCreateSubGraphFromSource(out subGraph, subGraphName, definition, _fallen8, algorithmTypeName, parameter);
        }

        /// <summary>
        /// Tries to create a subgraph from an explicit source graph (this graph for a
        /// root-level subgraph, or another registered subgraph for a nested subgraph).
        /// The subgraph is registered in this factory and its dependency on the source is
        /// tracked so it participates in recalculation and persistence.
        /// </summary>
        public bool TryCreateSubGraphFromSource(out SubGraphResult subGraph, string subGraphName, SubGraphDefinition definition,
                                      IFallen8 source,
                                      string algorithmTypeName = BreathFirstSearchSubgraphAlgorithm.AlgorithmPluginName,
                                      IDictionary<string, object> parameter = null)
        {
            subGraph = null;

            if (source == null)
            {
                _logger.LogError(String.Format("Cannot create subgraph \"{0}\": source graph is null.", subGraphName));
                return false;
            }

            if (!TryGetOrLoadAlgorithm(out var algo, source, algorithmTypeName, parameter))
            {
                return false;
            }

            return CreateAndRegisterSubGraph(out subGraph, subGraphName, definition, algo, algorithmTypeName, parameter, source);
        }

        /// <summary>
        /// Gets an algorithm from cache or loads it from the plugin system.
        /// </summary>
        /// <param name="algo">The loaded algorithm instance.</param>
        /// <param name="algorithmTypeName">The name of the algorithm plugin.</param>
        /// <param name="parameter">Optional parameters for algorithm initialization.</param>
        /// <returns><c>true</c> if the algorithm was successfully retrieved or loaded; otherwise, <c>false</c>.</returns>
        private bool TryGetOrLoadAlgorithm(out ISubGraphAlgorithm algo, IFallen8 fallen8, string algorithmTypeName, IDictionary<string, object> parameter)
        {
            algo = null;

            // Check cache first
            Object cachedAlgo;
            if (!_pluginCache.SubGraph.TryGetValue(algorithmTypeName, out cachedAlgo))
            {
                // Algorithm was not cached - find and initialize it
                if (!PluginFactory.TryFindPlugin(out algo, algorithmTypeName))
                {
                    _logger.LogError(String.Format("Could not find subgraph algorithm plugin with name \"{0}\".", algorithmTypeName));
                    return false;
                }

                if (!InitializeAndCacheAlgorithm(fallen8, algo, parameter, algorithmTypeName))
                {
                    return false;
                }
            }
            else
            {
                algo = (ISubGraphAlgorithm)cachedAlgo;

                // The algorithm holds its source graph as instance state (set in Initialize),
                // so a cache hit MUST re-bind it to the requested source. Otherwise a
                // recalculation against a different source (e.g. a subgraph of a subgraph)
                // would silently extract from the originally-bound graph.
                if (!InitializeAndCacheAlgorithm(fallen8, algo, parameter, algorithmTypeName))
                {
                    return false;
                }
            }

            if (algo == null)
            {
                _logger.LogError(String.Format("Algorithm \"{0}\" is null after cache/initialization.", algorithmTypeName));
                return false;
            }

            return true;
        }

        /// <summary>
        /// Tries to create a subgraph using a typed algorithm without reflection.
        /// </summary>
        /// <typeparam name="T">The type of the subgraph algorithm.</typeparam>
        /// <param name="subGraph">The created subgraph result.</param>
        /// <param name="subGraphName">The name for the subgraph.</param>
        /// <param name="definition">The subgraph definition.</param>
        /// <param name="parameter">Parameter for the algorithm. Default is null.</param>
        /// <returns><c>true</c> if the subgraph was created; otherwise, <c>false</c>.</returns>
        public bool TryCreateSubGraph<T>(out SubGraphResult subGraph, string subGraphName, SubGraphDefinition definition,
                                         IDictionary<string, object> parameter = null)
            where T : ISubGraphAlgorithm
        {
            return TryCreateSubGraphFromSource<T>(out subGraph, subGraphName, definition, _fallen8, parameter);
        }

        /// <summary>
        /// Typed variant of <see cref="TryCreateSubGraphFromSource"/> (no reflection lookup).
        /// </summary>
        public bool TryCreateSubGraphFromSource<T>(out SubGraphResult subGraph, string subGraphName, SubGraphDefinition definition,
                                         IFallen8 source, IDictionary<string, object> parameter = null)
            where T : ISubGraphAlgorithm
        {
            subGraph = null;

            if (source == null)
            {
                _logger.LogError(String.Format("Cannot create subgraph \"{0}\": source graph is null.", subGraphName));
                return false;
            }

            Type subGraphType = typeof(T);
            var algo = Activator.CreateInstance(subGraphType, false) as ISubGraphAlgorithm;

            if (algo == null)
            {
                _logger.LogError(String.Format("Failed to create instance of subgraph algorithm type \"{0}\".", subGraphType.Name));
                return false;
            }

            // Reuse the cached instance if present, but always (re)initialize it with the
            // requested source: the algorithm holds its source as state, so a nested create
            // (or a create after another source was used) must rebind before running.
            Object cachedAlgo;
            if (_pluginCache.SubGraph.TryGetValue(algo.PluginName, out cachedAlgo))
            {
                algo = (ISubGraphAlgorithm)cachedAlgo;
            }

            if (!InitializeAndCacheAlgorithm(source, algo, parameter, algo.PluginName))
            {
                return false;
            }

            return CreateAndRegisterSubGraph(out subGraph, subGraphName, definition, algo, algo.PluginName, parameter, source);
        }

        /// <summary>
        /// Initializes an algorithm and adds it to the cache.
        /// </summary>
        /// <param name="algo">The algorithm to initialize.</param>
        /// <param name="parameter">Optional parameters for initialization.</param>
        /// <param name="algorithmName">The name of the algorithm for logging.</param>
        /// <returns><c>true</c> if initialization succeeded; otherwise, <c>false</c>.</returns>
        private bool InitializeAndCacheAlgorithm(IFallen8 f8, ISubGraphAlgorithm algo, IDictionary<string, object> parameter, string algorithmName)
        {
            try
            {
                algo.Initialize(f8, parameter);
                _pluginCache.AddSubGraph(algo);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, String.Format("Failed to initialize subgraph algorithm \"{0}\": {1}", algorithmName, ex.Message));
                return false;
            }
        }

        /// <summary>
        /// Creates a subgraph using the algorithm and registers it.
        /// </summary>
        /// <param name="subGraph">The created subgraph result.</param>
        /// <param name="subGraphName">The name for the subgraph.</param>
        /// <param name="definition">The subgraph definition.</param>
        /// <param name="algo">The algorithm to use.</param>
        /// <param name="algorithmPluginName">The name of the algorithm plugin.</param>
        /// <param name="algorithmParameters">Optional parameters for the algorithm.</param>
        /// <returns><c>true</c> if creation and registration succeeded; otherwise, <c>false</c>.</returns>
        private bool CreateAndRegisterSubGraph(out SubGraphResult subGraph, string subGraphName, SubGraphDefinition definition,
                                              ISubGraphAlgorithm algo, string algorithmPluginName, IDictionary<string, object> algorithmParameters,
                                              IFallen8 source)
        {
            subGraph = null;

            try
            {
                // Quota: reject before doing any work if the subgraph count is at its ceiling.
                if (_subGraphsByName.Count >= _quota.MaxSubGraphCount)
                {
                    _logger.LogWarning(String.Format(
                        "Cannot create subgraph \"{0}\": the maximum number of subgraphs ({1}) has been reached.",
                        subGraphName, _quota.MaxSubGraphCount));
                    return false;
                }

                // Create the subgraph using the algorithm
                if (!algo.TryCreateSubgraph(out subGraph, definition))
                {
                    _logger.LogError(String.Format("Failed to create subgraph \"{0}\" using algorithm \"{1}\".", subGraphName, algo.PluginName));
                    return false;
                }

                // Quota: reject an oversized subgraph (checked after materialization, since
                // the size is only known once the algorithm has run). The source graph is a
                // separate instance and is never mutated, so discarding is safe.
                int elementCount = subGraph.SubGraph.VertexCount + subGraph.SubGraph.EdgeCount;
                if (elementCount > _quota.MaxElementsPerSubGraph)
                {
                    _logger.LogWarning(String.Format(
                        "Cannot create subgraph \"{0}\": its {1} elements exceed the per-subgraph limit of {2}.",
                        subGraphName, elementCount, _quota.MaxElementsPerSubGraph));
                    subGraph = null;
                    return false;
                }

                if (CurrentTotalElements() + elementCount > _quota.MaxTotalElements)
                {
                    _logger.LogWarning(String.Format(
                        "Cannot create subgraph \"{0}\": it would exceed the total materialized element limit of {1}.",
                        subGraphName, _quota.MaxTotalElements));
                    subGraph = null;
                    return false;
                }

                // Store the source Fallen8 and algorithm metadata for recalculation. The
                // source may be this graph (root subgraph) or another registered subgraph
                // (nested subgraph); it drives dependency tracking and recalculation order.
                subGraph.SourceFallen8 = source;
                subGraph.SourceFallen8Id = source.Id;
                subGraph.AlgorithmPluginName = algorithmPluginName;
                subGraph.AlgorithmParameters = algorithmParameters;

                // Register the created subgraph
                if (!TryRegisterSubGraph(subGraph))
                {
                    _logger.LogWarning(String.Format("Subgraph \"{0}\" was created but could not be registered (name already exists).", subGraphName));
                    // No result on failure: callers use a non-null out to signal success, and
                    // the create transaction's rollback deregisters by name - leaving this
                    // non-null would make a losing racer report success and its rollback delete
                    // the winner's subgraph.
                    subGraph = null;
                    return false;
                }

                _logger.LogInformation(String.Format("Successfully created and registered subgraph \"{0}\".", subGraphName));
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, String.Format("Exception occurred while creating subgraph \"{0}\": {1}", subGraphName, ex.Message));
                subGraph = null;
                return false;
            }
        }

        private bool TrackSubgraphDependency(Guid subGraphId, Guid sourceGraphId)
        {
            return _subGraphDependencies.TryAdd(subGraphId, sourceGraphId);
        }

        private bool UnTrackSubgraphDependency(Guid subGraphId, Guid sourceGraphId)
        {
            return _subGraphDependencies.TryRemove(subGraphId, out _);
        }

        /// <summary>
        /// Registers a subgraph with the factory.
        /// </summary>
        /// <param name="subGraphName">The name to register the subgraph under.</param>
        /// <param name="subGraph">The subgraph result to register.</param>
        /// <returns><c>true</c> if the subgraph was registered; otherwise, <c>false</c>.</returns>
        public bool TryRegisterSubGraph(SubGraphResult subGraph)
        {
            if (subGraph == null || subGraph.Definitions == null || subGraph.SubGraph == null)
            {
                _logger.LogError("Cannot register subgraph: subGraph, Definitions, or SubGraph is null.");
                return false;
            }

            return _subGraphsByName.TryAdd(subGraph.Definitions.Name, subGraph) && _subGraphsById.TryAdd(subGraph.SubGraph.Id, subGraph) && TrackSubgraphDependency(subGraph.SubGraph.Id, subGraph.SourceFallen8Id);
        }

        /// <summary>
        /// Deregisters a subgraph from the factory.
        /// </summary>
        /// <param name="subGraphName">The name of the subgraph to deregister.</param>
        /// <returns><c>true</c> if the subgraph was deregistered; otherwise, <c>false</c>.</returns>
        public bool TryDeregisterSubGraph(string subGraphName)
        {
            if (_subGraphsByName.TryGetValue(subGraphName, out var subGraph))
            {
                return _subGraphsByName.TryRemove(subGraphName, out _) && _subGraphsById.TryRemove(subGraph.SubGraph.Id, out _) && UnTrackSubgraphDependency(subGraph.SubGraph.Id, subGraph.SourceFallen8Id);
            }

            return false;
        }

        /// <summary>
        /// Tries to get a subgraph.
        /// </summary>
        /// <param name="subGraph">The subgraph result.</param>
        /// <param name="subGraphName">The name of the subgraph.</param>
        /// <returns><c>true</c> if the subgraph was found; otherwise, <c>false</c>.</returns>
        public bool TryGetSubGraph(out SubGraphResult subGraph, string subGraphName)
        {
            return _subGraphsByName.TryGetValue(subGraphName, out subGraph);
        }

        public bool TryGetSubGraph(out SubGraphResult subGraph, Guid subGraphId)
        {
            return _subGraphsById.TryGetValue(subGraphId, out subGraph);
        }

        /// <summary>
        /// Deletes all subgraphs.
        /// </summary>
        public void DeleteAllSubGraphs()
        {
            _subGraphsByName.Clear();
            _subGraphsById.Clear();
            _subGraphDependencies.Clear();
        }

        /// <summary>
        /// Tries to recalculate a single subgraph.
        /// </summary>
        /// <param name="subGraphName">The name of the subgraph to recalculate.</param>
        /// <returns><c>true</c> if the subgraph was successfully recalculated; otherwise, <c>false</c>.</returns>
        /// <remarks>
        /// This method can only recalculate a subgraph if the source Fallen8 instance is not null.
        /// The IDs and names of the subgraph remain stable after recalculation.
        /// </remarks>
        public bool TryRecalculateSubGraph(string subGraphName)
        {
            // Try to get the existing subgraph
            if (!_subGraphsByName.TryGetValue(subGraphName, out var outdatedSubGraphResult))
            {
                _logger.LogError(String.Format("Subgraph \"{0}\" not found.", subGraphName));
                return false;
            }

            // Validate that the subgraph can be recalculated
            if (outdatedSubGraphResult.SourceFallen8 == null)
            {
                _logger.LogError(String.Format("Cannot recalculate subgraph \"{0}\" - source Fallen8 is null.", subGraphName));
                return false;
            }

            if (outdatedSubGraphResult.Definitions == null || string.IsNullOrEmpty(outdatedSubGraphResult.AlgorithmPluginName))
            {
                _logger.LogError(String.Format("Cannot recalculate subgraph \"{0}\" - missing definition or algorithm plugin name.", subGraphName));
                return false;
            }

            try
            {
                _logger.LogInformation(String.Format("Recalculating subgraph \"{0}\" using algorithm \"{1}\"...", subGraphName, outdatedSubGraphResult.AlgorithmPluginName));

                // Get or load the algorithm
                if (!TryGetOrLoadAlgorithm(out var algo, outdatedSubGraphResult.SourceFallen8, outdatedSubGraphResult.AlgorithmPluginName, outdatedSubGraphResult.AlgorithmParameters))
                {
                    return false;
                }

                // Create the new subgraph using the algorithm
                if (!algo.TryCreateSubgraph(out var newSubGraph, outdatedSubGraphResult.Definitions))
                {
                    _logger.LogError(String.Format("Failed to recalculate subgraph \"{0}\" using algorithm \"{1}\".", subGraphName, outdatedSubGraphResult.AlgorithmPluginName));
                    return false;
                }

                // Preserve the existing IDs and metadata
                Guid oldSubGraphId = outdatedSubGraphResult.SubGraph.Id;

                // Set the ID of the new subgraph to match the old one (using internal setter)
                newSubGraph.SubGraph.SetId(oldSubGraphId);

                outdatedSubGraphResult.SourceFallen8 = newSubGraph.SourceFallen8;
                outdatedSubGraphResult.AlgorithmPluginName = newSubGraph.AlgorithmPluginName;
                outdatedSubGraphResult.AlgorithmParameters = newSubGraph.AlgorithmParameters;
                outdatedSubGraphResult.Definitions = newSubGraph.Definitions;
                outdatedSubGraphResult.SubGraph = newSubGraph.SubGraph;

                _logger.LogInformation(String.Format("Successfully recalculated subgraph \"{0}\".", subGraphName));
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, String.Format("Exception occurred while recalculating subgraph \"{0}\": {1}", subGraphName, ex.Message));
                return false;
            }
        }

        public int RecalculateAllSubGraphs()
        {
            int successes = 0;
            int failures = 0;

            // Start with subgraphs that depend on the root Fallen8 instance.
            // Recalculation is sequential: the algorithm plugin instance is shared and
            // re-bound to a source on each call, so parallel recalculation would race on
            // that shared state and could extract from the wrong graph.
            var rootSubGraphs = _subGraphsById.Where(_ => _.Value.SourceFallen8Id.Equals(_fallen8.Id)).ToList();

            // Guard against revisiting a subgraph (and against dependency cycles, which
            // cannot form by construction but must never hang if they somehow did).
            var visited = new HashSet<Guid>();

            foreach (var _ in rootSubGraphs)
            {
                if (!visited.Add(_.Value.SubGraph.Id))
                {
                    continue;
                }

                _.Value.SourceFallen8 = _fallen8;

                if (TryRecalculateSubGraph(_.Value.Definitions.Name))
                {
                    successes++;

                    // Recursively recalculate all dependent subgraphs
                    var (nestedSuccesses, nestedFailures) = RecalculateSubGraphsRecursive(_.Value.SubGraph.Id, visited);
                    successes += nestedSuccesses;
                    failures += nestedFailures;
                }
                else
                {
                    failures++;
                }
            }

            _logger.LogInformation(String.Format("Recalculated {0} subgraph(s) with {1} successes and {2} failure(s).", successes + failures, successes, failures));
            return successes;
        }

        /// <summary>
        /// Recursively recalculates all subgraphs that depend on a given source graph.
        /// </summary>
        /// <param name="sourceGraphId">The ID of the source graph whose dependent subgraphs should be recalculated.</param>
        /// <param name="visited">Subgraph ids already recalculated in this pass (cycle/revisit guard).</param>
        /// <returns>A tuple containing the number of successful recalculations and failures.</returns>
        private (int successes, int failures) RecalculateSubGraphsRecursive(Guid sourceGraphId, HashSet<Guid> visited)
        {
            int successes = 0;
            int failures = 0;

            // Find all subgraphs that depend on the given source graph
            var dependentSubGraphs = _subGraphsById.Where(_ => _.Value.SourceFallen8Id.Equals(sourceGraphId)).ToList();

            if (dependentSubGraphs.Count == 0)
            {
                return (0, 0); // Base case: no more dependent subgraphs
            }

            foreach (var _ in dependentSubGraphs)
            {
                if (!visited.Add(_.Value.SubGraph.Id))
                {
                    // Already recalculated (or a cycle) - do not process again.
                    continue;
                }

                // Rebind to the current (just-recalculated) source instance.
                if (_subGraphsById.TryGetValue(sourceGraphId, out var sourceSubGraph))
                {
                    _.Value.SourceFallen8 = sourceSubGraph.SubGraph;
                }

                if (TryRecalculateSubGraph(_.Value.Definitions.Name))
                {
                    successes++;

                    // Recursively recalculate subgraphs that depend on this one
                    var (nestedSuccesses, nestedFailures) = RecalculateSubGraphsRecursive(_.Value.SubGraph.Id, visited);
                    successes += nestedSuccesses;
                    failures += nestedFailures;
                }
                else
                {
                    failures++;
                }
            }

            return (successes, failures);
        }

        /// <summary>
        /// Gets a list of all registered subgraph names.
        /// </summary>
        /// <returns>An enumerable collection of subgraph names.</returns>
        public IEnumerable<string> GetAllSubGraphNames()
        {
            return _subGraphsByName.Keys;
        }

        /// <summary>
        /// Checks if a subgraph can be recalculated.
        /// </summary>
        /// <param name="subGraphName">The name of the subgraph to check.</param>
        /// <returns><c>true</c> if the subgraph exists and can be recalculated; otherwise, <c>false</c>.</returns>
        public bool CanRecalculateSubGraph(string subGraphName)
        {
            if (_subGraphsByName.TryGetValue(subGraphName, out var result))
            {
                return result.Definitions != null && !string.IsNullOrEmpty(result.AlgorithmPluginName) && result.SourceFallen8 != null;
            }
            return false;
        }

        /// <summary>
        /// Gets the recipes of all registered subgraphs that can be persisted and rebuilt.
        /// </summary>
        /// <remarks>
        /// Only root-level subgraphs (sourced directly from this graph) that carry a recipe
        /// are returned. Subgraphs created directly from delegates have no recipe and cannot
        /// be rebuilt from persistence.
        /// </remarks>
        public IEnumerable<SubGraphRecipe> GetPersistableRecipes()
        {
            // Every subgraph carrying a recipe is persistable, including nested ones (whose
            // source is another subgraph). Delegate-only subgraphs have no recipe.
            return _subGraphsById.Values
                .Where(r => r.Recipe != null)
                .Select(r => r.Recipe)
                .ToList();
        }

        /// <summary>
        /// Rebuilds subgraphs from persisted recipes against the current graph.
        /// </summary>
        /// <param name="recipes">The recipes to rehydrate.</param>
        /// <param name="compiler">Compiler that turns a recipe back into a definition.</param>
        /// <returns>The number of subgraphs successfully rehydrated.</returns>
        public int RehydrateFromRecipes(IEnumerable<SubGraphRecipe> recipes, ISubGraphRecipeCompiler compiler)
        {
            if (recipes == null)
            {
                return 0;
            }

            if (compiler == null)
            {
                _logger.LogWarning("Cannot rehydrate subgraphs: no recipe compiler is registered.");
                return 0;
            }

            int restored = 0;

            // Rehydrate in dependency order: a subgraph can only be rebuilt once its source
            // exists. Sources are resolved by their saved id via this map, seeded with the
            // (restored) root graph. Root subgraphs' SourceFallen8Id equals the root id;
            // nested subgraphs' SourceFallen8Id equals a parent subgraph's saved id.
            var sourcesBySavedId = new Dictionary<Guid, IFallen8> { { _fallen8.Id, _fallen8 } };
            var pending = recipes.Where(r => r != null).ToList();

            bool madeProgress = true;
            while (pending.Count > 0 && madeProgress)
            {
                madeProgress = false;

                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    var recipe = pending[i];

                    if (!sourcesBySavedId.TryGetValue(recipe.SourceFallen8Id, out var source))
                    {
                        // Source not rehydrated yet; try again on a later pass.
                        continue;
                    }

                    pending.RemoveAt(i);
                    madeProgress = true;

                    try
                    {
                        if (!compiler.TryCompile(recipe, out var definition, out var error))
                        {
                            _logger.LogError(String.Format("Could not compile recipe for subgraph \"{0}\": {1}", recipe.Name, error));
                            continue;
                        }

                        if (TryCreateSubGraphFromSource(out var result, recipe.Name, definition, source, recipe.AlgorithmPluginName))
                        {
                            // Retain the recipe so it can be persisted again, and expose this
                            // subgraph (by its saved id) as a source for its dependents.
                            result.Recipe = recipe;
                            if (recipe.SubGraphId != Guid.Empty)
                            {
                                sourcesBySavedId[recipe.SubGraphId] = result.SubGraph;
                            }
                            restored++;
                        }
                        else
                        {
                            _logger.LogError(String.Format("Could not recreate subgraph \"{0}\" from its recipe.", recipe.Name));
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, String.Format("Exception rehydrating subgraph \"{0}\": {1}", recipe.Name, ex.Message));
                    }
                }
            }

            if (pending.Count > 0)
            {
                _logger.LogWarning(String.Format(
                    "{0} subgraph recipe(s) could not be rehydrated because their source graph was not restored.", pending.Count));
            }

            _logger.LogInformation(String.Format("Rehydrated {0} subgraph(s) from recipes.", restored));
            return restored;
        }

        #endregion
    }
}
