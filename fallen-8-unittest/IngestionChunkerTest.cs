// MIT License
//
// IngestionChunkerTest.cs
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
using System.Linq;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Ingestion;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Feature unstructured-ingestion FR-6: golden chunker cases for the structured
    ///   DoclingDocument path (reading order, heading hierarchy, intact tables, row windows,
    ///   page provenance, cycle guard) and the markdown/plain fallbacks (bounds, unicode,
    ///   determinism). The inline fixture JSON pins the DoclingModels schema subset.
    /// </summary>
    [TestClass]
    public class IngestionChunkerTest
    {
        private static Fallen8IngestionOptions Options(Int32 min = 1, Int32 max = 4_000, Int32 identifierCap = 64)
        {
            return new Fallen8IngestionOptions
            {
                ChunkMinChars = min,
                ChunkMaxChars = max,
                MaxIdentifiersPerChunk = identifierCap
            };
        }

        #region markdown fallback

        [TestMethod]
        public void Markdown_SplitsByHeadings_TracksHierarchy()
        {
            var markdown = "# Alpha\n\nIntro paragraph.\n\n## Beta\n\nNested paragraph.\n\n# Gamma\n\nTail paragraph.";
            var chunks = DocumentChunker.ChunkMarkdown(markdown, Options());

            Assert.AreEqual(3, chunks.Count);
            Assert.AreEqual("Alpha", chunks[0].HeadingPath);
            StringAssert.StartsWith(chunks[0].Text, "Alpha");
            StringAssert.Contains(chunks[0].Text, "Intro paragraph.");
            Assert.AreEqual("Alpha > Beta", chunks[1].HeadingPath, "nested headings build a path");
            Assert.AreEqual("Gamma", chunks[2].HeadingPath, "a same-level heading pops the stack");
            Assert.AreEqual(0, chunks[0].Order);
            Assert.AreEqual(2, chunks[2].Order);
            Assert.IsTrue(chunks.All(c => c.Kind == DocumentChunk.TextKind));
        }

        [TestMethod]
        public void Markdown_ContentBeforeFirstHeading_IsKept()
        {
            var chunks = DocumentChunker.ChunkMarkdown("Preamble text.\n\n# First\n\nBody.", Options());
            Assert.AreEqual(2, chunks.Count);
            Assert.AreEqual("Preamble text.", chunks[0].Text);
            Assert.IsNull(chunks[0].HeadingPath, "pre-heading content has no heading path");
        }

        [TestMethod]
        public void Markdown_WithoutHeadings_IsOneChunk()
        {
            var chunks = DocumentChunker.ChunkMarkdown("Just one flowing text without structure.", Options());
            Assert.AreEqual(1, chunks.Count);
            Assert.IsNull(chunks[0].HeadingPath);
        }

        [TestMethod]
        public void MergeShort_CombinesAdjacentSections_KeepsFirstHeadingPath()
        {
            var markdown = "# A\n\ntiny\n\n# B\n\nalso tiny\n\n# C\n\nstill tiny";
            var chunks = DocumentChunker.ChunkMarkdown(markdown, Options(min: 800));

            Assert.AreEqual(1, chunks.Count, "everything below the minimum merges");
            Assert.AreEqual("A", chunks[0].HeadingPath, "the merged chunk keeps the FIRST chunk's path");
            StringAssert.Contains(chunks[0].Text, "also tiny");
            StringAssert.Contains(chunks[0].Text, "still tiny");
        }

        [TestMethod]
        public void SplitLong_BreaksAtParagraphBoundaries()
        {
            var paragraphs = String.Join("\n\n", Enumerable.Range(0, 10)
                .Select(i => new String((Char)('a' + i), 120)));
            var chunks = DocumentChunker.ChunkMarkdown("# H\n\n" + paragraphs, Options(max: 300));

            Assert.IsTrue(chunks.Count > 1, "an over-max section splits");
            Assert.IsTrue(chunks.All(c => c.Text.Length <= 300), "every piece respects the bound");
            Assert.IsTrue(chunks.All(c => c.HeadingPath == "H"), "pieces inherit the heading path");
        }

        [TestMethod]
        public void SplitLong_HardSplitsAnUnbrokenParagraph()
        {
            var wordSoup = String.Join(" ", Enumerable.Repeat("token", 200));
            var chunks = DocumentChunker.ChunkPlainText(wordSoup, Options(max: 200));

            Assert.IsTrue(chunks.Count > 1);
            Assert.IsTrue(chunks.All(c => c.Text.Length <= 200));
            Assert.IsTrue(chunks.All(c => !c.Text.StartsWith(" ") && !c.Text.EndsWith(" ")),
                "pieces are trimmed at the whitespace cut");
        }

        [TestMethod]
        public void PlainText_IsOneBoundedSection()
        {
            var chunks = DocumentChunker.ChunkPlainText("Short note.", Options());
            Assert.AreEqual(1, chunks.Count);
            Assert.AreEqual("Short note.", chunks[0].Text);
            Assert.IsNull(chunks[0].HeadingPath);
        }

        [TestMethod]
        public void EmptyInputs_YieldNoChunks()
        {
            Assert.AreEqual(0, DocumentChunker.ChunkMarkdown("", Options()).Count);
            Assert.AreEqual(0, DocumentChunker.ChunkMarkdown("   \n  ", Options()).Count);
            Assert.AreEqual(0, DocumentChunker.ChunkPlainText(null, Options()).Count);
        }

        [TestMethod]
        public void Unicode_SurvivesIntact()
        {
            var markdown = "# Überblick\n\nDie Straße misst 3 km, naïve Grüße. 東京タワーは高い。";
            var chunks = DocumentChunker.ChunkMarkdown(markdown, Options());
            Assert.AreEqual(1, chunks.Count);
            StringAssert.Contains(chunks[0].Text, "Straße");
            StringAssert.Contains(chunks[0].Text, "東京タワーは高い。");
            Assert.AreEqual("Überblick", chunks[0].HeadingPath);
        }

        [TestMethod]
        public void Chunking_IsDeterministic()
        {
            var markdown = "# A\n\nSome RETRY_BUDGET_MS text.\n\n## B\n\nMore text with CheckoutService.";
            var first = DocumentChunker.ChunkMarkdown(markdown, Options());
            var second = DocumentChunker.ChunkMarkdown(markdown, Options());

            Assert.AreEqual(first.Count, second.Count);
            for (var i = 0; i < first.Count; i++)
            {
                Assert.AreEqual(first[i].Text, second[i].Text);
                Assert.AreEqual(first[i].HeadingPath, second[i].HeadingPath);
                Assert.AreEqual(first[i].Order, second[i].Order);
                CollectionAssert.AreEqual(first[i].Identifiers, second[i].Identifiers);
            }
        }

        [TestMethod]
        public void Identifiers_AreExtractedPerChunk()
        {
            var chunks = DocumentChunker.ChunkMarkdown("# H\n\nThe RETRY_BUDGET_MS knob.", Options());
            CollectionAssert.AreEqual(new[] { "RETRY_BUDGET_MS" }, chunks[0].Identifiers);
        }

        #endregion

        #region structured path

        private static DoclingDocumentModel Fixture(String json)
        {
            return JsonSerializer.Deserialize<DoclingDocumentModel>(json);
        }

        /// <summary>A pinned DoclingDocument subset: title, section header, paragraphs with page
        /// provenance, a table, a list group, and skipped page furniture.</summary>
        private const String StructuredFixture = @"{
  ""texts"": [
    { ""self_ref"": ""#/texts/0"", ""label"": ""title"", ""text"": ""Network Handbook"", ""prov"": [ { ""page_no"": 1 } ] },
    { ""self_ref"": ""#/texts/1"", ""label"": ""page_header"", ""text"": ""CONFIDENTIAL"", ""prov"": [ { ""page_no"": 1 } ] },
    { ""self_ref"": ""#/texts/2"", ""label"": ""section_header"", ""text"": ""Edge Servers"", ""level"": 1, ""prov"": [ { ""page_no"": 1 } ] },
    { ""self_ref"": ""#/texts/3"", ""label"": ""text"", ""text"": ""The EDGE_TLS_01 server terminates tls."", ""prov"": [ { ""page_no"": 1 } ] },
    { ""self_ref"": ""#/texts/4"", ""label"": ""list_item"", ""text"": ""Rack three, slot one."", ""prov"": [ { ""page_no"": 2 } ] },
    { ""self_ref"": ""#/texts/5"", ""label"": ""section_header"", ""text"": ""Ports"", ""level"": 1, ""prov"": [ { ""page_no"": 2 } ] }
  ],
  ""tables"": [
    { ""self_ref"": ""#/tables/0"", ""prov"": [ { ""page_no"": 2 } ], ""data"": { ""grid"": [
      [ { ""text"": ""Port"" }, { ""text"": ""Use"" } ],
      [ { ""text"": ""443"" }, { ""text"": ""tls | https"" } ],
      [ { ""text"": ""22"" }, { ""text"": ""ssh"" } ]
    ] } }
  ],
  ""groups"": [
    { ""self_ref"": ""#/groups/0"", ""children"": [ { ""$ref"": ""#/texts/4"" } ] }
  ],
  ""body"": { ""children"": [
    { ""$ref"": ""#/texts/0"" },
    { ""$ref"": ""#/texts/1"" },
    { ""$ref"": ""#/texts/2"" },
    { ""$ref"": ""#/texts/3"" },
    { ""$ref"": ""#/groups/0"" },
    { ""$ref"": ""#/tables/0"" },
    { ""$ref"": ""#/texts/5"" }
  ] },
  ""pages"": { ""1"": {}, ""2"": {} }
}";

        [TestMethod]
        public void Structured_WalksReadingOrder_TablesStayIntact()
        {
            var chunks = DocumentChunker.ChunkStructured(Fixture(StructuredFixture), Options());

            // title chunk, section chunk (paragraph + list item), table chunk, trailing section header
            Assert.AreEqual(4, chunks.Count);

            Assert.AreEqual("Network Handbook", chunks[0].Text);
            Assert.AreEqual("Network Handbook", chunks[0].HeadingPath);
            Assert.AreEqual(1, chunks[0].PageFrom);

            StringAssert.Contains(chunks[1].Text, "EDGE_TLS_01");
            StringAssert.Contains(chunks[1].Text, "Rack three, slot one.");
            Assert.AreEqual("Network Handbook > Edge Servers", chunks[1].HeadingPath);
            Assert.AreEqual(1, chunks[1].PageFrom, "pages union across the section's items");
            Assert.AreEqual(2, chunks[1].PageTo);
            CollectionAssert.AreEqual(new[] { "EDGE_TLS_01" }, chunks[1].Identifiers);

            Assert.AreEqual(DocumentChunk.TableKind, chunks[2].Kind);
            StringAssert.StartsWith(chunks[2].Text, "| Port | Use |\n| --- | --- |");
            StringAssert.Contains(chunks[2].Text, "| 443 | tls \\| https |");
            Assert.AreEqual(2, chunks[2].PageFrom);
            Assert.AreEqual("Network Handbook > Edge Servers", chunks[2].HeadingPath);

            Assert.AreEqual("Ports", chunks[3].Text, "a trailing header still yields its chunk");
            Assert.AreEqual("Network Handbook > Ports", chunks[3].HeadingPath);

            StringAssert.DoesNotMatch(String.Join("\n", chunks.Select(c => c.Text)),
                new System.Text.RegularExpressions.Regex("CONFIDENTIAL"),
                "page furniture is skipped");
        }

        [TestMethod]
        public void Structured_OversizeTable_SplitsIntoWindows_RepeatingHeader()
        {
            var rows = String.Join(",\n", Enumerable.Range(1, 40)
                .Select(i => $@"[ {{ ""text"": ""row{i:D2}"" }}, {{ ""text"": ""value of row {i:D2}"" }} ]"));
            var json = $@"{{
  ""tables"": [ {{ ""self_ref"": ""#/tables/0"", ""data"": {{ ""grid"": [
      [ {{ ""text"": ""Key"" }}, {{ ""text"": ""Value"" }} ],
      {rows}
  ] }} }} ],
  ""body"": {{ ""children"": [ {{ ""$ref"": ""#/tables/0"" }} ] }}
}}";

            var chunks = DocumentChunker.ChunkStructured(Fixture(json), Options(max: 300));

            Assert.IsTrue(chunks.Count > 1, "an over-max table row-windows");
            foreach (var chunk in chunks)
            {
                Assert.AreEqual(DocumentChunk.TableKind, chunk.Kind);
                StringAssert.StartsWith(chunk.Text, "| Key | Value |\n| --- | --- |",
                    "every window repeats the header");
                Assert.IsTrue(chunk.Text.Split('\n').Length > 2, "every window carries at least one body row");
            }

            var allRows = String.Join("\n", chunks.Select(c => c.Text));
            StringAssert.Contains(allRows, "row01");
            StringAssert.Contains(allRows, "row40");
        }

        [TestMethod]
        public void Structured_CyclicGroups_DoNotHang()
        {
            const String json = @"{
  ""texts"": [ { ""self_ref"": ""#/texts/0"", ""label"": ""text"", ""text"": ""Reachable content."" } ],
  ""groups"": [
    { ""self_ref"": ""#/groups/0"", ""children"": [ { ""$ref"": ""#/groups/1"" }, { ""$ref"": ""#/texts/0"" } ] },
    { ""self_ref"": ""#/groups/1"", ""children"": [ { ""$ref"": ""#/groups/0"" } ] }
  ],
  ""body"": { ""children"": [ { ""$ref"": ""#/groups/0"" } ] }
}";
            var chunks = DocumentChunker.ChunkStructured(Fixture(json), Options());
            Assert.AreEqual(1, chunks.Count);
            Assert.AreEqual("Reachable content.", chunks[0].Text);
        }

        [TestMethod]
        public void Structured_EmptyOrBodyless_YieldsNoChunks()
        {
            Assert.AreEqual(0, DocumentChunker.ChunkStructured(Fixture("{}"), Options()).Count);
            Assert.AreEqual(0, DocumentChunker.ChunkStructured(
                Fixture(@"{ ""body"": { ""children"": [] } }"), Options()).Count);
        }

        [TestMethod]
        public void Structured_HeaderOnlyTable_IsOneWindow()
        {
            const String json = @"{
  ""tables"": [ { ""self_ref"": ""#/tables/0"", ""data"": { ""grid"": [
      [ { ""text"": ""Lonely"" }, { ""text"": ""Header"" } ]
  ] } } ],
  ""body"": { ""children"": [ { ""$ref"": ""#/tables/0"" } ] }
}";
            var chunks = DocumentChunker.ChunkStructured(Fixture(json), Options());
            Assert.AreEqual(1, chunks.Count);
            Assert.AreEqual("| Lonely | Header |\n| --- | --- |", chunks[0].Text);
        }

        #endregion
    }
}
