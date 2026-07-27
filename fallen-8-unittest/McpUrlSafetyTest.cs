// MIT License
//
// McpUrlSafetyTest.cs
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

using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Mcp.Bridge;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   URL-construction integrity is the tier-gating/isolation security boundary (feature
    ///   mcp-server §3.9): a caller-supplied namespace must never be able to inject an extra path
    ///   segment, a query, or a fragment into a downstream URL, and an invalid name must fail fast
    ///   rather than reach the wire.
    /// </summary>
    [TestClass]
    public class McpUrlSafetyTest
    {
        [DataTestMethod]
        [DataRow("people")]
        [DataRow("My Graph")]        // spaces are legal in a Fallen-8 namespace name
        [DataRow("graph-2024")]
        public void ValidNamespace_IsAcceptedAndEncodedToASingleSegment(String name)
        {
            Assert.IsTrue(UrlSafety.TryEncodeNamespace(name, out var encoded, out var error), error);
            Assert.IsFalse(encoded.Contains('/'), "no path separator survives encoding");
            Assert.IsFalse(encoded.Contains('?'), "no query separator survives encoding");
            Assert.IsFalse(encoded.Contains('#'), "no fragment separator survives encoding");
        }

        [DataTestMethod]
        [DataRow("")]                          // empty
        [DataRow("a/b")]                        // path separator
        [DataRow("a\\b")]                       // backslash
        [DataRow(".")]                          // dot
        [DataRow("..")]                         // traversal
        [DataRow(" leading")]                   // whitespace padding
        public void InvalidNamespace_IsRejectedBeforeItReachesTheWire(String name)
        {
            Assert.IsFalse(UrlSafety.TryEncodeNamespace(name, out _, out var error));
            Assert.IsFalse(String.IsNullOrEmpty(error), "a rejection carries a client-facing reason");
        }

        [TestMethod]
        public void TooLongNamespace_IsRejected()
        {
            Assert.IsFalse(UrlSafety.TryEncodeNamespace(new String('x', 64), out _, out _));
            Assert.IsTrue(UrlSafety.TryEncodeNamespace(new String('x', 63), out _, out _));
        }

        [TestMethod]
        public void InjectionCharacters_ArePercentEncoded_NotPassedThrough()
        {
            // A name that WOULD change the route if interpolated raw: query, fragment, traversal.
            Assert.IsTrue(UrlSafety.TryEncodeNamespace("foo?bar#baz", out var encoded, out _));
            Assert.IsFalse(encoded.Contains('?'));
            Assert.IsFalse(encoded.Contains('#'));

            var pct = UrlSafety.EncodeSegment("a%2e%2e/b");
            Assert.IsFalse(pct.Contains('/'), "an encoded percent-traversal cannot introduce a segment");
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("default")]
        public void DefaultNamespace_IsRecognised(String name)
        {
            Assert.IsTrue(UrlSafety.IsDefault(name));
        }

        [TestMethod]
        public void NamedNamespace_IsNotDefault()
        {
            Assert.IsFalse(UrlSafety.IsDefault("people"));
        }
    }
}
