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
        /// <param name="algorithmTypeName">The subgraph algorithm type name. Default is BreathFirstSearchSubgraphAlgorithm.</param>
        /// <param name="parameter">Parameter for the algorithm. Default is null.</param>
        /// <returns><c>true</c> if the subgraph was created; otherwise, <c>false</c>.</returns>
        public bool TryCreateSubGraph(out SubGraphResult subGraph, string subGraphName, SubGraphDefinition definition,
                                      string algorithmTypeName = "BreathFirstSearchSubgraphAlgorithm",
                                      IDictionary<string, object> parameter = null)
        {
            subGraph = null;

            if (!TryGetOrLoadAlgorithm(out var algo, _fallen8, algorithmTypeName, parameter))
            {
                return false;
            }

            return CreateAndRegisterSubGraph(out subGraph, subGraphName, definition, algo, algorithmTypeName, parameter);
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
            subGraph = null;
            Type subGraphType = typeof(T);
            var algo = Activator.CreateInstance(subGraphType, false) as ISubGraphAlgorithm;

            if (algo == null)
            {
                _logger.LogError(String.Format("Failed to create instance of subgraph algorithm type \"{0}\".", subGraphType.Name));
                return false;
            }

            // Check cache first
            Object cachedAlgo;
            if (!_pluginCache.SubGraph.TryGetValue(algo.PluginName, out cachedAlgo))
            {
                // Algorithm was not cached - initialize it
                if (!InitializeAndCacheAlgorithm(_fallen8, algo, parameter, algo.PluginName))
                {
                    return false;
                }
            }
            else
            {
                algo = (ISubGraphAlgorithm)cachedAlgo;
            }

            return CreateAndRegisterSubGraph(out subGraph, subGraphName, definition, algo, algo.PluginName, parameter);
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
                                              ISubGraphAlgorithm algo, string algorithmPluginName, IDictionary<string, object> algorithmParameters)
        {
            subGraph = null;

            try
            {
                // Create the subgraph using the algorithm
                if (!algo.TryCreateSubgraph(out subGraph, definition))
                {
                    _logger.LogError(String.Format("Failed to create subgraph \"{0}\" using algorithm \"{1}\".", subGraphName, algo.PluginName));
                    return false;
                }

                // Store the source Fallen8 and algorithm metadata for recalculation
                subGraph.SourceFallen8 = _fallen8;
                subGraph.AlgorithmPluginName = algorithmPluginName;
                subGraph.AlgorithmParameters = algorithmParameters;

                // Register the created subgraph
                if (!TryRegisterSubGraph(subGraph))
                {
                    _logger.LogWarning(String.Format("Subgraph \"{0}\" was created but could not be registered (name already exists).", subGraphName));
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

            // Start with subgraphs that depend on the root Fallen8 instance
            var rootSubGraphs = _subGraphsById.Where(_ => _.Value.SourceFallen8Id.Equals(_fallen8.Id)).ToList();

            Parallel.ForEach(rootSubGraphs, _ =>
            {
                _.Value.SourceFallen8 = _fallen8;

                if (TryRecalculateSubGraph(_.Value.Definitions.Name))
                {
                    Interlocked.Increment(ref successes);

                    // Recursively recalculate all dependent subgraphs
                    var (nestedSuccesses, nestedFailures) = RecalculateSubGraphsRecursive(_.Value.SubGraph.Id);
                    Interlocked.Add(ref successes, nestedSuccesses);
                    Interlocked.Add(ref failures, nestedFailures);
                }
                else
                {
                    Interlocked.Increment(ref failures);
                }
            });

            _logger.LogInformation(String.Format("Recalculated {0} subgraph(s) with {1} successes and {2} failure(s).", successes + failures, successes, failures));
            return successes;
        }

        /// <summary>
        /// Recursively recalculates all subgraphs that depend on a given source graph.
        /// </summary>
        /// <param name="sourceGraphId">The ID of the source graph whose dependent subgraphs should be recalculated.</param>
        /// <returns>A tuple containing the number of successful recalculations and failures.</returns>
        private (int successes, int failures) RecalculateSubGraphsRecursive(Guid sourceGraphId)
        {
            int successes = 0;
            int failures = 0;

            // Find all subgraphs that depend on the given source graph
            var dependentSubGraphs = _subGraphsById.Where(_ => _.Value.SourceFallen8Id.Equals(sourceGraphId)).ToList();

            if (dependentSubGraphs.Count == 0)
            {
                return (0, 0); // Base case: no more dependent subgraphs
            }

            Parallel.ForEach(dependentSubGraphs, _ =>
            {
                // Update the source reference
                if (_subGraphsById.TryGetValue(sourceGraphId, out var sourceSubGraph))
                {
                    _.Value.SourceFallen8 = sourceSubGraph.SubGraph;
                }

                if (TryRecalculateSubGraph(_.Value.Definitions.Name))
                {
                    Interlocked.Increment(ref successes);

                    // Recursively recalculate subgraphs that depend on this one
                    var (nestedSuccesses, nestedFailures) = RecalculateSubGraphsRecursive(_.Value.SubGraph.Id);
                    Interlocked.Add(ref successes, nestedSuccesses);
                    Interlocked.Add(ref failures, nestedFailures);
                }
                else
                {
                    Interlocked.Increment(ref failures);
                }
            });

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

        #endregion
    }
}
