// MIT License
//
// IntegrationsDiagnosticBudgetTest.cs
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
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Integrations.Contract;
using NoSQL.GraphDB.Integrations.Run;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   How many diagnostics of one code a job report carries, and that the rest of them are in the log
    ///   rather than gone. One test per rule, because the two failure directions are opposite and both
    ///   silent: a cap that does not hold leaves a report nobody reads, and a cap that drops what it
    ///   removes turns a run's only account into a summary of itself.
    ///
    ///   <para>The wiring - that a real run's report passes through this on its way out, after the
    ///   credential scrub - is pinned in <c>IntegrationsWritePathTest</c>, where the runner is.</para>
    /// </summary>
    [TestClass]
    public class IntegrationsDiagnosticBudgetTest
    {
        private const String Flooded = DiagnosticCodes.RowWithoutMac;
        private const String Other = DiagnosticCodes.WeakOnlyIdentity;

        [TestMethod]
        public void AReportUnderTheCapIsLeftExactlyAsItWas()
        {
            var diagnostics = Many(Flooded, 3);

            var elided = DiagnosticBudget.Apply(diagnostics, NullLogger.Instance);

            Assert.AreEqual(0, elided);
            Assert.AreEqual(3, diagnostics.Count);
            CollectionAssert.AreEqual(new[] { "1", "2", "3" }, Subjects(diagnostics),
                "an ordinary run's report must come back untouched, in its own order: the cap is a ceiling " +
                "and not a formatting pass");
        }

        [TestMethod]
        public void AReportExactlyAtTheCapKeepsEveryOneAndClaimsNothingWasCut()
        {
            var diagnostics = Many(Flooded, DiagnosticBudget.PerCode);

            var elided = DiagnosticBudget.Apply(diagnostics, NullLogger.Instance);

            Assert.AreEqual(0, elided, "the boundary is the cap itself, and an off-by-one here would cut a " +
                                       "diagnostic while reporting that it cut none");
            Assert.AreEqual(DiagnosticBudget.PerCode, diagnostics.Count);
            Assert.IsFalse(diagnostics.Any(d => d.Code == DiagnosticCodes.DiagnosticsElided),
                "an elision entry with nothing elided sends a reader to a log that has nothing more in it");
        }

        [TestMethod]
        public void OneOverTheCapKeepsTheFIRSTOnesAndCountsTheRestInAnEntryOfItsOwn()
        {
            var diagnostics = Many(Flooded, DiagnosticBudget.PerCode + 1);

            var elided = DiagnosticBudget.Apply(diagnostics, NullLogger.Instance);

            Assert.AreEqual(1, elided);
            Assert.AreEqual(DiagnosticBudget.PerCode + 1, diagnostics.Count,
                "the kept ones plus one entry accounting for the rest");

            var kept = diagnostics.Where(d => d.Code == Flooded).ToList();
            Assert.AreEqual(DiagnosticBudget.PerCode, kept.Count);
            CollectionAssert.AreEqual(
                Enumerable.Range(1, DiagnosticBudget.PerCode)
                    .Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray(),
                kept.Select(d => d.Subject).ToArray(),
                "the FIRST of them, in the order the run raised them: examples taken from an arbitrary " +
                "place cannot be compared against the next run's");

            var entry = diagnostics[diagnostics.Count - 1];
            Assert.AreEqual(DiagnosticCodes.DiagnosticsElided, entry.Code,
                "and the entry comes last, after what it accounts for");
            Assert.AreEqual(Flooded, entry.Subject,
                "the subject names WHICH code was cut, which is what makes the count mean anything");
            StringAssert.Contains(entry.Message, "1 further diagnostics");
            StringAssert.Contains(entry.Message, DiagnosticBudget.LogCategory,
                "the message names the category to raise, or the detail is unreachable in practice");
        }

        [TestMethod]
        public void TwoFloodedCodesGetOneEntryEach_InTheOrderTheyFirstAppeared()
        {
            var diagnostics = new List<DiagnosticDto>();
            diagnostics.AddRange(Many(Flooded, DiagnosticBudget.PerCode + 2));
            diagnostics.AddRange(Many(Other, DiagnosticBudget.PerCode + 5));

            var elided = DiagnosticBudget.Apply(diagnostics, NullLogger.Instance);

            Assert.AreEqual(7, elided);

            var entries = diagnostics.Where(d => d.Code == DiagnosticCodes.DiagnosticsElided).ToList();
            Assert.AreEqual(2, entries.Count,
                "one per code, or two unrelated floods read as one number and neither can be acted on");
            Assert.AreEqual(Flooded, entries[0].Subject,
                "in first-appearance order, so the same run twice produces the same report");
            Assert.AreEqual(Other, entries[1].Subject);
            StringAssert.Contains(entries[0].Message, "2 further diagnostics");
            StringAssert.Contains(entries[1].Message, "5 further diagnostics");
        }

        [TestMethod]
        public void AQuietCodeSurvivesAnotherCodesFlood_WithTheKeptOnesStillInRunOrder()
        {
            // Interleaved, because that is how a run raises them: the two that mean something arrive in the
            // middle of the flood, and grouping by code to count would reorder the report.
            var diagnostics = new List<DiagnosticDto>();
            for (var i = 1; i <= DiagnosticBudget.PerCode + 3; i++)
            {
                diagnostics.Add(One(Flooded, i.ToString(CultureInfo.InvariantCulture)));
                if (i == 4)
                {
                    diagnostics.Add(One(Other, "the lonely one"));
                }
            }

            DiagnosticBudget.Apply(diagnostics, NullLogger.Instance);

            Assert.AreEqual(1, diagnostics.Count(d => d.Code == Other),
                "the diagnostic that meant something is exactly what the flood used to bury, and a per-code " +
                "cap is the only shape that keeps it");
            Assert.AreEqual("4", diagnostics[3].Subject);
            Assert.AreEqual(Other, diagnostics[4].Code,
                "and it stays where the run raised it, between the fourth and the fifth of the flood");
        }

        [TestMethod]
        public void EveryDiagnosticReachesTheLog_IncludingTheOnesTheReportKept()
        {
            var sink = new TestLogSink();
            using var loggers = sink.CreateFactory();
            var diagnostics = Many(Flooded, DiagnosticBudget.PerCode + 2);

            DiagnosticBudget.Apply(diagnostics, loggers.CreateLogger(DiagnosticBudget.LogCategory));

            Assert.AreEqual(DiagnosticBudget.PerCode + 2,
                sink.Entries.Count(e => e.Level == LogLevel.Debug),
                "the log is the WHOLE account, kept ones included: a log holding only the remainder cannot " +
                "be read as the list the report summarises");
            Assert.IsTrue(sink.Contains(LogLevel.Debug, "Integration diagnostic", Flooded,
                    (DiagnosticBudget.PerCode + 2).ToString(CultureInfo.InvariantCulture)),
                "and the last one, which the report left off, is there with its code and its subject: " +
                String.Join(" | ", sink.Entries.Select(e => e.Message)));
            Assert.IsTrue(sink.Contains(LogLevel.Information, "were left off the report"),
                "one line at INFORMATION says the detail exists, because an unconfigured log is where an " +
                "operator finds out that raising the level buys them something");
        }

        [TestMethod]
        public void NoDiagnosticIsFormattedWhenTheCategoryIsNotAtDebug()
        {
            // What an unconfigured container has. The guard is not politeness: a vehicle-sized job raises
            // tens of thousands of these, and formatting them for a sink that discards them is the one cost
            // this rule is not allowed to have.
            var sink = new TestLogSink();
            using var loggers = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Information);
                builder.AddProvider(sink);
            });
            var diagnostics = Many(Flooded, DiagnosticBudget.PerCode + 2);

            DiagnosticBudget.Apply(diagnostics, loggers.CreateLogger(DiagnosticBudget.LogCategory));

            Assert.IsFalse(sink.Contains(LogLevel.Trace, "Integration diagnostic"),
                "nothing per diagnostic at all: " + String.Join(" | ", sink.Entries.Select(e => e.Message)));
            Assert.IsTrue(sink.Contains(LogLevel.Information, "were left off the report"),
                "while the summary line still lands, since it is the only thing that says there was more");
        }

        [TestMethod]
        public void NoSummaryLineIsLoggedWhenNothingWasElided()
        {
            var sink = new TestLogSink();
            using var loggers = sink.CreateFactory();

            DiagnosticBudget.Apply(Many(Flooded, DiagnosticBudget.PerCode), loggers
                .CreateLogger(DiagnosticBudget.LogCategory));

            Assert.IsFalse(sink.Contains(LogLevel.Information, "were left off the report"),
                "a line about a cut that did not happen sends an operator to raise a level for nothing");
        }

        [TestMethod]
        public void ADiagnosticWithNoCodeIsCountedAsItsOwnGroupRatherThanThrownOver()
        {
            var diagnostics = new List<DiagnosticDto>();
            for (var i = 0; i < DiagnosticBudget.PerCode + 1; i++)
            {
                diagnostics.Add(new DiagnosticDto { Message = "Raised with no code at all." });
            }

            var elided = DiagnosticBudget.Apply(diagnostics, NullLogger.Instance);

            Assert.AreEqual(1, elided,
                "a missing code is a defect in whoever raised it, and losing the whole report over it would " +
                "hide the run's real outcome behind an exception in its last frame");
            Assert.AreEqual(DiagnosticBudget.PerCode + 1, diagnostics.Count);
        }

        [TestMethod]
        public void TheArgumentsAreRequired()
        {
            Assert.ThrowsException<ArgumentNullException>(
                () => DiagnosticBudget.Apply(null, NullLogger.Instance));
            Assert.ThrowsException<ArgumentNullException>(
                () => DiagnosticBudget.Apply(new List<DiagnosticDto>(), null),
                "a caller with no logger would silently make the cap lossy, which is the one thing it must " +
                "never be");
        }

        private static IList<DiagnosticDto> Many(String code, Int32 count)
        {
            var diagnostics = new List<DiagnosticDto>(count);
            for (var i = 1; i <= count; i++)
            {
                diagnostics.Add(One(code, i.ToString(CultureInfo.InvariantCulture)));
            }

            return diagnostics;
        }

        private static DiagnosticDto One(String code, String subject)
        {
            return new DiagnosticDto(code, "Something the source could not say.", subject);
        }

        private static String[] Subjects(IEnumerable<DiagnosticDto> diagnostics)
        {
            return diagnostics.Select(d => d.Subject).ToArray();
        }
    }
}
