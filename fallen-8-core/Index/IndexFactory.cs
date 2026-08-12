// MIT License
//
// IndexFactory.cs
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
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Core.Error;
using NoSQL.GraphDB.Core.Helper;
using NoSQL.GraphDB.Core.Plugin;
using NoSQL.GraphDB.Core.Serializer;

namespace NoSQL.GraphDB.Core.Index
{
    /// <summary>
    ///   Index factory.
    /// </summary>
    public sealed class IndexFactory : AThreadSafeElement
    {
        #region Data

        /// <summary>
        ///   The created indices.
        /// </summary>
        public IDictionary<String, IIndex> Indices;

        /// <summary>
        /// The logger
        /// </summary>
        private ILogger<IndexFactory> _logger;

        private Fallen8 _fallen8;

        #endregion

        #region constructor

        /// <summary>
        ///   Initializes a new instance of the IndexFactory class.
        /// </summary>
        public IndexFactory(Fallen8 myF8, ILogger<IndexFactory> logger)
        {
            Indices = new Dictionary<String, IIndex>();
            _logger = logger;
            _fallen8 = myF8;
        }

        #endregion

        #region IFallen8IndexFactory implementation

        /// <summary>
        ///   Gets the available index plugin names: the discovered built-ins unioned with this
        ///   namespace's registered index types, by the union rule
        ///   <see cref="PluginFactory.AvailablePluginNames" /> owns.
        /// </summary>
        /// <returns> The available index plugins. </returns>
        [RequiresUnreferencedCode(PluginFactory.DiscoveryIsNotTrimSafe)]
        public IEnumerable<String> GetAvailableIndexPlugins()
        {
            return PluginFactory.AvailablePluginNames(Plugins.PluginContract.Index, _fallen8?.Plugins);
        }

        /// <summary>
        ///   Tries to create an index.
        /// </summary>
        /// <returns> <c>true</c> if the index was created; otherwise, <c>false</c> . </returns>
        /// <param name='index'> The created index. </param>
        /// <param name='indexName'> Index name. </param>
        /// <param name='indexTypeName'> Index type. Default is DictionaryIndex </param>
        /// <param name='parameter'> Parameter for the index. Default is Null </param>
        public bool TryCreateIndex(out IIndex index, string indexName, string indexTypeName = "DictionaryIndex",
                                   IDictionary<string, object> parameter = null)
        {
            if (TryResolveIndexPlugin(out index, indexTypeName))
            {
                try
                {
                    index.Initialize(_fallen8, parameter);

                    if (WriteResource())
                    {
                        var added = false;
                        try
                        {
                            if (!Indices.ContainsKey(indexName))
                            {
                                Indices.Add(indexName, index);
                                added = true;
                            }
                            else
                            {
                                _logger.LogError("The index with name \"{IndexName}\" already exists.", indexName);
                            }
                        }
                        finally
                        {
                            FinishWriteResource();
                        }

                        if (added)
                        {
                            // A bound vector index populates itself lock-free (RebuildProjection)
                            // BEFORE it is registered above. An element removed in that window is
                            // missed by the engine's write-end purge (the index was not yet in the
                            // map) and would linger as a pinned tombstone. Now that the index IS
                            // registered - so every subsequent removal's purge sees it - sweep any
                            // tombstone the window left. Run OUTSIDE the factory lock (the sweep is
                            // O(elements)); it takes the index's own write lock, serialising with a
                            // concurrent RemoveValue. Non-vector indices need no sweep.
                            (index as Vector.VectorIndex)?.PurgeTombstones();
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Log before returning false so a failed index creation (e.g. a bad parameter or
                    // an invalid embeddingName/dimension/metric thrown by VectorIndex.Initialize)
                    // leaves a diagnostic trail, matching the log-then-return-false discipline of the
                    // sibling factories (feature vector-index: the failure is logged).
                    _logger.LogError(ex, "The index with name \"{IndexName}\" (type \"{IndexTypeName}\") could not be created.", indexName, indexTypeName);
                    index = null;
                    return false;
                }
            }
            else
            {
                PluginFactory.LogPluginNotFound(_logger, "index", indexTypeName);
            }

            index = null;
            return false;
        }

        /// <summary>
        ///   Tries to delete the index.
        /// </summary>
        /// <returns> <c>true</c> if the index was deleted; otherwise, <c>false</c> . </returns>
        /// <param name='indexName'> Index name. </param>
        public bool TryDeleteIndex(string indexName)
        {
            if (WriteResource())
            {
                try
                {
                    return Indices.Remove(indexName);
                }
                finally
                {
                    FinishWriteResource();
                }
            }

            throw new CollisionException();
        }

        /// <summary>
        ///   Returns a point-in-time snapshot of the registered indices, copied under the read lock so
        ///   it never observes a torn <see cref="Indices" /> mid-mutation (create/delete take the write
        ///   lock). Used by the engine's write-end index purge (feature index-lifecycle 3.3) to
        ///   enumerate the indices safely off the single-writer thread without racing a concurrent
        ///   create/delete on the request thread.
        /// </summary>
        internal IReadOnlyList<IIndex> GetIndicesSnapshot()
        {
            if (ReadResource())
            {
                try
                {
                    return new List<IIndex>(Indices.Values);
                }
                finally
                {
                    FinishReadResource();
                }
            }

            throw new CollisionException();
        }

        /// <summary>
        ///   Returns a point-in-time snapshot of the registered index ids paired with their plugin
        ///   type name, copied under the read lock so it never observes a torn <see cref="Indices" />
        ///   mid-mutation. Public so callers off the writer thread (e.g. the save-game registry's KPI
        ///   capture) can enumerate index ids + types without racing a concurrent create/delete.
        /// </summary>
        public IReadOnlyList<KeyValuePair<String, String>> GetIndexPluginTypesSnapshot()
        {
            if (ReadResource())
            {
                try
                {
                    var result = new List<KeyValuePair<String, String>>(Indices.Count);
                    foreach (var kv in Indices)
                    {
                        result.Add(new KeyValuePair<String, String>(kv.Key, kv.Value?.PluginName));
                    }
                    return result;
                }
                finally
                {
                    FinishReadResource();
                }
            }

            throw new CollisionException();
        }

        /// <summary>
        ///   Returns a point-in-time snapshot of the registered index ids paired with their
        ///   index instances, copied under the read lock (feature observability). Public so
        ///   GET /statistics can enumerate the inventory (name, type, counts) off the writer
        ///   thread without racing a concurrent create/delete on the plain dictionary.
        /// </summary>
        public IReadOnlyList<KeyValuePair<String, IIndex>> GetNamedIndicesSnapshot()
        {
            if (ReadResource())
            {
                try
                {
                    var result = new List<KeyValuePair<String, IIndex>>(Indices.Count);
                    foreach (var kv in Indices)
                    {
                        result.Add(kv);
                    }
                    return result;
                }
                finally
                {
                    FinishReadResource();
                }
            }

            throw new CollisionException();
        }

        /// <summary>
        ///   Tries the index of the get.
        /// </summary>
        /// <returns> <c>true</c> if the index was found; otherwise, <c>false</c> . </returns>
        /// <param name='index'> Index. </param>
        /// <param name='indexName'> Index name. </param>
        public bool TryGetIndex(out IIndex index, string indexName)
        {
            if (ReadResource())
            {
                try
                {
                    return Indices.TryGetValue(indexName, out index);
                }
                finally
                {
                    FinishReadResource();
                }
            }

            throw new CollisionException();
        }

        /// <summary>
        /// Deletes all indices
        /// </summary>
        public void DeleteAllIndices()
        {
            if (WriteResource())
            {
                try
                {
                    Indices = new Dictionary<string, IIndex>();

                    return;
                }
                finally
                {
                    FinishWriteResource();
                }
            }

            throw new CollisionException();
        }

        #endregion

        #region internal methods

        /// <summary>
        ///   Opens and deserializes an index
        /// </summary>
        /// <param name="indexName"> The index name </param>
        /// <param name="indexPluginName"> The index plugin name </param>
        /// <param name="reader"> Serialization reader </param>
        /// <param name="fallen8"> Fallen-8 </param>
        internal void OpenIndex(string indexName, string indexPluginName, SerializationReader reader)
        {
            IIndex index;
            if (TryResolveIndexPlugin(out index, indexPluginName))
            {
                index.Load(reader, _fallen8);

                if (WriteResource())
                {
                    try
                    {
                        Indices.Add(indexName, index);

                        return;
                    }
                    finally
                    {
                        FinishWriteResource();
                    }
                }

                throw new CollisionException();
            }

            PluginFactory.LogPluginNotFound(_logger, "index", indexPluginName);
        }

        /// <summary>
        ///   Resolves an index plugin by name for BOTH ways an index comes into existence - a create and
        ///   a checkpoint rehydration - so a host-registered index type survives a save/load round trip.
        ///   This namespace's <see cref="Fallen8.Plugins" /> registry is consulted first and assembly
        ///   discovery second, the precedence <c>Fallen8.ResolveCachedPlugin</c> documents for every
        ///   other plugin family. There is no instance cache: an index IS the instance handed back, so a
        ///   fresh one per call is required here rather than merely safe.
        /// </summary>
        private bool TryResolveIndexPlugin(out IIndex index, string indexPluginName)
        {
            // Captured once: Dispose nulls the engine's Plugins while a request thread may still be
            // creating an index.
            var registry = _fallen8?.Plugins;
            if (registry != null && registry.TryActivate(out index, indexPluginName))
            {
                return true;
            }

            return TryFindDiscoveredIndexSuppressed(out index, indexPluginName);
        }

        /// <summary>
        ///   The suppression seam for the discovery half of <see cref="TryResolveIndexPlugin" />: a
        ///   one-line pass-through so the suppression covers exactly this call (the
        ///   <c>Fallen8.ReplaySubGraphCreateSuppressed</c> pattern). Suppressed rather than propagated
        ///   because discovery degrades to a clean not-found - see the <see cref="PluginFactory" /> type
        ///   remarks for that contract - and the supported way to reach an index type in a trimmed or
        ///   browser host is <c>Fallen8.RegisterPluginType</c>, which resolves through the registry
        ///   above with no reflection over strings at all. Index creation would otherwise hand every
        ///   caller a trim warning for a capability the registry path provides trim-safely.
        /// </summary>
        [UnconditionalSuppressMessage("Trimming", "IL2026",
            Justification = "Discovery degrades to a clean not-found; the trim-safe path is host type registration. See the doc comment.")]
        private static bool TryFindDiscoveredIndexSuppressed(out IIndex index, string indexPluginName)
        {
            return PluginFactory.TryFindPlugin(out index, indexPluginName);
        }

        #endregion
    }
}
