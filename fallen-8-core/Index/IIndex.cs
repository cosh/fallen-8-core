using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Persistency;
using NoSQL.GraphDB.Core.Plugin;

namespace NoSQL.GraphDB.Core.Index
{
    /// <summary>
    ///   The Fallen8 index interface.
    /// </summary>
    public interface IIndex : IPlugin, IFallen8Serializable
    {
        /// <summary>
        ///   Count of the keys.
        /// </summary>
        /// <returns> The key count. </returns>
        Int32 CountOfKeys();

        /// <summary>
        ///   Count of the values.
        /// </summary>
        /// <returns> The value count. </returns>
        Int32 CountOfValues();

        /// <summary>
        ///   Tries to add or update.
        /// </summary>
        /// <param name='key'> Key. </param>
        /// <param name='graphElement'> Graph element. </param>
        void AddOrUpdate(Object key, AGraphElement graphElement);

        /// <summary>
        ///   Tries to remove a key.
        /// </summary>
        /// <returns> <c>true</c> if something was removed; otherwise, <c>false</c> . </returns>
        /// <param name='key'> Key. </param>
        Boolean TryRemoveKey(Object key);

        /// <summary>
        ///   Remove a value.
        /// </summary>
        /// <param name='graphElement'> Graph element. </param>
        void RemoveValue(AGraphElement graphElement);

        /// <summary>
        ///   Wipe this instance.
        /// </summary>
        void Wipe();

        /// <summary>
        ///   Gets the keys.
        /// </summary>
        /// <returns> The keys. </returns>
        IEnumerable<Object> GetKeys();

        /// <summary>
        ///   Gets the key values.
        /// </summary>
        /// <returns> The key values. </returns>
        IEnumerable<KeyValuePair<object, ReadOnlyCollection<AGraphElement>>> GetKeyValues();

        /// <summary>
        ///   Gets the value.
        /// </summary>
        /// <returns> <c>true</c> if something was found; otherwise, <c>false</c> . </returns>
        /// <param name='result'> Result. </param>
        /// <param name='key'> Key. </param>
        Boolean TryGetValue(out ReadOnlyCollection<AGraphElement> result, Object key);
    }
}
