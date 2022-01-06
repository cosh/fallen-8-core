using System.Runtime.Serialization;

namespace NoSQL.GraphDB.Core.Expression
{
    /// <summary>
	/// Binary operator.
	/// </summary>
    public enum BinaryOperator
    {
        [EnumMember(Value = "==")]
        Equals,

        [EnumMember(Value = ">")]
        Greater,

        [EnumMember(Value = ">=")]
        GreaterOrEquals,

        [EnumMember(Value = "<")]
        Lower,

        [EnumMember(Value = "<=")]
        LowerOrEquals,

        [EnumMember(Value = "!=")]
        NotEquals
    }
}
