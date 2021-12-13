using NoSQL.GraphDB.Core.Expression;
using System.ComponentModel.DataAnnotations;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   The scan specification
    /// </summary>
    public class ScanSpecification
    {
        /// <summary>
        ///   Binary Operator
        /// </summary>
        [Required]
        public BinaryOperator Operator { get; set; }

        /// <summary>
        ///   Literal specification
        /// </summary>
        [Required]
        public LiteralSpecification Literal { get; set; }

        /// <summary>
        ///   Result type specification
        /// </summary>
        [Required]
        public ResultTypeSpecification ResultType { get; set; }
    }
}
