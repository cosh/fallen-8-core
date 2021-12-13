using System;
using System.ComponentModel.DataAnnotations;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    /// The search distance request object for spatial queries
    /// </summary>
    public sealed class SearchDistanceSpecification
    {
        #region data

        /// <summary>
        ///   Index identifier
        /// </summary>
        [Required]
        public String IndexId { get; set; }

        /// <summary>
        /// The graph element identifier.
        /// </summary>
        [Required]
        public Int32 GraphElementId;

        /// <summary>
        /// The distance.
        /// </summary>
        [Required]
        public float Distance;

        #endregion
    }
}
