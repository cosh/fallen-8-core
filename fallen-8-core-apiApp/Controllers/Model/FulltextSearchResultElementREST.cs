using NoSQL.GraphDB.Core.Index.Fulltext;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace NoSQL.GraphDB.App.Controllers.Model
{
	/// <summary>
	///   The pendant to the embedded FulltextSearchResultElement
	/// </summary>
	public sealed class FulltextSearchResultElementREST
	{
		#region data

		/// <summary>
		/// Gets or sets the graph element id.
		/// </summary>
		/// <value>
		/// The graph element.
		/// </value>
		[Required]
		public readonly Int32 GraphElementId;

		/// <summary>
		/// Gets or sets the highlights.
		/// </summary>
		/// <value>
		/// The highlights.
		/// </value>
		[Required]
		public readonly IList<string> Highlights;

		/// <summary>
		/// Gets or sets the score.
		/// </summary>
		/// <value>
		/// The score.
		/// </value>
		[Required]
		public readonly Double Score;

		#endregion

		#region constructor

		/// <summary>
		/// Creates a new FulltextSearchResultElementREST instance
		/// </summary>
		public FulltextSearchResultElementREST(FulltextSearchResultElement toBeTransferredResult)
		{
			if (toBeTransferredResult != null)
			{
				GraphElementId = toBeTransferredResult.GraphElement.Id;
				Highlights = toBeTransferredResult.Highlights;
				Score = toBeTransferredResult.Score;
			}
		}

		#endregion
	}
}
