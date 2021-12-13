using NoSQL.GraphDB.Core.Model;
using System;
using System.Collections.Generic;
using System.Text;

namespace NoSQL.GraphDB.Core.Index.Fulltext
{
	/// <summary>
	/// Fulltext search result element.
	/// </summary>
	public class FulltextSearchResultElement
	{
		#region data

		/// <summary>
		/// Gets or sets the graph element.
		/// </summary>
		/// <value>
		/// The graph element.
		/// </value>
		public AGraphElement GraphElement { get; private set; }

		/// <summary>
		/// Gets or sets the highlights.
		/// </summary>
		/// <value>
		/// The highlights.
		/// </value>
		public IList<string> Highlights { get; private set; }

		/// <summary>
		/// Gets or sets the score.
		/// </summary>
		/// <value>
		/// The score.
		/// </value>
		public Double Score { get; private set; }

		#endregion

		#region constructor

		/// <summary>
		/// Create a new FulltextSearchResultElement instance
		/// </summary>
		/// <param name="graphElement">Graph element</param>
		/// <param name="score">Score</param>
		/// <param name="highlights">Highlights</param>
		public FulltextSearchResultElement(AGraphElement graphElement, Double score, IEnumerable<String> highlights = null)
		{
			GraphElement = graphElement;
			Score = score;
			Highlights = highlights == null ? new List<string>() : new List<string>(highlights);
		}

		#endregion

		#region public methods

		/// <summary>
		/// Adds an highlight
		/// </summary>
		/// <param name="highlight">Highlight</param>
		public void AddHighlight(String highlight)
		{
			Highlights.Add(highlight);
		}

		#endregion
	}
}
