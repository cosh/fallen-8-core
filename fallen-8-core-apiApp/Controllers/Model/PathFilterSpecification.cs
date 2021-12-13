using System;
using System.ComponentModel.DataAnnotations;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   The path filter specification
    /// </summary>
    public sealed class PathFilterSpecification
    {
        /// <summary>
        /// The edge property filter function (JS)
        /// </summary>
        [Required]
        public String EdgeProperty { get; set; }

        /// <summary>
        /// The vertex filter function (JS)
        /// </summary>
        [Required]
        public String Vertex { get; set; }

        /// <summary>
        /// The edge filter function (JS)
        /// </summary>
        [Required]
        public String Edge { get; set; }
    }
}
