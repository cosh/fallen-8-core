// MIT License
//
// IntegrationsBlueprintTest.cs
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
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Integrations.Conformance;
using NoSQL.GraphDB.Integrations.Contract;
using NoSQL.GraphDB.Integrations.Identity;
using NoSQL.GraphDB.Integrations.Providers.AutosarArxml;
using NoSQL.GraphDB.Integrations.Providers.CsvDeviceList;
using NoSQL.GraphDB.Integrations.Providers.FroniusSolar;
using NoSQL.GraphDB.Integrations.Providers.UnifiNetwork;
using NoSQL.GraphDB.Integrations.Run;
using NoSQL.GraphDB.Integrations.Summary;
using NoSQL.GraphDB.Integrations.Validation;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The shipped blueprints (feature integrations, spec section 14; autosar-arxml): every trap row and every
    ///   vendor finding is a test here, because each of them is a thing a provider written from a summary
    ///   gets wrong and every one of those mistakes DELETES data rather than merely reporting it wrongly.
    ///
    ///   <para>The pure CSV parser is driven directly. Everything provider-level runs through the PUBLIC
    ///   conformance verifier, which drives the real <c>JobRunner</c>, catalog, validator, credential
    ///   resolver and in-memory graph offline, twice; a provider's own output is then read back through
    ///   <see cref="IObservableProvider.LastSnapshot"/> on the instance handed in.</para>
    /// </summary>
    [TestClass]
    public class IntegrationsBlueprintTest
    {
        /// <summary>The identity every run in this file asserts as.</summary>
        private const String Instance = "blueprint-suite";

        /// <summary>How many times the verifier runs a candidate: determinism and idempotence are
        /// statements about a repeat, so every request count seen by a stub is doubled.</summary>
        private const Int32 VerifierRuns = 2;

        private const String CsvFileName = "devices.csv";

        private const String ConsoleBaseUrl = "https://console.test/proxy/network/integration";
        private const String KeyValue = "test-console-api-key";

        private const String SiteId = "11111111-1111-1111-1111-111111111111";
        private const String GatewayId = "22222222-2222-2222-2222-222222222222";
        private const String SwitchId = "33333333-3333-3333-3333-333333333333";
        private const String ClientId = "44444444-4444-4444-4444-444444444444";

        private const String DeviceAddress = "http://192.168.1.50";

        // --- csv-device-list: the pure parser, driven with a string ---------------------------------

        [TestMethod]
        public void AQuotedFieldKeepsWhatItWrapsAndADoubledQuoteBecomesOneQuote()
        {
            var text = "mac,name\n44:D2:44:AA:BB:CC,\"  Meeting room \"\"A\"\", floor 2  \"\n";

            Assert.IsTrue(CsvTable.TryParse(text, ',', out var table, out var failure),
                "a file with a header row parses, and refusing this one would lose every device in it: " + failure);

            var row = table.Rows[0];
            Assert.AreEqual(2, row.Cells.Count,
                "the delimiter inside the quoted field split the row, which shifts every later cell one " +
                "column left and lands one device's name in another device's note");
            Assert.AreEqual("  Meeting room \"A\", floor 2  ", row.Cells[1],
                "a quoted cell is verbatim and a doubled quote is one literal quote; getting either wrong " +
                "changes the value written onto the device on every run, which makes every run a write");
        }

        [TestMethod]
        public void AHeaderRowInAnyCaseNamesTheSameColumn()
        {
            Assert.IsTrue(CsvTable.TryParse("MAC,Name,NOTE\n44:D2:44:AA:BB:CC,Printer,Lobby\n", ',',
                out var table, out _), "the file has a header row, so it parses");

            Assert.IsTrue(table.TryGetColumn("mac", out var mac),
                "a person types the header row, so MAC and mac are one column: not matching it means no " +
                "row can be identified and the whole run is refused");
            Assert.AreEqual(0, mac);
            Assert.IsTrue(table.TryGetColumn("note", out var note),
                "the optional columns fold the same way, or the note a person wrote never reaches the graph");
            Assert.AreEqual(2, note);
        }

        [TestMethod]
        public void BlankLinesAddNoRowsAndTheRowsThatRemainKeepTheirFileLineNumbers()
        {
            Assert.IsTrue(CsvTable.TryParse("mac\n\n44:D2:44:AA:BB:CC\n\n\n", ',', out var table, out _),
                "the file has a header row, so it parses");

            Assert.AreEqual(1, table.Rows.Count,
                "a blank line is not a device: turning one into a row produces an entity with no MAC on " +
                "every run, and the diagnostic list then hides the rows that really are broken");
            Assert.AreEqual(3, table.Rows[0].LineNumber,
                "a diagnostic names the PHYSICAL line so an operator can open the file at it; a number " +
                "counted from the header sends them to the wrong row");
        }

        [TestMethod]
        public void ARowWithFewerCellsThanTheHeaderIsKeptAndItsMissingColumnReadsAsAbsent()
        {
            Assert.IsTrue(CsvTable.TryParse("mac,name,note\n44:D2:44:AA:BB:CC,Printer\n", ',',
                out var table, out _), "the file has a header row, so it parses");

            var row = table.Rows[0];
            Assert.AreEqual("Printer", row.Cell(1),
                "a hand-edited short row is a fact about the file, not a reason to drop the device: dropping " +
                "it withdraws the device from the graph and deletes it on its last claim");
            Assert.IsNull(row.Cell(2),
                "a column the row does not reach is ABSENT, and writing an empty string for it would make the " +
                "property exist and overwrite what another integration knows about the same device");
        }

        [TestMethod]
        public void ALeadingByteOrderMarkIsNotPartOfTheFirstColumnName()
        {
            Assert.IsTrue(CsvTable.TryParse("﻿mac,name\n44:D2:44:AA:BB:CC,Printer\n", ',',
                out var table, out _), "the file has a header row, so it parses");

            Assert.AreEqual("mac", table.Header[0],
                "a spreadsheet program writes the mark ahead of the header, and leaving it attached means the " +
                "mac column is not found, the run is refused, and a file that looks correct never lands");
            Assert.IsTrue(table.TryGetColumn("mac", out _),
                "the mark must not survive into the column lookup either");
        }

        [TestMethod]
        public void ANewlineInsideAQuotedFieldIsReportedAsTheRowItLooksLikeRatherThanSilentlyMisParsed()
        {
            var text = "mac,name\n44:D2:44:AA:BB:CC,\"Line one\nLine two\",tail\n";

            Assert.IsTrue(CsvTable.TryParse(text, ',', out var table, out _),
                "the file has a header row, so it parses");

            Assert.AreEqual(2, table.Rows.Count,
                "the two physical lines stay two rows: joining them is the silent mis-parse this reader " +
                "exists to avoid, which moves one row's cells into another row's columns");
            Assert.IsTrue(table.Rows[0].UnterminatedQuote,
                "the open quote is REPORTED, because a newline inside a quoted field is unsupported and an " +
                "unreported one leaves an operator with a graph whose values came from the wrong columns");
            Assert.AreEqual("Line one", table.Rows[0].Cells[1],
                "the row was read as the row it LOOKS like, which is what makes the report actionable");
            Assert.IsFalse(table.Rows[1].UnterminatedQuote,
                "the following line is not itself broken, so reporting it too would bury the one row that is");
        }

        // --- csv-device-list: through the verifier, which runs the real stack -----------------------

        [TestMethod]
        public async Task TheShippedCsvBlueprintConforms()
        {
            var provider = new CsvDeviceListProvider();

            var report = await ConformanceVerifier.VerifyAsync(provider, CsvJob(HappyCsv),cancellationToken: CancellationToken.None);

            Assert.IsTrue(report.Conforms,
                "the floor of the provider contract must pass every check, or the suite that licenses a " +
                "fourth integration without an identity review is not trustworthy: " + Failures(report));
        }

        [TestMethod]
        public async Task EveryNamedDelimiterFormAndASingleLiteralCharacterReadTheWholeFile()
        {
            var forms = new[]
            {
                new[] { "tab", "\t" },
                new[] { "semicolon", ";" },
                new[] { "pipe", "|" },
                new[] { "comma", "," },
                new[] { "#", "#" },
            };

            foreach (var form in forms)
            {
                var provider = new CsvDeviceListProvider();
                var file = "mac" + form[1] + "name\n44:D2:44:AA:BB:CC" + form[1] + "Reception printer\n";

                var report = await ConformanceVerifier.VerifyAsync(provider, CsvJob(file, delimiter: form[0]),cancellationToken: CancellationToken.None);

                var snapshot = SnapshotOf(provider);
                Assert.AreEqual(1, snapshot.Entities.Count,
                    "delimiter form '" + form[0] + "' did not separate the columns, so the whole header read " +
                    "as one column, no row could be identified and every device in the file is withdrawn: " +
                    Failures(report));
                Assert.AreEqual("Reception printer", Property(snapshot.Entities[0], "csv.name"),
                    "delimiter form '" + form[0] + "' split the row somewhere else than the separator, so the " +
                    "value landing on the device came from the wrong column");
            }
        }

        [TestMethod]
        public async Task AFileWithNoHeaderRowFailsTheRunRatherThanReportingAnEmptyList()
        {
            var provider = new CsvDeviceListProvider();

            var report = await ConformanceVerifier.VerifyAsync(provider, CsvJob("\n   \n\n"),cancellationToken: CancellationToken.None);

            StringAssert.Contains(Refusal(report), CsvTableFailure.NoHeaderRow.ToString(),
                "a file with no header row has no column to read a MAC out of, so the run must fail naming " +
                "that: reporting an empty list instead withdraws every device this identity ever claimed");
            AssertWithdrewNothing(report,
                "the run failed, so the graph must keep every device it had and the next run start from it");
            Assert.IsNull(provider.LastSnapshot,
                "a refused file must produce no snapshot at all, because a complete snapshot with no devices " +
                "in it is the statement that the source is empty");
        }

        [TestMethod]
        public async Task AFileWithNoMacColumnFailsTheRunAndTheRefusalNamesTheColumnsFound()
        {
            var provider = new CsvDeviceListProvider();

            var report = await ConformanceVerifier.VerifyAsync(provider, CsvJob("name;note\nReception printer;Lobby\n"),
                cancellationToken: CancellationToken.None);

            var refusal = Refusal(report);
            StringAssert.Contains(refusal, "'name;note'",
                "the refusal must NAME the columns it found: the usual cause is the wrong delimiter, which " +
                "leaves the whole header row as one column, and a reader who is only told 'no mac column' " +
                "cannot see that and fixes the file instead of the setting");
            StringAssert.Contains(refusal, "no 'mac' column",
                "the run must fail rather than report an empty list, which would withdraw every device this " +
                "identity claimed and delete the ones nothing else claims");
            AssertWithdrewNothing(report, "a failed run withdraws nothing at all");
        }

        [TestMethod]
        public async Task ARowWithNoMacIsReportedAndSkippedWhileLaterRowsStillLand()
        {
            var provider = new CsvDeviceListProvider();

            var report = await ConformanceVerifier.VerifyAsync(provider, CsvJob("mac,name\n,No mac in this row\nAA:BB:CC:DD:EE:01,Later row\n"),
                cancellationToken: CancellationToken.None);

            var snapshot = SnapshotOf(provider);
            Assert.AreEqual(1, Diagnostics(snapshot, DiagnosticCodes.RowWithoutMac),
                "the row nothing could resolve to must be REPORTED, or an operator never learns that a device " +
                "they put in the file is missing from the graph: " + Failures(report));
            StringAssert.Contains(FirstDiagnostic(snapshot, DiagnosticCodes.RowWithoutMac).Subject, "row 2",
                "the diagnostic names the physical line, which is the only thing an operator can act on");
            Assert.AreEqual(1, snapshot.Entities.Count,
                "the row is SKIPPED and not fatal: losing the whole run to one typo leaves every later row " +
                "unobserved, and the next complete run then withdraws every device this identity claimed");
            Assert.AreEqual("Later row", Property(snapshot.Entities[0], "csv.name"),
                "the rows after the broken one must still land, which is the entire reason for skipping it");
        }

        [TestMethod]
        public async Task ARepeatedMacIsReportedAndOnlyTheFirstRowIsUsed()
        {
            var provider = new CsvDeviceListProvider();

            var report = await ConformanceVerifier.VerifyAsync(provider, CsvJob("mac,name\n44:D2:44:AA:BB:CC,First spelling\n44d244aabbcc,Second spelling\n"),
                cancellationToken: CancellationToken.None);

            var snapshot = SnapshotOf(provider);
            Assert.AreEqual(1, snapshot.Entities.Count,
                "two rows carrying one MAC resolve to ONE element and overwrite each other by file order, so " +
                "emitting both makes the graph depend on the order somebody typed the file in: " +
                Failures(report));
            Assert.AreEqual("First spelling", Property(snapshot.Entities[0], "csv.name"),
                "the FIRST row is the one used, so re-ordering the file never changes what the device says");
            Assert.AreEqual(1, Diagnostics(snapshot, DiagnosticCodes.DuplicateMacInFile),
                "the repeat is reported once, or nobody learns that a row they wrote was never applied");
            StringAssert.Contains(FirstDiagnostic(snapshot, DiagnosticCodes.DuplicateMacInFile).Message, "row 2",
                "the diagnostic names the row that WAS used, which is the row an operator has to edit");
        }

        [TestMethod]
        public async Task AJobCarryingNoFileIsRefusedRatherThanRunAndWithdrawsNothing()
        {
            var provider = new CsvDeviceListProvider();
            var job = CsvJob(HappyCsv);

            // The equivalent of the old "the mount does not have that file": with nothing opened by name,
            // the way a run ends up without its source is a job that did not carry one. It is refused
            // BEFORE the provider is invoked, because once a run has reached the provider it has begun
            // making withdrawal-relevant decisions.
            job.Files.Clear();

            var report = await ConformanceVerifier.VerifyAsync(provider, job,
                cancellationToken: CancellationToken.None);

            var refusal = Refusal(report);
            StringAssert.Contains(refusal, CsvDeviceListProvider.FileSetting,
                "the refusal names the setting that wanted a file, or the caller has to guess which field " +
                "the form left empty");
            StringAssert.Contains(refusal, "files",
                "and it names where a file belongs, because the runtime opens nothing on disk: there is no " +
                "directory the caller could put one in instead");
            AssertWithdrewNothing(report, "a job that never ran withdraws nothing");
        }

        [TestMethod]
        public async Task TheLabelSettingRenamesEveryRowAndSelectsNone()
        {
            var provider = new CsvDeviceListProvider();

            await ConformanceVerifier.VerifyAsync(provider, CsvJob(HappyCsv, label: "printer"),cancellationToken: CancellationToken.None);

            var snapshot = SnapshotOf(provider);
            Assert.AreEqual(2, snapshot.Entities.Count,
                "the label RENAMES rows and never selects them: a setting that changed which rows are read " +
                "would change what a complete snapshot covers, so switching it would withdraw and delete the " +
                "rows it stopped looking at");
            Assert.AreEqual(2, CountByKind(snapshot, "printer"),
                "every row takes the label, or the graph ends up with two kinds of element for one file and " +
                "the rows under the old label are withdrawn on the next run");
        }

        // --- unifi-network: the three paging defences ----------------------------------------------

        [TestMethod]
        public async Task TheOffsetAdvancesByTheItemsActuallyReturnedSoAShortPageSkipsNothing()
        {
            var provider = new UnifiNetworkProvider();
            var console = new SourceDouble(request =>
            {
                var uri = request.RequestUri;
                if (IsSites(uri))
                {
                    return Json(OffsetOf(uri) == 0 ? Page(1, SiteItem) : Page(1));
                }

                if (IsDeviceDetails(uri))
                {
                    return Json("{}");
                }

                if (IsDevices(uri))
                {
                    // A console serving ONE item for a page of two hundred: the offset may only advance by
                    // what was actually served, never by the size asked for.
                    var offset = OffsetOf(uri);
                    if (offset == 0)
                    {
                        return Json(Page(2, GatewayItem));
                    }

                    return Json(offset == 1 ? Page(2, SwitchItem) : Page(2));
                }

                return IsClients(uri) ? Json(Page(0)) : Refused(HttpStatusCode.NotFound);
            });

            var report = await VerifyUnifiAsync(provider, console);

            var snapshot = SnapshotOf(provider);
            Assert.AreEqual(1, CountByClaim(snapshot, "unifi-device-id", GatewayId),
                "a complete snapshot's absences are WITHDRAWALS, so a list that stops early does not miss " +
                "devices, it removes them and deletes them on their last claim: " + Failures(report));
            Assert.AreEqual(1, CountByClaim(snapshot, "unifi-device-id", SwitchId),
                "the device served on the short page's successor must be collected exactly once; advancing by " +
                "the size asked for skips it, and a complete snapshot then deletes it");
            Assert.AreEqual(2, CountByKind(snapshot, "device"),
                "no device may be collected twice either, or one console device becomes two graph elements");
        }

        [TestMethod]
        public async Task AnEmptyPageEndsTheListRatherThanTheTotalCountSoTheRunDoesNotSpin()
        {
            var provider = new UnifiNetworkProvider();
            var console = new SourceDouble(request =>
            {
                var uri = request.RequestUri;
                if (IsSites(uri))
                {
                    // The console PROMISES five and will only ever serve one.
                    return Json(OffsetOf(uri) == 0 ? Page(5, SiteItem) : Page(5));
                }

                return Refused(HttpStatusCode.NotFound);
            });

            var report = await VerifyUnifiAsync(provider, console);

            Assert.IsTrue(console.Count("/v1/sites?") <= 2 * VerifierRuns,
                "the loop must stop on the EMPTY page rather than chase a totalCount the console will not " +
                "serve, which costs exactly one extra request per list: a loop that trusts the total keeps " +
                "asking until the page-count backstop fires, and the run then fails for the wrong reason " +
                "after hundreds of requests aimed at somebody's console");
            StringAssert.Contains(Refusal(report), "promised 5",
                "a complete snapshot's absences are withdrawals, so a list that ended below what the console " +
                "promised must refuse the run rather than describe four sites out of five as the whole console");
            AssertWithdrewNothing(report, "the refused run withdraws nothing, so the next run starts intact");
        }

        [TestMethod]
        public async Task AListEndingBelowThePromisedTotalRefusesTheRunRatherThanDescribingItAsComplete()
        {
            var provider = new UnifiNetworkProvider();
            var console = new SourceDouble(request =>
            {
                var uri = request.RequestUri;
                if (IsSites(uri))
                {
                    return Json(OffsetOf(uri) == 0 ? Page(1, SiteItem) : Page(1));
                }

                if (IsDeviceDetails(uri))
                {
                    return Json("{}");
                }

                if (IsDevices(uri))
                {
                    // Three promised, two ever served.
                    var offset = OffsetOf(uri);
                    if (offset == 0)
                    {
                        return Json(Page(3, GatewayItem));
                    }

                    return Json(offset == 1 ? Page(3, SwitchItem) : Page(3));
                }

                return IsClients(uri) ? Json(Page(0)) : Refused(HttpStatusCode.NotFound);
            });

            var report = await VerifyUnifiAsync(provider, console);

            var refusal = Refusal(report);
            StringAssert.Contains(refusal, "promised 3",
                "the console promised more devices than it served, and a complete snapshot missing them " +
                "withdraws them and deletes the ones nothing else claims: the run must be refused instead");
            StringAssert.Contains(refusal, "served 2",
                "the refusal names what was actually served, so an operator can see how far the list got");
            Assert.IsNull(provider.LastSnapshot,
                "no snapshot may come out of a short list: describing two of three devices as the whole " +
                "console is exactly the answer that deletes the third");
            AssertWithdrewNothing(report, "a refused run withdraws nothing");
        }

        [TestMethod]
        public async Task ThePromiseCheckedAgainstIsTheLowestTotalAnyPageReported()
        {
            var provider = new UnifiNetworkProvider();
            var console = new SourceDouble(request =>
            {
                var uri = request.RequestUri;
                if (IsSites(uri))
                {
                    // A churning list: the first page claims three, the last claims one, and one was served.
                    return Json(OffsetOf(uri) == 0 ? Page(3, SiteItem) : Page(1));
                }

                if (IsDeviceDetails(uri))
                {
                    return Json("{}");
                }

                if (IsDevices(uri))
                {
                    return Json(OffsetOf(uri) == 0 ? Page(1, GatewayItem) : Page(1));
                }

                return IsClients(uri) ? Json(Page(0)) : Refused(HttpStatusCode.NotFound);
            });

            var report = await VerifyUnifiAsync(provider, console);

            var snapshot = SnapshotOf(provider);
            Assert.AreEqual(1, CountByKind(snapshot, "site"),
                "a list that churns while it is paged reports a different total per page, and a run that saw " +
                "everything the console claimed throughout must NOT fail: refusing it here means a busy " +
                "console can never be read at all, and no snapshot means the graph is never updated: " +
                Failures(report));
        }

        // --- unifi-network: the rest of the traps --------------------------------------------------

        [TestMethod]
        public async Task ADeviceAnsweringNotFoundOnItsDetailsReadIsOmittedEntirelyAndReported()
        {
            var provider = new UnifiNetworkProvider();
            var console = new SourceDouble(request =>
            {
                var uri = request.RequestUri;
                if (IsDeviceDetails(uri))
                {
                    return uri.AbsolutePath.EndsWith(SwitchId, StringComparison.Ordinal)
                        ? Refused(HttpStatusCode.NotFound, "no such device")
                        : Json("{}");
                }

                return HappyConsole(request);
            });

            var report = await VerifyUnifiAsync(provider, console);

            var snapshot = SnapshotOf(provider);
            Assert.AreEqual(0, CountByClaim(snapshot, "unifi-device-id", SwitchId),
                "a device that was listed and then answered 404 was removed mid-run, and emitting it without " +
                "the uplink it has reads as a topology change that never happened: " + Failures(report));
            Assert.AreEqual(1, CountByClaim(snapshot, "unifi-device-id", GatewayId),
                "the other devices must still land, or one device removed during a run withdraws the console");
            Assert.AreEqual(1, Diagnostics(snapshot, DiagnosticCodes.DeviceRemovedDuringRun),
                "the omission is reported, or a device silently vanishing from the graph looks like a bug");
        }

        [TestMethod]
        public async Task AnyOtherFailureOnADetailsReadFailsTheRunBecauseA500DoesNotMeanTheDeviceIsGone()
        {
            var provider = new UnifiNetworkProvider();
            var console = new SourceDouble(request =>
            {
                return IsDeviceDetails(request.RequestUri)
                    ? Refused(HttpStatusCode.InternalServerError, "console is unwell")
                    : HappyConsole(request);
            });

            var report = await VerifyUnifiAsync(provider, console);

            StringAssert.Contains(Refusal(report), "500",
                "a 500 does not mean the device is gone: treating it like the tolerated 404 omits a device " +
                "that is plainly still adopted, and a complete snapshot then withdraws and deletes it");
            Assert.IsNull(provider.LastSnapshot,
                "no snapshot may come out of a console that failed on a device read");
            AssertWithdrewNothing(report, "the failed run withdraws nothing");
        }

        [TestMethod]
        public async Task AConsoleListingNoSitesFailsTheRun()
        {
            var provider = new UnifiNetworkProvider();
            var console = new SourceDouble(request =>
            {
                return IsSites(request.RequestUri) ? Json(Page(0)) : Refused(HttpStatusCode.NotFound);
            });

            var report = await VerifyUnifiAsync(provider, console);

            StringAssert.Contains(Refusal(report), "listed no sites",
                "a console always has at least one site, so an empty list is an answer that cannot be " +
                "trusted, and an empty COMPLETE snapshot withdraws every element this integration ever " +
                "claimed and deletes the ones nothing else claims");
            AssertWithdrewNothing(report, "the refused run withdraws nothing");
        }

        [TestMethod]
        public async Task EveryRequestOfAWholeRunWasAGet()
        {
            var provider = new UnifiNetworkProvider();
            var console = new SourceDouble(HappyConsole);

            await VerifyUnifiAsync(provider, console);

            Assert.IsTrue(console.Methods.Count > 0,
                "a run that issued no request at all would make this assertion vacuous");
            foreach (var method in console.Methods)
            {
                Assert.AreEqual("GET", method,
                    "read-only has to be a property of this code rather than of the credential: the vendor's " +
                    "contract has verbs that restart devices and rewrite firewall policy, and with no " +
                    "declared security scheme there is no published way to scope a key");
            }
        }

        [TestMethod]
        public async Task EveryRequestOfAWholeRunCarriedTheLeasedKeyInTheVendorsHeader()
        {
            var provider = new UnifiNetworkProvider();
            var console = new SourceDouble(HappyConsole);

            await VerifyUnifiAsync(provider, console);

            Assert.IsTrue(console.ApiKeys.Count > 0,
                "a run that issued no request at all would make this assertion vacuous");
            foreach (var sent in console.ApiKeys)
            {
                // Nothing else in the suite can see this. A source double answers the same whether the
                // header is there or not, so without this assertion the one line that puts the key on the
                // request could be deleted and every test in this file would still pass - while every real
                // run answered 401 and the report blamed the credential the operator had just checked.
                Assert.AreEqual(KeyValue, sent,
                    "every request must carry the LEASED value in the vendor's own header (" +
                    UnifiClient.ApiKeyHeader + "), or the console refuses a request that never " +
                    "authenticated and the failure reads as a wrong key");
            }
        }

        [TestMethod]
        public async Task VpnAndTeleportClientsAreCountedOnceAndNotEmitted()
        {
            var provider = new UnifiNetworkProvider();
            var console = new SourceDouble(request =>
            {
                var uri = request.RequestUri;
                if (IsClients(uri))
                {
                    return Json(OffsetOf(uri) == 0
                        ? Page(3,
                            ClientItem(ClientId, "Laptop", "WIRED", "192.168.1.50", "aa:bb:cc:dd:ee:11", GatewayId),
                            ClientItem("55555555-5555-5555-5555-555555555555", "Road warrior", "VPN",
                                "192.168.1.51"),
                            ClientItem("66666666-6666-6666-6666-666666666666", "Phone away", "TELEPORT",
                                "192.168.1.52"))
                        : Page(3));
                }

                return HappyConsole(request);
            });

            var report = await VerifyUnifiAsync(provider, console);

            var snapshot = SnapshotOf(provider);
            Assert.AreEqual(1, CountByKind(snapshot, "client"),
                "a VPN or Teleport connection carries no MAC and no uplink device, so nothing about it would " +
                "identify the same thing next run: emitting it creates a new element every run and withdraws " +
                "the one the previous run created: " + Failures(report));
            Assert.AreEqual(1, Diagnostics(snapshot, DiagnosticCodes.ClientsWithoutHardwareIdentity),
                "they are counted ONCE and not reported one by one, because a busy console has many and the " +
                "fact worth reporting is that they exist at all");
            StringAssert.Contains(
                FirstDiagnostic(snapshot, DiagnosticCodes.ClientsWithoutHardwareIdentity).Message, "2",
                "the count is the whole content of that one diagnostic, so it has to be the real number");
        }

        [TestMethod]
        public async Task AClientWithNoIdIsReportedIndividually()
        {
            var provider = new UnifiNetworkProvider();
            var console = new SourceDouble(request =>
            {
                var uri = request.RequestUri;
                if (IsClients(uri))
                {
                    return Json(OffsetOf(uri) == 0
                        ? Page(2,
                            ClientItem(ClientId, "Laptop", "WIRED", "192.168.1.50", "aa:bb:cc:dd:ee:11", GatewayId),
                            ClientItem(null, "Nameless", "WIRED", "192.168.1.53", "aa:bb:cc:dd:ee:12", GatewayId))
                        : Page(2));
                }

                return HappyConsole(request);
            });

            var report = await VerifyUnifiAsync(provider, console);

            var snapshot = SnapshotOf(provider);
            Assert.AreEqual(1, Diagnostics(snapshot, DiagnosticCodes.ClientWithoutId),
                "the vendor's own contract requires the id, so this is a broken contract rather than a kind " +
                "of client, and one is worth looking at individually: " + Failures(report));
            Assert.AreEqual("Nameless", FirstDiagnostic(snapshot, DiagnosticCodes.ClientWithoutId).Subject,
                "the diagnostic names the client, which is all an operator has to go on");
            Assert.AreEqual(1, CountByKind(snapshot, "client"),
                "the client with no identity is skipped: with nothing to resolve by, every run would create " +
                "another copy of it and withdraw the last one");
        }

        [TestMethod]
        public async Task ABareHostInTheBaseUrlIsRefusedNamingBothPublishedForms()
        {
            var provider = new UnifiNetworkProvider();

            // No stand-in at all: a request escaping BEFORE the refusal is itself the failure, because the
            // request that would escape carries the API key.
            var report = await ConformanceVerifier.VerifyAsync(provider, UnifiJob(provider, "https://192.168.1.1"),
                cancellationToken: CancellationToken.None);

            var refusal = Refusal(report);
            StringAssert.Contains(refusal, "https://{consoleIP}/proxy/network/integration",
                "the refusal must name the LOCAL console form, because guessing the path wrong sends the API " +
                "key to the console's own web UI, which is a login form on the same host and certificate");
            StringAssert.Contains(refusal,
                "https://api.ui.com/v1/connector/consoles/{consoleId}/proxy/network/integration",
                "and the cloud connector form, since one setting reaches either and an operator cannot know " +
                "which one their console needs unless both are named");
            Assert.IsNull(provider.LastSnapshot,
                "a bare host is refused rather than repaired, so nothing is described and nothing withdrawn");
        }

        [TestMethod]
        public async Task AShortRetryAfterIsHonouredAndTheRunContinues()
        {
            var provider = new UnifiNetworkProvider();
            var rateLimited = 0;
            var console = new SourceDouble(request =>
            {
                if (IsSites(request.RequestUri) && rateLimited < 1)
                {
                    rateLimited++;
                    return RateLimited("0");
                }

                return HappyConsole(request);
            });

            var report = await VerifyUnifiAsync(provider, console);

            Assert.AreEqual(1, rateLimited, "the fixture must actually have rate limited the run once");
            var snapshot = SnapshotOf(provider);
            Assert.AreEqual(1, CountByKind(snapshot, "site"),
                "429 is never a partial success: a run that gave up on a rate limit it was told how to wait " +
                "out would describe part of the console as the whole of it and delete the rest: " +
                Failures(report));
            Assert.AreEqual(2, CountByKind(snapshot, "device"),
                "the retried request must return the list it was rate limited for, in full");
        }

        [TestMethod]
        public async Task AConsoleThatKeepsAnswering429ExhaustsTheRunsBudgetRatherThanRetryingForever()
        {
            var provider = new UnifiNetworkProvider();
            var console = new SourceDouble(request => RateLimited("0"));

            var report = await VerifyUnifiAsync(provider, console);

            StringAssert.Contains(Refusal(report), "more than " +
                UnifiClient.RetryBudget.ToString(CultureInfo.InvariantCulture) + " times",
                "the budget is counted per RUN: a per-request budget lets a console answering 429 to " +
                "everything keep a run alive indefinitely, and a caller is waiting on this job");
            Assert.IsTrue(console.Urls.Count >= UnifiClient.RetryBudget + 1,
                "a run that gave up before spending its budget would refuse a console that was only asking " +
                "for a moment, and every such refusal leaves the graph one run staler");
            Assert.IsTrue(console.Urls.Count <= (UnifiClient.RetryBudget + 1) * VerifierRuns,
                "the retries are BOUNDED by the run's budget, so a rate limiting console costs a named " +
                "failure rather than a run that never returns and a run gate no other run for this identity " +
                "can enter");
            AssertWithdrewNothing(report, "the refused run withdraws nothing");
        }

        [TestMethod]
        public async Task ALongRetryAfterFailsTheRunNamingWhatTheConsoleAskedFor()
        {
            var provider = new UnifiNetworkProvider();
            var console = new SourceDouble(request => RateLimited("600"));

            var report = await VerifyUnifiAsync(provider, console);

            var refusal = Refusal(report);
            StringAssert.Contains(refusal, "600",
                "the refusal quotes what the console ASKED for, which is the only way an operator learns the " +
                "console is rate limiting harder than one snapshot can be read through");
            StringAssert.Contains(refusal,
                UnifiClient.LongestRetryAfter.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture),
                "and the limit it exceeded, because a caller is waiting on this job and a snapshot either " +
                "describes the whole console or must not claim to");
            AssertWithdrewNothing(report, "the refused run withdraws nothing");
        }

        [TestMethod]
        public async Task A429WithNoRetryAfterAtAllFailsTheRun()
        {
            var provider = new UnifiNetworkProvider();
            var console = new SourceDouble(request => RateLimited(null));

            var report = await VerifyUnifiAsync(provider, console);

            StringAssert.Contains(Refusal(report), "no Retry-After",
                "rate limiting is undocumented for this API, so there is no published interval to fall back " +
                "on: guessing one would make the snapshot's completeness a guess too, and a short list " +
                "withdraws and deletes whatever it did not reach");
            AssertWithdrewNothing(report, "the refused run withdraws nothing");
        }

        [TestMethod]
        public async Task AConsoleThatDoesNotAnswerAtAllFailsTheRunAndWithdrawsNothing()
        {
            var provider = new UnifiNetworkProvider();
            var console = new SourceDouble(
                request => throw new HttpRequestException("connection refused"));

            var report = await VerifyUnifiAsync(provider, console);

            var refusal = Refusal(report);
            StringAssert.Contains(refusal, "did not answer",
                "\"I could not look\" must never become \"there is nothing there\": a console that never " +
                "answered is a failed run, and reading it as an empty console withdraws every element this " +
                "integration ever claimed");
            StringAssert.Contains(refusal, "connection refused",
                "the transport's own words are what tell an operator whether to look at the address, the " +
                "certificate or the network");
            Assert.IsNull(provider.LastSnapshot, "a source that did not answer describes nothing at all");
            AssertWithdrewNothing(report, "the failed run withdraws nothing");
        }

        [TestMethod]
        public async Task EveryUnifiKindRendersASummaryWithNoWordLeftBehindByAHoleItCannotFill()
        {
            var provider = new UnifiNetworkProvider();

            await VerifyUnifiAsync(provider, new SourceDouble(HappyConsole));

            // The RENDERED text, not the template: one template serves three kinds, and a site fills only
            // the name while a client fills no model and no state. Hole collapse removes the punctuation
            // around a hole an entity cannot fill but it cannot remove a word, so a literal word here is
            // embedded verbatim into the semantic text of every kind that has no such value ("site HQ,
            // state"), which is the shape of the template rather than the description of the thing.
            CollectionAssert.AreEquivalent(
                new[]
                {
                    "site HQ",
                    "device Gateway, USW-24, ONLINE, 192.168.1.1",
                    "device Switch, USW-24, ONLINE, 192.168.1.2",
                    "client Laptop, 192.168.1.50",
                },
                RenderedSummaries(provider),
                "these four strings are what a semantic query is matched against, so a token none of them " +
                "describes is noise in every comparison this integration's embeddings ever take part in");
        }

        [TestMethod]
        public async Task TheUnifiNetworkBlueprintConforms()
        {
            var provider = new UnifiNetworkProvider();

            var report = await VerifyUnifiAsync(provider, new SourceDouble(HappyConsole));

            Assert.IsTrue(report.Conforms,
                "the many-entity blueprint must pass every check: it is the one that proves entity ordering " +
                "is not a provider's problem and that a credential's lifetime is the runtime's: " +
                Failures(report));
        }

        // --- fronius-solar ------------------------------------------------------------------------

        [TestMethod]
        public async Task A200CarryingStatusCode12FailsTheRunSayingDeviceNotAvailable()
        {
            var provider = new FroniusSolarProvider();
            var device = FroniusDevice(resource => resource == FroniusClient.InverterInfoResource
                ? Json(Envelope(12, "{}", "device not answering"))
                : Json(VersionJson()));

            var report = await VerifyFroniusAsync(provider, device);

            var refusal = Refusal(report);
            StringAssert.Contains(refusal, "DeviceNotAvailable",
                "failure arrives with HTTP 200 on this API, and the transcribed table is what turns the " +
                "device's number into a word a reader can act on rather than the bare '12'");
            StringAssert.Contains(refusal, "empty installation",
                "a provider checking only the HTTP status reads this as an empty installation, and an empty " +
                "COMPLETE snapshot withdraws every inverter this identity claimed and deletes it");
            AssertWithdrewNothing(report, "the failed run withdraws nothing");
        }

        [TestMethod]
        public async Task AStatusCodeArrivingAsAStringLandsAsTheDevicesOwnWordWhileANumberIsTranslated()
        {
            var provider = new FroniusSolarProvider();
            var device = FroniusDevice(Fronius(
                Inverters(
                    Inverter("1", "\"UniqueID\":\"1234567\",\"StatusCode\":\"Running\""),
                    Inverter("2", "\"UniqueID\":\"7654321\",\"StatusCode\":8")),
                Logger("240.107620")));

            var report = await VerifyFroniusAsync(provider, device);

            var snapshot = SnapshotOf(provider);
            Assert.AreEqual("Running", Property(ByClaim(snapshot, "fronius-unique-id", "1234567"), "fronius.status"),
                "the vendor declares this an integer and a GEN24 answers the string 'Running': a typed read " +
                "throws there and loses the whole run, which withdraws nothing but leaves the graph stale " +
                "forever on that platform: " + Failures(report));
            Assert.AreEqual("Standby", Property(ByClaim(snapshot, "fronius-unique-id", "7654321"), "fronius.status"),
                "a numeric state is translated through the document's own table, or the graph carries a " +
                "number nobody reading it can interpret");
        }

        [TestMethod]
        public async Task ACustomNameArrivingAsHtmlEntitiesLandsDecoded()
        {
            var provider = new FroniusSolarProvider();
            var device = FroniusDevice(Fronius(
                Inverters(Inverter("1", "\"UniqueID\":\"1234567\",\"CustomName\":\"&#80;&#114;&#105;\"")),
                Logger("240.107620")));

            await VerifyFroniusAsync(provider, device);

            var snapshot = SnapshotOf(provider);
            Assert.AreEqual("Pri", Property(ByClaim(snapshot, "fronius-unique-id", "1234567"), "fronius.customName"),
                "a Datamanager and a Symo Hybrid send the name as HTML entities, and the undecoded run is " +
                "what would otherwise land in the graph and be read by every person and every query after it");
        }

        [TestMethod]
        public async Task APlainCustomNameIsUnchangedByTheSameDecoding()
        {
            var provider = new FroniusSolarProvider();
            var device = FroniusDevice(Fronius(
                Inverters(Inverter("1", "\"UniqueID\":\"1234567\",\"CustomName\":\"Carport & roof\"")),
                Logger("240.107620")));

            await VerifyFroniusAsync(provider, device);

            var snapshot = SnapshotOf(provider);
            Assert.AreEqual("Carport & roof",
                Property(ByClaim(snapshot, "fronius-unique-id", "1234567"), "fronius.customName"),
                "decoding is idempotent on plain text, which is what makes ONE code path correct for both " +
                "platforms: a platform switch instead would leave the GEN24 branch untested and mangled");
        }

        [TestMethod]
        public async Task ALoggerUniqueIdContainingADotClaimsUnderTheLoggerIdType()
        {
            var provider = new FroniusSolarProvider();
            var device = FroniusDevice(Fronius(HappyInverters, Logger("240.107620")));

            var report = await VerifyFroniusAsync(provider, device);

            var snapshot = SnapshotOf(provider);
            var datamanager = ByClaim(snapshot, "fronius-logger-id", "240.107620");
            Assert.IsNotNull(datamanager,
                "the vendor's own example of a logger id is 240.107620 and the inverter type's accept pattern " +
                "rejects the dot, so claiming it as fronius-unique-id leaves every logging device with no " +
                "identity and creates another copy of it on every run: " + Failures(report));
            Assert.IsNull(Claim(datamanager, "fronius-unique-id"),
                "the two id spaces are not documented as disjoint either, so one type for both would let a " +
                "logger and an inverter reporting the same value resolve to one element");
        }

        [TestMethod]
        public async Task GetLoggerInfoFailingTheDocumentedWayIsToleratedAndReported()
        {
            var provider = new FroniusSolarProvider();
            var device = FroniusDevice(Fronius(HappyInverters, logger: null));

            var report = await VerifyFroniusAsync(provider, device);

            var snapshot = SnapshotOf(provider);
            Assert.AreEqual(1, CountByKind(snapshot, "inverter"),
                "GetLoggerInfo fails BY DESIGN on a GEN24, Tauro and Verto, where the inverter itself serves " +
                "the API: failing the run there means this integration can never read those platforms at " +
                "all: " + Failures(report));
            Assert.AreEqual(1, Diagnostics(snapshot, DiagnosticCodes.LoggerInfoUnavailable),
                "it is the ONE tolerated call, and saying so on the report is what stops a reader concluding " +
                "the logging device was deleted");
            Assert.AreEqual(0, CountByKind(snapshot, "datamanager"),
                "no logging device may be invented for a platform that has none, because a complete snapshot " +
                "would then withdraw it again on the next run against a device that does have one");
        }

        [TestMethod]
        public async Task AFailureOnAnyCallOtherThanGetLoggerInfoFailsTheRun()
        {
            var provider = new FroniusSolarProvider();
            var device = FroniusDevice(resource => resource == FroniusClient.InverterInfoResource
                ? Refused(HttpStatusCode.InternalServerError, "inverter bus is down")
                : Json(VersionJson()));

            var report = await VerifyFroniusAsync(provider, device);

            StringAssert.Contains(Refusal(report), "500",
                "only GetLoggerInfo is tolerated: treating a failed inverter list as an empty installation " +
                "withdraws every inverter this identity claimed and deletes the ones nothing else claims");
            Assert.IsNull(provider.LastSnapshot, "a failed read describes nothing at all");
            AssertWithdrewNothing(report, "the failed run withdraws nothing");
        }

        [TestMethod]
        public async Task A404OnTheVersionProbeNamesTheSwitchedOffSolarApiAndWhereToTurnItOn()
        {
            var provider = new FroniusSolarProvider();
            var device = FroniusDevice(resource =>
                Refused(HttpStatusCode.NotFound, "Solar API disabled by customer config"));

            var report = await VerifyFroniusAsync(provider, device);

            var refusal = Refusal(report);
            StringAssert.Contains(refusal, "1.14.1",
                "a device delivered or factory reset at bundle 1.14.1 or higher has the Solar API OFF by " +
                "default, and a reader who is not told that has no way to guess why a working inverter " +
                "answers nothing");
            StringAssert.Contains(refusal, "Communication",
                "the refusal names where the switch is, in the inverter's own web interface, or the operator " +
                "is left with a run that fails forever and a graph that never updates");
            StringAssert.Contains(refusal, "Solar API disabled by customer config",
                "and it quotes what the device itself said, which is the sentence that identifies the cause");
        }

        [TestMethod]
        public async Task TheAddressClaimLandsOnTheLoggingDeviceWhenOneAnswers()
        {
            var provider = new FroniusSolarProvider();
            var device = FroniusDevice(Fronius(HappyInverters, Logger("240.107620")));

            var report = await VerifyFroniusAsync(provider, device);

            var snapshot = SnapshotOf(provider);
            Assert.AreEqual("192.168.1.50", Claim(ByClaim(snapshot, "fronius-logger-id", "240.107620"), "ipv4"),
                "a datamanager card fronts several inverters at ONE address, so the address belongs to the " +
                "device that answers at it: " + Failures(report));
            Assert.IsNull(Claim(ByClaim(snapshot, "fronius-unique-id", "1234567"), "ipv4"),
                "giving each inverter that address advertises one overlap per inverter against the same " +
                "switch port, all but one of them wrong, and a wrong overlap is a wrong answer downstream");
        }

        [TestMethod]
        public async Task TheAddressClaimLandsOnTheSingleInverterWhenNoLoggingDeviceAnswers()
        {
            var provider = new FroniusSolarProvider();
            var device = FroniusDevice(Fronius(HappyInverters, logger: null));

            var report = await VerifyFroniusAsync(provider, device);

            var snapshot = SnapshotOf(provider);
            Assert.AreEqual("192.168.1.50", Claim(ByClaim(snapshot, "fronius-unique-id", "1234567"), "ipv4"),
                "on a GEN24 the inverter itself serves the API, so it is the device holding the address: " +
                "dropping the claim there loses this provider's ONLY overlap with another view of the same " +
                "box, since the Solar API exposes no MAC and no manufacturer serial anywhere: " +
                Failures(report));
        }

        [TestMethod]
        public async Task NoDeviceHoldsTheAddressWhenSeveralInvertersShareItAndNothingLogsThem()
        {
            var provider = new FroniusSolarProvider();
            var device = FroniusDevice(Fronius(
                Inverters(
                    Inverter("1", "\"UniqueID\":\"1234567\""),
                    Inverter("2", "\"UniqueID\":\"7654321\"")),
                logger: null));

            var report = await VerifyFroniusAsync(provider, device);

            var snapshot = SnapshotOf(provider);
            foreach (var entity in snapshot.Entities)
            {
                Assert.IsNull(Claim(entity, "ipv4"),
                    "with more than one inverter and no logging device there is no honest holder of the " +
                    "address, and asserting it on each would advertise one overlap per inverter against the " +
                    "same switch port, all but one of them wrong: " + Failures(report));
            }

            Assert.AreEqual(2, CountByKind(snapshot, "inverter"),
                "the inverters themselves must still be described: the address is the only thing in doubt");
        }

        [TestMethod]
        public async Task AHostNameRatherThanAnIpv4LiteralAssertsNoAddressClaimAndReportsWhy()
        {
            var provider = new FroniusSolarProvider();
            var device = FroniusDevice(Fronius(HappyInverters, logger: null));

            var report = await VerifyFroniusAsync(provider, device, "http://solar.local");

            var snapshot = SnapshotOf(provider);
            Assert.IsNull(Claim(ByClaim(snapshot, "fronius-unique-id", "1234567"), "ipv4"),
                "a host NAME under the ipv4 type is a value that never canonicalises to an address, so it " +
                "would sit in the claim space matching nothing: " + Failures(report));
            Assert.AreEqual(1, Diagnostics(snapshot, DiagnosticCodes.AddressIsNotAnIpv4Literal),
                "that claim is this integration's only overlap with another view of the same box, so its " +
                "absence is REPORTED rather than left invisible to whoever wonders why nothing overlaps");
        }

        [TestMethod]
        public async Task AnEmptyInverterListFailsTheRun()
        {
            var provider = new FroniusSolarProvider();
            var device = FroniusDevice(Fronius(Inverters(), Logger("240.107620")));

            var report = await VerifyFroniusAsync(provider, device);

            StringAssert.Contains(Refusal(report), "named no inverter",
                "the device reports every inverter it has seen in the LAST 24 HOURS, so an empty list is not " +
                "an empty installation: reading it as one declares a complete snapshot with nothing in it, " +
                "which withdraws and deletes every inverter this identity claimed");
            AssertWithdrewNothing(report, "the refused run withdraws nothing");
        }

        [TestMethod]
        public async Task AnInverterWithNoUniqueIdIsSkippedAndCountedWhileTheRestLand()
        {
            var provider = new FroniusSolarProvider();
            var device = FroniusDevice(Fronius(
                Inverters(
                    Inverter("1", "\"UniqueID\":\"1234567\""),
                    Inverter("2", "\"CustomName\":\"No id at all\"")),
                Logger("240.107620")));

            var report = await VerifyFroniusAsync(provider, device);

            var snapshot = SnapshotOf(provider);
            Assert.AreEqual(1, CountByKind(snapshot, "inverter"),
                "without a UniqueID nothing could ever resolve to it, so emitting it creates another copy on " +
                "every run and withdraws the previous one: " + Failures(report));
            Assert.AreEqual(1, Diagnostics(snapshot, DiagnosticCodes.InverterWithoutUniqueId),
                "it is counted and reported, or an inverter missing from the graph looks like a lost device");
        }

        [TestMethod]
        public async Task AUniqueIdArrivingAsABareNumberStillIdentifiesTheInverter()
        {
            var provider = new FroniusSolarProvider();
            var device = FroniusDevice(Fronius(
                Inverters(Inverter("1", "\"UniqueID\":476")), Logger("240.107620")));

            var report = await VerifyFroniusAsync(provider, device);

            var snapshot = SnapshotOf(provider);
            Assert.IsNotNull(ByClaim(snapshot, "fronius-unique-id", "476"),
                "the document declares this field a string and independent captures show values as short as " +
                "476: a typed read loses the run over the ONE field the identity depends on, and a dropped " +
                "claim creates another copy of the inverter on every run: " + Failures(report));
        }

        [TestMethod]
        public async Task AnErrorCodeOfMinusOneIsAbsenceWhileARealOneIsRecorded()
        {
            var provider = new FroniusSolarProvider();
            var device = FroniusDevice(Fronius(
                Inverters(
                    Inverter("1", "\"UniqueID\":\"1234567\",\"ErrorCode\":-1"),
                    Inverter("2", "\"UniqueID\":\"7654321\",\"ErrorCode\":102")),
                Logger("240.107620")));

            var report = await VerifyFroniusAsync(provider, device);

            var snapshot = SnapshotOf(provider);
            Assert.IsNull(Property(ByClaim(snapshot, "fronius-unique-id", "1234567"), "fronius.errorCode"),
                "minus one is ABSENCE per the document, and recording it makes every healthy inverter carry " +
                "a number somebody will read as a fault: " + Failures(report));
            Assert.AreEqual((Object)102,
                Property(ByClaim(snapshot, "fronius-unique-id", "7654321"), "fronius.errorCode"),
                "a real error number must still land, or a faulty inverter looks healthy in the graph");
        }

        [TestMethod]
        public async Task ShowIsRecordedRatherThanObeyed()
        {
            var provider = new FroniusSolarProvider();
            var device = FroniusDevice(Fronius(
                Inverters(Inverter("1", "\"UniqueID\":\"1234567\",\"Show\":0")), Logger("240.107620")));

            var report = await VerifyFroniusAsync(provider, device);

            var snapshot = SnapshotOf(provider);
            var inverter = ByClaim(snapshot, "fronius-unique-id", "1234567");
            Assert.IsNotNull(inverter,
                "'do not display this in visualizations' is a DASHBOARD preference: obeying it would withdraw " +
                "the inverter from the graph the moment somebody set it, and delete it on its last claim: " +
                Failures(report));
            Assert.AreEqual((Object)false, Property(inverter, "fronius.show"),
                "the flag is recorded, so whoever wants to honour it downstream still can");
        }

        [TestMethod]
        public async Task TheDeviceTypeIsRecordedAsANumberRatherThanAModelName()
        {
            var provider = new FroniusSolarProvider();
            var device = FroniusDevice(Fronius(
                Inverters(Inverter("1", "\"UniqueID\":\"1234567\",\"DT\":192")), Logger("240.107620")));

            await VerifyFroniusAsync(provider, device);

            var snapshot = SnapshotOf(provider);
            Assert.AreEqual((Object)192,
                Property(ByClaim(snapshot, "fronius-unique-id", "1234567"), "fronius.deviceType"),
                "the document's type table has more than 250 entries, exists only in a PDF, and is wrong on " +
                "the newest platforms, which always report type 1: a mapped name would put a value in the " +
                "graph the vendor never said, and correcting the table later rewrites every element");
        }

        [TestMethod]
        public async Task TheResourceRootIsAskedRatherThanConfigured()
        {
            var provider = new FroniusSolarProvider();
            var device = FroniusDevice(Fronius(HappyInverters, Logger("240.107620"), "/solar_api/v0/"));

            var report = await VerifyFroniusAsync(provider, device);

            Assert.IsTrue(device.Count("/solar_api/v0/" + FroniusClient.InverterInfoResource) > 0,
                "v0 versus v1 is a property of the DEVICE, so the root every other request hangs off is asked " +
                "rather than configured: a hard-coded root answers 404 on the other generation, and a run " +
                "that cannot look must not describe an empty installation: " + Failures(report));
            Assert.AreEqual(0, device.Count("/solar_api/v1/"),
                "and nothing may be requested under a root the device did not name, or the address the run " +
                "reads from depends on this code rather than on the device in front of it");
            Assert.IsNotNull(provider.LastSnapshot,
                "and the run must actually complete against the root the device named");
        }

        [TestMethod]
        public async Task NoRealtimeRequestIsIssuedAnywhereInARun()
        {
            var provider = new FroniusSolarProvider();
            var device = FroniusDevice(Fronius(HappyInverters, Logger("240.107620")));

            await VerifyFroniusAsync(provider, device);

            Assert.IsTrue(device.Urls.Count > 0, "a run that issued no request would make this vacuous");
            foreach (var url in device.Urls)
            {
                Assert.IsFalse(url.Contains("GetInverterRealtimeData", StringComparison.Ordinal),
                    "power, current, voltage and energy counters change between any two runs, so landing them " +
                    "would make every run a write and make the zero-mutation invariant unobservable for the " +
                    "one provider whose source is never unchanged: " + url);
                Assert.IsFalse(url.Contains("GetPowerFlowRealtimeData", StringComparison.Ordinal),
                    "the same holds for the power flow resource, and reading a resource whose values are not " +
                    "recorded would still cost the device a request on every run: " + url);
            }
        }

        [TestMethod]
        public async Task ADeviceThatAnswersTooLateFailsTheRunAsATimeoutRatherThanACancellation()
        {
            var provider = new FroniusSolarProvider();
            var device = FroniusDevice(
                resource => throw new TaskCanceledException("the client's own timeout elapsed"));

            var report = await VerifyFroniusAsync(provider, device);

            StringAssert.Contains(Refusal(report), "in time",
                "a request that timed out is told apart from a run somebody cancelled by the TOKEN and not " +
                "by the exception type, which is the same for both: reported as a cancellation the operator " +
                "is sent to look for whoever cancelled, and nothing names the device that went quiet");
            Assert.IsNull(provider.LastSnapshot, "a device that answered nothing describes nothing at all");
            AssertWithdrewNothing(report, "the failed run withdraws nothing");
        }

        [TestMethod]
        public async Task TheInverterAndTheDatamanagerRenderSummariesWithNoWordLeftBehindByAnUnfilledHole()
        {
            var provider = new FroniusSolarProvider();
            var device = FroniusDevice(Fronius(HappyInverters, Logger("240.107620")));

            await VerifyFroniusAsync(provider, device);

            // The logging device carries neither a custom name nor a status: it is a card that fronts the
            // API, and both holes of the one template belong to an inverter. A literal word beside either
            // would be all that is left of its summary.
            CollectionAssert.AreEquivalent(
                new[] { "inverter Carport, Running", "datamanager" },
                RenderedSummaries(provider),
                "the status word is the only coarse state this provider embeds at all, so a summary reading " +
                "'datamanager, status' says nothing true about the device and drags the template's own " +
                "vocabulary into every semantic comparison");
        }

        [TestMethod]
        public async Task TheFroniusSolarBlueprintConformsWithNoCredentialSetting()
        {
            var provider = new FroniusSolarProvider();
            foreach (var setting in provider.Descriptor.Settings)
            {
                Assert.AreNotEqual(SettingKind.Credential, setting.Kind,
                    "this blueprint exists to prove nothing in the contract FORCES a credential: a credential " +
                    "setting here would make an unauthenticated local API unusable without inventing a secret");
            }

            var report = await VerifyFroniusAsync(provider, FroniusDevice(Fronius(HappyInverters,
                Logger("240.107620"))));

            Assert.IsTrue(report.Conforms,
                "the no-strong-overlap blueprint must pass every check with no credential offered at all: " +
                Failures(report));
        }

        // --- fixtures and helpers -----------------------------------------------------------------

        private const String HappyCsv =
            "mac,name,note,hostname\n" +
            "44:D2:44:AA:BB:CC,Reception printer,Ground floor by the lift,reception-printer\n" +
            "AA-BB-CC-DD-EE-FF,\"Meeting room \"\"A\"\" TV\",,meeting-tv\n";

        private static readonly String HappyInverters = Inverters(Inverter("1",
            "\"UniqueID\":\"1234567\",\"CustomName\":\"Carport\",\"DT\":192,\"ErrorCode\":-1," +
            "\"PVPower\":5000,\"Show\":1,\"StatusCode\":7"));

        // --- autosar-arxml: through the verifier, which runs the real stack -------------------------

        private const String ArxmlFileName = "network.arxml";

        /// <summary>
        ///   A minimal invented FlexRay network: one bus, one sending ECU, one frame, one PDU and one
        ///   signal that carries both descriptions and a unit two hops away. Small on purpose, because
        ///   the reader's own suite owns the parsing rules; what this fixture has to support is a
        ///   conforming RUN and a summary with every hole filled.
        /// </summary>
        private const String HappyArxml = """
            <AUTOSAR xmlns="http://autosar.org/schema/r4.0">
              <AR-PACKAGES>
                <AR-PACKAGE>
                  <SHORT-NAME>Units</SHORT-NAME>
                  <ELEMENTS>
                    <UNIT><SHORT-NAME>UNIT_KM</SHORT-NAME><DISPLAY-NAME>km</DISPLAY-NAME></UNIT>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>CompuMethods</SHORT-NAME>
                  <ELEMENTS>
                    <COMPU-METHOD>
                      <SHORT-NAME>CM_TotalDistance</SHORT-NAME>
                      <CATEGORY>LINEAR</CATEGORY>
                      <UNIT-REF DEST="UNIT">/Units/UNIT_KM</UNIT-REF>
                    </COMPU-METHOD>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>SystemSignals</SHORT-NAME>
                  <ELEMENTS>
                    <SYSTEM-SIGNAL>
                      <SHORT-NAME>SYS_OdoTotalDist</SHORT-NAME>
                      <PHYSICAL-PROPS>
                        <SW-DATA-DEF-PROPS-VARIANTS>
                          <SW-DATA-DEF-PROPS-CONDITIONAL>
                            <COMPU-METHOD-REF DEST="COMPU-METHOD">/CompuMethods/CM_TotalDistance</COMPU-METHOD-REF>
                          </SW-DATA-DEF-PROPS-CONDITIONAL>
                        </SW-DATA-DEF-PROPS-VARIANTS>
                      </PHYSICAL-PROPS>
                    </SYSTEM-SIGNAL>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>ISignals</SHORT-NAME>
                  <ELEMENTS>
                    <I-SIGNAL>
                      <SHORT-NAME>SIG_OdoTotalDist</SHORT-NAME>
                      <DESC>
                        <L-2 L="DE">Gesamtstrecke seit Auslieferung</L-2>
                        <L-2 L="EN">Accumulated distance travelled since delivery</L-2>
                      </DESC>
                      <LENGTH>32</LENGTH>
                      <SYSTEM-SIGNAL-REF DEST="SYSTEM-SIGNAL">/SystemSignals/SYS_OdoTotalDist</SYSTEM-SIGNAL-REF>
                    </I-SIGNAL>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Pdus</SHORT-NAME>
                  <ELEMENTS>
                    <I-SIGNAL-I-PDU>
                      <SHORT-NAME>PDU_DistanceReport</SHORT-NAME>
                      <LENGTH>8</LENGTH>
                      <I-SIGNAL-TO-PDU-MAPPINGS>
                        <I-SIGNAL-TO-I-PDU-MAPPING>
                          <SHORT-NAME>MAP_Odo</SHORT-NAME>
                          <I-SIGNAL-REF DEST="I-SIGNAL">/ISignals/SIG_OdoTotalDist</I-SIGNAL-REF>
                        </I-SIGNAL-TO-I-PDU-MAPPING>
                      </I-SIGNAL-TO-PDU-MAPPINGS>
                    </I-SIGNAL-I-PDU>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Frames</SHORT-NAME>
                  <ELEMENTS>
                    <FLEXRAY-FRAME>
                      <SHORT-NAME>FRM_Main</SHORT-NAME>
                      <FRAME-LENGTH>32</FRAME-LENGTH>
                      <PDU-TO-FRAME-MAPPINGS>
                        <PDU-TO-FRAME-MAPPING>
                          <SHORT-NAME>FMAP_Main</SHORT-NAME>
                          <PDU-REF DEST="I-SIGNAL-I-PDU">/Pdus/PDU_DistanceReport</PDU-REF>
                        </PDU-TO-FRAME-MAPPING>
                      </PDU-TO-FRAME-MAPPINGS>
                    </FLEXRAY-FRAME>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>EcuInstances</SHORT-NAME>
                  <ELEMENTS>
                    <ECU-INSTANCE>
                      <SHORT-NAME>ALPHA_CTRL</SHORT-NAME>
                      <CONNECTORS>
                        <FLEXRAY-COMMUNICATION-CONNECTOR>
                          <SHORT-NAME>ALPHA_CONN</SHORT-NAME>
                          <ECU-COMM-PORT-INSTANCES>
                            <FRAME-PORT>
                              <SHORT-NAME>FP_Main_Out</SHORT-NAME>
                              <COMMUNICATION-DIRECTION>OUT</COMMUNICATION-DIRECTION>
                            </FRAME-PORT>
                          </ECU-COMM-PORT-INSTANCES>
                        </FLEXRAY-COMMUNICATION-CONNECTOR>
                      </CONNECTORS>
                    </ECU-INSTANCE>
                  </ELEMENTS>
                </AR-PACKAGE>
                <AR-PACKAGE>
                  <SHORT-NAME>Clusters</SHORT-NAME>
                  <ELEMENTS>
                    <FLEXRAY-CLUSTER>
                      <SHORT-NAME>DEMOBUS</SHORT-NAME>
                      <FLEXRAY-CLUSTER-VARIANTS>
                        <FLEXRAY-CLUSTER-CONDITIONAL>
                          <PHYSICAL-CHANNELS>
                            <FLEXRAY-PHYSICAL-CHANNEL>
                              <SHORT-NAME>DEMOBUS_CH_A</SHORT-NAME>
                              <COMM-CONNECTORS>
                                <COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                                  <COMMUNICATION-CONNECTOR-REF DEST="FLEXRAY-COMMUNICATION-CONNECTOR">/EcuInstances/ALPHA_CTRL/ALPHA_CONN</COMMUNICATION-CONNECTOR-REF>
                                </COMMUNICATION-CONNECTOR-REF-CONDITIONAL>
                              </COMM-CONNECTORS>
                              <FRAME-TRIGGERINGS>
                                <FLEXRAY-FRAME-TRIGGERING>
                                  <SHORT-NAME>FT_Main</SHORT-NAME>
                                  <FRAME-PORT-REFS>
                                    <FRAME-PORT-REF DEST="FRAME-PORT">/EcuInstances/ALPHA_CTRL/ALPHA_CONN/FP_Main_Out</FRAME-PORT-REF>
                                  </FRAME-PORT-REFS>
                                  <FRAME-REF DEST="FLEXRAY-FRAME">/Frames/FRM_Main</FRAME-REF>
                                  <ABSOLUTELY-SCHEDULED-TIMINGS>
                                    <FLEXRAY-ABSOLUTELY-SCHEDULED-TIMING>
                                      <SLOT-ID>3</SLOT-ID>
                                    </FLEXRAY-ABSOLUTELY-SCHEDULED-TIMING>
                                  </ABSOLUTELY-SCHEDULED-TIMINGS>
                                </FLEXRAY-FRAME-TRIGGERING>
                              </FRAME-TRIGGERINGS>
                            </FLEXRAY-PHYSICAL-CHANNEL>
                          </PHYSICAL-CHANNELS>
                        </FLEXRAY-CLUSTER-CONDITIONAL>
                      </FLEXRAY-CLUSTER-VARIANTS>
                    </FLEXRAY-CLUSTER>
                  </ELEMENTS>
                </AR-PACKAGE>
              </AR-PACKAGES>
            </AUTOSAR>
            """;

        [TestMethod]
        public async Task TheShippedArxmlBlueprintConforms()
        {
            var provider = new AutosarArxmlProvider();

            var report = await ConformanceVerifier.VerifyAsync(provider, ArxmlJob(HappyArxml),cancellationToken: CancellationToken.None);

            Assert.IsTrue(report.Conforms,
                "the standards blueprint must pass every check, or the suite that licenses a fifth " +
                "integration without an identity review is not trustworthy: " + Failures(report));
        }

        [TestMethod]
        public async Task TheArxmlRunDescribesTheWholeMatrix_WithItsFlowAndContainment()
        {
            var provider = new AutosarArxmlProvider();

            await ConformanceVerifier.VerifyAsync(provider, ArxmlJob(HappyArxml),
                cancellationToken: CancellationToken.None);

            var snapshot = SnapshotOf(provider);
            Assert.AreEqual(SnapshotCompleteness.Complete, snapshot.Declares,
                "a system extract IS the complete description of its network, and that declaration is the " +
                "whole reason this is an integration rather than a converter: it is what makes the next " +
                "release's run withdraw exactly what the release removed");
            Assert.AreEqual(1, CountByKind(snapshot, "network"));
            Assert.AreEqual(1, CountByKind(snapshot, "ecu"));
            Assert.AreEqual(1, CountByKind(snapshot, "signal"));

            foreach (var entity in snapshot.Entities)
            {
                Assert.AreEqual(1, entity.Claims.Count,
                    "every element is claimed by exactly its AUTOSAR path, which the standard makes both " +
                    "its identity and the way every cross-reference addresses it");
                Assert.AreEqual(AutosarArxmlProvider.PathClaimType, entity.Claims[0].Type);
                Assert.IsNull(entity.Claims[0].DeclaredStrength,
                    "a provider never declares a strength for its own claim type");
            }

            // Every claimed path, so a relation TARGET can be checked to name something the snapshot
            // actually describes. Without this the targets were never asserted at all, and every edge
            // could have pointed at its own source or at a path the file never had.
            var claimed = new HashSet<String>(StringComparer.Ordinal);
            foreach (var entity in snapshot.Entities)
            {
                claimed.Add(entity.Claims[0].Value);
            }

            var edges = new List<String>();
            foreach (var entity in snapshot.Entities)
            {
                var owner = entity.Claims[0].Value;
                foreach (var relation in entity.Relations)
                {
                    Assert.AreEqual(AutosarArxmlProvider.PathClaimType, relation.Target.Type,
                        "a relation addresses its target by claim rather than by element id, so the provider " +
                        "never needs to know whether the target exists yet");
                    Assert.IsTrue(claimed.Contains(relation.Target.Value),
                        "the '" + relation.Type + "' edge from " + owner + " points at '" +
                        relation.Target.Value + "', which no entity in this snapshot claims. The runtime " +
                        "would drop it as an unresolvable target, so the graph would silently lose the " +
                        "topology this provider exists to describe");
                    Assert.AreNotEqual(owner, relation.Target.Value,
                        "no edge here is a self-loop, and one would mean the resolution wired an element " +
                        "to itself");
                    edges.Add(relation.Type + " " + owner + " -> " + relation.Target.Value);
                }
            }

            // The exact topology of the small fixture, so a rewiring is visible rather than merely a
            // change in counts.
            CollectionAssert.Contains(edges, "attachedTo /EcuInstances/ALPHA_CTRL -> /Clusters/DEMOBUS");
            CollectionAssert.Contains(edges, "sends /EcuInstances/ALPHA_CTRL -> /Frames/FRM_Main");
            CollectionAssert.Contains(edges, "contains /Frames/FRM_Main -> /Pdus/PDU_DistanceReport");
            CollectionAssert.Contains(edges, "contains /Pdus/PDU_DistanceReport -> /ISignals/SIG_OdoTotalDist");
            CollectionAssert.Contains(edges, "implements /ISignals/SIG_OdoTotalDist -> /SystemSignals/SYS_OdoTotalDist");
            CollectionAssert.Contains(edges, "scaledBy /SystemSignals/SYS_OdoTotalDist -> /CompuMethods/CM_TotalDistance");
        }

        [TestMethod]
        public async Task TheSummaryTemplateAndTheSignalsProperties_AgreeOnEveryHole()
        {
            var provider = new AutosarArxmlProvider();
            var template = provider.Descriptor.EntitySummaryTemplate;

            // The template is the whole semantic surface (spec section 9), so its holes are asserted
            // rather than assumed: dropping one silently narrows what a query can ever match.
            Assert.AreEqual("{kind} {arxml.name}, {arxml.descEn}, {arxml.descDe}, {arxml.unit}", template,
                "the four holes are the semantic payload: the name an engineer already knows, both " +
                "language descriptions because the prose is bilingual and a query arrives in either, and " +
                "the unit, which is the ONLY thing connecting an odometer whose description says " +
                "'accumulated distance' to somebody searching for kilometers");
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(template, "[A-Za-z]+ *\\{"),
                "no LITERAL WORD may sit next to a hole. Hole collapse removes the punctuation around a " +
                "hole an element cannot fill but it cannot remove a word, so 'unit {arxml.unit}' would " +
                "end every ECU, frame and PDU summary with a dangling 'unit' and embed the shape of the " +
                "template instead of the description of the thing");

            await ConformanceVerifier.VerifyAsync(provider, ArxmlJob(HappyArxml),
                cancellationToken: CancellationToken.None);

            var signal = SnapshotOf(provider).Entities
                .Single(e => e.Kind == "signal");

            foreach (var key in new[] { "arxml.name", "arxml.descEn", "arxml.descDe", "arxml.unit" })
            {
                Assert.IsTrue(signal.Properties.ContainsKey(key),
                    "the template names {" + key + "} and the signal does not carry it, so that hole " +
                    "collapses and the embedded text is narrower than the template promises");
            }

            Assert.AreEqual("km", signal.Properties["arxml.unit"],
                "the unit has to arrive as the display name; 'UNIT_KM' is an identifier and would match " +
                "nothing a person searches for");
        }

        [TestMethod]
        public async Task AnExtractWithNoBus_FailsTheRun_AndWithdrawsNothing()
        {
            var provider = new AutosarArxmlProvider();

            var report = await ConformanceVerifier.VerifyAsync(provider, ArxmlJob("""
                    <AUTOSAR xmlns="http://autosar.org/schema/r4.0">
                      <AR-PACKAGES>
                        <AR-PACKAGE>
                          <SHORT-NAME>ISignals</SHORT-NAME>
                          <ELEMENTS>
                            <I-SIGNAL><SHORT-NAME>SIG_Lonely</SHORT-NAME><LENGTH>8</LENGTH></I-SIGNAL>
                          </ELEMENTS>
                        </AR-PACKAGE>
                      </AR-PACKAGES>
                    </AUTOSAR>
                    """),
                cancellationToken: CancellationToken.None);

            AssertWithdrewNothing(report,
                "a readable extract that carries no bus has not been observed, it has failed to be " +
                "observed: reporting it as an empty COMPLETE snapshot would withdraw and then delete the " +
                "whole network a previous run described");
            Assert.IsTrue(Refusal(report).Contains("FlexRay", StringComparison.OrdinalIgnoreCase),
                "the refusal must say which shape of file it wanted, since the file itself is valid " +
                "AUTOSAR: " + Refusal(report));
        }

        [TestMethod]
        public async Task AnUnreadableExtract_FailsTheRun_AndWithdrawsNothing()
        {
            var provider = new AutosarArxmlProvider();

            var report = await ConformanceVerifier.VerifyAsync(provider, ArxmlJob("this is not xml at all"),
                cancellationToken: CancellationToken.None);

            // The assertion that can actually fail: the provider must have produced NO document. A
            // report-only check cannot distinguish "the run failed as required" from "the run
            // succeeded and described an empty network", which is the exact mistake this test exists
            // to catch, and an empty complete snapshot deletes the whole network.
            Assert.IsNull(provider.LastSnapshot,
                "the provider returned a snapshot for a file that is not XML. If that snapshot declares " +
                "completeness, reconciliation withdraws every element this identity ever claimed and then " +
                "deletes them, which is the one mutation re-running cannot undo");

            AssertWithdrewNothing(report,
                "an unreadable file must fail the run rather than describe an empty network");
        }

        /// <summary>
        ///   A job carrying its extract, which is the ONLY way a file reaches a provider: nothing is
        ///   mounted and nothing is opened by name, so the suite exercises the real path by construction
        ///   rather than by substituting a store for it (the same reason there is no credentials fixture).
        /// </summary>
        private static IntegrationJob ArxmlJob(String content)
        {
            var job = new IntegrationJob
            {
                ProviderId = AutosarArxmlProvider.ProviderId,
                IntegrationInstanceId = Instance,
            };

            job.Files[AutosarArxmlProvider.FileSetting] = JobFileOf(ArxmlFileName, content);
            return job;
        }

        /// <summary>One file on a job: its own name, and its bytes as the wire carries them.</summary>
        private static JobFile JobFileOf(String name, String content)
        {
            return new JobFile
            {
                Name = name,
                ContentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)),
            };
        }

        private static IntegrationJob CsvJob(String content, String delimiter = null, String label = null)
        {
            var job = new IntegrationJob
            {
                ProviderId = CsvDeviceListProvider.ProviderId,
                IntegrationInstanceId = Instance,
            };

            job.Files[CsvDeviceListProvider.FileSetting] = JobFileOf(CsvFileName, content);
            if (delimiter != null)
            {
                job.Settings[CsvDeviceListProvider.DelimiterSetting] = delimiter;
            }

            if (label != null)
            {
                job.Settings[CsvDeviceListProvider.LabelSetting] = label;
            }

            return job;
        }

        private static IntegrationJob UnifiJob(IIntegrationProvider provider, String baseUrl = ConsoleBaseUrl)
        {
            var job = new IntegrationJob
            {
                ProviderId = provider.Descriptor.Id,
                IntegrationInstanceId = Instance,
            };

            job.Settings[UnifiNetworkProvider.BaseUrlSetting] = baseUrl;
            job.CredentialValues[UnifiNetworkProvider.ApiKeySetting] = KeyValue;
            return job;
        }

        private static Task<ConformanceReport> VerifyUnifiAsync(UnifiNetworkProvider provider,
            SourceDouble console)
        {
            return ConformanceVerifier.VerifyAsync(provider, UnifiJob(provider), sourceDouble: console,
                cancellationToken: CancellationToken.None);
        }

        private static Task<ConformanceReport> VerifyFroniusAsync(FroniusSolarProvider provider,
            SourceDouble device, String baseUrl = DeviceAddress)
        {
            var job = new IntegrationJob
            {
                ProviderId = provider.Descriptor.Id,
                IntegrationInstanceId = Instance,
            };

            job.Settings["baseUrl"] = baseUrl;

            return ConformanceVerifier.VerifyAsync(provider, job, sourceDouble: device,
                cancellationToken: CancellationToken.None);
        }

        /// <summary>
        ///   The sentence a run that produced nothing failed with. The envelope check carries it, because a
        ///   run with no snapshot to judge records its outcome there.
        /// </summary>
        private static String Refusal(ConformanceReport report)
        {
            return report.DetailOf(ConformanceCheck.SnapshotValid);
        }

        private static void AssertWithdrewNothing(ConformanceReport report, String consequence)
        {
            Assert.IsFalse(report.Failed.Contains(ConformanceCheck.UnreadableSourceFails),
                consequence + " - the suite saw otherwise: " +
                report.DetailOf(ConformanceCheck.UnreadableSourceFails));
        }

        private static String Failures(ConformanceReport report)
        {
            var parts = new List<String>();
            foreach (var check in report.Failed)
            {
                parts.Add(check + " (" + report.DetailOf(check) + ")");
            }

            return parts.Count == 0 ? "every conformance check passed" : String.Join(" | ", parts);
        }

        private static SnapshotDocument SnapshotOf(IObservableProvider provider)
        {
            var snapshot = provider.LastSnapshot;
            Assert.IsNotNull(snapshot,
                "the run described nothing at all, so it failed: the graph then keeps whatever it had, and " +
                "everything this test is about is unobservable");
            return snapshot;
        }

        /// <summary>
        ///   Every entity's summary as the embedding write sees it: the provider's OWN template, rendered
        ///   through the real validator, because a hole is filled from a property that has already been
        ///   rendered for the wire.
        /// </summary>
        private static List<String> RenderedSummaries<TProvider>(TProvider provider)
            where TProvider : IIntegrationProvider, IObservableProvider
        {
            var validated = new SnapshotValidator(IdentifierVocabulary.Shipped)
                .Validate(SnapshotOf(provider), provider.Descriptor);
            Assert.IsTrue(validated.EnvelopeAccepted,
                "the run's own document did not survive validation, so nothing below is a statement about a " +
                "summary");

            var summaries = new List<String>();
            foreach (var entity in validated.Entities)
            {
                var text = EntitySummaryTemplate.Render(provider.Descriptor.EntitySummaryTemplate, entity);
                Assert.IsNotNull(text,
                    "every hole of the '" + entity.Kind + "' summary collapsed, so that entity would be " +
                    "embedded as nothing at all");
                summaries.Add(text);
            }

            return summaries;
        }

        private static Int32 CountByKind(SnapshotDocument snapshot, String kind)
        {
            var count = 0;
            foreach (var entity in snapshot.Entities)
            {
                if (String.Equals(entity.Kind, kind, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        private static Int32 CountByClaim(SnapshotDocument snapshot, String type, String value)
        {
            var count = 0;
            foreach (var entity in snapshot.Entities)
            {
                if (String.Equals(Claim(entity, type), value, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        private static EntityDto ByClaim(SnapshotDocument snapshot, String type, String value)
        {
            foreach (var entity in snapshot.Entities)
            {
                if (String.Equals(Claim(entity, type), value, StringComparison.Ordinal))
                {
                    return entity;
                }
            }

            return null;
        }

        private static String Claim(EntityDto entity, String type)
        {
            if (entity == null)
            {
                return null;
            }

            foreach (var claim in entity.Claims)
            {
                if (String.Equals(claim.Type, type, StringComparison.Ordinal))
                {
                    return claim.Value;
                }
            }

            return null;
        }

        private static Object Property(EntityDto entity, String key)
        {
            if (entity == null)
            {
                return null;
            }

            return entity.Properties.TryGetValue(key, out var value) ? value : null;
        }

        private static Int32 Diagnostics(SnapshotDocument snapshot, String code)
        {
            var count = 0;
            foreach (var diagnostic in snapshot.Diagnostics)
            {
                if (String.Equals(diagnostic.Code, code, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        private static DiagnosticDto FirstDiagnostic(SnapshotDocument snapshot, String code)
        {
            foreach (var diagnostic in snapshot.Diagnostics)
            {
                if (String.Equals(diagnostic.Code, code, StringComparison.Ordinal))
                {
                    return diagnostic;
                }
            }

            Assert.Fail("the snapshot carries no '" + code + "' diagnostic, so what the run could not do is " +
                        "invisible to whoever reads the report");
            return null;
        }

        // --- the UniFi console double -------------------------------------------------------------

        private static String SiteItem
        {
            get { return "{\"id\":\"" + SiteId + "\",\"name\":\"HQ\",\"internalReference\":\"default\"}"; }
        }

        private static String GatewayItem
        {
            get { return DeviceItem(GatewayId, "Gateway", "aa:bb:cc:dd:ee:01", "192.168.1.1"); }
        }

        private static String SwitchItem
        {
            get { return DeviceItem(SwitchId, "Switch", "aa:bb:cc:dd:ee:02", "192.168.1.2"); }
        }

        private static String DeviceItem(String id, String name, String mac, String address)
        {
            return "{\"id\":\"" + id + "\",\"name\":\"" + name + "\",\"model\":\"USW-24\"," +
                   "\"macAddress\":\"" + mac + "\",\"ipAddress\":\"" + address + "\",\"state\":\"ONLINE\"," +
                   "\"firmwareVersion\":\"7.0.23\",\"features\":{\"switching\":{}}," +
                   "\"interfaces\":{\"ports\":[{\"idx\":1}],\"radios\":[]}}";
        }

        private static String ClientItem(String id, String name, String type, String address,
            String mac = null, String uplink = null)
        {
            var text = new StringBuilder("{");
            if (id != null)
            {
                text.Append("\"id\":\"").Append(id).Append("\",");
            }

            text.Append("\"name\":\"").Append(name).Append("\",\"type\":\"").Append(type)
                .Append("\",\"ipAddress\":\"").Append(address).Append('"');
            if (mac != null)
            {
                text.Append(",\"macAddress\":\"").Append(mac).Append('"');
            }

            if (uplink != null)
            {
                text.Append(",\"uplinkDeviceId\":\"").Append(uplink).Append('"');
            }

            return text.Append('}').ToString();
        }

        private static String Page(Int32 totalCount, params String[] items)
        {
            return "{\"count\":" + items.Length.ToString(CultureInfo.InvariantCulture) +
                   ",\"offset\":0,\"limit\":200,\"totalCount\":" +
                   totalCount.ToString(CultureInfo.InvariantCulture) +
                   ",\"data\":[" + String.Join(",", items) + "]}";
        }

        /// <summary>A console with one site, two devices and one wired client, all answering usably.</summary>
        private static HttpResponseMessage HappyConsole(HttpRequestMessage request)
        {
            var uri = request.RequestUri;
            if (IsSites(uri))
            {
                return Json(OffsetOf(uri) == 0 ? Page(1, SiteItem) : Page(1));
            }

            if (IsDeviceDetails(uri))
            {
                return Json(uri.AbsolutePath.EndsWith(SwitchId, StringComparison.Ordinal)
                    ? "{\"uplink\":{\"deviceId\":\"" + GatewayId + "\"}}"
                    : "{}");
            }

            if (IsDevices(uri))
            {
                return Json(OffsetOf(uri) == 0 ? Page(2, GatewayItem, SwitchItem) : Page(2));
            }

            if (IsClients(uri))
            {
                return Json(OffsetOf(uri) == 0
                    ? Page(1, ClientItem(ClientId, "Laptop", "WIRED", "192.168.1.50", "aa:bb:cc:dd:ee:11",
                        GatewayId))
                    : Page(1));
            }

            return Refused(HttpStatusCode.NotFound, "no such resource");
        }

        private static Boolean IsSites(Uri uri)
        {
            return uri.AbsolutePath.EndsWith("/v1/sites", StringComparison.Ordinal);
        }

        private static Boolean IsDevices(Uri uri)
        {
            return uri.AbsolutePath.EndsWith("/devices", StringComparison.Ordinal);
        }

        private static Boolean IsDeviceDetails(Uri uri)
        {
            return uri.AbsolutePath.Contains("/devices/", StringComparison.Ordinal);
        }

        private static Boolean IsClients(Uri uri)
        {
            return uri.AbsolutePath.EndsWith("/clients", StringComparison.Ordinal);
        }

        private static Int32 OffsetOf(Uri uri)
        {
            const String Marker = "offset=";
            var query = uri.Query;
            var start = query.IndexOf(Marker, StringComparison.Ordinal);
            if (start < 0)
            {
                return 0;
            }

            start += Marker.Length;
            var end = query.IndexOf('&', start);
            var text = end < 0 ? query.Substring(start) : query.Substring(start, end - start);
            return Int32.Parse(text, CultureInfo.InvariantCulture);
        }

        // --- the Fronius device double ------------------------------------------------------------

        private static SourceDouble FroniusDevice(Func<String, HttpResponseMessage> byResource)
        {
            return new SourceDouble(request =>
            {
                var path = request.RequestUri.AbsolutePath;
                if (path.EndsWith(FroniusClient.ApiVersionResource, StringComparison.Ordinal))
                {
                    return byResource(FroniusClient.ApiVersionResource);
                }

                if (path.EndsWith(FroniusClient.InverterInfoResource, StringComparison.Ordinal))
                {
                    return byResource(FroniusClient.InverterInfoResource);
                }

                if (path.EndsWith(FroniusClient.LoggerInfoResource, StringComparison.Ordinal))
                {
                    return byResource(FroniusClient.LoggerInfoResource);
                }

                return Refused(HttpStatusCode.NotFound, "no such resource");
            });
        }

        /// <summary>A device answering the three resources, with a null logger reply standing for the
        /// documented GetLoggerInfo failure of a GEN24, Tauro or Verto.</summary>
        private static Func<String, HttpResponseMessage> Fronius(String inverters, String logger,
            String resourceRoot = "/solar_api/v1/")
        {
            return resource =>
            {
                if (resource == FroniusClient.ApiVersionResource)
                {
                    return Json(VersionJson(resourceRoot));
                }

                if (resource == FroniusClient.InverterInfoResource)
                {
                    return Json(inverters);
                }

                return logger == null
                    ? Refused(HttpStatusCode.NotFound, "no logger on this platform")
                    : Json(logger);
            };
        }

        private static String VersionJson(String resourceRoot = "/solar_api/v1/")
        {
            return "{\"APIVersion\":1,\"BaseURL\":\"" + resourceRoot + "\",\"CompatibilityRange\":\"1.6-1\"}";
        }

        private static String Envelope(Int32 code, String body, String reason = "")
        {
            return "{\"Head\":{\"Status\":{\"Code\":" + code.ToString(CultureInfo.InvariantCulture) +
                   ",\"Reason\":\"" + reason + "\",\"UserMessage\":\"\"}},\"Body\":" + body + "}";
        }

        private static String Inverters(params String[] entries)
        {
            return Envelope(0, "{\"Data\":{" + String.Join(",", entries) + "}}");
        }

        private static String Inverter(String deviceId, String fields)
        {
            return "\"" + deviceId + "\":{" + fields + "}";
        }

        private static String Logger(String uniqueId)
        {
            return Envelope(0, "{\"LoggerInfo\":{\"UniqueID\":\"" + uniqueId + "\"," +
                               "\"ProductID\":\"fronius-datamanager\",\"PlatformID\":\"wilma\"," +
                               "\"HWVersion\":\"2.4D\",\"SWVersion\":\"3.16.7-1\"," +
                               "\"TimezoneLocation\":\"Vienna\"}}");
        }

        // --- HTTP plumbing ------------------------------------------------------------------------

        private static HttpResponseMessage Json(String body)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }

        private static HttpResponseMessage Refused(HttpStatusCode status, String body = "")
        {
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/plain"),
            };
        }

        private static HttpResponseMessage RateLimited(String retryAfterSeconds)
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };

            if (retryAfterSeconds != null)
            {
                response.Headers.TryAddWithoutValidation("Retry-After", retryAfterSeconds);
            }

            return response;
        }

        /// <summary>
        ///   The stand-in for a provider's own service, behind the verifier's recording handler and the
        ///   runtime's outbound guard. It records METHOD and URL of everything it was asked, which is what
        ///   makes read-only, "no realtime request" and "the retries are bounded" assertable over a whole
        ///   run rather than merely documented.
        /// </summary>
        private sealed class SourceDouble : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;
            private readonly List<String> _methods = new List<String>();
            private readonly List<String> _urls = new List<String>();
            private readonly List<String> _apiKeys = new List<String>();

            internal SourceDouble(Func<HttpRequestMessage, HttpResponseMessage> answer)
            {
                _answer = answer;
            }

            internal IReadOnlyList<String> Methods
            {
                get { return _methods; }
            }

            internal IReadOnlyList<String> Urls
            {
                get { return _urls; }
            }

            /// <summary>
            ///   The API key header of every request, or the empty string where none was sent. Recorded
            ///   because nothing else in the suite can see whether the credential was SENT: a source double
            ///   answers the same whether the header is there or not, so without this the one line that puts
            ///   the key on the request could be deleted and every test would stay green.
            /// </summary>
            internal IReadOnlyList<String> ApiKeys
            {
                get { return _apiKeys; }
            }

            internal Int32 Count(String fragment)
            {
                var count = 0;
                foreach (var url in _urls)
                {
                    if (url.Contains(fragment, StringComparison.Ordinal))
                    {
                        count++;
                    }
                }

                return count;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                _methods.Add(request.Method.Method);
                _urls.Add(request.RequestUri == null ? String.Empty : request.RequestUri.ToString());
                _apiKeys.Add(request.Headers.TryGetValues(UnifiClient.ApiKeyHeader, out var sent)
                    ? String.Join(",", sent)
                    : String.Empty);
                return Task.FromResult(_answer(request));
            }
        }
    }
}
