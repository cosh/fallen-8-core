using System;

namespace NoSQL.GraphDB.Core.Persistency
{
    /// <summary>
    /// A helper struct to track the edges that have to be added to the vertices
    /// </summary>
    public struct EdgeOnVertexToDo
    {
        /// <summary>
        /// The vertex identifier.
        /// </summary>
        public Int32 VertexId;

        /// <summary>
        /// The edge property identifier.
        /// </summary>
        public UInt16 EdgePropertyId;

        /// <summary>
        /// Is this an incoming edge?
        /// </summary>
        public Boolean IsIncomingEdge;
    }
}
