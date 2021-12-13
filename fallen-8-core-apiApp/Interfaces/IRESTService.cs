using NoSQL.GraphDB.Core.Persistency;
using System;

namespace NoSQL.GraphDB.App.Interfaces
{
    public interface IRESTService : IDisposable, IFallen8Serializable
    {
        void Shutdown();
    }
}
