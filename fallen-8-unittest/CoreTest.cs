// MIT License
//
// CoreTest.cs
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

using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Expression;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Tests.Helper;

namespace NoSQL.GraphDB.Tests
{
    [TestClass]
    public class CoreTest
    {
        private readonly Fallen8 _fallen8 = new Fallen8();

        public CoreTest()
        {
            TestGraphGenerator.GenerateSampleGraph(_fallen8);
        }


        [TestMethod]
        public void FindAlice()
        {
            List<AGraphElement> result;
            _fallen8.GraphScan(out result, 0, "Alice", BinaryOperator.Equals);

            Assert.IsNotNull(result);

            Assert.AreEqual(1, result.Count);

            VertexModel alice = (VertexModel)result[0];

            String name;
            alice.TryGetProperty(out name, 0);

            Assert.AreEqual("Alice", name);
        }
    }
}
