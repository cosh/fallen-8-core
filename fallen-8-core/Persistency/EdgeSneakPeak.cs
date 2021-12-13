using NoSQL.GraphDB.Core.Model;
using System;
using System.Collections.Generic;
using System.Text;

namespace NoSQL.GraphDB.Core.Persistency
{
    /// <summary>
    /// Edge sneak peak.
    /// </summary>
    public struct EdgeSneakPeak
    {
        /// <summary>
        /// The identifier of the edge.
        /// </summary>
        public Int32 Id;

        /// <summary>
        /// The creation date.
        /// </summary>
        public UInt32 CreationDate;

        /// <summary>
        /// The modification date.
        /// </summary>
        public UInt32 ModificationDate;

        /// <summary>
        /// The properties.
        /// </summary>
        public PropertyContainer[] Properties;

        /// <summary>
        /// The source vertex identifier.
        /// </summary>
        public Int32 SourceVertexId;

        /// <summary>
        /// The target vertex identifier.
        /// </summary>
        public Int32 TargetVertexId;
    }
}
