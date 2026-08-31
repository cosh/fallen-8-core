// MIT License
//
// IntegrationsMultipartTest.cs
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
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Integrations.Hosting;
using NoSQL.GraphDB.Integrations.Run;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   HOW A JOB ARRIVES (feature integration-file-transport): as JSON with its files base64 in the
    ///   document, or as a multipart form with each file's raw bytes in its own part.
    ///
    ///   <para>The multipart arm exists because the JSON one has a ceiling nothing can configure: a browser
    ///   composing that body holds the bytes, their base64 and the serialised request at once, and a
    ///   JavaScript string caps at 512 MiB, so the encoder died at about 384 MiB of input while the runtime
    ///   was configured to accept more. A vehicle's AUTOSAR extract is several gigabytes.</para>
    ///
    ///   <para>What every test here is really protecting is the SAMENESS. Two transports for one job is two
    ///   chances to drift, and a drift in this particular path is invisible from the graph: a job whose files
    ///   arrived in a different order is a different graph (the AUTOSAR reader gives a re-declared path to
    ///   the first file that declared it), and a job that silently arrived with FEWER files is a complete
    ///   snapshot that withdraws every element only the missing ones described. So the grammar refuses
    ///   rather than interprets, and the refusals are asserted on their message strings to prove the same
    ///   checks are reached down both arms.</para>
    /// </summary>
    [TestClass]
    public class IntegrationsMultipartTest
    {
        private const String Boundary = "----fallen8-test-boundary";
        private const String CsvProviderId = "csv-device-list";
        private const String ArxmlProviderId = "autosar-arxml";
        private const String FileSetting = "file";

        #region one job, two transports, one normalized result

        /// <summary>
        ///   The anchor. Everything else in this file is a detail of the grammar; this is the guarantee the
        ///   grammar exists to keep, and it is asserted on the NORMALIZED job because that is what the run
        ///   actually reads.
        /// </summary>
        [TestMethod]
        public async Task OneJobSubmittedEitherWay_NormalizesToTheSameThing()
        {
            var document = "{\"providerId\":\"" + ArxmlProviderId + "\"," +
                           "\"integrationInstanceId\":\"vehicle-7\"," +
                           "\"namespace\":\"vehicles\"," +
                           "\"settings\":{\"label\":\"fleet\"}," +
                           "\"credentialValues\":{\"token\":\"s3cret\"}," +
                           "\"embedSummaries\":true,\"embeddingName\":\"summaries\"";

            var chassis = Encoding.UTF8.GetBytes("<CHASSIS/>");
            var body = Encoding.UTF8.GetBytes("<BODY/>");

            var json = JsonSerializer.Deserialize<IntegrationJob>(
                document + ",\"files\":{\"" + FileSetting + "\":[" +
                "{\"name\":\"chassis.arxml\",\"contentBase64\":\"" + Convert.ToBase64String(chassis) + "\"}," +
                "{\"name\":\"body.arxml\",\"contentBase64\":\"" + Convert.ToBase64String(body) + "\"}]}}",
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            var read = await Read(Multipart(
                (ValuePart("job"), Encoding.UTF8.GetBytes(document + "}")),
                (FilePart("files[file][0]", "chassis.arxml"), chassis),
                (FilePart("files[file][1]", "body.arxml"), body)));

            Assert.IsNotNull(read.Job, read.Failure);
            Assert.IsTrue(json!.TryNormalize(out var viaJson, out var jsonFailure), jsonFailure);
            Assert.IsTrue(read.Job!.TryNormalize(out var viaForm, out var formFailure), formFailure);

            AssertSameJob(viaJson!, viaForm!);
        }

        /// <summary>
        ///   The one-file spellings agree too, and that is a separate case: the JSON form's single OBJECT and
        ///   the multipart form's un-numbered part both have to produce a group that is NOT a list, because
        ///   whether the caller asked for the multiple shape is what a single-file setting refuses on.
        /// </summary>
        [TestMethod]
        public async Task TheSingleFileSpellingsAgreeAboutNotBeingAList()
        {
            var document = "{\"providerId\":\"" + CsvProviderId + "\"," +
                           "\"integrationInstanceId\":\"office\",\"settings\":{}";
            var content = Encoding.UTF8.GetBytes("mac\n44:D2:44:AA:BB:CC\n");

            var json = JsonSerializer.Deserialize<IntegrationJob>(
                document + ",\"files\":{\"" + FileSetting + "\":{\"name\":\"devices.csv\"," +
                "\"contentBase64\":\"" + Convert.ToBase64String(content) + "\"}}}",
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            var read = await Read(Multipart(
                (ValuePart("job"), Encoding.UTF8.GetBytes(document + "}")),
                (FilePart("files[file]", "devices.csv"), content)));

            Assert.IsNotNull(read.Job, read.Failure);
            Assert.IsTrue(json!.TryNormalize(out var viaJson, out _));
            Assert.IsTrue(read.Job!.TryNormalize(out var viaForm, out var failure), failure);

            AssertSameJob(viaJson!, viaForm!);
            Assert.IsFalse(viaForm!.Files[FileSetting].AsList,
                "an un-numbered part produced the LIST shape, so a setting that takes exactly one file " +
                "would refuse a perfectly ordinary single-file upload");
        }

        /// <summary>
        ///   The other half of the same pair, and the reason the ordinal is explicit rather than implied by
        ///   part order: <c>[0]</c> is a list of one, which is a different statement from one file, and a
        ///   setting the descriptor does not declare <c>multiple</c> refuses it. Repeated same-named parts
        ///   could not express this, which is why the grammar was not simplified to them.
        /// </summary>
        [TestMethod]
        public async Task ANumberedPartIsAListOfOne_EvenWhenThereIsOnlyOne()
        {
            var read = await Read(Multipart(
                (ValuePart("job"), Encoding.UTF8.GetBytes(JobDocument(CsvProviderId, "office"))),
                (FilePart("files[file][0]", "devices.csv"), Encoding.UTF8.GetBytes("mac\n"))));

            Assert.IsNotNull(read.Job, read.Failure);
            Assert.IsTrue(read.Job!.TryNormalize(out var normalized, out var failure), failure);
            Assert.IsTrue(normalized!.Files[FileSetting].AsList,
                "'files[file][0]' did not produce the list shape, so the difference between one file and a " +
                "list of one has been lost and a single-file setting would silently accept both");
        }

        #endregion

        #region the bytes and the name arrive exactly as sent

        /// <summary>
        ///   VERBATIM, including a zero byte and a UTF-16 byte-order mark. This is the whole reason the file
        ///   travels as bytes rather than as text: a transport that decoded it would hand an extract a vendor
        ///   tool wrote as UTF-16 to the provider as mojibake, the run would SUCCEED, and the graph would
        ///   quietly hold rubbish.
        /// </summary>
        [TestMethod]
        public async Task ThePartsBytesArriveVerbatim()
        {
            var content = new List<Byte> { 0xFF, 0xFE };
            content.AddRange(Encoding.Unicode.GetBytes("mac\r\n"));
            content.Add(0x00);
            content.AddRange(new Byte[] { 0x0D, 0x0A, 0x2D, 0x2D });

            var read = await Read(Multipart(
                (ValuePart("job"), Encoding.UTF8.GetBytes(JobDocument(CsvProviderId, "office"))),
                (FilePart("files[file]", "devices.csv"), content.ToArray())));

            Assert.IsNotNull(read.Job, read.Failure);
            Assert.IsTrue(read.Job!.TryNormalize(out var normalized, out var failure), failure);
            CollectionAssert.AreEqual(content.ToArray(), normalized!.Files[FileSetting].First.Content,
                "the bytes changed on the way, so a UTF-16 extract reaches its provider as mojibake");
        }

        /// <summary>
        ///   A file of exactly zero bytes is refused, by the SAME message the JSON arm produces, because the
        ///   danger is identical: read as a complete snapshot describing nothing, it withdraws every element
        ///   the identity ever claimed.
        /// </summary>
        [TestMethod]
        public async Task AnEmptyPartIsRefusedAsAnEmptyFile()
        {
            var read = await Read(Multipart(
                (ValuePart("job"), Encoding.UTF8.GetBytes(JobDocument(CsvProviderId, "office"))),
                (FilePart("files[file]", "devices.csv"), Array.Empty<Byte>())));

            Assert.IsNotNull(read.Job, read.Failure);
            Assert.IsFalse(read.Job!.TryNormalize(out _, out var failure),
                "an empty part was accepted as a file, and an empty source withdraws everything");
            StringAssert.Contains(failure, "is empty", failure);
            StringAssert.Contains(failure, "withdraw", failure);
        }

        /// <summary>
        ///   <c>filename*</c> wins over <c>filename</c> when both are sent, which is how a name with a
        ///   non-ASCII character survives a header at all. Sending both is what browsers do.
        /// </summary>
        [TestMethod]
        public async Task AnEncodedFileNameIsPreferredOverTheAsciiFallback()
        {
            var read = await Read(Multipart(
                (ValuePart("job"), Encoding.UTF8.GetBytes(JobDocument(CsvProviderId, "office"))),
                ("form-data; name=\"files[file]\"; filename=\"gerate.csv\"; filename*=UTF-8''ger%C3%A4te.csv",
                    Encoding.UTF8.GetBytes("mac\n"))));

            Assert.IsNotNull(read.Job, read.Failure);
            Assert.IsTrue(read.Job!.TryNormalize(out var normalized, out var failure), failure);
            Assert.AreEqual("geräte.csv", normalized!.Files[FileSetting].First.Name,
                "the ASCII fallback was kept over the encoded name, so a file is called something other " +
                "than what the person who picked it sees on their own disk");
        }

        /// <summary>A quoted name with a space in it survives, which the header grammar allows.</summary>
        [TestMethod]
        public async Task AQuotedFileNameWithASpaceSurvives()
        {
            var read = await Read(Multipart(
                (ValuePart("job"), Encoding.UTF8.GetBytes(JobDocument(CsvProviderId, "office"))),
                (FilePart("files[file]", "office devices.csv"), Encoding.UTF8.GetBytes("mac\n"))));

            Assert.IsNotNull(read.Job, read.Failure);
            Assert.IsTrue(read.Job!.TryNormalize(out var normalized, out var failure), failure);
            Assert.AreEqual("office devices.csv", normalized!.Files[FileSetting].First.Name);
        }

        /// <summary>
        ///   The file name goes through the SAME shape check as the JSON arm, so both transports refuse the
        ///   same names. It is display text on the job report and in every log line about the run, which is
        ///   the only place an operator goes to read what happened, so an unbounded one is a mess there.
        /// </summary>
        [TestMethod]
        public async Task AFileNameIsHeldToTheSameShapeRuleAsOnTheJsonArm()
        {
            var read = await Read(Multipart(
                (ValuePart("job"), Encoding.UTF8.GetBytes(JobDocument(CsvProviderId, "office"))),
                (FilePart("files[file]", new String('n', 300) + ".csv"), Encoding.UTF8.GetBytes("mac\n"))));

            Assert.IsNotNull(read.Job, read.Failure);
            Assert.IsFalse(read.Job!.TryNormalize(out _, out var failure),
                "a 304-character file name was accepted, so the multipart arm is not going through the same " +
                "name check as the JSON one and the two will drift");
            StringAssert.Contains(failure, "at most 260", failure);
        }

        /// <summary>
        ///   ORDER IS PART OF THE MEANING, so it is kept and pinned: the AUTOSAR reader resolves references
        ///   across the union of its files and gives a re-declared path to the FIRST file that declared it,
        ///   so a reordered set is a different graph with nothing on the report to say so.
        /// </summary>
        [TestMethod]
        public async Task TheOrdinalsDecideTheOrder_NotTheNamesAndNotTheAlphabet()
        {
            var read = await Read(Multipart(
                (ValuePart("job"), Encoding.UTF8.GetBytes(JobDocument(ArxmlProviderId, "vehicle-7"))),
                (FilePart("files[file][0]", "zeta.arxml"), Encoding.UTF8.GetBytes("z")),
                (FilePart("files[file][1]", "alpha.arxml"), Encoding.UTF8.GetBytes("a")),
                (FilePart("files[file][2]", "mid.arxml"), Encoding.UTF8.GetBytes("m"))));

            Assert.IsNotNull(read.Job, read.Failure);
            Assert.IsTrue(read.Job!.TryNormalize(out var normalized, out var failure), failure);
            CollectionAssert.AreEqual(new[] { "zeta.arxml", "alpha.arxml", "mid.arxml" },
                normalized!.Files[FileSetting].Files.Select(f => f.Name).ToArray(),
                "the files were reordered, and for a provider that composes an ordered union that is a " +
                "different graph");
        }

        #endregion

        #region the grammar refuses rather than interprets

        /// <summary>
        ///   Three distinct refusals for three distinct ordering mistakes, and each names what it saw. One
        ///   generic "bad ordinals" message would leave a caller guessing which of the three they made.
        /// </summary>
        [TestMethod]
        public async Task TheThreeOrdinalMistakesGetThreeDifferentMessages()
        {
            var descending = await Read(Multipart(
                (ValuePart("job"), Encoding.UTF8.GetBytes(JobDocument(ArxmlProviderId, "v"))),
                (FilePart("files[file][1]", "b.arxml"), Encoding.UTF8.GetBytes("b")),
                (FilePart("files[file][0]", "a.arxml"), Encoding.UTF8.GetBytes("a"))));
            Assert.IsNull(descending.Job, "file parts numbered 1 then 0 were accepted");
            StringAssert.Contains(descending.Failure, "numbered from 0", descending.Failure);

            var gap = await Read(Multipart(
                (ValuePart("job"), Encoding.UTF8.GetBytes(JobDocument(ArxmlProviderId, "v"))),
                (FilePart("files[file][0]", "a.arxml"), Encoding.UTF8.GetBytes("a")),
                (FilePart("files[file][2]", "c.arxml"), Encoding.UTF8.GetBytes("c"))));
            Assert.IsNull(gap.Job, "a gap in the ordinals was accepted, so a file went silently unread");
            StringAssert.Contains(gap.Failure, "jump from 0 to 2", gap.Failure);
            StringAssert.Contains(gap.Failure, "withdraws", gap.Failure);

            var repeated = await Read(Multipart(
                (ValuePart("job"), Encoding.UTF8.GetBytes(JobDocument(ArxmlProviderId, "v"))),
                (FilePart("files[file][0]", "a.arxml"), Encoding.UTF8.GetBytes("a")),
                (FilePart("files[file][0]", "b.arxml"), Encoding.UTF8.GetBytes("b"))));
            Assert.IsNull(repeated.Job, "the same ordinal twice was accepted");
            StringAssert.Contains(repeated.Failure, "jump from 0 to 0", repeated.Failure);

            Assert.AreNotEqual(descending.Failure, gap.Failure);
            Assert.AreNotEqual(gap.Failure, repeated.Failure);
        }

        /// <summary>A list that does not start at 0 is named as that, not as a gap.</summary>
        [TestMethod]
        public async Task AListThatDoesNotStartAtZeroSaysSo()
        {
            var read = await Read(Multipart(
                (ValuePart("job"), Encoding.UTF8.GetBytes(JobDocument(ArxmlProviderId, "v"))),
                (FilePart("files[file][1]", "b.arxml"), Encoding.UTF8.GetBytes("b"))));

            Assert.IsNull(read.Job, "a list starting at 1 was accepted");
            StringAssert.Contains(read.Failure, "is numbered 1", read.Failure);
        }

        /// <summary>
        ///   The two forms are not mixed for one setting, in either direction. Mixing them is a caller
        ///   asking for one file and a list at the same time, and there is no reading of that a run should
        ///   guess at.
        /// </summary>
        [TestMethod]
        public async Task TheTwoFormsAreNotMixedForOneSetting()
        {
            var singleFirst = await Read(Multipart(
                (ValuePart("job"), Encoding.UTF8.GetBytes(JobDocument(ArxmlProviderId, "v"))),
                (FilePart("files[file]", "a.arxml"), Encoding.UTF8.GetBytes("a")),
                (FilePart("files[file][0]", "b.arxml"), Encoding.UTF8.GetBytes("b"))));
            Assert.IsNull(singleFirst.Job, "the single form followed by a numbered one was accepted");
            StringAssert.Contains(singleFirst.Failure, "both forms", singleFirst.Failure);

            var listFirst = await Read(Multipart(
                (ValuePart("job"), Encoding.UTF8.GetBytes(JobDocument(ArxmlProviderId, "v"))),
                (FilePart("files[file][0]", "a.arxml"), Encoding.UTF8.GetBytes("a")),
                (FilePart("files[file]", "b.arxml"), Encoding.UTF8.GetBytes("b"))));
            Assert.IsNull(listFirst.Job, "a numbered form followed by the single one was accepted");
            StringAssert.Contains(listFirst.Failure, "both forms", listFirst.Failure);
        }

        /// <summary>
        ///   The single form appears at most once. Two parts of the same name is the shape a form library
        ///   produces for a multi-select, and reading it as "the last one wins" would silently drop a file.
        /// </summary>
        [TestMethod]
        public async Task TheSingleFormAppearsAtMostOnce()
        {
            var read = await Read(Multipart(
                (ValuePart("job"), Encoding.UTF8.GetBytes(JobDocument(ArxmlProviderId, "v"))),
                (FilePart("files[file]", "a.arxml"), Encoding.UTF8.GetBytes("a")),
                (FilePart("files[file]", "b.arxml"), Encoding.UTF8.GetBytes("b"))));

            Assert.IsNull(read.Job, "'files[file]' twice was accepted, so one of the two files vanished");
            StringAssert.Contains(read.Failure, "more than once", read.Failure);
            StringAssert.Contains(read.Failure, "files[file][0]", read.Failure);
        }

        /// <summary>
        ///   An unknown part is REFUSED, never ignored, and the message says why that choice was made. This
        ///   is the single most consequential rule in the grammar: a misspelled file part that was ignored
        ///   would submit a snapshot that does not mention whatever that file described, and a complete
        ///   snapshot withdraws what it does not mention.
        /// </summary>
        [TestMethod]
        public async Task AnUnknownPartIsRefusedRatherThanIgnored()
        {
            var read = await Read(Multipart(
                (ValuePart("job"), Encoding.UTF8.GetBytes(JobDocument(CsvProviderId, "office"))),
                (FilePart("file", "devices.csv"), Encoding.UTF8.GetBytes("mac\n"))));

            Assert.IsNull(read.Job, "a part named 'file' rather than 'files[file]' was ignored");
            StringAssert.Contains(read.Failure, "'file'", read.Failure);
            StringAssert.Contains(read.Failure, "withdraws what it does not mention", read.Failure);
        }

        /// <summary>A key that is not a key shape is named as that, not as an unknown part.</summary>
        [TestMethod]
        public async Task ASettingKeyWithABracketInItIsNamedAsABadKey()
        {
            var read = await Read(Multipart(
                (ValuePart("job"), Encoding.UTF8.GetBytes(JobDocument(CsvProviderId, "office"))),
                (FilePart("files[fi[0]le]", "devices.csv"), Encoding.UTF8.GetBytes("mac\n"))));

            Assert.IsNull(read.Job, "a setting key with a bracket in it was accepted");
            StringAssert.Contains(read.Failure, "setting key", read.Failure);
            StringAssert.Contains(read.Failure, "letters, digits, dot, dash and underscore", read.Failure);
        }

        /// <summary>Neither an empty key nor a colon in one, which is a claim-key separator.</summary>
        [TestMethod]
        [DataRow("files[]", DisplayName = "an empty key")]
        [DataRow("files[a:b]", DisplayName = "a colon in the key")]
        [DataRow("files[a b]", DisplayName = "a space in the key")]
        public async Task ASettingKeyOfTheWrongShapeIsRefused(String partName)
        {
            var read = await Read(Multipart(
                (ValuePart("job"), Encoding.UTF8.GetBytes(JobDocument(CsvProviderId, "office"))),
                (FilePart(partName, "devices.csv"), Encoding.UTF8.GetBytes("mac\n"))));

            Assert.IsNull(read.Job, partName + " was accepted as a setting key");
        }

        /// <summary>
        ///   A part after the setting key that is neither nothing nor a decimal index gets its own message,
        ///   because the caller's mistake is in the INDEX rather than in the key they can see is fine.
        /// </summary>
        [TestMethod]
        [DataRow("files[file][x]", DisplayName = "a non-numeric index")]
        [DataRow("files[file][-1]", DisplayName = "a negative index")]
        [DataRow("files[file]extra", DisplayName = "trailing junk")]
        public async Task AnIndexThatIsNotADecimalIsRefused(String partName)
        {
            var read = await Read(Multipart(
                (ValuePart("job"), Encoding.UTF8.GetBytes(JobDocument(ArxmlProviderId, "v"))),
                (FilePart(partName, "a.arxml"), Encoding.UTF8.GetBytes("a"))));

            Assert.IsNull(read.Job, partName + " was accepted");
        }

        /// <summary>
        ///   A file part with no filename is refused: the name is what every message about the run calls the
        ///   file, and the runtime has nothing else to call it because it opens nothing on disk.
        /// </summary>
        [TestMethod]
        public async Task AFilePartWithNoFileNameIsRefused()
        {
            var read = await Read(Multipart(
                (ValuePart("job"), Encoding.UTF8.GetBytes(JobDocument(CsvProviderId, "office"))),
                (ValuePart("files[file]"), Encoding.UTF8.GetBytes("mac\n"))));

            Assert.IsNull(read.Job, "a file part with no filename was accepted");
            StringAssert.Contains(read.Failure, "no filename", read.Failure);
        }

        /// <summary>
        ///   Two keys differing only in case reach the SAME refusal the JSON arm produces, which is the
        ///   point of asserting on the message: it proves the multipart arm feeds the same normalisation
        ///   rather than a parallel set of checks that will drift.
        /// </summary>
        [TestMethod]
        public async Task CaseCollidingKeysReachTheSameRefusalAsOnTheJsonArm()
        {
            var read = await Read(Multipart(
                (ValuePart("job"), Encoding.UTF8.GetBytes(JobDocument(CsvProviderId, "office"))),
                (FilePart("files[file]", "a.csv"), Encoding.UTF8.GetBytes("a")),
                (FilePart("files[FILE]", "b.csv"), Encoding.UTF8.GetBytes("b"))));

            Assert.IsNotNull(read.Job, read.Failure);
            Assert.IsFalse(read.Job!.TryNormalize(out _, out var failure));
            StringAssert.Contains(failure, "differ only in case", failure);
        }

        /// <summary>
        ///   Two files with one name reach the same refusal too. Names are compared case-insensitively
        ///   there because the point is what a READER sees: 'Body.arxml' beside 'body.arxml' reads as one
        ///   file mentioned twice, and the commonest cause is the same file picked twice by mistake.
        /// </summary>
        [TestMethod]
        public async Task DuplicateFileNamesReachTheSameRefusalAsOnTheJsonArm()
        {
            var read = await Read(Multipart(
                (ValuePart("job"), Encoding.UTF8.GetBytes(JobDocument(ArxmlProviderId, "v"))),
                (FilePart("files[file][0]", "body.arxml"), Encoding.UTF8.GetBytes("a")),
                (FilePart("files[file][1]", "Body.arxml"), Encoding.UTF8.GetBytes("b"))));

            Assert.IsNotNull(read.Job, read.Failure);
            Assert.IsFalse(read.Job!.TryNormalize(out _, out var failure));
            StringAssert.Contains(failure, "one name once case is set aside", failure);
        }

        /// <summary>Two settings each with their own files, which the count and total span.</summary>
        [TestMethod]
        public async Task TwoFileSettingsOnOneJobBothArrive()
        {
            var read = await Read(Multipart(
                (ValuePart("job"), Encoding.UTF8.GetBytes(JobDocument(ArxmlProviderId, "v"))),
                (FilePart("files[file][0]", "a.arxml"), Encoding.UTF8.GetBytes("a")),
                (FilePart("files[extra]", "b.arxml"), Encoding.UTF8.GetBytes("b")),
                (FilePart("files[file][1]", "c.arxml"), Encoding.UTF8.GetBytes("c"))));

            Assert.IsNotNull(read.Job, read.Failure);
            Assert.IsTrue(read.Job!.TryNormalize(out var normalized, out var failure), failure);
            Assert.AreEqual(2, normalized!.Files.Count,
                "one of the two file settings was lost, and a run reading fewer files withdraws what only " +
                "the missing ones described");
            Assert.AreEqual(2, normalized.Files["file"].Files.Count,
                "interleaving another setting's part broke the numbered list it sat between");
            Assert.IsFalse(normalized.Files["extra"].AsList);
        }

        #endregion

        #region the job part

        /// <summary>
        ///   The <c>job</c> part comes FIRST, because the files are read as they stream past and the
        ///   document is what says which setting each belongs to. A late one cannot work, so it is refused
        ///   rather than tolerated by buffering.
        /// </summary>
        [TestMethod]
        public async Task TheJobPartHasToBeFirst()
        {
            var read = await Read(Multipart(
                (FilePart("files[file]", "devices.csv"), Encoding.UTF8.GetBytes("mac\n")),
                (ValuePart("job"), Encoding.UTF8.GetBytes(JobDocument(CsvProviderId, "office")))));

            Assert.IsNull(read.Job, "a form whose job document came after its files was accepted");
            StringAssert.Contains(read.Failure, "rather than 'job'", read.Failure);
        }

        /// <summary>A form with no job part at all.</summary>
        [TestMethod]
        public async Task AFormWithNoJobPartIsRefused()
        {
            var read = await Read(Multipart(
                (ValuePart("notTheJob"), Encoding.UTF8.GetBytes("{}"))));

            Assert.IsNull(read.Job);
            StringAssert.Contains(read.Failure, "rather than 'job'", read.Failure);
        }

        /// <summary>An empty form is refused as one carrying no job.</summary>
        [TestMethod]
        public async Task AnEmptyFormIsRefused()
        {
            var read = await Read(Multipart());

            Assert.IsNull(read.Job);
            StringAssert.Contains(read.Failure, "no 'job' part", read.Failure);
        }

        /// <summary>A second job part is a second answer to what to run, so it is refused.</summary>
        [TestMethod]
        public async Task ASecondJobPartIsRefused()
        {
            var read = await Read(Multipart(
                (ValuePart("job"), Encoding.UTF8.GetBytes(JobDocument(CsvProviderId, "office"))),
                (ValuePart("job"), Encoding.UTF8.GetBytes(JobDocument(ArxmlProviderId, "elsewhere")))));

            Assert.IsNull(read.Job, "two job parts were accepted, so which one ran depended on the reader");
            StringAssert.Contains(read.Failure, "has to be the first", read.Failure);
        }

        /// <summary>
        ///   The <c>job</c> part is a VALUE part. A filename on it means it was appended as a Blob, which is
        ///   the exact mistake that would otherwise send the envelope as a file and leave the runtime with
        ///   no document at all.
        /// </summary>
        [TestMethod]
        public async Task AJobPartSentAsAFileIsRefused()
        {
            var read = await Read(Multipart(
                (FilePart("job", "blob"), Encoding.UTF8.GetBytes(JobDocument(CsvProviderId, "office")))));

            Assert.IsNull(read.Job, "the job envelope was accepted as a file part");
            StringAssert.Contains(read.Failure, "declares a filename", read.Failure);
        }

        /// <summary>
        ///   The document may not carry a <c>files</c> map of its own on this transport: the file PARTS are
        ///   the files, and a second list would be a second answer to which files the run reads.
        /// </summary>
        [TestMethod]
        public async Task AJobPartCarryingItsOwnFilesMapIsRefused()
        {
            var document = "{\"providerId\":\"" + CsvProviderId + "\",\"integrationInstanceId\":\"office\"," +
                           "\"settings\":{},\"files\":{\"file\":{\"name\":\"a.csv\"," +
                           "\"contentBase64\":\"bWFjCg==\"}}}";

            var read = await Read(Multipart((ValuePart("job"), Encoding.UTF8.GetBytes(document))));

            Assert.IsNull(read.Job, "a multipart job carrying base64 files in its document was accepted");
            StringAssert.Contains(read.Failure, "'files' map of its own", read.Failure);
        }

        /// <summary>
        ///   A <c>job</c> part over its own bound is refused by that bound rather than by an
        ///   out-of-memory, which is what a caller who puts a file's bytes in the envelope would otherwise
        ///   find.
        /// </summary>
        [TestMethod]
        public async Task AnOversizedJobPartIsRefusedByItsOwnBound()
        {
            var padding = new String('x', JobRequestReader.MaxJobPartBytes + 1);
            var document = "{\"providerId\":\"" + CsvProviderId + "\",\"integrationInstanceId\":\"office\"," +
                           "\"settings\":{\"label\":\"" + padding + "\"}}";

            var read = await Read(Multipart((ValuePart("job"), Encoding.UTF8.GetBytes(document))));

            Assert.IsNull(read.Job, "a job envelope over its bound was read in full");
            StringAssert.Contains(read.Failure, "carries the job DOCUMENT", read.Failure);
        }

        /// <summary>A job part that is empty, which is a form with the field but nothing in it.</summary>
        [TestMethod]
        public async Task AnEmptyJobPartIsRefused()
        {
            var read = await Read(Multipart((ValuePart("job"), Array.Empty<Byte>())));

            Assert.IsNull(read.Job);
            StringAssert.Contains(read.Failure, "empty", read.Failure);
        }

        /// <summary>
        ///   Malformed JSON is named as malformed on BOTH arms. It used to be the framework's own 400 on the
        ///   JSON arm, and losing that to "a job definition is required" would tell a caller with a typo to
        ///   look for a missing body.
        /// </summary>
        [TestMethod]
        public async Task MalformedJsonIsNamedAsMalformedOnBothArms()
        {
            var form = await Read(Multipart((ValuePart("job"), Encoding.UTF8.GetBytes("{\"providerId\":"))));
            Assert.IsNull(form.Job);
            StringAssert.Contains(form.Failure, "not valid JSON", form.Failure);

            var json = await ReadJsonBody("{\"providerId\":");
            Assert.IsNull(json.Job);
            StringAssert.Contains(json.Failure, "not valid JSON", json.Failure);
            Assert.AreEqual(StatusCodes.Status400BadRequest, json.Status);
        }

        /// <summary>A JSON body of literal null is the absent job, not a malformed one.</summary>
        [TestMethod]
        public async Task AJsonBodyOfNullIsTheAbsentJob()
        {
            var read = await ReadJsonBody("null");

            Assert.IsNull(read.Job);
            StringAssert.Contains(read.Failure, "job definition is required", read.Failure);
        }

        #endregion

        #region the ceilings, on the transport that has no declared lengths

        /// <summary>
        ///   A part over the per-file ceiling stops being READ at one byte past it, and the refusal says
        ///   "more than" rather than a size. A multipart part declares no length, so a measured size would
        ///   have to be produced by reading the whole thing, which is the cost the ceiling exists to avoid.
        /// </summary>
        [TestMethod]
        public async Task APartOverThePerFileCeilingIsRefusedAsMoreThanTheCeiling()
        {
            var read = await Read(Multipart(
                    (ValuePart("job"), Encoding.UTF8.GetBytes(JobDocument(CsvProviderId, "office"))),
                    (FilePart("files[file]", "devices.csv"), new Byte[4096])),
                maxFileBytes: 1024);

            Assert.IsNotNull(read.Job, read.Failure);
            Assert.IsFalse(read.Job!.TryNormalize(out _, out var failure, maxFileBytes: 1024));
            StringAssert.Contains(failure, "more than 1024 bytes", failure);
            StringAssert.Contains(failure, "stopped reading at the ceiling", failure);
        }

        /// <summary>
        ///   The truncated part keeps NO bytes. Holding the 128 MiB of a file that is about to be refused
        ///   would be paying exactly the cost the ceiling was configured to prevent.
        /// </summary>
        [TestMethod]
        public async Task ATruncatedPartHoldsNothing()
        {
            var read = await Read(Multipart(
                    (ValuePart("job"), Encoding.UTF8.GetBytes(JobDocument(CsvProviderId, "office"))),
                    (FilePart("files[file]", "devices.csv"), new Byte[4096])),
                maxFileBytes: 1024);

            Assert.IsNotNull(read.Job, read.Failure);
            var file = read.Job!.Files["file"].Files[0];
            Assert.IsTrue(file.Truncated, "the part was not marked truncated, so the refusal will claim a size");
            Assert.AreEqual(0, file.Content!.Length,
                "a file that is going to be refused was kept in memory anyway");
        }

        /// <summary>
        ///   A file at EXACTLY the ceiling is accepted, which is the off-by-one this pins: the reader has to
        ///   read one byte past to know it was over, and reading one byte past must not itself be the
        ///   refusal.
        /// </summary>
        [TestMethod]
        public async Task AFileAtExactlyTheCeilingIsAccepted()
        {
            var content = Enumerable.Range(0, 1024).Select(i => (Byte)(i % 251)).ToArray();

            var read = await Read(Multipart(
                    (ValuePart("job"), Encoding.UTF8.GetBytes(JobDocument(CsvProviderId, "office"))),
                    (FilePart("files[file]", "devices.csv"), content)),
                maxFileBytes: 1024);

            Assert.IsNotNull(read.Job, read.Failure);
            Assert.IsTrue(read.Job!.TryNormalize(out var normalized, out var failure, maxFileBytes: 1024),
                failure);
            CollectionAssert.AreEqual(content, normalized!.Files["file"].First.Content,
                "a file of exactly the ceiling arrived changed");
        }

        /// <summary>
        ///   A file spanning several read segments arrives whole. One segment is 1 MiB, so nothing smaller
        ///   would ever exercise the join, and a bug there would corrupt every real extract while every
        ///   small fixture passed.
        /// </summary>
        [TestMethod]
        public async Task AFileLargerThanOneReadSegmentArrivesWhole()
        {
            var content = new Byte[(1024 * 1024) + 4096];
            for (var i = 0; i < content.Length; i++)
            {
                content[i] = (Byte)(i % 251);
            }

            var read = await Read(Multipart(
                (ValuePart("job"), Encoding.UTF8.GetBytes(JobDocument(CsvProviderId, "office"))),
                (FilePart("files[file]", "devices.csv"), content)));

            Assert.IsNotNull(read.Job, read.Failure);
            Assert.IsTrue(read.Job!.TryNormalize(out var normalized, out var failure), failure);
            CollectionAssert.AreEqual(content, normalized!.Files["file"].First.Content,
                "a file spanning more than one pooled segment was joined wrongly, which would corrupt " +
                "every extract big enough to matter while every small fixture passed");
        }

        /// <summary>
        ///   The count is refused AT the part that breaks it, and the rest of the form is not read. Reading
        ///   a thousand parts to find out there were too many is the cost the count exists to bound.
        /// </summary>
        [TestMethod]
        public async Task TheFileCountIsRefusedAtThePartThatBreaksIt()
        {
            var read = await Read(Multipart(
                    (ValuePart("job"), Encoding.UTF8.GetBytes(JobDocument(ArxmlProviderId, "v"))),
                    (FilePart("files[file][0]", "a.arxml"), Encoding.UTF8.GetBytes("a")),
                    (FilePart("files[file][1]", "b.arxml"), Encoding.UTF8.GetBytes("b")),
                    (FilePart("files[file][2]", "c.arxml"), Encoding.UTF8.GetBytes("c"))),
                maxJobFiles: 2);

            Assert.IsNull(read.Job, "a job over the file-count ceiling was read in full");
            StringAssert.Contains(read.Failure, "more than 2 files", read.Failure);
            StringAssert.Contains(read.Failure, "not read", read.Failure);
        }

        /// <summary>A count ceiling of zero is switched off, as every other ceiling here is.</summary>
        [TestMethod]
        public async Task ACountCeilingOfZeroIsSwitchedOff()
        {
            var parts = new List<(String, Byte[])>
            {
                (ValuePart("job"), Encoding.UTF8.GetBytes(JobDocument(ArxmlProviderId, "v"))),
            };
            for (var i = 0; i < 12; i++)
            {
                parts.Add((FilePart("files[file][" + i + "]", "f" + i + ".arxml"),
                    Encoding.UTF8.GetBytes("f" + i)));
            }

            var read = await Read(Multipart(parts.ToArray()), maxJobFiles: 0);

            Assert.IsNotNull(read.Job, read.Failure);
            Assert.AreEqual(12, read.Job!.Files["file"].Files.Count);
        }

        #endregion

        #region the content type decides which arm, and an unknown one is 415

        /// <summary>
        ///   415 rather than 400, and it names BOTH accepted types: a caller sending the wrong content type
        ///   has not written a bad job, they have written a good one nobody read.
        /// </summary>
        [TestMethod]
        [DataRow("text/plain", DisplayName = "text")]
        [DataRow("application/x-www-form-urlencoded", DisplayName = "a urlencoded form")]
        [DataRow("multipart/mixed; boundary=x", DisplayName = "the wrong multipart subtype")]
        [DataRow(null, DisplayName = "no content type at all")]
        public async Task AnUnsupportedContentTypeIs415NamingBothAcceptedTypes(String contentType)
        {
            var context = new DefaultHttpContext();
            context.Request.ContentType = contentType;
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{}"));

            var read = await JobRequestReader.ReadAsync(context.Request, 0, 0, CancellationToken.None);

            Assert.IsNull(read.Job);
            Assert.AreEqual(StatusCodes.Status415UnsupportedMediaType, read.Status,
                "an unreadable content type was answered as a bad job rather than an unsupported one");
            StringAssert.Contains(read.Failure, "application/json", read.Failure);
            StringAssert.Contains(read.Failure, "multipart/form-data", read.Failure);
        }

        /// <summary>
        ///   A multipart content type with no boundary is unreadable, so it is 415 rather than a crash: the
        ///   boundary is the only thing that says where the parts are.
        /// </summary>
        [TestMethod]
        public async Task MultipartWithNoBoundaryIs415()
        {
            var context = new DefaultHttpContext();
            context.Request.ContentType = "multipart/form-data";
            context.Request.Body = new MemoryStream(Array.Empty<Byte>());

            var read = await JobRequestReader.ReadAsync(context.Request, 0, 0, CancellationToken.None);

            Assert.AreEqual(StatusCodes.Status415UnsupportedMediaType, read.Status, read.Failure);
        }

        /// <summary>
        ///   A charset on the JSON content type still takes the JSON arm. Every browser and most clients
        ///   send one, so a strict equality check here would refuse the transport that has always worked.
        /// </summary>
        [TestMethod]
        public async Task JsonWithACharsetStillTakesTheJsonArm()
        {
            var context = new DefaultHttpContext();
            context.Request.ContentType = "application/json; charset=utf-8";
            context.Request.Body = new MemoryStream(
                Encoding.UTF8.GetBytes(JobDocument(CsvProviderId, "office")));

            var read = await JobRequestReader.ReadAsync(context.Request, 0, 0, CancellationToken.None);

            Assert.IsNotNull(read.Job, read.Failure);
            Assert.AreEqual(CsvProviderId, read.Job!.ProviderId);
        }

        /// <summary>
        ///   A quoted boundary is legal in the header grammar and is what some clients send, so the quotes
        ///   are stripped rather than made part of the delimiter nothing would then match.
        /// </summary>
        [TestMethod]
        public async Task AQuotedBoundaryIsRead()
        {
            var context = new DefaultHttpContext();
            context.Request.ContentType = "multipart/form-data; boundary=\"" + Boundary + "\"";
            context.Request.Body = new MemoryStream(Multipart(
                (ValuePart("job"), Encoding.UTF8.GetBytes(JobDocument(CsvProviderId, "office")))));

            var read = await JobRequestReader.ReadAsync(context.Request, 0, 0, CancellationToken.None);

            Assert.IsNotNull(read.Job, read.Failure);
        }

        #endregion

        #region nothing touches disk

        /// <summary>
        ///   THE NO-DISK GUARANTEE, behaviourally. The convention gate in <c>CodeQualityTest</c> bans the
        ///   form-reader API by name; this is the other half, with a part four times over the 64 KiB
        ///   threshold at which that reader spools a part to a temp file.
        ///
        ///   <para>Asserted on the ASPNETCORE_ prefix, because that is the name
        ///   <c>FileBufferingReadStream</c> gives every file it writes, so an entry appearing under it is
        ///   attributable to the transport rather than to whatever else is in a shared temp directory.
        ///   Nothing else in this suite reads a form, which is what makes the count stable.</para>
        /// </summary>
        [TestMethod]
        public async Task AFileWellOverTheFormSpoolThresholdLeavesNothingOnDisk()
        {
            var temp = Path.GetTempPath();
            var before = Directory.GetFileSystemEntries(temp, "ASPNETCORE_*").Length;

            // 256 KiB: four times FormOptions.MemoryBufferThreshold. A small fixture would pass whichever
            // reader was used, which is the whole reason this one is not small.
            var content = new Byte[256 * 1024];
            for (var i = 0; i < content.Length; i++)
            {
                content[i] = (Byte)(i % 251);
            }

            var read = await Read(Multipart(
                (ValuePart("job"), Encoding.UTF8.GetBytes(JobDocument(CsvProviderId, "office"))),
                (FilePart("files[file]", "devices.csv"), content)));

            Assert.IsNotNull(read.Job, read.Failure);
            CollectionAssert.AreEqual(content, read.Job!.Files["file"].Files[0].Content,
                "the file did not arrive whole");

            Assert.AreEqual(before, Directory.GetFileSystemEntries(temp, "ASPNETCORE_*").Length,
                "reading the form wrote a buffering temp file, so a caller's extract is being put on the " +
                "container's filesystem by the transport - which is exactly what the runtime publishes that " +
                "it never does");
        }

        #endregion

        #region helpers

        /// <summary>
        ///   The two arms agree on everything a run reads. Compared field by field rather than by
        ///   serialising, because the bytes and the AsList flag are exactly the parts a round trip would
        ///   flatten.
        /// </summary>
        private static void AssertSameJob(NormalizedJob left, NormalizedJob right)
        {
            Assert.AreEqual(left.ProviderId, right.ProviderId);
            Assert.AreEqual(left.InstanceId, right.InstanceId);
            Assert.AreEqual(left.Namespace, right.Namespace);
            Assert.AreEqual(left.EmbedSummaries, right.EmbedSummaries);
            Assert.AreEqual(left.EmbeddingName, right.EmbeddingName);

            CollectionAssert.AreEquivalent(left.Settings.Keys.ToArray(), right.Settings.Keys.ToArray());
            foreach (var pair in left.Settings)
            {
                Assert.AreEqual(pair.Value, right.Settings[pair.Key], "setting '" + pair.Key + "'");
            }

            CollectionAssert.AreEquivalent(left.Credentials.Keys.ToArray(),
                right.Credentials.Keys.ToArray());
            foreach (var pair in left.Credentials)
            {
                Assert.AreEqual(pair.Value, right.Credentials[pair.Key], "credential '" + pair.Key + "'");
            }

            CollectionAssert.AreEquivalent(left.Files.Keys.ToArray(), right.Files.Keys.ToArray());
            foreach (var pair in left.Files)
            {
                var other = right.Files[pair.Key];
                Assert.AreEqual(pair.Value.AsList, other.AsList,
                    "the two transports disagree about whether setting '" + pair.Key + "' was given a LIST, " +
                    "which is what a single-file setting refuses on");
                Assert.AreEqual(pair.Value.Files.Count, other.Files.Count, "file count for '" + pair.Key + "'");
                for (var i = 0; i < pair.Value.Files.Count; i++)
                {
                    Assert.AreEqual(pair.Value.Files[i].Name, other.Files[i].Name,
                        "file " + i + " of '" + pair.Key + "' arrived under a different name or in a " +
                        "different position, and for a provider composing an ordered union that is a " +
                        "different graph");
                    CollectionAssert.AreEqual(pair.Value.Files[i].Content, other.Files[i].Content,
                        "file " + i + " of '" + pair.Key + "' arrived with different bytes");
                }
            }
        }

        private static String JobDocument(String providerId, String instanceId)
        {
            return "{\"providerId\":\"" + providerId + "\",\"integrationInstanceId\":\"" + instanceId +
                   "\",\"settings\":{}}";
        }

        private static String ValuePart(String name)
        {
            return "form-data; name=\"" + name + "\"";
        }

        private static String FilePart(String name, String fileName)
        {
            return "form-data; name=\"" + name + "\"; filename=\"" + fileName + "\"";
        }

        /// <summary>
        ///   A multipart body, written by hand rather than by <c>MultipartFormDataContent</c>: the tests
        ///   here are about part NAMES and their order, several of which that type will not produce.
        /// </summary>
        private static Byte[] Multipart(params (String Disposition, Byte[] Content)[] parts)
        {
            var buffer = new MemoryStream();
            foreach (var part in parts)
            {
                Ascii(buffer, "--" + Boundary + "\r\n");
                Ascii(buffer, "Content-Disposition: " + part.Disposition + "\r\n\r\n");
                buffer.Write(part.Content, 0, part.Content.Length);
                Ascii(buffer, "\r\n");
            }

            Ascii(buffer, "--" + Boundary + "--\r\n");
            return buffer.ToArray();
        }

        private static void Ascii(Stream stream, String text)
        {
            // UTF-8 rather than ASCII: filename* is percent-encoded, but a raw non-ASCII byte in a
            // disposition is what a careless client sends, and the test that exercises one needs it to
            // survive the fixture.
            var bytes = Encoding.UTF8.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static Task<JobRequest> Read(Byte[] body, Int64 maxFileBytes = 0, Int32 maxJobFiles = 0)
        {
            var context = new DefaultHttpContext();
            context.Request.ContentType = "multipart/form-data; boundary=" + Boundary;
            context.Request.Body = new MemoryStream(body);
            context.Request.ContentLength = body.Length;

            return JobRequestReader.ReadAsync(context.Request, maxFileBytes, maxJobFiles,
                CancellationToken.None);
        }

        private static Task<JobRequest> ReadJsonBody(String json)
        {
            var context = new DefaultHttpContext();
            context.Request.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(json);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;

            return JobRequestReader.ReadAsync(context.Request, 0, 0, CancellationToken.None);
        }

        #endregion
    }
}
