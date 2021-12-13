using System;
using System.ComponentModel.DataAnnotations;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   The property
    /// </summary>
    public class Property
    {
        // <summary>
        ///   The property id
        /// </summary>
        [Required]
        public UInt16 PropertyId { get; set; }

        /// <summary>
        ///   The property string representation
        /// </summary>
        [Required]
        public String PropertyValue { get; set; }
    }
}
