using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   The edge specification
    /// </summary>
    public class EdgeSpecification
    {
        /// <summary>
        ///   The creation date
        /// </summary>
        [Required]
        public UInt32 CreationDate { get; set; }

        /// <summary>
        ///   The source vertex
        /// </summary>
        [Required]
        public Int32 SourceVertex { get; set; }

        /// <summary>
        ///   The target vertex
        /// </summary>
        [Required]
        public Int32 TargetVertex { get; set; }

        /// <summary>
        ///   The edge property identifier
        /// </summary>
        [Required]
        public UInt16 EdgePropertyId { get; set; }

        /// <summary>
        ///   The properties of the vertex
        /// </summary>
        public List<PropertySpecification> Properties { get; set; }
    }
}
