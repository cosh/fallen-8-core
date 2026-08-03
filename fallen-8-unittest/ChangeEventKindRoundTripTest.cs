// MIT License
//
// ChangeEventKindRoundTripTest.cs
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
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core.ChangeFeed;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Pins the single-homed <see cref="ChangeEventKind"/> wire-name bijection
    ///   (consolidation-audit CA-11): every kind the SSE stream can emit via
    ///   <see cref="ChangeEventREST.KindName"/> must parse back through
    ///   <see cref="ChangeEventREST.TryParseKind"/> (the <c>?kinds=</c> filter), so the two
    ///   directions can never drift. Adding a kind without extending the shared map makes
    ///   <c>KindName</c> fall back to <c>ToString()</c> and <c>TryParseKind</c> return false, so
    ///   this round trip fails - the intended forcing function.
    /// </summary>
    [TestClass]
    public class ChangeEventKindRoundTripTest
    {
        [TestMethod]
        public void EveryKind_RoundTripsThroughItsWireName()
        {
            var kinds = Enum.GetValues<ChangeEventKind>();

            // [Flags] with single-bit members, so GetValues yields exactly the declared kinds; the
            // count pin forces a newly added kind to acquire a wire-name mapping here.
            Assert.AreEqual(7, kinds.Length, "a new ChangeEventKind needs a wire name in the shared map");

            foreach (var kind in kinds)
            {
                var wire = ChangeEventREST.KindName(kind);
                Assert.IsTrue(ChangeEventREST.TryParseKind(wire, out var parsed),
                    kind + " wire name '" + wire + "' must parse back");
                Assert.AreEqual(kind, parsed, "the round trip must be identity");
            }
        }

        [TestMethod]
        public void TryParseKind_IsCaseSensitive_AndRejectsUnknownOrNull()
        {
            // The parser was ordinal case-sensitive; preserve that (the stream emits camelCase only).
            Assert.IsFalse(ChangeEventREST.TryParseKind("VertexCreated", out _), "PascalCase is not a wire name");
            Assert.IsFalse(ChangeEventREST.TryParseKind("vertexmangled", out _), "an unknown kind is rejected");
            Assert.IsFalse(ChangeEventREST.TryParseKind("", out _), "empty is rejected");
            Assert.IsFalse(ChangeEventREST.TryParseKind(null, out _), "null is rejected without throwing");
        }

        [TestMethod]
        public void KindName_EmitsTheCamelCaseWireContract()
        {
            // The exact strings are part of the public SSE contract; pin them so a rename here is a
            // conscious contract change, caught alongside the round trip.
            Assert.AreEqual("vertexCreated", ChangeEventREST.KindName(ChangeEventKind.VertexCreated));
            Assert.AreEqual("vertexRemoved", ChangeEventREST.KindName(ChangeEventKind.VertexRemoved));
            Assert.AreEqual("edgeCreated", ChangeEventREST.KindName(ChangeEventKind.EdgeCreated));
            Assert.AreEqual("edgeRemoved", ChangeEventREST.KindName(ChangeEventKind.EdgeRemoved));
            Assert.AreEqual("propertySet", ChangeEventREST.KindName(ChangeEventKind.PropertySet));
            Assert.AreEqual("propertyRemoved", ChangeEventREST.KindName(ChangeEventKind.PropertyRemoved));
            Assert.AreEqual("resync", ChangeEventREST.KindName(ChangeEventKind.Resync));
        }
    }
}
