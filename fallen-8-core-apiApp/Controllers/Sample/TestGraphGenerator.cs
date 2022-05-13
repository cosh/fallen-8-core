﻿// MIT License
//
// TestGraphGenerator.cs
//
// Copyright (c) 2022 Henning Rauch
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

using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;
using System.Collections.Generic;

namespace NoSQL.GraphDB.App.Controllers.Sample
{
    public static class TestGraphGenerator
    {
        public static SampleStats GenerateSampleGraph(Fallen8 f8)
        {
            uint creationDate = 0;

            #region Vertices

            var vertexTx = new CreateVerticesTransaction();
            vertexTx.AddVertex(creationDate, "person", new List<PropertyContainer>(1) { CreateName("Alice") });
            vertexTx.AddVertex(creationDate, "person", new List<PropertyContainer>(1) { CreateName("Bob") });
            vertexTx.AddVertex(creationDate, "person", new List<PropertyContainer>(1) { CreateName("Eve") });
            vertexTx.AddVertex(creationDate, "person", new List<PropertyContainer>(1) { CreateName("Mallory") });
            vertexTx.AddVertex(creationDate, "person", new List<PropertyContainer>(1) { CreateName("Trent") });

            var vertexTxInfo = f8.EnqueueTransaction(vertexTx);

            vertexTxInfo.WaitUntilFinished();

            var verticesCreated = vertexTx.GetCreatedVertices();
            var alice = verticesCreated[0];
            var bob = verticesCreated[1];
            var eve = verticesCreated[2];
            var mallory = verticesCreated[3];
            var trent = verticesCreated[4];

            #endregion

            #region Edges

            ushort communicatesWith = 10;
            ushort trusts = 11;
            ushort attacks = 12;

            var edgesTx = new CreateEdgesTransaction();
            edgesTx.AddEdge(alice.Id, communicatesWith, bob.Id, creationDate);
            //edgesTx.AddEdge(bob.Id, communicatesWith, alice.Id, creationDate);
            edgesTx.AddEdge(alice.Id, trusts, trent.Id, creationDate);
            edgesTx.AddEdge(bob.Id, trusts, trent.Id, creationDate);
            edgesTx.AddEdge(eve.Id, attacks, alice.Id, creationDate);
            edgesTx.AddEdge(mallory.Id, attacks, alice.Id, creationDate);
            edgesTx.AddEdge(mallory.Id, attacks, bob.Id, creationDate);

            var edgesTxInfo = f8.EnqueueTransaction(edgesTx);

            edgesTxInfo.WaitUntilFinished();

            #endregion

            var stats = new SampleStats() { VertexCount = 5, EdgeCount = 7 };

            return stats;
        }

        private static PropertyContainer CreateName(string name)
        {
            return new PropertyContainer() { PropertyId = "name", Value = name };
        }

        public static SampleStats GenerateAbcGraph(Fallen8 f8)
        {
            uint creationDate = 0;

            #region Vertices

            var vertexTx = new CreateVerticesTransaction();
            vertexTx.AddVertex(creationDate, "letter", new List<PropertyContainer>(1) { CreateName("a") });
            vertexTx.AddVertex(creationDate, "letter", new List<PropertyContainer>(1) { CreateName("b") });
            vertexTx.AddVertex(creationDate, "letter", new List<PropertyContainer>(1) { CreateName("c") });
            vertexTx.AddVertex(creationDate, "letter", new List<PropertyContainer>(1) { CreateName("d") });
            vertexTx.AddVertex(creationDate, "letter", new List<PropertyContainer>(1) { CreateName("e") });
            vertexTx.AddVertex(creationDate, "letter", new List<PropertyContainer>(1) { CreateName("f") });
            vertexTx.AddVertex(creationDate, "letter", new List<PropertyContainer>(1) { CreateName("g") });
            vertexTx.AddVertex(creationDate, "letter", new List<PropertyContainer>(1) { CreateName("h") });
            vertexTx.AddVertex(creationDate, "letter", new List<PropertyContainer>(1) { CreateName("i") });
            vertexTx.AddVertex(creationDate, "letter", new List<PropertyContainer>(1) { CreateName("j") });
            vertexTx.AddVertex(creationDate, "letter", new List<PropertyContainer>(1) { CreateName("k") });
            vertexTx.AddVertex(creationDate, "letter", new List<PropertyContainer>(1) { CreateName("l") });
            vertexTx.AddVertex(creationDate, "letter", new List<PropertyContainer>(1) { CreateName("m") });
            vertexTx.AddVertex(creationDate, "letter", new List<PropertyContainer>(1) { CreateName("n") });
            vertexTx.AddVertex(creationDate, "letter", new List<PropertyContainer>(1) { CreateName("o") });
            vertexTx.AddVertex(creationDate, "letter", new List<PropertyContainer>(1) { CreateName("p") });
            vertexTx.AddVertex(creationDate, "letter", new List<PropertyContainer>(1) { CreateName("q") });
            vertexTx.AddVertex(creationDate, "letter", new List<PropertyContainer>(1) { CreateName("r") });
            vertexTx.AddVertex(creationDate, "letter", new List<PropertyContainer>(1) { CreateName("s") });
            vertexTx.AddVertex(creationDate, "letter", new List<PropertyContainer>(1) { CreateName("t") });
            vertexTx.AddVertex(creationDate, "letter", new List<PropertyContainer>(1) { CreateName("u") });
            vertexTx.AddVertex(creationDate, "letter", new List<PropertyContainer>(1) { CreateName("v") });
            vertexTx.AddVertex(creationDate, "letter", new List<PropertyContainer>(1) { CreateName("w") });
            vertexTx.AddVertex(creationDate, "letter", new List<PropertyContainer>(1) { CreateName("x") });
            vertexTx.AddVertex(creationDate, "letter", new List<PropertyContainer>(1) { CreateName("y") });
            vertexTx.AddVertex(creationDate, "letter", new List<PropertyContainer>(1) { CreateName("z") });

            var vertexTxInfo = f8.EnqueueTransaction(vertexTx);

            vertexTxInfo.WaitUntilFinished();


            #endregion

            #region Edges

            ushort communicatesWith = 0;

            var edgesTx = new CreateEdgesTransaction();
            for (int i = 0; i < 25; i++)
            {
                edgesTx.AddEdge(i, communicatesWith, i+1, creationDate);
            }

            var edgesTxInfo = f8.EnqueueTransaction(edgesTx);

            edgesTxInfo.WaitUntilFinished();

            #endregion

            var stats = new SampleStats() { VertexCount = 26, EdgeCount = 25 };

            return stats;
        }
    }
}
