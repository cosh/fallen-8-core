using NoSQL.GraphDB.Core.Model;
using System;
using System.Collections.Generic;
using System.Text;

namespace NoSQL.GraphDB.Core.Algorithms.Path
{
    /// <summary>
    /// Path delegates
    /// </summary>
    public static class PathDelegates
    {
        /// <summary>
        /// Filter for edge properties
        /// </summary>
        /// <param name="edgePropertyId">The edge property identifier.</param>
        /// <param name="direction">The direction of the edge.</param>
        /// <returns>False will filter the edge property</returns>
        public delegate bool EdgePropertyFilter(UInt16 edgePropertyId, Direction direction);

        /// <summary>
        /// Filter for edges
        /// </summary>
        /// <param name="edge">The edge.</param>
        /// <param name="direction">The direction of the edge.</param>
        /// <returns>False will filter the edge</returns>
        public delegate bool EdgeFilter(EdgeModel edge, Direction direction);

        /// <summary>
        /// Filter for vertices
        /// </summary>
        /// <param name="edge">The vertex.</param>
        /// <returns>False will filter the vertex</returns>
        public delegate bool VertexFilter(VertexModel edge);

        /// <summary>
        /// Sets the cost of for the edge
        /// </summary>
        /// <param name="edge">The edge</param>
        /// <returns>The order key.</returns>
        public delegate double EdgeCost(EdgeModel edge);

        /// <summary>
        /// Sets the cost of for the vertex
        /// </summary>
        /// <param name="vertex">The vertex</param>
        public delegate double VertexCost(VertexModel vertex);
    }
}
