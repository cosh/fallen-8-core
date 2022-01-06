// MIT License
//
// TestGraphGenerator.cs
//
// Copyright (c) 2021 Henning Rauch
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;

namespace NoSQL.GraphDB.Tests.Helper
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
