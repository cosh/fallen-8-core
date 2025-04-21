// MIT License
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
using System;
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

            vertexTx.AddVertex(creationDate, "person", new Dictionary<String, Object>(1) { { "name", "Alice" } });
            vertexTx.AddVertex(creationDate, "person", new Dictionary<String, Object>(1) { { "name", "Bob" } });
            vertexTx.AddVertex(creationDate, "person", new Dictionary<String, Object>(1) { { "name", "Eve" } });
            vertexTx.AddVertex(creationDate, "person", new Dictionary<String, Object>(1) { { "name", "Mallory" } });
            vertexTx.AddVertex(creationDate, "person", new Dictionary<String, Object>(1) { { "name", "Trent" } });

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

            String communicatesWith = "communicatesWith";
            String trusts = "trusts";
            String attacks = "attacks";

            var edgesTx = new CreateEdgesTransaction();
            edgesTx.AddEdge(alice.Id, communicatesWith, bob.Id, creationDate);
            edgesTx.AddEdge(alice.Id, trusts, trent.Id, creationDate, label: trusts); // Explicitly set label
            edgesTx.AddEdge(bob.Id, trusts, trent.Id, creationDate, label: trusts); // Explicitly set label
            edgesTx.AddEdge(eve.Id, attacks, alice.Id, creationDate);
            edgesTx.AddEdge(mallory.Id, attacks, alice.Id, creationDate);
            edgesTx.AddEdge(mallory.Id, attacks, bob.Id, creationDate);

            var edgesTxInfo = f8.EnqueueTransaction(edgesTx);

            edgesTxInfo.WaitUntilFinished();

            #endregion

            var stats = new SampleStats() { VertexCount = 5, EdgeCount = 7 };

            return stats;
        }

        public static SampleStats GenerateAbcGraph(Fallen8 f8)
        {
            uint creationDate = 0;

            #region Vertices

            var vertexTx = new CreateVerticesTransaction();
            vertexTx.AddVertex(creationDate, "letter", new Dictionary<String, Object>(1) { { "name", "a" } });
            vertexTx.AddVertex(creationDate, "letter", new Dictionary<String, Object>(1) { { "name", "b" } });
            vertexTx.AddVertex(creationDate, "letter", new Dictionary<String, Object>(1) { { "name", "c" } });
            vertexTx.AddVertex(creationDate, "letter", new Dictionary<String, Object>(1) { { "name", "d" } });
            vertexTx.AddVertex(creationDate, "letter", new Dictionary<String, Object>(1) { { "name", "e" } });
            vertexTx.AddVertex(creationDate, "letter", new Dictionary<String, Object>(1) { { "name", "f" } });
            vertexTx.AddVertex(creationDate, "letter", new Dictionary<String, Object>(1) { { "name", "g" } });
            vertexTx.AddVertex(creationDate, "letter", new Dictionary<String, Object>(1) { { "name", "h" } });
            vertexTx.AddVertex(creationDate, "letter", new Dictionary<String, Object>(1) { { "name", "i" } });
            vertexTx.AddVertex(creationDate, "letter", new Dictionary<String, Object>(1) { { "name", "j" } });
            vertexTx.AddVertex(creationDate, "letter", new Dictionary<String, Object>(1) { { "name", "k" } });
            vertexTx.AddVertex(creationDate, "letter", new Dictionary<String, Object>(1) { { "name", "l" } });
            vertexTx.AddVertex(creationDate, "letter", new Dictionary<String, Object>(1) { { "name", "m" } });
            vertexTx.AddVertex(creationDate, "letter", new Dictionary<String, Object>(1) { { "name", "n" } });
            vertexTx.AddVertex(creationDate, "letter", new Dictionary<String, Object>(1) { { "name", "o" } });
            vertexTx.AddVertex(creationDate, "letter", new Dictionary<String, Object>(1) { { "name", "p" } });
            vertexTx.AddVertex(creationDate, "letter", new Dictionary<String, Object>(1) { { "name", "q" } });
            vertexTx.AddVertex(creationDate, "letter", new Dictionary<String, Object>(1) { { "name", "r" } });
            vertexTx.AddVertex(creationDate, "letter", new Dictionary<String, Object>(1) { { "name", "s" } });
            vertexTx.AddVertex(creationDate, "letter", new Dictionary<String, Object>(1) { { "name", "t" } });
            vertexTx.AddVertex(creationDate, "letter", new Dictionary<String, Object>(1) { { "name", "u" } });
            vertexTx.AddVertex(creationDate, "letter", new Dictionary<String, Object>(1) { { "name", "v" } });
            vertexTx.AddVertex(creationDate, "letter", new Dictionary<String, Object>(1) { { "name", "w" } });
            vertexTx.AddVertex(creationDate, "letter", new Dictionary<String, Object>(1) { { "name", "x" } });
            vertexTx.AddVertex(creationDate, "letter", new Dictionary<String, Object>(1) { { "name", "y" } });
            vertexTx.AddVertex(creationDate, "letter", new Dictionary<String, Object>(1) { { "name", "z" } });

            var vertexTxInfo = f8.EnqueueTransaction(vertexTx);

            vertexTxInfo.WaitUntilFinished();


            #endregion

            #region Edges

            String communicatesWith = "gefolgtVon";

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
