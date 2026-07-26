// MIT License
//
// GraphScanLabelFilterTest.cs
//
// Copyright (c) 2025 Henning Rauch
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

using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Expression;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Pins the GraphScan label filter as FUNCTIONAL: the interestingLabel argument (and the REST
    /// ScanSpecification.Label it carries) restricts a property scan to elements of that label.
    /// Before the fix the private FindElements overload dropped interestingLabel, so the filter was a
    /// silent no-op that scanned every label.
    /// </summary>
    [TestClass]
    public class GraphScanLabelFilterTest
    {
        [TestMethod]
        public void GraphScan_WithInterestingLabel_RestrictsToThatLabel()
        {
            var f8 = new Fallen8(TestLoggerFactory.Create());
            var vtx = new CreateVerticesTransaction();
            vtx.AddVertex(1u, "person", new Dictionary<string, object> { { "team", "x" } });
            vtx.AddVertex(1u, "person", new Dictionary<string, object> { { "team", "x" } });
            vtx.AddVertex(1u, "company", new Dictionary<string, object> { { "team", "x" } });
            f8.EnqueueTransaction(vtx).WaitUntilFinished();

            // No label filter: every element with team=="x" (all three).
            Assert.IsTrue(f8.GraphScan(out var all, "team", "x"), "Unfiltered scan should match.");
            Assert.AreEqual(3, all.Count, "No label filter scans every label.");

            // interestingLabel="person": only the two person vertices (this was a no-op before the fix).
            Assert.IsTrue(f8.GraphScan(out var persons, "team", "x", BinaryOperator.Equals, "person"),
                "The label-filtered scan should still match the person vertices.");
            Assert.AreEqual(2, persons.Count, "The label filter must restrict the scan to 'person' elements.");
            Assert.IsTrue(persons.All(e => e.Label == "person"), "Only 'person' elements may be returned.");

            // A label with no members yields no matches (GraphScan returns false = empty).
            Assert.IsFalse(f8.GraphScan(out var none, "team", "x", BinaryOperator.Equals, "nonexistent"),
                "A label matching no element yields an empty scan.");
            Assert.AreEqual(0, none.Count);

            f8.Dispose();
        }
    }
}
