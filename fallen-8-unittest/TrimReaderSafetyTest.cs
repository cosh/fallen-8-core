// MIT License
//
// TrimReaderSafetyTest.cs
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Pins the trim-reader-safety fixes: a <see cref="VertexModel"/>'s hash is identity-stable, so an
    /// in-place <c>Id</c> change (a Trim renumber) can no longer move an already-inserted key's bucket
    /// in a hash-keyed container - the mechanism BLS's frontier/visited containers rely on (Part A) -
    /// and that membership holds under a Trim running concurrently with the reader.
    /// </summary>
    [TestClass]
    public class TrimReaderSafetyTest
    {
        private ILoggerFactory _loggerFactory;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
        }

        private static void SetId(AGraphElementModel element, int newId)
        {
            var method = typeof(AGraphElementModel).GetMethod("SetId", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(method, "The internal SetId method must exist.");
            method.Invoke(element, new object[] { newId });
        }

        [TestMethod]
        public void VertexModelHash_IsStableAcrossIdMutation()
        {
            var v = new VertexModel(5, 1u, "x");
            var set = new HashSet<VertexModel> { v };
            var dict = new Dictionary<VertexModel, string> { [v] = "hi" };

            var hashBefore = v.GetHashCode();

            // Renumber the vertex in place, exactly as Trim_internal's SetId(i) does.
            SetId(v, 999);

            Assert.AreEqual(hashBefore, v.GetHashCode(),
                "A VertexModel's hash must NOT change when its Id changes (identity hash, feature trim-reader-safety Part A).");
            Assert.AreEqual(999, v.Id, "The Id itself did change.");
            Assert.IsTrue(set.Contains(v),
                "The vertex must still be found in a HashSet<VertexModel> after its Id changed (would fail with GetHashCode() => Id).");
            Assert.IsTrue(dict.ContainsKey(v), "The vertex must still be a resolvable Dictionary key after its Id changed.");
            Assert.IsTrue(dict.TryGetValue(v, out var value) && value == "hi");
        }

        [TestMethod]
        public void TwoVerticesSharingAnId_DoNotCollide()
        {
            // Two distinct instances that happen to carry the same Id must be distinct hash-container
            // members (identity, not value): the old GetHashCode() => Id would alias them.
            var a = new VertexModel(7, 1u, "a");
            var b = new VertexModel(7, 1u, "b");

            var set = new HashSet<VertexModel> { a, b };
            Assert.AreEqual(2, set.Count, "Two distinct vertices with the same Id must be two distinct set members.");
            Assert.IsTrue(set.Contains(a));
            Assert.IsTrue(set.Contains(b));
        }

        [TestMethod]
        public void HashSetMembership_SurvivesConcurrentTrimRenumber()
        {
            // Build a graph with gaps (removed vertices) so an explicit Trim actually RENUMBERS the
            // survivors, then check that a reader holding those survivor objects in a hash container
            // keeps finding them while the Trim renumbers their Ids on the writer thread. Post-fix
            // (identity hash) this is deterministic; pre-fix (hash == Id) the renumber moved buckets
            // and Contains would miss.
            var fallen8 = new Fallen8(_loggerFactory);

            var createTx = new CreateVerticesTransaction();
            for (var i = 0; i < 2000; i++)
            {
                createTx.AddVertex(1u, "n", new Dictionary<string, object> { { "seq", i } });
            }
            fallen8.EnqueueTransaction(createTx).WaitUntilFinished();
            var ids = createTx.GetCreatedVertices().Select(v => v.Id).ToList();

            // Remove every other vertex to create tombstones, so the next Trim compacts + renumbers.
            var toRemove = ids.Where((_, i) => (i & 1) == 0).ToList();
            fallen8.EnqueueTransaction(new RemoveGraphElementsTransaction { GraphElementIds = toRemove }).WaitUntilFinished();

            // The survivor objects the reader holds in a hash container.
            var survivors = fallen8.GetAllVertices().ToList();
            Assert.IsTrue(survivors.Count > 500, "Sanity: a meaningful survivor set.");
            var set = new HashSet<VertexModel>(survivors);

            Exception readerFault = null;
            var membershipLost = false;
            var stop = false;

            var reader = Task.Run(() =>
            {
                try
                {
                    while (!Volatile.Read(ref stop))
                    {
                        foreach (var v in survivors)
                        {
                            if (!set.Contains(v))
                            {
                                membershipLost = true;
                                return;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    readerFault = ex;
                }
            });

            // Renumber repeatedly on the writer thread while the reader checks membership. Each Trim
            // renumbers the survivors' Ids in place (the first one compacts the gaps; later ones are
            // no-ops once dense, but the reader loops throughout the renumber window).
            for (var i = 0; i < 50; i++)
            {
                fallen8.EnqueueTransaction(new TrimTransaction()).WaitUntilFinished();
            }

            Volatile.Write(ref stop, true);
            reader.Wait(TimeSpan.FromSeconds(30));

            Assert.IsNull(readerFault, "A concurrent reader over a hash container of vertices must not fault during a Trim renumber.");
            Assert.IsFalse(membershipLost,
                "A VertexModel's HashSet membership must survive a concurrent Trim renumber (feature trim-reader-safety Part A).");
        }
    }
}
