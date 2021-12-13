using System;
using System.ComponentModel.DataAnnotations;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   The range scan specification
    /// </summary>
    public sealed class RangeIndexScanSpecification
    {
        /// <summary>
        ///   Index identifier
        /// </summary>
        [Required]
        public String IndexId { get; set; }

        /// <summary>
        ///   Left limit
        /// </summary>
        [Required]
        public String LeftLimit { get; set; }

        /// <summary>
        ///   Right limit
        /// </summary>
        [Required]
        public String RightLimit { get; set; }

        /// <summary>
        ///   The type of the literals
        /// </summary>
        [Required]
        public String FullQualifiedTypeName { get; set; }

        /// <summary>
        ///   Include left limit
        /// </summary>
        public Boolean IncludeLeft { get; set; }

        /// <summary>
        ///   Include right limit
        /// </summary>
        public Boolean IncludeRight { get; set; }

        /// <summary>
        ///   Result type specification
        /// </summary>
        [Required]
        public ResultTypeSpecification ResultType { get; set; }
    }
}
