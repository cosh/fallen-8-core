// MIT License
//
// IngestionIdentifierExtractorTest.cs
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

using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Ingestion;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Feature unstructured-ingestion FR-6: the generic identifier patterns, their
    ///   false-positive guards, the first-occurrence cap and the sorted, deduplicated output.
    /// </summary>
    [TestClass]
    public class IngestionIdentifierExtractorTest
    {
        [TestMethod]
        public void UnderscoreIdentifiers_AreExtracted()
        {
            var ids = IdentifierExtractor.Extract(
                "The RETRY_BUDGET_MS knob and the Tls_Frontend_V2 proxy interact.", 64);
            CollectionAssert.AreEqual(new List<string> { "RETRY_BUDGET_MS", "Tls_Frontend_V2" }, ids);
        }

        [TestMethod]
        public void CamelCaseIdentifiers_AreExtracted()
        {
            var ids = IdentifierExtractor.Extract("The CheckoutService calls PaymentGateway2 twice.", 64);
            CollectionAssert.AreEqual(new List<string> { "CheckoutService", "PaymentGateway2" }, ids);
        }

        [TestMethod]
        public void HexIdentifiers_AreExtracted()
        {
            var ids = IdentifierExtractor.Extract("Frame 0x1A2B answered, frame 0x1 did not.", 64);
            CollectionAssert.AreEqual(new List<string> { "0x1A2B" }, ids,
                "a single hex digit is below the guard");
        }

        [TestMethod]
        public void ShortTokens_AreGuarded()
        {
            var ids = IdentifierExtractor.Extract("A_B is short, AbCd and GoDog are short camels.", 64);
            Assert.AreEqual(0, ids.Count,
                "underscore tokens under 4 chars and camels under 6 chars are false-positive-guarded");
        }

        [TestMethod]
        public void PlainProse_YieldsNothing()
        {
            var ids = IdentifierExtractor.Extract(
                "The server that terminates tls for the shop lives in the third rack.", 64);
            Assert.AreEqual(0, ids.Count, "lowercase prose (including sentence starts) is not an identifier");
        }

        [TestMethod]
        public void LowercaseUnderscore_IsNotExtracted()
        {
            var ids = IdentifierExtractor.Extract("the retry_budget_ms field", 64);
            Assert.AreEqual(0, ids.Count, "the patterns require an uppercase start");
        }

        [TestMethod]
        public void Cap_KeepsFirstOccurrences_OutputSorted()
        {
            var ids = IdentifierExtractor.Extract("ZETA_ONE then ALPHA_TWO then MIDDLE_THREE.", 2);
            CollectionAssert.AreEqual(new List<string> { "ALPHA_TWO", "ZETA_ONE" }, ids,
                "the cap keeps the first two by position, the output is sorted");
        }

        [TestMethod]
        public void Duplicates_CountOnce()
        {
            var ids = IdentifierExtractor.Extract("RETRY_MS and again RETRY_MS and AAAA_BB.", 2);
            CollectionAssert.AreEqual(new List<string> { "AAAA_BB", "RETRY_MS" }, ids);
        }

        [TestMethod]
        public void EmptyTextOrZeroCap_YieldEmpty()
        {
            Assert.AreEqual(0, IdentifierExtractor.Extract(null, 64).Count);
            Assert.AreEqual(0, IdentifierExtractor.Extract("", 64).Count);
            Assert.AreEqual(0, IdentifierExtractor.Extract("RETRY_BUDGET_MS", 0).Count);
        }

        [TestMethod]
        public void MixedCaseUnderscore_IsExtracted()
        {
            var ids = IdentifierExtractor.Extract("See Gw_Rev4 and IO_BridgePort_A1 for details.", 64);
            CollectionAssert.AreEqual(new List<string> { "Gw_Rev4", "IO_BridgePort_A1" }, ids);
        }
    }
}
