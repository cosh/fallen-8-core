using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   The vertex specification
    /// </summary>
    public class VertexSpecification
    {
        /// <summary>
        ///   The creation date
        /// </summary>
        [Required]
        public UInt32 CreationDate { get; set; }

        /// <summary>
        ///   The properties of the vertex
        /// </summary>
        public List<PropertySpecification> Properties { get; set; }
    }
}
