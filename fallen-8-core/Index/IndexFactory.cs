using NoSQL.GraphDB.Core.Error;
using NoSQL.GraphDB.Core.Helper;
using NoSQL.GraphDB.Core.Log;
using NoSQL.GraphDB.Core.Plugin;
using NoSQL.GraphDB.Serializer;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

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

        #endregion

        #region constructor

        /// <summary>
        ///   Initializes a new instance of the IndexFactory class.
        /// </summary>
        public IndexFactory()
        {
            Indices = new Dictionary<String, IIndex>();
        }

        #endregion

        #region IFallen8IndexFactory implementation

        /// <summary>
        ///   Gets the available index plugins.
        /// </summary>
        /// <returns> The available index plugins. </returns>
        public IEnumerable<String> GetAvailableIndexPlugins()
        {
            IEnumerable<String> result;

            PluginFactory.TryGetAvailablePlugins<IIndex>(out result);

            return result;
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
            if (PluginFactory.TryFindPlugin(out index, indexTypeName))
            {
                try
                {
                    index.Initialize(null, parameter);

                    if (WriteResource())
                    {
                        try
                        {
                            if (!Indices.ContainsKey(indexName))
                            {
                                Indices.Add(indexName, index);

                                return true;
                            }
                            Logger.LogError(String.Format("The index with name \"{0}\" already exists.", indexName));
                        }
                        finally
                        {
                            FinishWriteResource();
                        }
                    }
                }
                catch (Exception)
                {
                    index = null;
                    return false;
                }
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

            throw new CollisionException(this);
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

            throw new CollisionException(this);
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

            throw new CollisionException(this);
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
        internal void OpenIndex(string indexName, string indexPluginName, SerializationReader reader, Fallen8 fallen8)
        {
            IIndex index;
            if (PluginFactory.TryFindPlugin(out index, indexPluginName))
            {
                index.Load(reader, fallen8);

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

                throw new CollisionException(this);
            }

            Logger.LogError(String.Format("Could not find index plugin with name \"{0}\".", indexPluginName));
        }

        #endregion
    }
}
