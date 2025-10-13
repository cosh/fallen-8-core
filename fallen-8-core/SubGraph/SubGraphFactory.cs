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

        #endregion

        #region constructor

        /// <summary>
        /// Initializes a new instance of the SubGraphFactory class.
        /// </summary>
        /// <param name="myF8">The Fallen-8 instance.</param>
        /// <param name="logger">The logger instance.</param>
        public SubGraphFactory(Fallen8 myF8, ILogger<SubGraphFactory> logger)
        {
            _subGraphs = new ConcurrentDictionary<String, SubGraphResult>();
            _logger = logger;
            _fallen8 = myF8;
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

            // Find the subgraph algorithm plugin
            if (!PluginFactory.TryFindPlugin(out ISubGraphAlgorithm algorithm, algorithmTypeName))
            {
                _logger.LogError(String.Format("Could not find subgraph algorithm plugin with name \"{0}\".", algorithmTypeName));
                return false;
            }

            try
            {
                // Initialize the algorithm
                algorithm.Initialize(_fallen8, parameter, _fallen8.GetLoggerFactory());

                // Create the subgraph using the algorithm
                if (!algorithm.TryCreateSubgraph(out subGraph, definition))
                {
                    _logger.LogError(String.Format("Failed to create subgraph \"{0}\" using algorithm \"{1}\".", subGraphName, algorithmTypeName));
                    return false;
                }

                // Register the created subgraph
                if (!TryRegisterSubGraph(subGraphName, subGraph))
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
        }        /// <summary>
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

        #endregion
    }
}
