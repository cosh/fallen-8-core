using System;
using System.Collections.Generic;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   The Fallen-8 status
    /// </summary>
    public sealed class StatusREST
    {
        /// <summary>
        ///   The used memory
        /// </summary>
        public Int64 UsedMemory { get; set; }

        /// <summary>
        ///   Vertex count
        /// </summary>
        public Int32 VertexCount { get; set; }

        /// <summary>
        ///   Edge count
        /// </summary>
        public Int32 EdgeCount { get; set; }

        /// <summary>
        ///   Available index plugins
        /// </summary>
        public List<String> AvailableIndexPlugins { get; set; }

        /// <summary>
        ///   Available path plugins
        /// </summary>
        public List<String> AvailablePathPlugins { get; set; }

        /// <summary>
        ///   Available index plugins
        /// </summary>
        public List<String> AvailableServicePlugins { get; set; }
    }
}
