using System;
using System.ComponentModel.DataAnnotations;

namespace NoSQL.GraphDB.App.Controllers.Model
{

    /// <summary>
    ///   The index scan specification
    /// </summary>
    public sealed class IndexScanSpecification : ScanSpecification
    {
        /// <summary>
        ///   Index identifier
        /// </summary>
        [Required]
        public String IndexId { get; set; }
    }
}
