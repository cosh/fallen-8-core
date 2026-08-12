// MIT License
//
// LiteralRoundTripTest.cs
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
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Transaction;
using NoSQL.GraphDB.App.Controllers.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Helper;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Wire-literal ingress must be the exact inverse of egress (feature platform-integrity-audit W6).
    ///
    ///   <para>The culture half of this was already fixed and tested (feature
    ///   property-ingestion-culture): parse with InvariantCulture so a comma-decimal host does not read
    ///   "0.8" as 8. Its own stated principle is "egress mirrors ingress" - and the KIND dimension of
    ///   date values did not mirror. <c>Convert.ChangeType</c> parses with default styles, which converts
    ///   a UTC ("...Z") wire value to the host's LOCAL time; egress renders with "O" and therefore emits
    ///   a different string than the one that was sent.</para>
    ///
    ///   <para>The instant was always preserved, so this is representation asymmetry rather than
    ///   corruption. It matters because it defeats any client that decides "has anything changed?" by
    ///   comparing the value it intends to write against the value it just read: EVERY date property
    ///   would differ on EVERY comparison, forever, producing a write on every poll of an unchanged
    ///   source. It also made the stored tick value host-timezone-dependent.</para>
    ///
    ///   <para>These tests force a non-UTC timezone. That is the whole point: CI and the container both
    ///   run UTC, where the bug is invisible, which is exactly how it survived.</para>
    /// </summary>
    [TestClass]
    public class LiteralRoundTripTest
    {
        /// <summary>Runs <paramref name="body"/> with the process timezone forced to a non-UTC zone with
        /// a whole-hour offset (so an incorrect conversion shifts the wall clock visibly).</summary>
        private static void InNonUtcTimeZone(Action body)
        {
            var original = TimeZoneInfo.Local;
            try
            {
                // Both ids resolve on Windows and on Linux respectively; pick whichever exists.
                TimeZoneInfo zone = null;
                foreach (var id in new[] { "W. Europe Standard Time", "Europe/Berlin", "Etc/GMT-2" })
                {
                    try { zone = TimeZoneInfo.FindSystemTimeZoneById(id); break; } catch { }
                }

                if (zone == null || zone.BaseUtcOffset == TimeSpan.Zero)
                {
                    Assert.Inconclusive("No non-UTC timezone available on this host to exercise the asymmetry.");
                    return;
                }

                TimeZoneInfo.ClearCachedData();
                Environment.SetEnvironmentVariable("TZ", zone.Id);
                body();
            }
            finally
            {
                Environment.SetEnvironmentVariable("TZ", original.Id);
                TimeZoneInfo.ClearCachedData();
            }
        }

        [TestMethod]
        public void AUtcDateTime_RoundTripsToTheIdenticalWireString()
        {
            // The exact case a reconciling client hits: send a UTC instant, read it back, compare the
            // strings. Before W6 these differed by the host's offset on any non-UTC host.
            const string wire = "2026-08-09T10:00:00.0000000Z";

            var parsed = (DateTime)AllowedLiteralTypes.ConvertInvariant(wire, typeof(DateTime));

            Assert.AreEqual(DateTimeKind.Utc, parsed.Kind,
                "A 'Z' wire value is a UTC instant and must be stored as one, not silently localised.");
            Assert.AreEqual(wire, parsed.ToString("O", CultureInfo.InvariantCulture),
                "Egress uses \"O\"; ingress must be its inverse or every comparison of a date property fails.");
        }

        [TestMethod]
        public void AUtcDateTime_RoundTrips_EvenOnANonUtcHost()
        {
            InNonUtcTimeZone(() =>
            {
                const string wire = "2026-08-09T10:00:00.0000000Z";

                var parsed = (DateTime)AllowedLiteralTypes.ConvertInvariant(wire, typeof(DateTime));

                Assert.AreEqual(DateTimeKind.Utc, parsed.Kind);
                Assert.AreEqual(10, parsed.Hour, "the wall clock must not shift by the host's offset");
                Assert.AreEqual(wire, parsed.ToString("O", CultureInfo.InvariantCulture));
            });
        }

        [TestMethod]
        public void AnUnspecifiedDateTime_StaysUnspecified()
        {
            // A wire value with no designator carries no zone information, so inventing one would be a
            // different lie. It must round-trip as written.
            const string wire = "2026-08-09T10:00:00.0000000";

            var parsed = (DateTime)AllowedLiteralTypes.ConvertInvariant(wire, typeof(DateTime));

            Assert.AreEqual(DateTimeKind.Unspecified, parsed.Kind);
            Assert.AreEqual(wire, parsed.ToString("O", CultureInfo.InvariantCulture));
        }

        [TestMethod]
        public void ADateTimeOffset_PreservesItsOffset()
        {
            const string wire = "2026-08-09T10:00:00.0000000+02:00";

            var parsed = (DateTimeOffset)AllowedLiteralTypes.ConvertInvariant(wire, typeof(DateTimeOffset));

            Assert.AreEqual(TimeSpan.FromHours(2), parsed.Offset);
            Assert.AreEqual(wire, parsed.ToString("O", CultureInfo.InvariantCulture));
        }

        /// <summary>
        ///   One value and its EXPECTED WIRE TEXT per allow-listed literal type. The text is spelled out
        ///   rather than computed, because a canonical form computed from the value is the same guess the
        ///   production code makes: the DateTimeOffset defect survived a round-trip assertion precisely
        ///   because the wrong text parsed back to the right value.
        /// </summary>
        private static readonly Dictionary<Type, (Object Value, String Wire)> _expectedEgress = new()
        {
            [typeof(String)] = ("text", "text"),
            [typeof(Boolean)] = (true, "True"),
            [typeof(Byte)] = ((Byte)7, "7"),
            [typeof(SByte)] = ((SByte)(-8), "-8"),
            [typeof(Int16)] = ((Int16)(-300), "-300"),
            [typeof(UInt16)] = ((UInt16)300, "300"),
            [typeof(Int32)] = (42, "42"),
            [typeof(UInt32)] = (42u, "42"),
            [typeof(Int64)] = (-9007199254740993L, "-9007199254740993"),
            [typeof(UInt64)] = (UInt64.MaxValue, "18446744073709551615"),
            [typeof(Single)] = (0.8f, "0.8"),
            [typeof(Double)] = (0.8d, "0.8"),
            [typeof(Decimal)] = (1.25m, "1.25"),
            [typeof(Char)] = ('x', "x"),
            [typeof(TimeSpan)] = (TimeSpan.FromMinutes(90), "01:30:00"),
            [typeof(DateTime)] = (new DateTime(2026, 8, 9, 10, 0, 0, DateTimeKind.Unspecified),
                                  "2026-08-09T10:00:00.0000000"),
            [typeof(DateTimeOffset)] = (new DateTimeOffset(2026, 8, 9, 10, 0, 0, TimeSpan.FromHours(2)),
                                        "2026-08-09T10:00:00.0000000+02:00"),
            [typeof(Guid)] = (Guid.Parse("2f1c4b6a-9d33-4f7e-9a11-6b2c8d5e4f30"),
                              "2f1c4b6a-9d33-4f7e-9a11-6b2c8d5e4f30"),
        };

        [TestMethod]
        public void EveryAllowedLiteralType_HasAnExpectedEgressText()
        {
            // Exhaustive BY CONSTRUCTION: the case list is the allow-list itself, so a 19th accepted type
            // fails here until someone states what it must look like on the wire. The previous version
            // hand-listed ten of the eighteen and its name over-claimed the rest.
            var uncovered = AllowedLiteralTypes.AllowedNames
                .Select(AllowedLiteralTypes.Resolve)
                .Where(type => !_expectedEgress.ContainsKey(type))
                .Select(type => type.FullName)
                .ToList();

            Assert.AreEqual(0, uncovered.Count,
                "Every allow-listed literal type needs a value and its expected wire text in this test; " +
                "missing: " + String.Join(", ", uncovered));
        }

        [TestMethod]
        public void EveryAllowedLiteral_RoundTripsThroughTheREALEgressFunction()
        {
            // The sibling tests assert against a HAND-WRITTEN ToString("O"), which is why they all passed
            // while the real egress rendered a DateTimeOffset as "08/09/2026 10:00:00 +02:00" - a form
            // ingress does not parse. So this one goes through the actual wire projection: build a real
            // element carrying one value per allow-listed type, project it exactly as the REST layer does,
            // and check both the rendered TEXT and what that text ingests back to. Why the text matters is
            // stated once, at the egress home (AGraphElement.FormatPropertyValue).
            var loggerFactory = TestLoggerFactory.Create();
            using var fallen8 = new Fallen8(loggerFactory);

            var values = new Dictionary<String, Object>();
            foreach (var expected in _expectedEgress)
            {
                values["p." + expected.Key.Name] = expected.Value.Value;
            }

            var tx = new CreateVerticesTransaction();
            tx.AddVertex(1u, "literals", values);
            fallen8.EnqueueTransaction(tx).WaitUntilFinished();
            Assert.IsTrue(fallen8.TryGetVertex(out var vertex, tx.GetCreatedVertices()[0].Id));

            var projected = new Vertex(vertex);
            Assert.AreEqual(_expectedEgress.Count, projected.Properties.Count,
                "every stored literal must be projected");

            foreach (var property in projected.Properties)
            {
                var declared = AllowedLiteralTypes.Resolve(property.FullQualifiedTypeName);
                var expected = _expectedEgress[declared];

                Assert.AreEqual(expected.Wire, property.PropertyValue,
                    property.PropertyId + ": egress must emit exactly this text");

                Assert.AreEqual(expected.Value, AllowedLiteralTypes.ConvertInvariant(property.PropertyValue, declared),
                    property.PropertyId + ": the wire form \"" + property.PropertyValue + "\" must ingest back " +
                    "to the value it was rendered from");
            }
        }

        [TestMethod]
        public void TheCultureContractIsUnchanged()
        {
            // Regression guard for property-ingestion-culture: the reason this conversion exists at all
            // is that a comma-decimal host must not read "0.8" as 8. Routing every call site through one
            // home must not have weakened that.
            InNonUtcTimeZone(() =>
            {
                Assert.AreEqual(0.8d, (double)AllowedLiteralTypes.ConvertInvariant("0.8", typeof(double)));
                Assert.AreEqual(0.8f, (float)AllowedLiteralTypes.ConvertInvariant("0.8", typeof(float)));
                Assert.AreEqual(0.8m, (decimal)AllowedLiteralTypes.ConvertInvariant("0.8", typeof(decimal)));
            });
        }

        [TestMethod]
        public void TheNonDateTypesStillConvertAsBefore()
        {
            Assert.AreEqual(42, (int)AllowedLiteralTypes.ConvertInvariant("42", typeof(int)));
            Assert.AreEqual(true, (bool)AllowedLiteralTypes.ConvertInvariant("true", typeof(bool)));
            Assert.AreEqual("plain", (string)AllowedLiteralTypes.ConvertInvariant("plain", typeof(string)));
            Assert.AreEqual(TimeSpan.FromMinutes(90),
                (TimeSpan)AllowedLiteralTypes.ConvertInvariant("01:30:00", typeof(TimeSpan)));
            Assert.AreEqual(Guid.Empty, (Guid)AllowedLiteralTypes.ConvertInvariant(Guid.Empty.ToString(), typeof(Guid)));
        }

        [TestMethod]
        public void ANullTargetTypeLeavesTheValueAString()
        {
            // The documented "no fullQualifiedTypeName means keep it a string" behaviour that three of
            // the call sites expressed with their own null check before they shared this home.
            Assert.AreEqual("as-is", AllowedLiteralTypes.ConvertInvariant("as-is", null));
        }
    }
}
