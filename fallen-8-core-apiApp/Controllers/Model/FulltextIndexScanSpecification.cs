using System;
using System.ComponentModel.DataAnnotations;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    /// The fulltext index request object
    /// </summary>
    public class FulltextIndexScanSpecification
    {
        #region data

        /// <summary>
        ///   Index identifier
        /// </summary>
        [Required]
        public String IndexId { get; set; }

        /// <summary>
        /// The request string
        /// </summary>
		[Required]
        public String RequestString;

        #endregion
    }
}
