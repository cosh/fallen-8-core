using System;

namespace NoSQL.GraphDB.Core.Index.Fulltext
{
    /// <summary>
    /// Fallen8 fulltext index interface.
    /// </summary>
    public interface IFulltextIndex : IIndex
    {
        /// <summary>
        /// Tries to query the fulltext index.
        /// </summary>
        /// <returns>
        /// <c>true</c> if something was found; otherwise, <c>false</c>.
        /// </returns>
        /// <param name='result'>
        /// Result.
        /// </param>
        /// <param name='query'>
        /// Query.
        /// </param>
        Boolean TryQuery(out FulltextSearchResult result, string query);
    }
}
