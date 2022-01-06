using System;
using System.Collections.ObjectModel;
using NoSQL.GraphDB.Core.Model;

namespace NoSQL.GraphDB.Core.Index.Range
{
    /// <summary>
    /// Fallen8 range index.
    /// </summary>
    public interface IRangeIndex : IIndex
    {
        /// <summary>
        /// Searches for graph elements lower than the the key.
        /// </summary>
        /// <returns>
        /// <c>true</c> if something was found; otherwise, <c>false</c>.
        /// </returns>
        /// <param name='result'>
        /// Result.
        /// </param>
        /// <param name='key'>
        /// Key.
        /// </param>
        /// <param name='includeKey'>
        /// Include the key.
        /// </param>
        Boolean LowerThan(out ReadOnlyCollection<AGraphElement> result, IComparable key, bool includeKey = true);

        /// <summary>
        /// Searches for graph elements greater than the the key.
        /// </summary>
        /// <returns>
        /// <c>true</c> if something was found; otherwise, <c>false</c>.
        /// </returns>
        /// <param name='result'>
        /// Result.
        /// </param>
        /// <param name='key'>
        /// Key.
        /// </param>
        /// <param name='includeKey'>
        /// Include the key.
        /// </param>
        Boolean GreaterThan(out ReadOnlyCollection<AGraphElement> result, IComparable key, bool includeKey = true);

        /// <summary>
        /// Searches for graph elements between a specified range
        /// </summary>
        /// <returns>
        /// <c>true</c> if something was found; otherwise, <c>false</c>.
        /// </returns>
        /// <param name='result'>
        /// Result.
        /// </param>
        /// <param name='lowerLimit'>
        /// Lower limit.
        /// </param>
        /// <param name='upperLimit'>
        /// Upper limit.
        /// </param>
        /// <param name='includeLowerLimit'>
        /// Include the lower limit.
        /// </param>
        /// <param name='includeUpperLimit'>
        /// Include the upper limit.
        /// </param>
        Boolean Between(out ReadOnlyCollection<AGraphElement> result, IComparable lowerLimit, IComparable upperLimit, bool includeLowerLimit = true, bool includeUpperLimit = true);
    }
}
