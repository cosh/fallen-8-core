// MIT License
//
// PropertyStoreFidelityTest.cs
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
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Fidelity guard for the compact property store (memory-footprint finding M1). The store was
    /// changed from a per-element ImmutableDictionary to a sorted key/value array with de-boxed
    /// common value types; these tests pin that the public contract - exact typing and round-trip
    /// through TryGetProperty / GetAllProperties / GetPropertyCount - is unchanged, and that the
    /// de-boxing shares one boxed instance without changing observable values.
    /// </summary>
    [TestClass]
    public class PropertyStoreFidelityTest
    {
        private ILoggerFactory _loggerFactory;

        [TestInitialize]
        public void TestInitialize()
        {
            _loggerFactory = TestLoggerFactory.Create();
        }

        private static VertexModel Vertex(Dictionary<string, object> properties)
        {
            return new VertexModel(1, 1u, "test", properties);
        }

        [TestMethod]
        public void TryGetProperty_PreservesExactTypeAndValue_ForEachPrimitive()
        {
            var when = new DateTime(2026, 7, 12, 8, 30, 0, DateTimeKind.Utc);
            var v = Vertex(new Dictionary<string, object>
            {
                { "i", 42 },
                { "big", 5_000_000 },      // outside the small-int cache
                { "l", 9_000_000_000L },
                { "d", 3.14159 },
                { "b", true },
                { "s", "hello" },
                { "when", when },
                { "nothing", null },
            });

            int i; Assert.IsTrue(v.TryGetProperty(out i, "i")); Assert.AreEqual(42, i);
            int big; Assert.IsTrue(v.TryGetProperty(out big, "big")); Assert.AreEqual(5_000_000, big);
            long l; Assert.IsTrue(v.TryGetProperty(out l, "l")); Assert.AreEqual(9_000_000_000L, l);
            double d; Assert.IsTrue(v.TryGetProperty(out d, "d")); Assert.AreEqual(3.14159, d, 0.0);
            bool b; Assert.IsTrue(v.TryGetProperty(out b, "b")); Assert.IsTrue(b);
            string s; Assert.IsTrue(v.TryGetProperty(out s, "s")); Assert.AreEqual("hello", s);
            DateTime got; Assert.IsTrue(v.TryGetProperty(out got, "when")); Assert.AreEqual(when, got);

            // A stored null value is a present property whose value is null.
            object nothing;
            Assert.IsTrue(v.TryGetProperty(out nothing, "nothing"), "A key with a null value is still present.");
            Assert.IsNull(nothing);

            // The runtime types are preserved exactly (no widening/narrowing).
            object boxed;
            v.TryGetProperty(out boxed, "i");
            Assert.AreEqual(typeof(int), boxed.GetType());
            v.TryGetProperty(out boxed, "l");
            Assert.AreEqual(typeof(long), boxed.GetType());
            v.TryGetProperty(out boxed, "d");
            Assert.AreEqual(typeof(double), boxed.GetType());
            v.TryGetProperty(out boxed, "b");
            Assert.AreEqual(typeof(bool), boxed.GetType());

            Assert.AreEqual(8, v.GetPropertyCount());
        }

        [TestMethod]
        public void TryGetProperty_MissingKey_ReturnsFalseAndDefault()
        {
            var v = Vertex(new Dictionary<string, object> { { "present", 1 } });

            int result;
            Assert.IsFalse(v.TryGetProperty(out result, "absent"));
            Assert.AreEqual(0, result, "A missing key yields default(T).");

            string sresult;
            Assert.IsFalse(v.TryGetProperty(out sresult, "absent"));
            Assert.IsNull(sresult);
        }

        [TestMethod]
        public void NullAndEmptyPropertySets_ReportAsEmpty_AndAllocateNoContainer()
        {
            var nullProps = Vertex(null);
            var emptyProps = Vertex(new Dictionary<string, object>());

            foreach (var v in new[] { nullProps, emptyProps })
            {
                Assert.AreEqual(0, v.GetPropertyCount());
                Assert.IsNotNull(v.GetAllProperties(), "GetAllProperties never returns null.");
                Assert.AreEqual(0, v.GetAllProperties().Count);
                object any;
                Assert.IsFalse(v.TryGetProperty(out any, "anything"));
            }
        }

        [TestMethod]
        public void GetAllProperties_RoundTripsEveryEntry()
        {
            var props = new Dictionary<string, object>
            {
                { "zeta", 1 }, { "alpha", "a" }, { "mu", true }, { "beta", 2.5 },
            };
            var v = Vertex(props);

            ImmutableDictionary<string, object> all = v.GetAllProperties();
            Assert.AreEqual(4, all.Count);
            Assert.AreEqual(1, all["zeta"]);
            Assert.AreEqual("a", all["alpha"]);
            Assert.AreEqual(true, all["mu"]);
            Assert.AreEqual(2.5, all["beta"]);
        }

        [TestMethod]
        public void ManyProperties_AllRetrievable_IndependentOfInsertionOrder()
        {
            // Insert keys in a deliberately non-sorted order to exercise the sorted-array + binary
            // search path across more than a handful of entries.
            var props = new Dictionary<string, object>();
            for (int k = 200; k >= 0; k--)
            {
                props["k" + k] = k;
            }
            var v = Vertex(props);

            Assert.AreEqual(201, v.GetPropertyCount());
            for (int k = 0; k <= 200; k++)
            {
                int value;
                Assert.IsTrue(v.TryGetProperty(out value, "k" + k), "Key k" + k + " must be found.");
                Assert.AreEqual(k, value);
            }
            object missing;
            Assert.IsFalse(v.TryGetProperty(out missing, "k201"));
        }

        [TestMethod]
        public void DeBoxing_SharesOneBox_ForBoolAndSmallInt_WithoutChangingValue()
        {
            var a = Vertex(new Dictionary<string, object> { { "flag", true }, { "n", 7 }, { "big", 10_000_000 } });
            var b = Vertex(new Dictionary<string, object> { { "flag", true }, { "n", 7 }, { "big", 10_000_000 } });

            var pa = a.GetAllProperties();
            var pb = b.GetAllProperties();

            // Values remain observably equal...
            Assert.AreEqual(true, pa["flag"]);
            Assert.AreEqual(7, pa["n"]);
            Assert.AreEqual(10_000_000, pa["big"]);

            // ...and the boxed bool / small-int are the SAME shared instance across elements
            // (the whole point of de-boxing: N elements do not each retain their own box).
            Assert.IsTrue(ReferenceEquals(pa["flag"], pb["flag"]), "true must be a shared box.");
            Assert.IsTrue(ReferenceEquals(pa["n"], pb["n"]), "small ints must be shared boxes.");
        }

        [TestMethod]
        public void AddPropertyToPropertyLessElement_Succeeds()
        {
            // A vertex created with NO properties (null) can still receive a property (this was an
            // NRE before M1). Driven through the public transaction API.
            var fallen8 = new Fallen8(_loggerFactory);
            var vtx = new CreateVertexTransaction
            {
                Definition = new VertexDefinition { CreationDate = 1, Properties = null }
            };
            fallen8.EnqueueTransaction(vtx).WaitUntilFinished();
            int id = vtx.VertexCreated.Id;

            var add = new AddPropertyTransaction
            {
                Definition = new PropertyAddDefinition { GraphElementId = id, PropertyId = "name", Property = "late" }
            };
            var info = fallen8.EnqueueTransaction(add);
            info.WaitUntilFinished();

            Assert.AreEqual(TransactionState.Finished, info.TransactionState,
                "Adding a first property to a property-less element must succeed.");

            VertexModel v;
            Assert.IsTrue(fallen8.TryGetVertex(out v, id));
            string name;
            Assert.IsTrue(v.TryGetProperty(out name, "name"));
            Assert.AreEqual("late", name);
        }

        [TestMethod]
        public void UpdateProperty_ViaRemoveThenAdd_ReplacesValue()
        {
            var fallen8 = new Fallen8(_loggerFactory);
            var vtx = new CreateVertexTransaction
            {
                Definition = new VertexDefinition
                {
                    CreationDate = 1,
                    Properties = new Dictionary<string, object> { { "k", "v1" } }
                }
            };
            fallen8.EnqueueTransaction(vtx).WaitUntilFinished();
            int id = vtx.VertexCreated.Id;

            fallen8.EnqueueTransaction(new RemovePropertyTransaction { GraphElementId = id, PropertyId = "k" }).WaitUntilFinished();
            fallen8.EnqueueTransaction(new AddPropertyTransaction
            {
                Definition = new PropertyAddDefinition { GraphElementId = id, PropertyId = "k", Property = "v2" }
            }).WaitUntilFinished();

            VertexModel v;
            Assert.IsTrue(fallen8.TryGetVertex(out v, id));
            string value;
            Assert.IsTrue(v.TryGetProperty(out value, "k"));
            Assert.AreEqual("v2", value, "Remove-then-add must replace the value.");
        }

        [TestMethod]
        public void SetProperty_DuplicateKeyDifferentValue_RollsBackAndKeepsOriginal()
        {
            // The previous ImmutableDictionary.Add threw when adding an existing key with a
            // different value; M1 preserves that (callers update via remove-then-add). Through the
            // transaction worker this surfaces as a rolled-back transaction, original value intact.
            var fallen8 = new Fallen8(_loggerFactory);
            var vtx = new CreateVertexTransaction
            {
                Definition = new VertexDefinition
                {
                    CreationDate = 1,
                    Properties = new Dictionary<string, object> { { "k", 1 } }
                }
            };
            fallen8.EnqueueTransaction(vtx).WaitUntilFinished();
            int id = vtx.VertexCreated.Id;

            var dup = new AddPropertyTransaction
            {
                Definition = new PropertyAddDefinition { GraphElementId = id, PropertyId = "k", Property = 2 }
            };
            var info = fallen8.EnqueueTransaction(dup);
            info.WaitUntilFinished();

            Assert.AreEqual(TransactionState.RolledBack, info.TransactionState,
                "Re-adding an existing key with a different value must roll back (unchanged semantics).");

            VertexModel v;
            Assert.IsTrue(fallen8.TryGetVertex(out v, id));
            int value;
            Assert.IsTrue(v.TryGetProperty(out value, "k"));
            Assert.AreEqual(1, value, "The original value must be retained after the rolled-back duplicate add.");
        }

        [TestMethod]
        public void SetProperty_DuplicateKeySameValue_IsSilentNoOp()
        {
            // Counterpart to the different-value case above: re-adding an existing key with the
            // SAME value pins the ImmutableDictionary.Add semantics M1 preserves - it is a silent
            // no-op (no throw, so the transaction commits), and the property count and value are
            // left unchanged.
            var fallen8 = new Fallen8(_loggerFactory);
            var vtx = new CreateVertexTransaction
            {
                Definition = new VertexDefinition
                {
                    CreationDate = 1,
                    Properties = new Dictionary<string, object> { { "k", 1 }, { "other", "x" } }
                }
            };
            fallen8.EnqueueTransaction(vtx).WaitUntilFinished();
            int id = vtx.VertexCreated.Id;

            VertexModel before;
            Assert.IsTrue(fallen8.TryGetVertex(out before, id));
            int countBefore = before.GetPropertyCount();

            var dup = new AddPropertyTransaction
            {
                Definition = new PropertyAddDefinition { GraphElementId = id, PropertyId = "k", Property = 1 }
            };
            var info = fallen8.EnqueueTransaction(dup);
            info.WaitUntilFinished();

            Assert.AreEqual(TransactionState.Finished, info.TransactionState,
                "Re-adding an existing key with the SAME value must be a silent no-op, not a rollback.");

            VertexModel v;
            Assert.IsTrue(fallen8.TryGetVertex(out v, id));
            Assert.AreEqual(countBefore, v.GetPropertyCount(),
                "A same-value re-add must not change the property count.");
            int value;
            Assert.IsTrue(v.TryGetProperty(out value, "k"));
            Assert.AreEqual(1, value, "A same-value re-add must leave the value unchanged.");
        }
    }
}
