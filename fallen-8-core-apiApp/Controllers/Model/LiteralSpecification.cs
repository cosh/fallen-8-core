using System;
using System.ComponentModel.DataAnnotations;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   The scan specification
    /// </summary>
    public sealed class LiteralSpecification
    {
        /// <summary>
        ///   The value of the literal
        /// </summary>
        [Required]
        public String Value { get; set; }

        /// <summary>
        ///   The type of the literal
        /// </summary>
        public String FullQualifiedTypeName { get; set; }
    }
}
