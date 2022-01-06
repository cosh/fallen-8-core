using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using NoSQL.GraphDB.Core.Index.Fulltext;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   The pendant to the embedded FulltextSearchResult
    /// </summary>
    public sealed class FulltextSearchResultREST
    {
        #region data

        /// <summary>
        /// Gets or sets the maximum score.
        /// </summary>
        /// <value>
        /// The maximum score.
        /// </value>
        [Required]
        public readonly Double MaximumScore;

        /// <summary>
        /// Gets or sets the elements.
        /// </summary>
        /// <value>
        /// The elements.
        /// </value>
        [Required]
        public readonly List<FulltextSearchResultElementREST> Elements;

        #endregion

        #region constructor

        /// <summary>
        /// Creates a new FulltextSearchResultREST instance
        /// </summary>
        public FulltextSearchResultREST(FulltextSearchResult toBeTransferredResult)
        {
            if (toBeTransferredResult != null)
            {
                MaximumScore = toBeTransferredResult.MaximumScore;
                Elements = new List<FulltextSearchResultElementREST>(toBeTransferredResult.Elements.Count);
                for (int i = 0; i < toBeTransferredResult.Elements.Count; i++)
                {
                    Elements.Add(new FulltextSearchResultElementREST(toBeTransferredResult.Elements[i]));
                }
            }
        }

        #endregion
    }
}
