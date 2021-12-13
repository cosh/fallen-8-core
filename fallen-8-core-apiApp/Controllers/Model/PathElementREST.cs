using NoSQL.GraphDB.Core.Algorithms.Path;
using System;
using System.ComponentModel.DataAnnotations;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    /// The REST pendant to PathElement
    /// </summary>
    public sealed class PathElementREST
    {
        #region data

        /// <summary>
        /// The source vertex identifier
        /// </summary>
        [Required]
        public Int64 SourceVertexId;

        /// <summary>
        /// The target vertex identifier
        /// </summary>
        [Required]
        public Int64 TargetVertexId;

        /// <summary>
        /// The edge identifier
        /// </summary>
        [Required]
        public Int64 EdgeId;

        /// <summary>
        /// The edge property identifier
        /// </summary>
        [Required]
        public UInt16 EdgePropertyId;

        /// <summary>
        /// The direction of the edge
        /// </summary>
        [Required]
        public Direction Direction;

        /// <summary>
        /// The weight of the pathelement
        /// </summary>
        [Required]
        public double Weight;

        #endregion

        #region constructor

        /// <summary>
        /// Creates a new PathElementREST instance
        /// </summary>
        public PathElementREST(PathElement toBeTransferredResult)
        {
            SourceVertexId = toBeTransferredResult.SourceVertex.Id;
            TargetVertexId = toBeTransferredResult.TargetVertex.Id;
            EdgeId = toBeTransferredResult.Edge.Id;
            EdgePropertyId = toBeTransferredResult.EdgePropertyId;
            Direction = toBeTransferredResult.Direction;
            Weight = toBeTransferredResult.Weight;
        }

        #endregion
    }
}
