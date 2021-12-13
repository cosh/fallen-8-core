using System;
using System.ComponentModel.DataAnnotations;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   The index update specification
    /// </summary>
    public class IndexAddToSpecification
    {
        /// <summary>
        ///   The graph element identifier
        /// </summary>
        [Required]
        public Int32 GraphElementId { get; set; }

        /// <summary>
        ///   The specification of the index key
        /// </summary>
        [Required]
        public PropertySpecification Key { get; set; }
    }
}
