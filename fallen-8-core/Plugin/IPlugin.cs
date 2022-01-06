using System;
using System.Collections.Generic;

namespace NoSQL.GraphDB.Core.Plugin
{
    public interface IPlugin : IDisposable
    {
        /// <summary>
        ///   Gets the name.
        /// </summary>
        /// <value> The name. </value>
        String PluginName { get; }

        /// <summary>
        ///   Gets or sets the plugin category.
        /// </summary>
        /// <value> The plugin category. </value>
        Type PluginCategory { get; }

        /// <summary>
        ///   Gets the description.
        /// </summary>
        /// <value> The description. </value>
        String Description { get; }

        /// <summary>
        ///   Gets the manufacturer.
        /// </summary>
        /// <value> The manufacturer. </value>
        String Manufacturer { get; }

        /// <summary>
        ///   Tries to inititialize the plugin.
        /// </summary>
        /// <param name='fallen8'> A fallen-8 session. </param>
        /// <param name='parameter'> Parameter. </param>
        /// <returns> The initialized plugin </returns>
        void Initialize(Fallen8 fallen8, IDictionary<String, Object> parameter);
    }
}
