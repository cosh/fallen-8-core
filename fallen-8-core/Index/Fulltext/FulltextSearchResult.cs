using System;
using System.Collections.Generic;
using System.Text;

namespace NoSQL.GraphDB.Core.Index.Fulltext
{
    /// <summary>
	/// Fulltext search result.
	/// </summary>
	public sealed class FulltextSearchResult
    {
        #region data

        /// <summary>
		/// Gets or sets the maximum score.
		/// </summary>
		/// <value>
		/// The maximum score.
		/// </value>
		public Double MaximumScore { get; set; }

        /// <summary>
        /// Gets or sets the elements.
        /// </summary>
        /// <value>
        /// The elements.
        /// </value>
        public List<FulltextSearchResultElement> Elements { get; set; }

        #endregion

        #region constructor

        /// <summary>
        /// Creates a new FulltextSearchResult instance
        /// </summary>
        public FulltextSearchResult()
        {
            Elements = new List<FulltextSearchResultElement>();
        }

        #endregion

        #region public methods

        /// <summary>
        /// Adds an element
        /// </summary>
        /// <param name="element">Element</param>
        public void AddElement(FulltextSearchResultElement element)
        {
            Elements.Add(element);

            if (element.Score > MaximumScore)
            {
                MaximumScore = element.Score;
            }
        }

        #endregion


    }
}
