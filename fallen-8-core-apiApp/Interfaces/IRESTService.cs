using System;
using NoSQL.GraphDB.Core.Persistency;

namespace NoSQL.GraphDB.App.Interfaces
{
    public interface IRESTService : IDisposable, IFallen8Serializable
    {
        void Shutdown();
    }
}
