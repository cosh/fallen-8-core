using System;
using System.ComponentModel.DataAnnotations;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   The path specification
    /// </summary>
    public sealed class PathSpecification
    {
        /// <summary>
        ///   The desired path algorithm plugin name
        /// </summary>
        [Required]
        public String PathAlgorithmName { get; set; } = "BLS";

        /// <summary>
        ///   The maximum depth
        /// </summary>
        [Required]
        public UInt16 MaxDepth { get; set; } = 7;

        /// <summary>
        ///   The maximum result count
        /// </summary>
        public UInt16 MaxResults { get; set; } = UInt16.MaxValue;

        /// <summary>
        ///   The maximum path weight
        /// </summary>
        public Double MaxPathWeight { get; set; } = Double.MaxValue;

        /// <summary>
        ///   The path filter specification
        /// </summary>
        public PathFilterSpecification Filter { get; set; }

        /// <summary>
        ///   The path cost specification
        /// </summary>
        public PathCostSpecification Cost { get; set; }
    }
}
