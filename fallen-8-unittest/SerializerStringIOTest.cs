// MIT License
//
// SerializerStringIOTest.cs
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
using System.Buffers.Binary;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Core.Serializer;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Byte-level parity and round-trip tests for the <see cref="SerializationWriter"/> /
    /// <see cref="SerializationReader"/> string codec.
    /// </summary>
    /// <remarks>
    /// The N4 modernization replaced the per-byte string write/read loops with a single bulk
    /// <c>Write(byte[])</c> / <c>ReadBytes</c>. These tests pin the on-disk string layout so the
    /// change cannot silently alter the serialized format: the encoding is an Int32 little-endian
    /// UTF-32 byte count followed by the UTF-32 (little-endian, no BOM) bytes.
    /// </remarks>
    [TestClass]
    public class SerializerStringIOTest
    {
        private static readonly string[] RepresentativeStrings =
        {
            "",                        // empty
            "A",                       // single char
            "AB",                      // short ascii
            "Hello, World!",           // ascii sentence
            "héllo wörld — 日本語",       // BMP + non-ascii
            "😀 emoji 🎉 surrogate",     // astral plane / surrogate pairs
            new string('x', 5000),     // long
        };

        /// <summary>
        /// Hard-coded golden bytes: the writer must emit "AB" as an Int32 little-endian length
        /// prefix (8 bytes of payload) followed by 'A' and 'B' each as UTF-32 little-endian.
        /// </summary>
        [TestMethod]
        public void WriteString_AB_ProducesGoldenByteLayout()
        {
            var expected = new byte[]
            {
                0x08, 0x00, 0x00, 0x00, // length prefix = 8 payload bytes
                0x41, 0x00, 0x00, 0x00, // 'A' in UTF-32 LE
                0x42, 0x00, 0x00, 0x00, // 'B' in UTF-32 LE
            };

            CollectionAssert.AreEqual(expected, WriteStringRegion("AB"));
        }

        /// <summary>
        /// The empty string must serialize to nothing but a zero length prefix.
        /// </summary>
        [TestMethod]
        public void WriteString_Empty_ProducesZeroLengthPrefixOnly()
        {
            var expected = new byte[] { 0x00, 0x00, 0x00, 0x00 };

            CollectionAssert.AreEqual(expected, WriteStringRegion(string.Empty));
        }

        /// <summary>
        /// For every representative string the writer's on-disk region must equal an
        /// independently computed [Int32 LE length][UTF-32 LE bytes] layout. This guards
        /// against any drift introduced by the bulk-write change.
        /// </summary>
        [TestMethod]
        public void WriteString_ByteLayout_MatchesIndependentUtf32Encoding()
        {
            var enc = new UTF32Encoding();

            foreach (var s in RepresentativeStrings)
            {
                var strBytes = enc.GetBytes(s);
                var expected = new byte[4 + strBytes.Length];
                BinaryPrimitives.WriteInt32LittleEndian(expected, strBytes.Length);
                Array.Copy(strBytes, 0, expected, 4, strBytes.Length);

                CollectionAssert.AreEqual(expected, WriteStringRegion(s),
                    string.Format("Byte layout drift for a string of length {0}.", s.Length));
            }
        }

        /// <summary>
        /// Every representative string must survive a write -> read round-trip unchanged.
        /// </summary>
        [TestMethod]
        public void WriteThenReadString_RoundTrips()
        {
            var ms = new MemoryStream();
            var writer = new SerializationWriter(ms);

            foreach (var s in RepresentativeStrings)
            {
                writer.Write(s);
            }

            var data = writer.ToArray();

            var reader = new SerializationReader(new MemoryStream(data));
            foreach (var s in RepresentativeStrings)
            {
                Assert.AreEqual(s, reader.ReadString());
            }
        }

        /// <summary>
        /// Interleaving strings with other primitives must not disturb the round-trip, proving
        /// the length prefix plus bulk byte block stays correctly framed.
        /// </summary>
        [TestMethod]
        public void WriteThenReadString_InterleavedWithPrimitives_RoundTrips()
        {
            var ms = new MemoryStream();
            var writer = new SerializationWriter(ms);

            writer.Write("first");
            writer.Write(42);            // Int32 -> BinaryWriter.Write(int)
            writer.Write("second");
            writer.Write("héllo 😀");

            var data = writer.ToArray();

            var reader = new SerializationReader(new MemoryStream(data));
            Assert.AreEqual("first", reader.ReadString());
            Assert.AreEqual(42, reader.ReadInt32());
            Assert.AreEqual("second", reader.ReadString());
            Assert.AreEqual("héllo 😀", reader.ReadString());
        }

        /// <summary>
        /// Captures exactly the bytes the writer emits for a single <c>Write(string)</c> call,
        /// excluding the fixed header the constructor writes.
        /// </summary>
        private static byte[] WriteStringRegion(string value)
        {
            var ms = new MemoryStream();
            var writer = new SerializationWriter(ms);

            var start = (int)ms.Position;
            writer.Write(value);
            writer.Flush();
            var end = (int)ms.Position;

            var all = ms.ToArray();
            var region = new byte[end - start];
            Array.Copy(all, start, region, 0, end - start);
            return region;
        }
    }
}
