using System;
using System.ComponentModel.DataAnnotations;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   The property specification
    /// </summary>
    public class PropertySpecification
    {
        // <summary>
        ///   The property id
        /// </summary>
        [Required]
        public UInt16 PropertyId { get; set; }

        // <summary>
        ///   The type name
        /// </summary>
        [Required]
        public String FullQualifiedTypeName { get; set; }

        /// <summary>
        ///   The property string representation
        /// </summary>
        [Required]
        public String PropertyValue { get; set; }
    }
}
