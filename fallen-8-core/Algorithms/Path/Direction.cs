using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Text;

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
