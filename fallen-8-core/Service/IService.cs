using NoSQL.GraphDB.Core.Persistency;
using NoSQL.GraphDB.Core.Plugin;
using System;
using System.Collections.Generic;
using System.Text;

namespace NoSQL.GraphDB.Core.Service
{
    /// <summary>
    ///   Fallen-8 service interface.
    /// </summary>
    public interface IService : IPlugin, IFallen8Serializable
    {
        /// <summary>
        ///   Gets the start time.
        /// </summary>
        /// <value> The start time. </value>
        DateTime StartTime { get; }

        /// <summary>
        ///   Gets a value indicating whether this instance is running.
        /// </summary>
        /// <value> <c>true</c> if this instance is running; otherwise, <c>false</c> . </value>
        Boolean IsRunning { get; }

        /// <summary>
        ///   Gets the metadata.
        /// </summary>
        /// <value> The metadata. </value>
        IDictionary<String, String> Metadata { get; }

        /// <summary>
        ///   Tries to stop this service.
        /// </summary>
        /// <returns> <c>true</c> if this instance is stopped; otherwise, <c>false</c> . </returns>
        bool TryStop();

        /// <summary>
        ///   Tries to start this service.
        /// </summary>
        /// <returns> <c>true</c> if this instance is started; otherwise, <c>false</c> . </returns>
        bool TryStart();

        /// <summary>
        /// Called when the service plugin was restarted.
        /// </summary>
        void OnServiceRestart();
    }
}
