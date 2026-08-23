// MIT License
//
// IntegrationsCredentialTest.cs
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
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Integrations.Configuration;
using NoSQL.GraphDB.Integrations.Conformance;
using NoSQL.GraphDB.Integrations.Credentials;
using NoSQL.GraphDB.Integrations.Run;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The credential path of the integrations runtime (feature integrations, spec sections 3 and 4): what a
    ///   supplied credential's characters mean, how long a run may hold the value, what redaction covers, and
    ///   where a run holding one may send it. A credential arrives on the job and nowhere else, so there is no
    ///   store here to test - and since feature integration-file-upload there is no name-to-path question
    ///   either: a file arrives on the job too, so this runtime opens nothing by name and the containment
    ///   checks that used to live here have no root left to contain anything in (see
    ///   <c>IntegrationsFileUploadTest</c> for what replaced them).
    ///
    ///   <para>Every assertion here stands in for a failure that is invisible from the graph. The three worst
    ///   ones, and why each has its own test: a blank credential accepted as "no credential" produces a run
    ///   that reads what the source shows the public, declares it complete, and withdraws every claim the
    ///   instance ever made; a credential value reaching any log sink hands somebody else's network-admin
    ///   password to whoever reads the container log; and a host check that is not enforced on the way out lets
    ///   whoever can edit a base URL aim that password at a machine of their choosing.</para>
    /// </summary>
    [TestClass]
    public class IntegrationsCredentialTest
    {
        private const String Secret = "s3cr3t-console-password";

        // ------------------------------------------------------------------------------------------------
        // Credential CONTENT rules, driven through the resolver, which is the ONE place they live and the only
        // way a credential enters the runtime at all.
        // ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void ExactlyOneTrailingLineEnding_IsStripped_SoAPastedValueSurvivesItsLineBreak()
        {
            Assert.IsTrue(TryReadContent("pw", out var bare, out _), "a value pasted with no line break is usable");
            Assert.AreEqual("pw", bare, "with nothing to strip, the value is what was supplied, verbatim");

            Assert.IsTrue(TryReadContent("pw\n", out var unix, out _), "a value pasted out of a terminal is usable");
            Assert.AreEqual("pw", unix,
                "a copy out of a terminal brings the newline with it, and keeping that byte produces an " +
                "authentication failure from somebody's controller with nothing in the report to explain it");

            Assert.IsTrue(TryReadContent("pw\r\n", out var windows, out _), "a value copied on Windows is usable");
            Assert.AreEqual("pw", windows,
                "a copy on Windows ends CRLF, and treating those two bytes as part of the password sends the " +
                "wrong value to the source");

            Assert.IsTrue(TryReadContent("pw\r", out var carriageReturn, out _), "a lone CR is usable too");
            Assert.AreEqual("pw", carriageReturn,
                "a lone carriage return is a line ending too, and one left on the value is invisible in every " +
                "log line the operator would use to diagnose the rejection");
        }

        [TestMethod]
        public void ASecondTrailingLineEnding_IsRefused_RatherThanStrippedOrSent()
        {
            // Exactly ONE ending is dropped, so what is left of "pw\n\n" is "pw\n" - and a credential whose
            // last character is a line break cannot go in an HTTP header at all. Both halves matter: the
            // value is never silently rewritten to make it work, AND it is refused here rather than at the
            // send, where the runner could only report that the source did not answer.
            foreach (var content in new String[] { "pw\n\n", "pw\r\n\r\n", "p\nw\n" })
            {
                Assert.IsFalse(TryReadContent(content, out var value, out var failure),
                    "a credential still carrying a line break after one ending is dropped must be REFUSED. " +
                    "Stripping the rest would send a value the caller never supplied, and sending it as it " +
                    "is throws inside the provider, which the runner can only report as 'the source did not " +
                    "answer' - the wrong system, when the answer is one character of the key");
                Assert.IsNull(value, "a refused credential must yield no value for a run to authenticate with");
                StringAssert.Contains(failure, "HTTP header",
                    "the refusal must say why the value cannot be used, or the caller re-pastes the same " +
                    "thing and gets the same failure");
                Assert.IsFalse(failure.Contains("pw", StringComparison.Ordinal),
                    "and it must not quote the value: this message is reported to the caller and logged, " +
                    "and a value refused before the lease is one redaction knows nothing about");
            }
        }

        [TestMethod]
        public void ACharacterOutsideAscii_IsRefused_BecauseNoHeaderCanCarryIt()
        {
            // What a copy out of a document or a chat window brings along: a curly quote, a non-breaking
            // space, an accented letter. .NET refuses to put any of them in a header, so a run that accepted
            // one would throw inside the provider and be reported as "the source did not answer".
            foreach (var content in new String[] { "sk-’key", "sk- key", "sk-kéy" })
            {
                Assert.IsFalse(TryReadContent(content, out var value, out var failure),
                    "a key carrying a character no HTTP header can hold must be refused as the credential " +
                    "problem it is, and named as one, or the operator is sent to look at their console");
                Assert.IsNull(value, "a refused credential must yield no value");
                StringAssert.Contains(failure, "ASCII",
                    "the refusal must say what is wrong with the value, because the character is invisible " +
                    "in the field it was pasted into");
                Assert.IsFalse(failure.Contains("sk-", StringComparison.Ordinal),
                    "and it must not quote the value, which is reported to the caller and logged");
            }
        }

        [TestMethod]
        public void SpacesAreUntouched_LeadingInternalAndTrailing_BecauseASpaceCanBePartOfAPassword()
        {
            Assert.IsTrue(TryReadContent("  pw\n", out var leading, out _), "the value is not empty");
            Assert.AreEqual("  pw", leading,
                "trimming a leading space would send a different password than the one supplied, and the source " +
                "reports only that authentication failed");

            Assert.IsTrue(TryReadContent("pass word\n", out var internalSpace, out _), "the value is not empty");
            Assert.AreEqual("pass word", internalSpace, "an internal space is ordinary in a real password");

            Assert.IsTrue(TryReadContent("pw  \n", out var trailing, out _), "the value is not empty");
            Assert.AreEqual("pw  ", trailing,
                "a trailing space is the one nobody can see in a field, so trimming it makes the value look " +
                "right and the run fail");
        }

        [TestMethod]
        public void AnEmptyCredentialValue_IsAFailure_NeverNoCredential()
        {
            foreach (var content in new[] { String.Empty, "\n", "\r\n" })
            {
                Assert.IsFalse(TryReadContent(content, out var value, out var failure),
                    "a form submitted before the paste must fail the job: read as 'no credential' the run reads " +
                    "what the source shows the public, declares that complete, and withdraws every claim the " +
                    "instance ever made");
                Assert.IsNull(value, "a failed read must hand back no value, or the run authenticates with nothing");
                Assert.IsFalse(String.IsNullOrWhiteSpace(failure),
                    "the failure must say why, because an unexplained credential failure is indistinguishable " +
                    "from a wrong password and sends the operator to the wrong system");
            }
        }

        [TestMethod]
        public void AWhitespaceOnlyCredentialValue_IsAFailure_ForTheSameReason()
        {
            foreach (var content in new[] { "   ", "\t", " \n", "\t \r\n" })
            {
                Assert.IsFalse(TryReadContent(content, out var value, out _),
                    "whitespace is what a half-filled field holds, and treating it as a credential produces a " +
                    "complete snapshot of what the source shows the public");
                Assert.IsNull(value, "a failed read must hand back no value");
            }
        }

        // ------------------------------------------------------------------------------------------------
        // CredentialResolver and CredentialLease.
        // ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void AProviderNeedingNoCredential_GetsALeaseThatIsEmptyAndHasNoFingerprint()
        {
            var active = new ActiveCredentials();
            var resolver = new CredentialResolver(active);

            using var fromNull = resolver.Resolve(null);
            using var fromEmpty = resolver.Resolve(
                new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase));

            Assert.IsTrue(fromNull.IsEmpty, "a provider needing no credential must get a lease holding nothing");
            Assert.IsTrue(fromEmpty.IsEmpty, "an empty credential map is the same statement as none at all");
            Assert.IsNull(fromNull.Fingerprint(),
                "a fingerprint over nothing must be null and not a hash of the empty string, or every " +
                "uncredentialed run reports one identical fingerprint that reads like a credential nobody rotated");
            Assert.IsTrue(active.IsEmpty,
                "an uncredentialed run must hold nothing, so the process's common state stays a filter with " +
                "nothing to do rather than substituting a value no run is using");
        }

        [TestMethod]
        public void EmptyIsAFactory_SoDisposingOneLeaseDoesNotEndAnother()
        {
            var first = CredentialLease.Empty();
            var second = CredentialLease.Empty();

            Assert.IsFalse(ReferenceEquals(first, second),
                "Empty() must hand back a NEW lease every time: one caller putting a static lease in a using " +
                "would end it permanently for every uncredentialed provider afterwards, and each of those runs " +
                "would then fail on a lease somebody else disposed");

            first.Dispose();

            Assert.IsTrue(first.Ended, "the disposed lease has ended");
            Assert.IsFalse(second.Ended,
                "the second lease must survive the first one's disposal, or one provider's using block ends " +
                "credential handling for the whole process");
            Assert.IsFalse(second.TryGet("password", out _),
                "the surviving lease must still ANSWER rather than throw, because a live lease that holds " +
                "nothing is exactly the uncredentialed case");

            second.Dispose();
        }

        [TestMethod]
        public void ACredentialSettingCarryingNothing_IsRefused_RatherThanTreatedAsNoCredential()
        {
            var resolver = new CredentialResolver(new ActiveCredentials());

            foreach (var supplied in new String[] { null, String.Empty, "   " })
            {
                var ex = Assert.ThrowsException<CredentialUnavailableException>(
                    () => resolver.Resolve(Supplied(("password", supplied))),
                    "a credential setting whose value is blank is a job that was half filled in, and running it " +
                    "anyway reads the source unauthenticated and declares that view complete");
                StringAssert.Contains(ex.Message, "password",
                    "the refusal must name the setting that was left empty, or the caller cannot fix the job");
            }
        }

        [TestMethod]
        public void ACredentialSettingKey_IsFoundWhateverCaseTheJobSpeltItIn()
        {
            var active = new ActiveCredentials();
            using var lease = new CredentialResolver(active).Resolve(Supplied(("Password", Secret)));

            Assert.AreEqual(Secret, lease.Require("password"),
                "a job arrives as JSON and deserialising yields an ordinal comparer whatever the initialiser " +
                "says, so 'Password' would otherwise slip past a provider asking for 'password' and the run " +
                "would authenticate with nothing while the value sits in the lease");
            Assert.IsTrue(lease.TryGet("PASSWORD", out var shouted), "the same folding applies to TryGet");
            Assert.AreEqual(Secret, shouted, "one credential, however the key is spelt");
        }

        [TestMethod]
        public void AfterTheRunEnds_TryGetAndRequireThrow_SoAProviderThatKeptTheContextFindsOut()
        {
            var active = new ActiveCredentials();
            var lease = new CredentialResolver(active).Resolve(Supplied(("password", Secret)));

            Assert.AreEqual(Secret, lease.Require("password"), "the value is readable while the run lasts");

            lease.Dispose();

            Assert.IsTrue(lease.Ended, "the lease must know the run is over");
            Assert.ThrowsException<InvalidOperationException>(() => { lease.TryGet("password", out _); },
                "a provider that squirrelled the context away must fail LOUDLY: quietly answering would let it " +
                "authenticate with a password the operator has since rotated, and the failure would surface as " +
                "a source rejection nothing in the graph explains");
            Assert.ThrowsException<InvalidOperationException>(() => lease.Require("password"),
                "the same refusal on the required path, or the loud seam is only the one nobody uses");
            Assert.IsTrue(active.IsEmpty,
                "the value must stop being held when the run ends, because a value held forever is a redaction " +
                "set that grows for the life of the process");
        }

        [TestMethod]
        public void ACredentialSuppliedWithTheJob_IsHeldForTheRunAndForgottenAfterIt()
        {
            var active = new ActiveCredentials();

            using (var lease = new CredentialResolver(active).Resolve(Supplied(("password", Secret))))
            {
                Assert.AreEqual(Secret, lease.Require("password"),
                    "the credential the caller supplied must reach the provider, which is the whole of the " +
                    "credential path now that there is nowhere else one can come from");
                Assert.IsFalse(lease.IsEmpty,
                    "the lease must count as credentialed, because that is what turns on the host guard and the " +
                    "no-plain-http rule for the outbound leg");
                Assert.IsFalse(active.IsEmpty,
                    "the value must be HELD while the run lasts: redaction substitutes against this set, so a " +
                    "supplied credential that never entered it would be printable in every log line");
                Assert.IsNotNull(lease.Fingerprint(),
                    "a supplied credential is still fingerprinted, so a caller who pasted a stale value can see " +
                    "the report change when they paste the new one");
            }

            Assert.IsTrue(active.IsEmpty,
                "and it must be FORGOTTEN when the run ends. This is the whole contract of supplying a value " +
                "with a job: no cache, no reuse by the next job, nothing to rotate");
        }

        [TestMethod]
        public void ARefusedSuppliedCredential_IsNotQuotedInTheFailure_BecauseThatMessageIsReported()
        {
            var resolver = new CredentialResolver(new ActiveCredentials());

            // A value that fails the content rules has NOT entered the lease, so redaction cannot cover it.
            // Anything the message quoted would travel out on the report in the clear.
            var ex = Assert.ThrowsException<CredentialUnavailableException>(
                () => resolver.Resolve(Supplied(("password", "  \n"))));

            Assert.IsFalse(ex.Message.Contains("  \n", StringComparison.Ordinal),
                "the message may say WHY a supplied value was refused and must never quote the value: it is " +
                "reported to the caller and logged, and a value rejected before the lease is a value redaction " +
                "knows nothing about");
        }

        [TestMethod]
        public void RequireForASettingNoCredentialWasSuppliedFor_NamesTheSetting()
        {
            var active = new ActiveCredentials();
            using var lease = new CredentialResolver(active).Resolve(Supplied(("password", Secret)));

            var ex = Assert.ThrowsException<InvalidOperationException>(() => lease.Require("token"),
                "a provider asking for a credential setting the job never filled in must be told, not handed " +
                "an empty string it would send to the source as a password");
            StringAssert.Contains(ex.Message, "token", "the failure must name the setting the provider asked for");
        }

        [TestMethod]
        public void AFingerprintOverTheSameValues_Agrees_AndChangesWhenAValueIsRotated()
        {
            var active = new ActiveCredentials();

            String first;
            using (var lease = CredentialLease.For(Map(("password", Secret)), active))
            {
                first = lease.Fingerprint();
            }

            String again;
            using (var lease = CredentialLease.For(Map(("password", Secret)), active))
            {
                again = lease.Fingerprint();
            }

            String rotated;
            using (var lease = CredentialLease.For(Map(("password", Secret + "-rotated")), active))
            {
                rotated = lease.Fingerprint();
            }

            Assert.IsFalse(String.IsNullOrEmpty(first), "a run that held a credential must report a fingerprint");
            Assert.AreEqual(first, again,
                "two runs over the same credential must report the same fingerprint, or the value is noise and " +
                "an operator cannot use it to tell a rotation from a run");
            Assert.AreNotEqual(first, rotated,
                "a caller who pasted a stale value must see the report change once they paste the new one: two " +
                "failures under one identical fingerprint say the value never reached this runtime, which is " +
                "a different problem from a value the source rejects");
        }

        [TestMethod]
        public void AFingerprint_DoesNotDependOnTheOrderTheCredentialsWereListedIn()
        {
            var active = new ActiveCredentials();

            String oneWay;
            using (var lease = CredentialLease.For(Map(("password", Secret), ("token", "t0ken")), active))
            {
                oneWay = lease.Fingerprint();
            }

            String otherWay;
            using (var lease = CredentialLease.For(Map(("token", "t0ken"), ("password", Secret)), active))
            {
                otherWay = lease.Fingerprint();
            }

            Assert.AreEqual(oneWay, otherWay,
                "a job's credential map is JSON and its order is whatever the caller typed, so a fingerprint " +
                "that follows dictionary order changes without any credential changing and reports a rotation " +
                "that never happened");
        }

        [TestMethod]
        public void AFingerprint_FoldsTheCaseOfTheSettingKey_AndNotOfTheValue()
        {
            var active = new ActiveCredentials();

            String lower;
            using (var lease = CredentialLease.For(Map(("password", Secret)), active))
            {
                lower = lease.Fingerprint();
            }

            String upper;
            using (var lease = CredentialLease.For(Map(("PASSWORD", Secret)), active))
            {
                upper = lease.Fingerprint();
            }

            String valueCase;
            using (var lease = CredentialLease.For(Map(("password", Secret.ToUpperInvariant())), active))
            {
                valueCase = lease.Fingerprint();
            }

            Assert.AreEqual(lower, upper,
                "the setting key is folded everywhere else, so a fingerprint that follows its case reports a " +
                "rotation because the caller pressed shift");
            Assert.AreNotEqual(lower, valueCase,
                "the VALUE is a password and its case is significant, so folding it would hide a real rotation " +
                "that only changed capitalisation");
        }

        [TestMethod]
        public void AFingerprint_SeparatesKeyFromValue_SoTwoDifferentSetsCannotHashAlike()
        {
            var active = new ActiveCredentials();

            String split;
            using (var lease = CredentialLease.For(Map(("ab", "c")), active))
            {
                split = lease.Fingerprint();
            }

            String shifted;
            using (var lease = CredentialLease.For(Map(("a", "bc")), active))
            {
                shifted = lease.Fingerprint();
            }

            Assert.AreNotEqual(split, shifted,
                "concatenating keys and values without a separator lets two different credential sets hash " +
                "alike, and the fingerprint's whole job is to differ when what the run held differs");
        }

        [TestMethod]
        public void AFingerprint_IsNotTheCredential()
        {
            var active = new ActiveCredentials();
            using var lease = CredentialLease.For(Map(("password", Secret)), active);

            var fingerprint = lease.Fingerprint();

            Assert.AreNotEqual(Secret, fingerprint, "the fingerprint travels on the job report, which the caller reads");
            Assert.IsFalse(fingerprint.Contains(Secret, StringComparison.Ordinal),
                "the report is the one place a credential must never appear, so a fingerprint carrying any part " +
                "of the value would hand somebody else's password to whoever submitted the job");
        }

        // ------------------------------------------------------------------------------------------------
        // ActiveCredentials: what runs are HOLDING, counted by value.
        // ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void TwoRunsHoldingTheSameValue_KeepItRedactable_UntilBothRelease()
        {
            var active = new ActiveCredentials();
            var one = CredentialLease.For(Map(("password", Secret)), active);
            var two = CredentialLease.For(Map(("password", Secret)), active);

            Assert.IsTrue(active.Snapshot().Contains(Secret), "both runs hold the value while they are in flight");

            one.Dispose();

            Assert.IsTrue(active.Snapshot().Contains(Secret),
                "two instances can be configured against the same credential, so per-run counting would switch " +
                "the other run's redaction OFF the moment the first run completed and the still-running job " +
                "would log its own password in clear");

            two.Dispose();

            Assert.IsTrue(active.IsEmpty,
                "with no run in flight the set must be empty, so the process's common state is a filter with " +
                "nothing to do rather than a value held for the life of the process");
        }

        [TestMethod]
        public void TheSnapshotIsLongestFirst_SoAShortCredentialCannotLeaveTheLongersTail()
        {
            var active = new ActiveCredentials();
            active.Hold("super");
            active.Hold("supersecret");

            Assert.AreEqual("supersecret", active.Snapshot()[0],
                "substituting the short value first turns 'supersecret' into a placeholder followed by 'secret', " +
                "which leaves the longer credential's tail in the line for anybody reading the log");

            var ties = new ActiveCredentials();
            ties.Hold("bbb");
            ties.Hold("aaa");

            Assert.AreEqual("aaa", ties.Snapshot()[0],
                "equal-length values are ordered ordinally so redaction does not depend on dictionary order, " +
                "which would make a leak reproduce on one run and not the next");
        }

        [TestMethod]
        public void WithNothingHeld_TheSetIsEmpty_AndABlankValueIsNeverHeld()
        {
            var active = new ActiveCredentials();

            Assert.IsTrue(active.IsEmpty, "a fresh process holds nothing");
            Assert.AreEqual(0, active.Snapshot().Count, "an empty set is what lets the log path skip work entirely");

            active.Hold(null);
            active.Hold(String.Empty);

            Assert.IsTrue(active.IsEmpty,
                "a blank value must never enter the substitution set: it matches every line, and redaction " +
                "would rewrite every log message in the process into placeholders");
        }

        [TestMethod]
        public void ReleasingAValueNothingHolds_IsANoOp_SoADoubleReleaseCannotUnholdAnotherRun()
        {
            var active = new ActiveCredentials();
            active.Hold(Secret);

            active.Release("never-held");
            Assert.IsTrue(active.Snapshot().Contains(Secret),
                "releasing something nothing holds must not disturb what is held, or one run's cleanup ends " +
                "another run's redaction");

            active.Release(Secret);
            active.Release(Secret);

            Assert.IsTrue(active.IsEmpty,
                "a release past zero must stay at zero rather than going negative, or the count never returns " +
                "to empty and the value stays in the substitution set for the life of the process");
        }

        // ------------------------------------------------------------------------------------------------
        // RedactingLoggerProvider: the coverage list is the message.
        // ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void APlainMessageQuotingTheCredential_ReachesNoSink()
        {
            var sink = new CapturingLoggerProvider();
            var active = new ActiveCredentials();
            using var factory = RedactingFactory(sink, active);
            active.Hold(Secret);

            factory.CreateLogger("provider").LogInformation("the console refused the password " + Secret);

            AssertNothingHoldsTheSecret(sink,
                "nothing in the runtime logs a credential on purpose, but a provider is written by somebody " +
                "else and one careless line in an HTTP failure path writes a network-admin password into the " +
                "container log");
            Assert.IsTrue(sink.Lines.Contains(
                    "the console refused the password " + RedactingLoggerProvider.Placeholder),
                "the line itself must still arrive, with the value substituted: a filter that dropped the line " +
                "would cost the operator the diagnostic that says the console refused at all");
        }

        [TestMethod]
        public void AStructuredTemplateValue_IsScrubbedInTheStateAndNotOnlyInTheMessage()
        {
            var sink = new CapturingLoggerProvider();
            var active = new ActiveCredentials();
            using var factory = RedactingFactory(sink, active);
            active.Hold(Secret);

            factory.CreateLogger("provider").LogInformation("the console refused {Key}", Secret);

            AssertNothingHoldsTheSecret(sink,
                "a sink may serialise the STRUCTURED STATE rather than the rendered message, so scrubbing only " +
                "the string leaves the credential in that sink's JSON where nobody thinks to look");
            Assert.IsTrue(sink.Lines.Contains("Key=" + RedactingLoggerProvider.Placeholder),
                "the STRUCTURED PAIR must reach the sink with the value substituted: a run whose message alone " +
                "was scrubbed leaves the credential in the JSON of every sink that serialises the state, and " +
                "the pair vanishing entirely would cost that sink the field it queries on");
        }

        [TestMethod]
        public void ANonStringValueWhoseRenderingIsTheCredential_IsScrubbedToo()
        {
            var sink = new CapturingLoggerProvider();
            var active = new ActiveCredentials();
            using var factory = RedactingFactory(sink, active);
            active.Hold(Secret);

            factory.CreateLogger("provider").LogInformation("the console refused {Key}", new SecretShaped(Secret));

            AssertNothingHoldsTheSecret(sink,
                "matching on values means matching on what a value RENDERS AS: a credential passed as an object " +
                "whose ToString is the password would otherwise reach a structured sink untouched, and " +
                "pattern-matching cannot help because a credential does not look like anything");
            Assert.IsTrue(sink.Lines.Contains("Key=" + RedactingLoggerProvider.Placeholder),
                "the non-string pair must arrive with its rendering substituted, which is the only form in which " +
                "a sink that serialises the state can be covered at all");
        }

        [TestMethod]
        public void ALogScopeQuotingTheCredential_ReachesNoSink()
        {
            var sink = new CapturingLoggerProvider();
            var active = new ActiveCredentials();
            using var factory = RedactingFactory(sink, active);
            active.Hold(Secret);

            var logger = factory.CreateLogger("provider");
            using (logger.BeginScope("run against {Address}", "https://console.example/?password=" + Secret))
            {
                logger.LogInformation("observing");
            }

            AssertNothingHoldsTheSecret(sink,
                "a scope is attached to every line inside it and most sinks render it in full, so a credential " +
                "in a scope leaks once per line rather than once");
            Assert.IsTrue(sink.Lines.Contains("run against https://console.example/?password=" +
                    RedactingLoggerProvider.Placeholder),
                "the scope must still REACH the sink, scrubbed: a filter that swallowed scopes would take the " +
                "run's correlation off every line the operator has to read the failure from");
        }

        [TestMethod]
        public void AnExceptionWhoseMessageQuotesTheCredential_ReachesNoSink()
        {
            var sink = new CapturingLoggerProvider();
            var active = new ActiveCredentials();
            using var factory = RedactingFactory(sink, active);
            active.Hold(Secret);

            var failure = new InvalidOperationException(
                "GET https://console.example/api/login?password=" + Secret + " failed");
            factory.CreateLogger("provider").LogError(failure, "the source did not answer");

            AssertNothingHoldsTheSecret(sink,
                "a provider whose exception message quotes the request it sent is ordinary, and most sinks " +
                "render an exception in full, so an unscrubbed exception object hands the credential to the " +
                "container log at the same moment the report hands it to the caller");
            Assert.IsTrue(sink.Lines.Any(line => line.Contains("InvalidOperationException", StringComparison.Ordinal)),
                "the original exception TYPE must survive the substitution, or the line no longer says what " +
                "went wrong and the redaction has cost the operator the diagnostic");
        }

        [TestMethod]
        public void AShortCredentialThatIsASubstringOfALongerOne_LeavesNoTailInTheLine()
        {
            var sink = new CapturingLoggerProvider();
            var active = new ActiveCredentials();
            using var factory = RedactingFactory(sink, active);
            active.Hold("super");
            active.Hold("supersecret");

            factory.CreateLogger("provider").LogInformation("the console refused {Key}", "supersecret");

            foreach (var line in sink.Lines)
            {
                Assert.IsFalse(line.Contains("secret", StringComparison.Ordinal),
                    "substituting the shorter value first leaves the longer credential's tail in the line, so " +
                    "two runs whose credentials share a prefix defeat redaction for the longer one. Line: " + line);
            }
        }

        [TestMethod]
        public void WithNothingHeld_TheLinePassesThroughUnchanged()
        {
            var sink = new CapturingLoggerProvider();
            var active = new ActiveCredentials();
            using var factory = RedactingFactory(sink, active);

            var failure = new InvalidOperationException("the console did not answer");
            factory.CreateLogger("provider").LogError(failure, "the source did not answer for {Host}",
                "console.example");

            var lines = sink.Lines;

            Assert.IsTrue(lines.Contains("the source did not answer for console.example"),
                "with no run in flight the filter has nothing to do, so a line must reach the sink exactly as " +
                "it was written rather than rebuilt");
            Assert.IsTrue(lines.Any(line => line.Contains("Host=console.example", StringComparison.Ordinal)),
                "the structured state must pass through with its own values, or every uncredentialed line pays " +
                "for a rewrite nothing asked for");
            Assert.IsFalse(lines.Any(line => line.Contains("RedactedException", StringComparison.Ordinal)),
                "the EXCEPTION OBJECT must pass through untouched when nothing is held: replacing it costs the " +
                "sink the stack trace and the inner exceptions, which is the whole of what an operator " +
                "diagnoses a source failure from");
        }

        [TestMethod]
        public void WrapRegisteredProviders_RewritesAProviderRegisteredBeforeIt_SoTheExporterRunsBehindTheFilter()
        {
            var sink = new CapturingLoggerProvider();
            var active = new ActiveCredentials();

            var services = new ServiceCollection();
            services.AddSingleton(active);
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace));

            // Registered BEFORE the wrap, exactly as the OTLP log exporter is.
            services.AddSingleton<ILoggerProvider>(sink);

            RedactingLoggerProvider.WrapRegisteredProviders(services);

            using var provider = services.BuildServiceProvider();
            active.Hold(Secret);

            provider.GetRequiredService<ILoggerFactory>().CreateLogger("exporter")
                .LogWarning("the console refused {Key}", Secret);

            Assert.IsTrue(sink.Lines.Length > 0,
                "the wrap must REWRITE the existing registrations rather than clear them, or the sink an " +
                "operator configured goes silent and the only log left is the one this code knows about");
            AssertNothingHoldsTheSecret(sink,
                "installed before the exporter, the collector would receive exactly what the console was " +
                "spared, and a credential in a telemetry backend is one nobody can delete");
        }

        // ------------------------------------------------------------------------------------------------
        // CredentialHostGuard, enforced ON THE WAY OUT, and the client the factory builds.
        // ------------------------------------------------------------------------------------------------

        [TestMethod]
        public async Task ACredentialedRunToAHostNotOnTheList_IsRefused_AndTheRefusalNamesTheConfigurationKey()
        {
            var stub = new StubHandler();
            using var client = GuardedClient(true, stub, "console.example");

            var ex = await Assert.ThrowsExceptionAsync<CredentialHostRefusedException>(
                () => client.GetAsync("https://evil.example/api"),
                "a source address arrives in the job's settings from whoever can reach the API, so without this " +
                "a caller who edits a base URL aims somebody's admin password at a host of their choosing and " +
                "the runtime authenticates to it");
            StringAssert.Contains(ex.Message, "Integrations:Credentials:AllowedHosts",
                "the refusal must name the CONFIGURATION KEY, or the operator who has to add the host cannot " +
                "find where the list lives");
            Assert.AreEqual(0, stub.Sent,
                "the refusal must happen on the way OUT and before anything is sent: a check that fires after " +
                "the request has left has already delivered the credential");
        }

        [TestMethod]
        public async Task ACredentialedRunToAHostOnTheList_IsAllowed()
        {
            var stub = new StubHandler();
            using var client = GuardedClient(true, stub, "console.example", "inverter.example");

            using var response = await client.GetAsync("https://console.example/api");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                "a guard that refuses the hosts the operator named would leave the feature unable to reach the " +
                "sources it exists for, and the list would be turned off rather than corrected");
            Assert.AreEqual(1, stub.Sent, "the request must actually reach the inner handler");
        }

        [TestMethod]
        public async Task PlainHttpIsRefusedForACredentialedRun_EvenToAnAllowedHost()
        {
            var stub = new StubHandler();
            using var client = GuardedClient(true, stub, "console.example");

            var ex = await Assert.ThrowsExceptionAsync<CredentialHostRefusedException>(
                () => client.GetAsync("http://console.example/api"),
                "a caller who can edit a base URL would otherwise DOWNGRADE an allowed host and read the " +
                "credential off the wire, with the host list still looking satisfied");
            StringAssert.Contains(ex.Message, "http",
                "the refusal must say the scheme is the problem, or the operator adds the host again and it " +
                "still fails");
            Assert.AreEqual(0, stub.Sent, "nothing may be sent in clear");
        }

        [TestMethod]
        public async Task LoopbackOverPlainHttp_IsAllowed_ByAddressAndByName_BecauseLoopbackHasNoWire()
        {
            var stub = new StubHandler();
            using var client = GuardedClient(true, stub, "127.0.0.1", "localhost");

            using var byAddress = await client.GetAsync("http://127.0.0.1:8080/api");
            using var byName = await client.GetAsync("http://localhost:8080/api");

            Assert.AreEqual(HttpStatusCode.OK, byAddress.StatusCode,
                "a source reached over loopback has no wire to read the credential off, and refusing it would " +
                "make a sidecar in the same pod unreachable and push the operator to disable the guard");
            Assert.AreEqual(HttpStatusCode.OK, byName.StatusCode,
                "the exception is about loopback and not about a spelling, so the NAME must be recognised too");

            var elsewhere = new StubHandler();
            using var restricted = GuardedClient(true, elsewhere, "console.example");

            await Assert.ThrowsExceptionAsync<CredentialHostRefusedException>(
                () => restricted.GetAsync("http://127.0.0.1:8080/api"),
                "the loopback exception covers the SCHEME only: a non-empty allowed list still decides which " +
                "hosts a credential may go to, or a caller pointing a job at a local port bypasses the list");
        }

        [TestMethod]
        public async Task ARunHoldingNoCredential_IsNotRestricted_ByHostOrByScheme()
        {
            var stub = new StubHandler();
            using var client = GuardedClient(false, stub, "console.example");

            using var response = await client.GetAsync("http://anything.example/api");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                "an unexpected host learns nothing from an uncredentialed run that it could not read from the " +
                "source itself, and restricting one would make the host list a general egress policy the " +
                "operator never asked for");
            Assert.AreEqual(1, stub.Sent, "the request must reach the inner handler");
        }

        [TestMethod]
        public async Task AnEmptyAllowedList_MeansNoHostRestriction_ButStillNoPlainHttp()
        {
            var stub = new StubHandler();
            using var client = ProviderHttpFactory.Wrap(ImmutableHashSet<String>.Empty, true, stub);

            using var response = await client.GetAsync("https://whatever.example/api");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                "an empty list means no restriction, and the runtime warns at startup instead: a list that " +
                "refused everything when unset would make the first credentialed run fail with no way to tell " +
                "an unset list from a wrong one");

            var second = new StubHandler();
            using var downgrade = ProviderHttpFactory.Wrap(ImmutableHashSet<String>.Empty, true, second);

            await Assert.ThrowsExceptionAsync<CredentialHostRefusedException>(
                () => downgrade.GetAsync("http://whatever.example/api"),
                "an unset host list must not also switch off the plain-http refusal: the two controls answer " +
                "different questions, and reading a credential off the wire needs no host to be named");
        }

        [TestMethod]
        public async Task TheAllowedHostComparison_FoldsCase_FromConfigurationThroughToTheRequest()
        {
            var stub = new StubHandler();
            var configured = new CredentialsOptions { AllowedHosts = "Console.Example, inverter.example" };
            using var client = ProviderHttpFactory.Wrap(configured.AllowedHostSet(), true, stub);

            using var response = await client.GetAsync("https://CONSOLE.EXAMPLE/api");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                "a host name is case-insensitive, so a list an operator typed with capitals must still match " +
                "the address a job supplies, or the guard refuses the very host it was told to allow and the " +
                "list gets emptied to make the run work");
        }

        [TestMethod]
        public void Refuses_AgreesWithWhatSendingWouldDo_SoAProviderCanFailEarly()
        {
            using var guard = new CredentialHostGuard(Hosts("console.example"), true, new StubHandler());

            Assert.IsTrue(guard.Refuses(new Uri("https://evil.example/api"), out var reason),
                "a provider that wants to check an address before building a request must get the same verdict " +
                "the send would give, or it reports a source failure for what is a configuration refusal");
            StringAssert.Contains(reason, "Integrations:Credentials:AllowedHosts",
                "the early verdict must carry the same reason, naming the key the operator has to edit");
            Assert.IsFalse(guard.Refuses(new Uri("https://console.example/api"), out _),
                "an allowed host must not be refused early either, or a provider gives up before it starts");
            Assert.IsTrue(guard.Refuses(null, out var noAddress),
                "no address at all must be refused rather than treated as harmless, because a relative address " +
                "is resolved against a base the guard never saw");
            StringAssert.Contains(noAddress, "absolute", "the reason must say what is wrong with the address");
            Assert.IsTrue(guard.Refuses(new Uri("/api", UriKind.Relative), out _),
                "a relative address hides the host the credential would go to");
        }

        [TestMethod]
        public void ARunHoldingNoCredential_IsNotRefusedEarlyEither()
        {
            using var guard = new CredentialHostGuard(Hosts("console.example"), false, new StubHandler());

            Assert.IsFalse(guard.Refuses(new Uri("http://anything.example/api"), out _),
                "the guard is about where a CREDENTIAL may go, so an uncredentialed run must see no restriction " +
                "in the early check either, or a provider reports a refusal the send would never have made");
        }

        [TestMethod]
        public void TheClientTheFactoryBuilds_PutsTheGuardOnTop_AndFollowsNoRedirectUnderneath()
        {
            var factory = new ProviderHttpFactory(Options.Create(new IntegrationsOptions()));
            using var client = factory.Create(true);

            var outermost = OutermostHandler(client);

            Assert.IsInstanceOfType(outermost, typeof(CredentialHostGuard),
                "the guard must be the OUTERMOST handler: anything above it sends without being asked, and the " +
                "point of enforcing on the way out is that every request passes the check");

            var inner = ((DelegatingHandler)outermost).InnerHandler as SocketsHttpHandler;

            Assert.IsNotNull(inner,
                "the handler that actually sends must be reachable from the guard, or this test cannot see " +
                "whether redirects are followed and the rule below is unenforced");
            Assert.IsFalse(inner.AllowAutoRedirect,
                "redirects would be followed by the INNER handler, BELOW the guard, so a source answering 302 " +
                "to another host would walk a credential off the allowed list with nothing refusing it. The " +
                "platform default is to follow, so this is a setting that has to be there rather than one that " +
                "happens to be");
        }

        // ------------------------------------------------------------------------------------------------
        // Fixtures.
        // ------------------------------------------------------------------------------------------------

        /// <summary>
        ///   Drives the credential CONTENT rules through the resolver, which is the one place they live and
        ///   the only way a credential enters the runtime. A refusal arrives as an exception, so it is turned
        ///   back into the try-shape the rules are stated in.
        /// </summary>
        private static Boolean TryReadContent(String content, out String value, out String failure)
        {
            var active = new ActiveCredentials();

            try
            {
                using var lease = new CredentialResolver(active).Resolve(Supplied(("password", content)));
                value = lease.Require("password");
                failure = null;
                return true;
            }
            catch (CredentialUnavailableException ex)
            {
                value = null;
                failure = ex.Message;
                return false;
            }
        }

        /// <summary>The credential VALUES a job carries, keyed by credential setting.</summary>
        private static IReadOnlyDictionary<String, String> Supplied(
            params (String SettingKey, String Value)[] pairs)
        {
            var supplied = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in pairs)
            {
                supplied[pair.SettingKey] = pair.Value;
            }

            return supplied;
        }

        /// <summary>The values a lease holds, keyed by credential setting key, in the order given.</summary>
        private static IDictionary<String, String> Map(params (String SettingKey, String Value)[] pairs)
        {
            var values = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in pairs)
            {
                values[pair.SettingKey] = pair.Value;
            }

            return values;
        }

        private static ILoggerFactory RedactingFactory(CapturingLoggerProvider sink, ActiveCredentials active)
        {
            return LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Trace);
                builder.AddProvider(new RedactingLoggerProvider(sink, active));
            });
        }

        private static void AssertNothingHoldsTheSecret(CapturingLoggerProvider sink, String consequence)
        {
            var lines = sink.Lines;

            Assert.IsTrue(lines.Length > 0,
                "nothing reached the sink at all, so this test proved nothing about redaction");

            foreach (var line in lines)
            {
                Assert.IsFalse(line.Contains(Secret, StringComparison.Ordinal),
                    consequence + " Line: " + line);
            }
        }

        private static ImmutableHashSet<String> Hosts(params String[] hosts)
        {
            return hosts.ToImmutableHashSet(StringComparer.Ordinal);
        }

        private static HttpClient GuardedClient(Boolean holdsCredential, StubHandler stub, params String[] allowedHosts)
        {
            return ProviderHttpFactory.Wrap(Hosts(allowedHosts), holdsCredential, stub);
        }

        /// <summary>
        ///   The handler the client would send through, which is the only way to see that the guard sits on top
        ///   and that the handler underneath follows no redirect.
        /// </summary>
        private static HttpMessageHandler OutermostHandler(HttpClient client)
        {
            for (var type = client.GetType(); type != null; type = type.BaseType)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public |
                                                     BindingFlags.NonPublic))
                {
                    if (typeof(HttpMessageHandler).IsAssignableFrom(field.FieldType))
                    {
                        return field.GetValue(client) as HttpMessageHandler;
                    }
                }
            }

            Assert.Fail("the client's handler chain could not be reached, so the redirect and guard-order rules " +
                        "are unenforced and this test needs a new way to look at it");
            return null;
        }

        /// <summary>A credential passed as something other than a string, whose rendering is the value.</summary>
        private sealed class SecretShaped
        {
            private readonly String _text;

            public SecretShaped(String text)
            {
                _text = text;
            }

            public override String ToString()
            {
                return _text;
            }
        }

        /// <summary>Stands in for whatever actually sends, and counts what got past the guard.</summary>
        private sealed class StubHandler : HttpMessageHandler
        {
            /// <summary>How many requests reached the handler below the guard.</summary>
            public Int32 Sent { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Sent++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
        }
    }
}
