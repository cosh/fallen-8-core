using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Tests.Helper;
using System;
using System.Collections.Generic;

namespace NoSQL.GraphDB.Core.Tests
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
        public void FindMallory()
        {
            List<AGraphElement> result;
            _fallen8.GraphScan(out result, 0, "Alice", Expression.BinaryOperator.Equals);

            Assert.IsNotNull(result);

            Assert.AreEqual(1, result.Count);

            VertexModel alice = (VertexModel)result[0];

            String name;
            alice.TryGetProperty(out name, 0);

            Assert.AreEqual("Alice", name);
        }
    }
}
