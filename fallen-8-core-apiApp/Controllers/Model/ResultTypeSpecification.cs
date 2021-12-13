using System.Runtime.Serialization;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   The result type specification
    /// </summary>
    public enum ResultTypeSpecification : byte
    {
        [EnumMember(Value = "V")]
        Vertices,

        [EnumMember(Value = "E")]
        Edges,

        [EnumMember(Value = "Both")]
        Both,
    }
}
