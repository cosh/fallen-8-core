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
        private readonly ConcurrentDictionary<String, SubGraphResult> _subGraphs;

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
            _subGraphs = new ConcurrentDictionary<String, SubGraphResult>();
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
            ISubGraphAlgorithm algo = null;

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

                if (!InitializeAndCacheAlgorithm(algo, parameter, algorithmTypeName))
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

            return CreateAndRegisterSubGraph(out subGraph, subGraphName, definition, algo, algorithmTypeName, parameter);
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
                if (!InitializeAndCacheAlgorithm(algo, parameter, algo.PluginName))
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
        private bool InitializeAndCacheAlgorithm(ISubGraphAlgorithm algo, IDictionary<string, object> parameter, string algorithmName)
        {
            try
            {
                algo.Initialize(_fallen8, parameter, _fallen8.GetLoggerFactory());
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
                if (!_subGraphs.TryAdd(subGraphName, subGraph))
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

        /// <summary>
        /// Registers a subgraph with the factory.
        /// </summary>
        /// <param name="subGraphName">The name to register the subgraph under.</param>
        /// <param name="subGraph">The subgraph result to register.</param>
        /// <returns><c>true</c> if the subgraph was registered; otherwise, <c>false</c>.</returns>
        public bool TryRegisterSubGraph(string subGraphName, SubGraphResult subGraph)
        {
            if (_subGraphs.TryAdd(subGraphName, subGraph))
            {
                return true;
            }

            _logger.LogError(String.Format("The subgraph with name \"{0}\" already exists.", subGraphName));
            return false;
        }

        /// <summary>
        /// Deregisters a subgraph from the factory.
        /// </summary>
        /// <param name="subGraphName">The name of the subgraph to deregister.</param>
        /// <returns><c>true</c> if the subgraph was deregistered; otherwise, <c>false</c>.</returns>
        public bool TryDeregisterSubGraph(string subGraphName)
        {
            return _subGraphs.TryRemove(subGraphName, out _);
        }

        /// <summary>
        /// Tries to delete a subgraph.
        /// </summary>
        /// <param name="subGraphName">The name of the subgraph to delete.</param>
        /// <returns><c>true</c> if the subgraph was deleted; otherwise, <c>false</c>.</returns>
        public bool TryDeleteSubGraph(string subGraphName)
        {
            return _subGraphs.TryRemove(subGraphName, out _);
        }

        /// <summary>
        /// Tries to get a subgraph.
        /// </summary>
        /// <param name="subGraph">The subgraph result.</param>
        /// <param name="subGraphName">The name of the subgraph.</param>
        /// <returns><c>true</c> if the subgraph was found; otherwise, <c>false</c>.</returns>
        public bool TryGetSubGraph(out SubGraphResult subGraph, string subGraphName)
        {
            return _subGraphs.TryGetValue(subGraphName, out subGraph);
        }

        /// <summary>
        /// Deletes all subgraphs.
        /// </summary>
        public void DeleteAllSubGraphs()
        {
            _subGraphs.Clear();
        }

        /// <summary>
        /// Recalculates a specific subgraph using its stored definition and algorithm.
        /// </summary>
        /// <param name="subGraphName">The name of the subgraph to recalculate.</param>
        /// <returns><c>true</c> if the subgraph was successfully recalculated; otherwise, <c>false</c>.</returns>
        /// <remarks>
        /// This method requires that the subgraph was originally created through the factory with a definition and algorithm.
        /// Subgraphs registered manually without these details cannot be recalculated.
        /// The algorithm plugin will be loaded from the plugin system if not already cached.
        /// </remarks>
        public bool TryRecalculateSubGraph(string subGraphName)
        {
            if (!_subGraphs.TryGetValue(subGraphName, out var result))
            {
                _logger.LogError(String.Format("Subgraph \"{0}\" not found.", subGraphName));
                return false;
            }

            if (result.Definitions == null || string.IsNullOrEmpty(result.AlgorithmPluginName) || result.SourceFallen8 == null)
            {
                _logger.LogError(String.Format("Subgraph \"{0}\" cannot be recalculated because it lacks definition, algorithm plugin name, or source Fallen8 information.", subGraphName));
                return false;
            }

            try
            {
                _logger.LogInformation(String.Format("Recalculating subgraph \"{0}\" using algorithm \"{1}\"...", subGraphName, result.AlgorithmPluginName));

                // Load the algorithm from the plugin system
                ISubGraphAlgorithm algo = null;
                Object cachedAlgo;
                if (!_pluginCache.SubGraph.TryGetValue(result.AlgorithmPluginName, out cachedAlgo))
                {
                    // Algorithm was not cached - find and initialize it
                    if (!PluginFactory.TryFindPlugin(out algo, result.AlgorithmPluginName))
                    {
                        _logger.LogError(String.Format("Could not find subgraph algorithm plugin with name \"{0}\" for recalculation.", result.AlgorithmPluginName));
                        return false;
                    }

                    if (!InitializeAndCacheAlgorithm(algo, result.AlgorithmParameters, result.AlgorithmPluginName))
                    {
                        return false;
                    }
                }
                else
                {
                    algo = (ISubGraphAlgorithm)cachedAlgo;
                }

                // Re-initialize the algorithm with the correct source Fallen8
                // This is crucial for subgraphs of subgraphs
                algo.Initialize(result.SourceFallen8 as Fallen8, result.AlgorithmParameters, _fallen8.GetLoggerFactory());

                // Create a new subgraph using the algorithm and definition on the original source
                if (!algo.TryCreateSubgraph(out var newSubGraph, result.Definitions))
                {
                    _logger.LogError(String.Format("Failed to recalculate subgraph \"{0}\" using algorithm \"{1}\".", subGraphName, result.AlgorithmPluginName));
                    return false;
                }

                // Update the result properties to maintain recalculation metadata
                newSubGraph.SourceFallen8 = result.SourceFallen8;
                newSubGraph.AlgorithmPluginName = result.AlgorithmPluginName;
                newSubGraph.AlgorithmParameters = result.AlgorithmParameters;
                _subGraphs[subGraphName] = newSubGraph;

                _logger.LogInformation(String.Format("Successfully recalculated subgraph \"{0}\".", subGraphName));
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, String.Format("Exception occurred while recalculating subgraph \"{0}\": {1}", subGraphName, ex.Message));
                return false;
            }
        }

        /// <summary>
        /// Recalculates all subgraphs that have stored definitions and algorithms.
        /// </summary>
        /// <returns>The number of subgraphs successfully recalculated.</returns>
        /// <remarks>
        /// This method iterates through all registered subgraphs and recalculates those that have
        /// the necessary definition and algorithm information. Subgraphs that cannot be recalculated
        /// are skipped with a warning logged. Algorithm plugins will be loaded from the plugin system if not already cached.
        /// </remarks>
        public int RecalculateAllSubGraphs()
        {
            _logger.LogInformation("Starting recalculation of all subgraphs...");
            int successCount = 0;
            int totalCount = _subGraphs.Count;
            int skippedCount = 0;

            foreach (var kvp in _subGraphs)
            {
                var subGraphName = kvp.Key;
                var result = kvp.Value;

                if (result.Definitions == null || string.IsNullOrEmpty(result.AlgorithmPluginName) || result.SourceFallen8 == null)
                {
                    _logger.LogWarning(String.Format("Skipping subgraph \"{0}\" - cannot be recalculated (missing definition, algorithm plugin name, or source Fallen8).", subGraphName));
                    skippedCount++;
                    continue;
                }

                try
                {
                    _logger.LogInformation(String.Format("Recalculating subgraph \"{0}\" using algorithm \"{1}\"...", subGraphName, result.AlgorithmPluginName));

                    // Load the algorithm from the plugin system
                    ISubGraphAlgorithm algo = null;
                    Object cachedAlgo;
                    if (!_pluginCache.SubGraph.TryGetValue(result.AlgorithmPluginName, out cachedAlgo))
                    {
                        // Algorithm was not cached - find and initialize it
                        if (!PluginFactory.TryFindPlugin(out algo, result.AlgorithmPluginName))
                        {
                            _logger.LogError(String.Format("Could not find subgraph algorithm plugin with name \"{0}\" for recalculation.", result.AlgorithmPluginName));
                            skippedCount++;
                            continue;
                        }

                        if (!InitializeAndCacheAlgorithm(algo, result.AlgorithmParameters, result.AlgorithmPluginName))
                        {
                            skippedCount++;
                            continue;
                        }
                    }
                    else
                    {
                        algo = (ISubGraphAlgorithm)cachedAlgo;
                    }

                    // Re-initialize the algorithm with the correct source Fallen8
                    algo.Initialize(result.SourceFallen8 as Fallen8, result.AlgorithmParameters, _fallen8.GetLoggerFactory());

                    // Create a new subgraph using the algorithm and definition on the original source
                    if (algo.TryCreateSubgraph(out var newSubGraph, result.Definitions))
                    {
                        // Update the result properties to maintain recalculation metadata
                        newSubGraph.SourceFallen8 = result.SourceFallen8;
                        newSubGraph.AlgorithmPluginName = result.AlgorithmPluginName;
                        newSubGraph.AlgorithmParameters = result.AlgorithmParameters;
                        _subGraphs[subGraphName] = newSubGraph;
                        successCount++;
                        _logger.LogInformation(String.Format("Successfully recalculated subgraph \"{0}\".", subGraphName));
                    }
                    else
                    {
                        _logger.LogError(String.Format("Failed to recalculate subgraph \"{0}\" using algorithm \"{1}\".", subGraphName, result.AlgorithmPluginName));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, String.Format("Exception occurred while recalculating subgraph \"{0}\": {1}", subGraphName, ex.Message));
                }
            }

            _logger.LogInformation(String.Format("Recalculation complete: {0} of {1} subgraphs recalculated successfully ({2} skipped).",
                successCount, totalCount, skippedCount));

            return successCount;
        }

        /// <summary>
        /// Gets a list of all registered subgraph names.
        /// </summary>
        /// <returns>An enumerable collection of subgraph names.</returns>
        public IEnumerable<string> GetAllSubGraphNames()
        {
            return _subGraphs.Keys;
        }

        /// <summary>
        /// Checks if a subgraph can be recalculated.
        /// </summary>
        /// <param name="subGraphName">The name of the subgraph to check.</param>
        /// <returns><c>true</c> if the subgraph exists and can be recalculated; otherwise, <c>false</c>.</returns>
        public bool CanRecalculateSubGraph(string subGraphName)
        {
            if (_subGraphs.TryGetValue(subGraphName, out var result))
            {
                return result.Definitions != null && !string.IsNullOrEmpty(result.AlgorithmPluginName) && result.SourceFallen8 != null;
            }
            return false;
        }

        #endregion
    }
}
