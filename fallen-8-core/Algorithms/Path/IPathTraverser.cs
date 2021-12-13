using System;
using System.Collections.Generic;
using System.Text;

namespace NoSQL.GraphDB.Core.Algorithms.Path
{
    public interface IPathTraverser
    {
        PathDelegates.EdgePropertyFilter EdgePropertyFilter();
        PathDelegates.VertexFilter VertexFilter();
        PathDelegates.EdgeFilter EdgeFilter();
        PathDelegates.EdgeCost EdgeCost();
        PathDelegates.VertexCost VertexCost();
    }
}
