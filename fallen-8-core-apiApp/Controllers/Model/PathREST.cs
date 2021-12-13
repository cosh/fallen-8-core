using NoSQL.GraphDB.Core.Algorithms.Path;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    /// The REST pendant to Path
    /// </summary>
    public sealed class PathREST
    {
        #region data

        /// <summary>
        /// The path elements
        /// </summary>
        [Required]
        public List<PathElementREST> PathElements;

        /// <summary>
        /// The weight of the path
        /// </summary>
        [Required]
        public double TotalWeight;

        #endregion

        #region constructor

        /// <summary>
        /// Creates a new PathREST instance
        /// </summary>
        public PathREST(Path toBeTransferredResult)
        {
            var toBeTransferredPathElements = toBeTransferredResult.GetPathElements();

            PathElements = new List<PathElementREST>(toBeTransferredPathElements.Count);

            for (var i = 0; i < toBeTransferredPathElements.Count; i++)
            {
                PathElements.Add(new PathElementREST(toBeTransferredPathElements[i]));
            }

            TotalWeight = toBeTransferredResult.Weight;
        }

        #endregion
    }
}
