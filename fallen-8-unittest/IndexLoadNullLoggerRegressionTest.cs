// MIT License
//
// IndexLoadNullLoggerRegressionTest.cs
//
// Copyright (c) 2011-2026 Henning Rauch
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

using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Index.Fulltext;
using NoSQL.GraphDB.Core.Index.Range;
using NoSQL.GraphDB.Core.Serializer;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Regression pin for consolidation-audit CA-16: the three legacy indices
    ///   (<see cref="DictionaryIndex"/>/<see cref="RangeIndex"/> via <c>ABucketIndex</c>,
    ///   <see cref="SingleValueIndex"/>, <see cref="RegExIndex"/>) used to dereference a null
    ///   <c>_logger</c> in the not-found branch of <c>Load</c>. The real load path
    ///   (<c>IndexFactory.OpenIndex</c>) activates the plugin WITHOUT calling <c>Initialize</c>,
    ///   so <c>_logger</c> stays null; a persisted index that references a graph element which no
    ///   longer exists (a dangling reference from a stale or tampered sidecar) hit that branch and
    ///   threw <c>NullReferenceException</c>, and <c>LoadIndices</c>' per-index catch then dropped
    ///   the WHOLE index. These tests reproduce the OpenIndex path (a fresh, NON-initialized index)
    ///   and assert that <c>Load</c> now skips the dangling entry instead of throwing.
    ///
    ///   <para>The bug reproduces only WITHOUT <c>Initialize</c>; calling it would wire the logger
    ///   and mask the defect, so these targets are intentionally never initialized.</para>
    /// </summary>
    [TestClass]
    public class IndexLoadNullLoggerRegressionTest
    {
        private Fallen8 _fallen8;

        // Any id that TryGetGraphElement will not resolve against the (empty) engine: the dangling
        // reference the not-found branch handles.
        private const int MissingGraphElementId = 4242;

        [TestInitialize]
        public void TestInitialize()
        {
            _fallen8 = new Fallen8(TestLoggerFactory.Create());
        }

        [TestCleanup]
        public void TestCleanup()
        {
            _fallen8.Dispose();
        }

        [TestMethod]
        public void DictionaryIndex_Load_SkipsADanglingReference_WithoutInitialize()
        {
            var target = new DictionaryIndex();
            LoadBucketFormatBodyWithOneMissingValue(target);

            // ABucketIndex always adds the key (with an empty bucket), even when its only value
            // was a dangling reference: the key survives, the missing value is skipped.
            Assert.AreEqual(1, target.CountOfKeys(), "the key is retained with an empty bucket");
            Assert.AreEqual(0, target.CountOfValues(), "the dangling value is skipped");
        }

        [TestMethod]
        public void RangeIndex_Load_SkipsADanglingReference_WithoutInitialize()
        {
            var target = new RangeIndex();
            LoadBucketFormatBodyWithOneMissingValue(target);

            Assert.AreEqual(1, target.CountOfKeys(), "the key is retained with an empty bucket");
            Assert.AreEqual(0, target.CountOfValues(), "the dangling value is skipped");
        }

        [TestMethod]
        public void SingleValueIndex_Load_SkipsADanglingReference_WithoutInitialize()
        {
            using var stream = new MemoryStream();
            var writer = new SerializationWriter(stream, true);
            writer.Write(0);                        // parameter
            writer.Write(1);                        // keyCount
            writer.WriteObject("k");                // key (ReadObject)
            writer.Write(MissingGraphElementId);    // dangling reference (no valueCount for a single-value index)
            writer.UpdateHeader();
            writer.Flush();
            stream.Position = 0;

            var target = new SingleValueIndex();
            // Must NOT Initialize: reproduces the OpenIndex path where _logger is null.
            target.Load(new SerializationReader(stream), _fallen8);

            // SingleValueIndex adds the key only when its element resolves, so a dangling-only
            // entry leaves the index empty rather than throwing.
            Assert.AreEqual(0, target.CountOfKeys(), "a key whose only element is missing is not added");
            Assert.AreEqual(0, target.CountOfValues(), "the dangling value is skipped");
        }

        [TestMethod]
        public void RegExIndex_Load_SkipsADanglingReference_WithoutInitialize()
        {
            using var stream = new MemoryStream();
            var writer = new SerializationWriter(stream, true);
            writer.Write(0);                        // parameter
            writer.Write(1);                        // keyCount
            writer.Write("k");                      // key (ReadString, not ReadObject)
            writer.Write(1);                        // valueCount
            writer.Write(MissingGraphElementId);    // dangling reference
            writer.UpdateHeader();
            writer.Flush();
            stream.Position = 0;

            var target = new RegExIndex();
            target.Load(new SerializationReader(stream), _fallen8);

            Assert.AreEqual(1, target.CountOfKeys(), "the key is retained with an empty bucket");
            Assert.AreEqual(0, target.CountOfValues(), "the dangling value is skipped");
        }

        // Serializes an ABucketIndex body (DictionaryIndex / RangeIndex share the format) with one
        // key whose single value is a dangling graph-element reference, then loads it into a fresh,
        // NON-initialized target - the OpenIndex path that left _logger null.
        private void LoadBucketFormatBodyWithOneMissingValue(ABucketIndex target)
        {
            using var stream = new MemoryStream();
            var writer = new SerializationWriter(stream, true);
            writer.Write(0);                        // parameter
            writer.Write(1);                        // keyCount
            writer.WriteObject("k");                // key (ReadObject)
            writer.Write(1);                        // valueCount
            writer.Write(MissingGraphElementId);    // dangling reference
            writer.UpdateHeader();
            writer.Flush();
            stream.Position = 0;

            target.Load(new SerializationReader(stream), _fallen8);
        }
    }
}
