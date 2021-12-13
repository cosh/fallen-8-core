using System;
using System.Collections.Generic;
using System.Text;

namespace NoSQL.GraphDB.Core.Model
{
    /// <summary>
    /// edge container.
    /// </summary>
    public struct EdgeContainer
    {
        #region Data

        /// <summary>
        /// Gets or sets the edge property identifier.
        /// </summary>
        /// <value>
        /// The edge property identifier.
        /// </value>
        public UInt16 EdgePropertyId { get; private set; }

        /// <summary>
        /// Gets or sets the value.
        /// </summary>
        /// <value>
        /// The value.
        /// </value>
        public List<EdgeModel> Edges { get; private set; }

        #endregion

        #region constructor

        public EdgeContainer(UInt16 edgePropertyId, List<EdgeModel> edges) : this()
        {
            EdgePropertyId = edgePropertyId;
            Edges = edges;
        }

        #endregion

        #region overrides

        public override string ToString()
        {
            return EdgePropertyId + ": |E|=" + Edges.Count;
        }

        #endregion
    }
}
