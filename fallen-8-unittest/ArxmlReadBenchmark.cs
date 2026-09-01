// MIT License
//
// ArxmlReadBenchmark.cs
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
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Integrations.Providers.AutosarArxml;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Opt-in benchmark for what an AUTOSAR extract costs to READ, in allocation rather than in time.
    ///
    ///   <para>It exists because a run holds a file's bytes for its whole life and used to ALSO decode one
    ///   whole extract to a UTF-16 string to parse it - two bytes per character, held for the parse, for a
    ///   reader that drives an <c>XmlReader</c> and never wanted a string. The claim that reading from bytes
    ///   removes that copy is arithmetic; this measures whether it is also true in practice, which is not the
    ///   same thing (a stream path that quietly buffered the document whole would satisfy the arithmetic and
    ///   fail here).</para>
    ///
    ///   <para>Cumulative allocation, not peak: <c>GC.GetTotalAllocatedBytes</c> is reproducible and is
    ///   exactly where a whole-document copy shows up, whereas a peak reading depends on when the collector
    ///   happened to run. Both arms START FROM THE BYTES, because that is what a run actually has - measuring
    ///   the text arm from a string it was handed for free would compare nothing a run can choose.</para>
    ///
    ///   <para>MEASURED, 2026-09-02, on a synthetic 15.7 MiB extract of 40,000 signals: 225.5 MiB
    ///   allocated through the text seam against 162.4 MiB from bytes, so 63.1 MiB saved - four times the
    ///   document, not the two the string alone accounts for, because the decode's own buffers and a second
    ///   character copy into the parser go with it. It was also faster (972 ms against 1263 ms), which was
    ///   not the goal and is the same cause.</para>
    ///
    ///   <para>The 162 MiB that REMAINS is the reader materialising the subtrees it collects, and is a
    ///   different problem from this one: it scales with how much of a document is interesting rather than
    ///   with the document.</para>
    ///
    ///   <para>Follows the repo convention (Benchmark category + [Ignore]) so it is NOT part of the default
    ///   run; run the methods explicitly to capture numbers. Output is prefixed "[ARXMLBENCH]".</para>
    /// </summary>
    [TestClass]
    public class ArxmlReadBenchmark
    {
        private static void Emit(String line)
        {
            Console.WriteLine("[ARXMLBENCH] " + line);
        }

        private static Int32 EnvInt(String name, Int32 fallback)
        {
            var raw = Environment.GetEnvironmentVariable(name);
            return Int32.TryParse(raw, out var parsed) && parsed > 0 ? parsed : fallback;
        }

        /// <summary>
        ///   A synthetic extract of <paramref name="signals"/> signals with their system signals, shaped like
        ///   the real thing in the one way that matters here: most of the document is elements the reader
        ///   collects, so the measurement is not dominated by material it skips.
        /// </summary>
        private static Byte[] Document(Int32 signals)
        {
            var text = new StringBuilder(signals * 400);
            text.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            text.Append("<AUTOSAR xmlns=\"http://autosar.org/schema/r4.0\">\n<AR-PACKAGES>\n");

            text.Append("<AR-PACKAGE>\n<SHORT-NAME>SystemSignals</SHORT-NAME>\n<ELEMENTS>\n");
            for (var i = 0; i < signals; i++)
            {
                text.Append("<SYSTEM-SIGNAL>\n<SHORT-NAME>SYS_").Append(i).Append("</SHORT-NAME>\n");
                text.Append("<DESC><L-2 L=\"EN\">Bus independent signal number ").Append(i)
                    .Append(", carried by one or more buses.</L-2></DESC>\n");
                text.Append("</SYSTEM-SIGNAL>\n");
            }

            text.Append("</ELEMENTS>\n</AR-PACKAGE>\n");

            text.Append("<AR-PACKAGE>\n<SHORT-NAME>ISignals</SHORT-NAME>\n<ELEMENTS>\n");
            for (var i = 0; i < signals; i++)
            {
                text.Append("<I-SIGNAL>\n<SHORT-NAME>SIG_").Append(i).Append("</SHORT-NAME>\n");
                text.Append("<DESC><L-2 L=\"EN\">Per bus realisation number ").Append(i)
                    .Append(" of the signal above.</L-2></DESC>\n");
                text.Append("<LENGTH>16</LENGTH>\n");
                text.Append("<SYSTEM-SIGNAL-REF DEST=\"SYSTEM-SIGNAL\">/SystemSignals/SYS_").Append(i)
                    .Append("</SYSTEM-SIGNAL-REF>\n");
                text.Append("</I-SIGNAL>\n");
            }

            text.Append("</ELEMENTS>\n</AR-PACKAGE>\n</AR-PACKAGES>\n</AUTOSAR>\n");

            return new UTF8Encoding(false).GetBytes(text.ToString());
        }

        private static Int64 Measure(Action work)
        {
            // Settled first, so the reading is this arm's allocation and not the tail of the previous one.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetTotalAllocatedBytes(precise: true);
            var clock = Stopwatch.StartNew();
            work();
            clock.Stop();
            var after = GC.GetTotalAllocatedBytes(precise: true);

            Emit(String.Format("    took {0} ms", clock.ElapsedMilliseconds));
            return after - before;
        }

        [TestMethod]
        [TestCategory("Benchmark")]
        [Ignore("Opt-in benchmark: run explicitly to capture numbers.")]
        public void ReadingAnExtractFromBytesAllocatesLessThanDecodingItFirst()
        {
            var signals = EnvInt("ARXMLBENCH_SIGNALS", 40_000);
            var bytes = Document(signals);
            Emit(String.Format("document: {0} signals, {1:F1} MiB of bytes", signals,
                bytes.Length / 1048576.0));

            // Warm: JIT and the reader's static tables, so neither arm pays for them.
            using (var warm = new MemoryStream(bytes, writable: false))
            {
                ArxmlReader.Read(warm);
            }

            var viaText = Measure(() =>
            {
                // What the runtime's text seam does: bytes to a string with mark detection, then parse it.
                using var source = new MemoryStream(bytes, writable: false);
                using var decoder = new StreamReader(source, Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true);
                ArxmlReader.Read(decoder.ReadToEnd());
            });

            var viaBytes = Measure(() =>
            {
                using var stream = new MemoryStream(bytes, writable: false);
                ArxmlReader.Read(stream);
            });

            Emit(String.Format("via text : {0:F1} MiB allocated", viaText / 1048576.0));
            Emit(String.Format("via bytes: {0:F1} MiB allocated", viaBytes / 1048576.0));
            Emit(String.Format("saved    : {0:F1} MiB ({1:F2}x)", (viaText - viaBytes) / 1048576.0,
                viaBytes == 0 ? 0 : (Double)viaText / viaBytes));

            // Stated as an assertion rather than only printed: the saving is the whole reason the byte path
            // exists, so a change that reintroduced a whole-document copy should fail here rather than be
            // noticed by whoever reads the numbers.
            Assert.IsTrue(viaBytes < viaText,
                "reading from bytes must allocate less than decoding the document first, or the seam bought " +
                "nothing");
        }
    }
}
