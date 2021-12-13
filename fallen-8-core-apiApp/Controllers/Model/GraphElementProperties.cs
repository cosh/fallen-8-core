using System;
using System.Collections.Generic;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   The Fallen-8 REST properties
    /// </summary>
    public sealed class GraphElementProperties
    {
        /// <summary>
        ///   The identifier
        /// </summary>
        public Int32 Id { get; set; }

        /// <summary>
        ///   The creation date
        /// </summary>
        public DateTime CreationDate { get; set; }

        /// <summary>
        ///   The modification date
        /// </summary>
        public DateTime ModificationDate { get; set; }

        /// <summary>
        ///   The properties
        /// </summary>
        public List<Property> Properties { get; set; }
    }
}
