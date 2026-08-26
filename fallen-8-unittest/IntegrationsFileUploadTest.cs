// MIT License
//
// IntegrationsFileUploadTest.cs
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
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Integrations.Contract;
using NoSQL.GraphDB.Integrations.Credentials;
using NoSQL.GraphDB.Integrations.Diagnostics;
using NoSQL.GraphDB.Integrations.Graph;
using NoSQL.GraphDB.Integrations.Identity;
using NoSQL.GraphDB.Integrations.Run;
using NoSQL.GraphDB.Integrations.Validation;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   How a FILE reaches a provider (feature integration-file-upload): it arrives with the job that
    ///   needs it and is dropped when the run ends, which is the credential rule applied to the other thing
    ///   a run cannot fetch for itself. There is no mount, no staging area and no name to resolve, so this
    ///   file is where the whole path is pinned.
    ///
    ///   <para>Every assertion here stands in for a failure that is invisible from the graph. The three
    ///   worst, and why each has its own test: an EMPTY upload read as an empty source produces a complete
    ///   snapshot describing nothing, which withdraws every element the identity ever claimed and deletes
    ///   the ones nothing else claims; a payload accepted as TEXT rather than bytes hands a provider
    ///   mojibake for any extract a vendor tool wrote as UTF-16, so the run succeeds and writes rubbish;
    ///   and a file setting satisfied from <c>settings</c> lets a job pass every pre-run check and then fail
    ///   in the middle of a source read, after the run has begun making withdrawal-relevant
    ///   decisions.</para>
    /// </summary>
    [TestClass]
    public class IntegrationsFileUploadTest
    {
        private const String Instance = "file-upload-suite";
        private const String FileSetting = "extract";
        private const String OptionalFileSetting = "overlay";
        private const String MultiFileSetting = "extracts";
        private const String FileName = "devices.csv";
        private const String Text = "mac,name\nAA:BB:CC:DD:EE:01,Reception\n";

        #region the file reaches the provider, unchanged

        [TestMethod]
        public async Task AFileOnTheJobReachesTheProviderVerbatim()
        {
            using var harness = new Harness();

            var report = await harness.RunAsync(Job(FileOf(FileName, Text)));

            Assert.IsNull(report.ErrorKind, "the run must succeed: " + report.Error);
            Assert.AreEqual(Text, harness.Provider.ReadText,
                "the provider must see the bytes the job carried, byte for byte. A transport that " +
                "re-wrapped, trimmed or re-encoded them would change what the source SAYS, and the run " +
                "would write that difference into the graph and call it what it observed");
        }

        [TestMethod]
        public async Task TheEffectiveSettingValueIsTheFilesOwnName_SoEveryMessageStillNamesIt()
        {
            using var harness = new Harness();

            var report = await harness.RunAsync(Job(FileOf("inventory-2026.csv", Text)));

            Assert.IsNull(report.ErrorKind, "the run must succeed: " + report.Error);
            Assert.AreEqual("inventory-2026.csv", harness.Provider.RequiredValue,
                "a provider reads the file setting with Required(key) for its messages and diagnostic " +
                "subjects ('devices.csv row 7'). If the runtime stopped putting the NAME there, every " +
                "shipped file provider would either fail its own Required() call or start naming a file " +
                "the caller never mentioned");
        }

        [TestMethod]
        public async Task AUtf16PayloadDecodesToTheSameTextAsItsUtf8Twin()
        {
            using var utf8 = new Harness();
            using var utf16 = new Harness();

            var eight = await utf8.RunAsync(Job(FileOf(FileName, Text, Encoding.UTF8)));
            var sixteen = await utf16.RunAsync(Job(FileOf(FileName, Text, Encoding.Unicode)));

            Assert.IsNull(eight.ErrorKind, "the UTF-8 run must succeed: " + eight.Error);
            Assert.IsNull(sixteen.ErrorKind, "the UTF-16 run must succeed: " + sixteen.Error);
            Assert.AreEqual(utf8.Provider.ReadText, utf16.Provider.ReadText,
                "the file travels as BYTES and is decoded with byte-order-mark detection, exactly as " +
                "File.ReadAllTextAsync did when it came off a mount. A transport carrying 'the text' would " +
                "hand the provider mojibake for any AUTOSAR extract a vendor tool wrote as UTF-16, and " +
                "nothing on the report would say so");
        }

        [TestMethod]
        public async Task AnOptionalFileSettingLeftOutIsAbsentRatherThanEmpty()
        {
            using var harness = new Harness();
            harness.Provider.ReadOptional = true;

            var report = await harness.RunAsync(Job(FileOf(FileName, Text)));

            Assert.IsNull(report.ErrorKind, "the run must succeed: " + report.Error);
            Assert.IsNull(harness.Provider.OptionalValue,
                "an optional file nobody sent must read as ABSENT, so a provider's own 'was one supplied' " +
                "check works. An empty string there would look like a supplied file with no name");
            Assert.IsFalse(harness.Provider.OptionalResolved,
                "and resolving it must say no, with a reason, rather than throwing: that is what lets a " +
                "provider decide for itself whether an optional overlay is worth asking for");
        }

        #endregion

        #region refusals, all of them BEFORE the provider is invoked

        [TestMethod]
        public async Task ARequiredFileNobodySentIsRefusedAndNamesTheSetting()
        {
            var refused = await Refusal(Job());

            Assert.AreEqual(JobErrorKinds.Configuration, refused.Kind,
                "the JOB is wrong, and 'the job is wrong' and 'the source will not answer' send an operator " +
                "to two different places");
            StringAssert.Contains(refused.Message, FileSetting,
                "the refusal names the setting that wanted a file, or a form with several cannot say which " +
                "one was left empty");
            StringAssert.Contains(refused.Message, "files",
                "and it names where a file belongs. With nothing opened on disk there is no directory to " +
                "put one in instead, so a message that did not say 'files' would leave the caller looking " +
                "for a mount that does not exist");
        }

        [TestMethod]
        public async Task AFileSettingNamedInSettingsIsRefusedRatherThanReadAsAName()
        {
            var job = Job();
            job.Settings[FileSetting] = FileName;

            var refused = await Refusal(job);

            StringAssert.Contains(refused.Message, "never in 'settings'",
                "a bare name in settings is refused HERE, before the run. It would otherwise satisfy this " +
                "pass, satisfy the provider's own Required() call, and fail only on the read - by which " +
                "point the run has reached the provider and begun making withdrawal-relevant decisions");
            StringAssert.Contains(refused.Message, FileSetting,
                "and it names the key, because the whole mistake is putting the right value in the wrong map");
        }

        [TestMethod]
        public async Task AFileSuppliedForAKeyTheProviderDoesNotDeclareIsRefused()
        {
            var job = Job(FileOf(FileName, Text));
            job.Files["notASetting"] = FileOf(FileName, Text);

            var refused = await Refusal(job);

            StringAssert.Contains(refused.Message, "notASetting",
                "a file for a key nothing reads is silently ignored unless it is refused, and a typo in a " +
                "key then means 'the run used a file you did not send'");
        }

        [TestMethod]
        public async Task AFileSuppliedForANonFileSettingIsRefused()
        {
            var job = Job(FileOf(FileName, Text));
            job.Files["label"] = FileOf(FileName, Text);

            var refused = await Refusal(job);

            StringAssert.Contains(refused.Message, "label",
                "the key exists but its kind is not File, so nothing would ever read the bytes. Accepting " +
                "it would make 'the job carried my file' and 'the run read my file' two different facts");
        }

        [TestMethod]
        public void TwoFileKeysDifferingOnlyInCaseAreRefused()
        {
            var job = Job(FileOf(FileName, Text));
            job.Files[FileSetting.ToUpperInvariant()] = FileOf(FileName, Text);

            Assert.IsFalse(job.TryNormalize(out _, out var failure),
                "the map is folded case-insensitively, so two keys differing only in case cannot be told " +
                "apart afterwards: one would silently win and the run would read a file the caller did not " +
                "mean to use");
            StringAssert.Contains(failure, "differ only in case",
                "the refusal has to say WHY, or the caller sees a rejection for a job whose two keys look " +
                "different to them");
        }

        [TestMethod]
        public void AnEmptyFileIsRefusedRatherThanReadAsAnEmptySource()
        {
            var job = Job(new JobFile { Name = FileName, ContentBase64 = String.Empty });

            Assert.IsFalse(job.TryNormalize(out _, out var failure),
                "an empty upload - a form submitted before the file was chosen, a truncated copy - parses " +
                "as a complete snapshot describing nothing, which withdraws every element this identity " +
                "ever claimed and deletes the ones nothing else claims. 'I could not look' must never " +
                "become 'there is nothing there'");
            StringAssert.Contains(failure, "empty",
                "and it says so plainly: this is the refusal most likely to be read by somebody who thinks " +
                "they attached a file");
        }

        [TestMethod]
        public void APayloadThatIsNotBase64IsRefusedAndSaysSo()
        {
            var job = Job(new JobFile { Name = FileName, ContentBase64 = "mac,name\nthis is the raw text" });

            Assert.IsFalse(job.TryNormalize(out _, out var failure),
                "raw text in contentBase64 is the obvious mistake for a hand-written job, and decoding it " +
                "as base64 would either throw deep in a run or silently yield different bytes");
            StringAssert.Contains(failure, "base64",
                "the message names the encoding, because the fix is one shell command (base64 -w0) and the " +
                "caller has to know that is what is wanted");
        }

        [TestMethod]
        public void AFileWithNoNameIsRefused()
        {
            var job = Job(new JobFile
            {
                Name = "   ",
                ContentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(Text)),
            });

            Assert.IsFalse(job.TryNormalize(out _, out var failure),
                "the name is what every message about this run calls the file, and it becomes the setting's " +
                "effective value - so a nameless file makes a provider's own Required() call fail with a " +
                "message about a setting the caller filled in");
            StringAssert.Contains(failure, "no name", "the refusal says which half is missing");
        }

        [TestMethod]
        public void AFileNameCarryingAControlCharacterIsRefused()
        {
            var job = Job(new JobFile
            {
                Name = "devices\u0007.csv",
                ContentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(Text)),
            });

            Assert.IsFalse(job.TryNormalize(out _, out var failure),
                "the name is written into log lines, onto the job report and into diagnostic subjects, and " +
                "a control character is invisible in exactly the place an operator goes to read what " +
                "happened. It is a display-string check and not a path check: nothing resolves this name");
            StringAssert.Contains(failure, "control character", "the refusal says what is wrong with it");
        }

        [TestMethod]
        public void AFileOverTheCeilingIsRefusedAndNamesBothSizes()
        {
            var oversized = new Byte[64];
            var job = Job(new JobFile { Name = FileName, ContentBase64 = Convert.ToBase64String(oversized) });

            Assert.IsFalse(job.TryNormalize(out _, out var failure, 63),
                "the ceiling is checked on the DECODED length, because that is what the run holds and what " +
                "the provider parses. Checking the encoded length would state a limit a third smaller than " +
                "the one configured");
            StringAssert.Contains(failure, "64", "the message names the file's actual size");
            StringAssert.Contains(failure, "63", "and the ceiling, so the caller can tell which to change");
            StringAssert.Contains(failure, "MaxFileBytes",
                "and the key that sets it - which lives in the RUNTIME's configuration, not the instance " +
                "the caller submitted through, so an unnamed number sends them to the wrong settings screen");
        }

        [TestMethod]
        public void AFileAtExactlyTheCeilingIsAccepted()
        {
            var exact = new Byte[64];
            var job = Job(new JobFile { Name = FileName, ContentBase64 = Convert.ToBase64String(exact) });

            Assert.IsTrue(job.TryNormalize(out var normalized, out var failure, 64),
                "the ceiling is inclusive, or the documented limit is one byte smaller than the one that " +
                "actually applies: " + failure);
            Assert.AreEqual(64, normalized.Files[FileSetting].First.Content.Length,
                "and the whole file survives normalisation");
        }

        #endregion

        #region several files, one source (feature integration-run-lifecycle)

        [TestMethod]
        public async Task EveryFileOfAMultipleSettingReachesTheProvider_InTheOrderTheJobListedThem()
        {
            using var harness = new Harness();
            harness.Provider.ReadMany = true;

            var report = await harness.RunAsync(JobWithMany(
                FileOf("chassis.arxml", "chassis"),
                FileOf("body.arxml", "body"),
                FileOf("powertrain.arxml", "powertrain")));

            Assert.IsNull(report.ErrorKind, "the run must succeed: " + report.Error);
            CollectionAssert.AreEqual(new[] { "chassis.arxml", "body.arxml", "powertrain.arxml" },
                harness.Provider.ManyNames.ToArray(),
                "order is part of the meaning for a provider that composes its files: which file owns a " +
                "re-declared path is decided by it, so a set that arrives sorted or reversed is a different " +
                "graph");
            CollectionAssert.AreEqual(new[] { "chassis", "body", "powertrain" },
                harness.Provider.ManyTexts.ToArray(),
                "each file must be readable by position, and read the file at that position");
        }

        [TestMethod]
        public async Task TheValueOfAMultipleSettingIsEveryName()
        {
            // What a message ABOUT THE SETTING says. The first name alone would be a message quietly about
            // one file of several, which is how a diagnostic ends up naming the wrong extract.
            using var harness = new Harness();
            harness.Provider.ReadMany = true;

            await harness.RunAsync(JobWithMany(FileOf("chassis.arxml", "a"), FileOf("body.arxml", "b")));

            Assert.AreEqual("chassis.arxml, body.arxml", harness.Provider.ManySettingValue);
        }

        [TestMethod]
        public async Task OneFileIsStillValidForAMultipleSetting()
        {
            // The compatibility half: a setting that CAN take several does not have to, and the single
            // object form stays exactly what it was.
            using var harness = new Harness();
            harness.Provider.ReadMany = true;

            var job = Job(FileOf(FileName, Text));
            job.Files[MultiFileSetting] = FileOf("only.arxml", "only");

            var report = await harness.RunAsync(job);

            Assert.IsNull(report.ErrorKind, "the run must succeed: " + report.Error);
            CollectionAssert.AreEqual(new[] { "only.arxml" }, harness.Provider.ManyNames.ToArray());
            Assert.AreEqual("only.arxml", harness.Provider.ManySettingValue,
                "and one file's value is still just its name, not a list of one");
        }

        [TestMethod]
        public async Task AListForASettingThatTakesOneFileIsRefused()
        {
            // The refusal that matters most in this feature. A provider not built to compose files would
            // read only the FIRST of a list - and this provider class declares COMPLETE snapshots, so the
            // files it never read would be reported as parts of the source that no longer exist, and
            // reconciliation would delete everything they describe.
            var job = Job();
            job.Files[FileSetting] = new JobFileGroup(FileOf("a.csv", "a"), FileOf("b.csv", "b"));

            var refused = await Refusal(job);

            Assert.AreEqual(JobErrorKinds.Configuration, refused.Kind);
            StringAssert.Contains(refused.Message, FileSetting,
                "the refusal has to name the setting, which is the only thing the caller can change: " +
                refused.Message);
            StringAssert.Contains(refused.Message, "ONE file",
                "and say what the setting actually takes: " + refused.Message);
        }

        [TestMethod]
        public async Task AListOfONEForASettingThatTakesOneFileIsRefusedToo()
        {
            // The shape that would otherwise work by accident and break the day it carried two. A caller
            // sending an array is asking for the multiple contract, and a setting that does not offer it
            // says so now rather than after the next file is added.
            var job = Job();
            job.Files[FileSetting] = new JobFileGroup(FileOf("a.csv", "a"));

            var refused = await Refusal(job);

            Assert.AreEqual(JobErrorKinds.Configuration, refused.Kind);
            StringAssert.Contains(refused.Message, FileSetting, refused.Message);
        }

        [TestMethod]
        public async Task TwoFilesWithOneNameAreRefused()
        {
            var refused = await Refusal(JobWithMany(
                FileOf("body.arxml", "first"), FileOf("BODY.arxml", "second")));

            Assert.AreEqual(JobErrorKinds.Configuration, refused.Kind);
            StringAssert.Contains(refused.Message, "body.arxml",
                "every diagnostic about a file names it, so two files with one name make each of those " +
                "messages ambiguous - and the commonest cause is the same file picked twice: " +
                refused.Message);
            StringAssert.Contains(refused.Message, "BODY.arxml",
                "and when the two spellings differ only in case, naming just one of them reads as a " +
                "complaint about a file the caller cannot find in what they sent: " + refused.Message);
        }

        [TestMethod]
        public void FilesThatAreLegalOneByOneCanStillBeRefusedAsATotal()
        {
            // The second ceiling, and why it is not a restatement of the first: each of these files is well
            // inside the per-file limit, and what this process has to hold at once is their sum.
            var job = JobWithMany(FileOf("a.arxml", new String('a', 40)),
                FileOf("b.arxml", new String('b', 40)));

            Assert.IsFalse(job.TryNormalize(out _, out var failure, maxFileBytes: 64, maxJobFileBytes: 100),
                "a job whose files come to more than the total ceiling was accepted");
            StringAssert.Contains(failure, "total ceiling",
                "and the refusal names WHICH ceiling was broken, because a caller shown the per-file number " +
                "would shrink files that were never the problem: " + failure);
            StringAssert.Contains(failure, "MaxJobFileBytes", failure);
        }

        [TestMethod]
        public void ThePerFileCeilingStillAppliesInsideASet()
        {
            // The first ceiling has to survive the second: one absurd file among small ones is still refused
            // for being one absurd file, with the message that names the per-file knob.
            var job = JobWithMany(FileOf("small.arxml", "aa"), FileOf("huge.arxml", new String('b', 200)));

            Assert.IsFalse(job.TryNormalize(out _, out var failure, maxFileBytes: 64,
                    maxJobFileBytes: 1_000_000),
                "one file over the per-file ceiling was accepted because the total was fine");
            StringAssert.Contains(failure, "MaxFileBytes", failure);
        }

        [TestMethod]
        public void ASetOfFilesInsideBothCeilingsIsAccepted()
        {
            var job = JobWithMany(FileOf("a.arxml", new String('a', 30)),
                FileOf("b.arxml", new String('b', 30)));

            Assert.IsTrue(job.TryNormalize(out var normalized, out var failure, maxFileBytes: 64,
                    maxJobFileBytes: 100),
                "the ceilings are inclusive, or the documented limits are smaller than the ones that apply: " +
                failure);
            Assert.AreEqual(2, normalized.Files[MultiFileSetting].Files.Count);
            Assert.AreEqual("a.arxml", normalized.Files[MultiFileSetting].First.Name,
                "and the first file of the set is the one the job listed first");
        }

        [TestMethod]
        public async Task AMultipleSettingNobodySentReadsAsNoFilesRatherThanThrowing()
        {
            // An OPTIONAL multiple setting that got nothing must be an empty list, not an exception: a
            // provider loops over the names it was offered, and a throw here would make every optional
            // multi-file setting a special case at the call site.
            using var harness = new Harness();
            harness.Provider.ReadMany = true;

            var report = await harness.RunAsync(Job(FileOf(FileName, Text)));

            Assert.IsNull(report.ErrorKind, "the run must succeed: " + report.Error);
            Assert.AreEqual(0, harness.Provider.ManyNames.Count);
            Assert.AreEqual(0, harness.Provider.ManyTexts.Count);
        }

        [TestMethod]
        public async Task ReadingPastTheEndOfASetIsAProviderDefect_NotTheFirstFileAgain()
        {
            // Answering with the first file instead would silently parse one extract twice, which is a wrong
            // graph nothing in the report could explain.
            using var harness = new Harness();
            harness.Provider.ReadMany = true;
            harness.Provider.ReadPastEndAt = 5;

            var report = await harness.RunAsync(JobWithMany(FileOf("a.arxml", "a"), FileOf("b.arxml", "b")));

            Assert.AreEqual(JobErrorKinds.Source, report.ErrorKind,
                "a provider reading past its own file list must fail the run rather than be handed a file it " +
                "did not ask for: " + report.Error);
            Assert.AreEqual(0, report.ElementsCreated, "and nothing is written on the way out");
        }

        #endregion

        #region the file is dropped when the run ends

        [TestMethod]
        public async Task ReadingAFileAfterTheRunHasEndedThrows()
        {
            using var harness = new Harness();

            var report = await harness.RunAsync(Job(FileOf(FileName, Text)));
            Assert.IsNull(report.ErrorKind, "the run must succeed first: " + report.Error);

            Assert.IsNotNull(harness.Provider.KeptContext,
                "the fixture has to keep the context to be able to misuse it, which is the whole point");

            var late = Assert.ThrowsException<InvalidOperationException>(
                () => harness.Provider.KeptContext.ReadFileAsync(FileSetting, CancellationToken.None)
                    .GetAwaiter().GetResult(),
                "a file belongs to the job it arrived with and to nothing else. A provider that squirrelled " +
                "the context away must FAIL rather than quietly read caller data after the run it belonged " +
                "to - the same time-boxing the credential lease gives, for the same reason");

            StringAssert.Contains(late.Message, "dropped",
                "and the message says the file is gone, not that the setting was wrong: those send an " +
                "author to two different lines of their own code");
        }

        #endregion

        #region fixtures

        /// <summary>
        ///   One file as the wire carries it: the bytes an editor saving in that encoding would write,
        ///   byte-order mark included, base64. The mark is what makes the UTF-16 case a real test rather
        ///   than a differently-spelled UTF-8 one.
        /// </summary>
        private static JobFile FileOf(String name, String content, Encoding encoding = null)
        {
            encoding ??= new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

            var preamble = encoding.GetPreamble();
            var body = encoding.GetBytes(content);
            var bytes = new Byte[preamble.Length + body.Length];
            Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
            Buffer.BlockCopy(body, 0, bytes, preamble.Length, body.Length);

            return new JobFile { Name = name, ContentBase64 = Convert.ToBase64String(bytes) };
        }

        private static IntegrationJob Job(JobFile file = null)
        {
            var job = new IntegrationJob
            {
                ProviderId = FileReadingProvider.Id,
                IntegrationInstanceId = Instance,
            };

            if (file != null)
            {
                job.Files[FileSetting] = file;
            }

            return job;
        }

        /// <summary>
        ///   A job whose MULTIPLE setting carries several files, as an array on the wire. The required
        ///   single-file setting is filled too, because the fixture provider requires it.
        /// </summary>
        private static IntegrationJob JobWithMany(params JobFile[] files)
        {
            var job = Job(FileOf(FileName, Text));
            job.Files[MultiFileSetting] = new JobFileGroup(files);
            return job;
        }

        /// <summary>The refusal a job earns, which is a JobRejectedException and never a failed report:
        /// a job that cannot be run at all never becomes a run.</summary>
        private static async Task<JobRejectedException> Refusal(IntegrationJob job)
        {
            using var harness = new Harness();

            var refused = await Assert.ThrowsExceptionAsync<JobRejectedException>(
                () => harness.RunAsync(job),
                "the job must be REFUSED rather than run and reported, because every one of these mistakes " +
                "is knowable before the provider is invoked");

            Assert.IsFalse(harness.Provider.WasInvoked,
                "and the provider must never have been invoked: once a run reaches it the run has begun " +
                "making withdrawal-relevant decisions, and the eager-checks-first design exists to keep " +
                "an unrunnable job on this side of that line");

            return refused;
        }

        /// <summary>
        ///   The REAL runner over a graph nothing reads, with the one provider this file needs: one that
        ///   reads a file and remembers what it saw.
        /// </summary>
        private sealed class Harness : IDisposable
        {
            private readonly ILoggerFactory _loggers;
            private readonly IntegrationsMetrics _metrics;
            private readonly JobRunner _runner;

            public Harness()
            {
                _loggers = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.None));
                _metrics = new IntegrationsMetrics();
                Provider = new FileReadingProvider();

                var vocabulary = IdentifierVocabulary.Shipped;
                var active = new ActiveCredentials();
                _runner = new JobRunner(
                    new ProviderCatalog(new IIntegrationProvider[] { Provider }, vocabulary),
                    new SnapshotValidator(vocabulary),
                    new SnapshotApplier(new IdentityResolver()),
                    new CredentialResolver(active),
                    new OneTarget(new InMemoryGraphTarget()),
                    new NoNetwork(),
                    new JobFilesFactory(Microsoft.Extensions.Options.Options.Create(
                        new Integrations.Configuration.IntegrationsOptions())),
                    active,
                    new RunGate(),
                    _metrics,
                    _loggers);
            }

            public FileReadingProvider Provider { get; }

            public Task<JobReport> RunAsync(IntegrationJob job)
            {
                return _runner.RunAsync(job, CancellationToken.None);
            }

            public void Dispose()
            {
                _metrics.Dispose();
                _loggers.Dispose();
            }

            private sealed class OneTarget : IGraphTargetFactory
            {
                private readonly IGraphTarget _target;

                public OneTarget(IGraphTarget target)
                {
                    _target = target;
                }

                public IGraphTarget Create(String namespaceName)
                {
                    return _target;
                }
            }

            private sealed class NoNetwork : IProviderHttpFactory
            {
                public HttpClient Create(Boolean holdsCredential)
                {
                    return new HttpClient(new Refusing(), disposeHandler: true);
                }

                private sealed class Refusing : HttpMessageHandler
                {
                    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                        CancellationToken cancellationToken)
                    {
                        throw new NotSupportedException("This fixture's provider reads no source.");
                    }
                }
            }
        }

        /// <summary>
        ///   A provider whose whole source is one file, plus an optional second, and which remembers
        ///   everything the runtime handed it - including the context, so the drop-after-the-run rule can be
        ///   tested from the position of the provider that breaks it.
        /// </summary>
        private sealed class FileReadingProvider : IIntegrationProvider
        {
            internal const String Id = "file-reading-fixture";

            public Boolean ReadOptional { get; set; }

            /// <summary>Whether to read the MULTIPLE setting, the way a composing provider does.</summary>
            public Boolean ReadMany { get; set; }

            /// <summary>How far past the end of the list to read, for the provider-defect case.</summary>
            public Int32 ReadPastEndAt { get; set; } = -1;

            public Boolean WasInvoked { get; private set; }

            public String ReadText { get; private set; }

            public String RequiredValue { get; private set; }

            public String OptionalValue { get; private set; }

            public Boolean OptionalResolved { get; private set; }

            /// <summary>The names the runtime offered for the multiple setting, in the order it offered them.</summary>
            public IReadOnlyList<String> ManyNames { get; private set; } = Array.Empty<String>();

            /// <summary>Each of those files' text, read one at a time and in the same order.</summary>
            public List<String> ManyTexts { get; } = new List<String>();

            /// <summary>The effective VALUE of the multiple setting, which is what a message about it says.</summary>
            public String ManySettingValue { get; private set; }

            public ProviderContext KeptContext { get; private set; }

            public ProviderDescriptor Descriptor { get; } = new ProviderDescriptor
            {
                Id = Id,
                DisplayName = "File reading fixture",
                Description = "Reads one file the job carried and describes nothing.",
                Settings = new[]
                {
                    new ProviderSetting
                    {
                        Key = FileSetting,
                        Label = "Extract",
                        Kind = SettingKind.File,
                        Required = true,
                        Accept = ".csv",
                        Help = "The file itself, sent with the job.",
                    },
                    new ProviderSetting
                    {
                        Key = OptionalFileSetting,
                        Label = "Overlay",
                        Kind = SettingKind.File,
                        Required = false,
                        Help = "An optional second file.",
                    },
                    new ProviderSetting
                    {
                        Key = MultiFileSetting,
                        Label = "Extracts",
                        Kind = SettingKind.File,
                        Required = false,
                        Multiple = true,
                        Help = "Several files that are ONE source together.",
                    },
                    new ProviderSetting
                    {
                        Key = "label",
                        Label = "Label",
                        Kind = SettingKind.Text,
                        Required = false,
                        Help = "What to call what it found.",
                    },
                },
                EntityKinds = new[] { "device" },
                ClaimTypes = Array.Empty<String>(),
                RelationTypes = Array.Empty<String>(),
                CanObserveCompleteState = true,
                ReadOnly = true,
            };

            public async Task<SnapshotDocument> ObserveAsync(ProviderContext context,
                CancellationToken cancellationToken)
            {
                WasInvoked = true;
                KeptContext = context;

                RequiredValue = context.Required(FileSetting);
                ReadText = await context.ReadFileAsync(FileSetting, cancellationToken).ConfigureAwait(false);

                if (ReadOptional)
                {
                    OptionalValue = context.Optional(OptionalFileSetting);
                    OptionalResolved = context.TryResolveFile(OptionalFileSetting, out _);
                }

                if (ReadMany)
                {
                    // The shape a composing provider uses: the names first, then one file at a time, so a
                    // set of tens-of-megabytes extracts is never all decoded at once.
                    ManyNames = context.FileNames(MultiFileSetting);
                    ManySettingValue = context.Optional(MultiFileSetting);
                    for (var i = 0; i < ManyNames.Count; i++)
                    {
                        ManyTexts.Add(await context
                            .RequireFileTextAtAsync(MultiFileSetting, i, cancellationToken)
                            .ConfigureAwait(false));
                    }

                    if (ReadPastEndAt >= 0)
                    {
                        ManyTexts.Add(await context
                            .RequireFileTextAtAsync(MultiFileSetting, ReadPastEndAt, cancellationToken)
                            .ConfigureAwait(false));
                    }
                }

                // Deliberately EMPTY and complete: this fixture is about how the file arrived, and an
                // entity would drag claim resolution into every assertion above.
                return new SnapshotDocument
                {
                    ProviderId = context.ProviderId,
                    IntegrationInstanceId = context.InstanceId,
                    Declares = SnapshotCompleteness.Complete,
                }.CapturedNow();
            }
        }

        #endregion
    }
}
