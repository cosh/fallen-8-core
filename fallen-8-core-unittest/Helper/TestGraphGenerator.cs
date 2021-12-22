using NoSQL.GraphDB.Core.Model;
using System;
using System.Collections.Generic;
using System.Text;

namespace NoSQL.GraphDB.Core.Tests.Helper
{
    public static class TestGraphGenerator
    {
        public static void GenerateSampleGraph(Fallen8 f8)
        {
            uint creationDate = 0;

            #region Vertices

            var alice = f8.CreateVertex(creationDate, new PropertyContainer[] { CreateName("Alice") });

            var bob = f8.CreateVertex(creationDate, new PropertyContainer[] { CreateName("Bob") });

            var eve = f8.CreateVertex(creationDate, new PropertyContainer[] { CreateName("Eve") });

            var mallory = f8.CreateVertex(creationDate, new PropertyContainer[] { CreateName("Mallory") });

            var trent = f8.CreateVertex(creationDate, new PropertyContainer[] { CreateName("Trent") });

            #endregion

            #region Edges

            ushort communicatesWith = 10;
            ushort trusts = 11;
            ushort attacks = 12;

            f8.CreateEdge(alice.Id, communicatesWith, bob.Id, creationDate);

            f8.CreateEdge(bob.Id, communicatesWith, alice.Id, creationDate);

            f8.CreateEdge(alice.Id, trusts, trent.Id, creationDate);

            f8.CreateEdge(bob.Id, trusts, trent.Id, creationDate);

            f8.CreateEdge(eve.Id, attacks, alice.Id, creationDate);

            f8.CreateEdge(mallory.Id, attacks, alice.Id, creationDate);

            f8.CreateEdge(mallory.Id, attacks, bob.Id, creationDate);

            #endregion
        }

        private static PropertyContainer CreateName(string name)
        {
            return new PropertyContainer() { PropertyId = 0, Value = name };
        }
    }
}
