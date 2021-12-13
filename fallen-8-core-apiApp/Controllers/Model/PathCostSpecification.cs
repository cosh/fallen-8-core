using System;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   The path cost specification
    /// </summary>
    public sealed class PathCostSpecification
    {
        /// <summary>
        /// The vertex cost function (JS)
        /// </summary>
        public String Vertex { get; set; }

        /// <summary>
        /// The edge cost function (JS)
        /// </summary>
        public String Edge { get; set; }
    }
}
