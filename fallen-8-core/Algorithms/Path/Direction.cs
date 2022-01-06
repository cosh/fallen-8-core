using System.Runtime.Serialization;

namespace NoSQL.GraphDB.Core.Algorithms.Path
{
    /// <summary>
    /// The direction enum
    /// </summary>
    public enum Direction : byte
    {
        [EnumMember(Value = "In")]
        IncomingEdge,

        [EnumMember(Value = "Out")]
        OutgoingEdge
    }
}
