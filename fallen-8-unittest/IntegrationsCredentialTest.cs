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
    ///   The credential path of the integrations runtime (feature integrations, spec sections 3 and 4): which
    ///   file a name may mean, what a credential file's bytes mean, how long a run may hold the value, what
    ///   redaction covers, and where a run holding one may send it.
    ///
    ///   <para>Every assertion here stands in for a failure that is invisible from the graph. The three worst
    ///   ones, and why each has its own test: a truncated credential file read as "no credential" produces a run
    ///   that reads what the source shows the public, declares it complete, and withdraws every claim the
    ///   instance ever made; a credential value reaching any log sink hands somebody else's network-admin
    ///   password to whoever reads the container log; and a host check that is not enforced on the way out lets
    ///   whoever can edit a base URL aim that password at a machine of their choosing.</para>
    /// </summary>
    [TestClass]
    public class IntegrationsCredentialTest
    {
        /// <summary>The credential mount the runtime defaults to, and the root the fixture store checks against.</summary>
        private const String Root = "/run/secrets";

        private const String CredentialName = "unifi-password";

        private const String Secret = "s3cr3t-console-password";

        /// <summary>A real read-only-in-production mount, so the directory store can be driven end to end.</summary>
        private String _mount;

        /// <summary>The sibling an operator leaves behind when they rotate by mounting a second directory.</summary>
        private String _retired;

        [TestInitialize]
        public void TestInitialize()
        {
            _mount = Path.Combine(Path.GetTempPath(), "f8i_cred_" + Guid.NewGuid().ToString("N"));
            _retired = _mount + "-old";
            Directory.CreateDirectory(_mount);
        }

        [TestCleanup]
        public void TestCleanup()
        {
            try { if (Directory.Exists(_mount)) Directory.Delete(_mount, true); } catch { }
            try { if (Directory.Exists(_retired)) Directory.Delete(_retired, true); } catch { }
        }

        // ------------------------------------------------------------------------------------------------
        // RootedNames.TryResolve - both halves are load-bearing, and either alone is historically the bug.
        // ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void APlainName_ResolvesToTheOneFileUnderTheConfiguredRoot()
        {
            var resolved = RootedNames.TryResolve(Root, CredentialName, "credential", out var path, out var failure);

            Assert.IsTrue(resolved,
                "a bare credential name is the ONLY thing a job may say, so refusing one leaves every " +
                "credentialed integration unable to read the mount at all: " + failure);
            Assert.IsNull(failure, "a name that resolved must carry no failure, or a caller logs a reason for a success");
            Assert.AreEqual(Path.Combine(Path.GetFullPath(Root), CredentialName), path,
                "the name must resolve to exactly one location inside the configured root: a resolution that " +
                "drifts elsewhere reads a file from the image instead of the operator's mount");
        }

        [TestMethod]
        public void ANameWithAForwardSlash_IsRefused_SoAJobCannotNameAPath()
        {
            Assert.IsFalse(RootedNames.TryResolve(Root, "../etc/shadow", "credential", out var path, out var failure),
                "a credential name arrives over the API from whoever can reach it, so a name that may contain a " +
                "path lets that caller read any file the container can and have it handed back in a report");
            Assert.IsNull(path, "a refused name must yield no path, or a caller opens what it was told to anyway");
            StringAssert.Contains(failure, "path separator",
                "the shape check must be the one that fires, because it is what refuses the name BEFORE the " +
                "platform gets a chance to normalise it into something a containment check accepts");
        }

        [TestMethod]
        public void ANameWithABackslash_IsRefused_BecauseTheOtherPlatformsSeparatorIsASeparatorToo()
        {
            Assert.IsFalse(RootedNames.TryResolve(Root, "sub\\shadow", "credential", out var path, out var failure),
                "the runtime's image is Linux and a developer's machine is not, so a name refused on one platform " +
                "and resolved on the other is a hole that only opens where nobody tests");
            Assert.IsNull(path, "a refused name must yield no path");
            StringAssert.Contains(failure, "path separator", "the refusal must say which rule the name broke");
        }

        [TestMethod]
        public void ANameContainingDotDot_IsRefused_BeforeThePlatformNormalisesIt()
        {
            Assert.IsFalse(RootedNames.TryResolve(Root, "..", "credential", out var path, out var failure),
                "the parent-directory segment is how a name leaves the mount, and a name that leaves the mount " +
                "can name the credential directory from the files mount and the other way round");
            Assert.IsNull(path, "a refused name must yield no path");
            StringAssert.Contains(failure, "may not contain '..'",
                "the SHAPE check must be what refuses it, and it must say so: leaving '..' to the containment " +
                "check makes the refusal depend on how the platform normalises the name, which is the half of " +
                "this primitive that historically let a name out of the mount");
        }

        [TestMethod]
        public void ARootedName_IsRefused_AndResolvesToNothing()
        {
            var rooted = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "steal.txt");

            Assert.IsFalse(RootedNames.TryResolve(Root, rooted, "credential", out var path, out _),
                "an absolute path as a name would make the configured root advisory, and the root is the only " +
                "thing that keeps a job from naming a file the operator never mounted as a credential");
            Assert.IsNull(path, "a refused name must yield no path");

            // The rooted form that carries NO separator, and so is refused by the rooted check alone. It only
            // exists where a drive-relative path does, which is the platform whose normalisation rules the shape
            // check is there for.
            if (Path.IsPathRooted("C:steal.txt"))
            {
                Assert.IsFalse(RootedNames.TryResolve(Root, "C:steal.txt", "credential", out var driveRelative,
                        out var failure),
                    "a drive-relative name is rooted while looking like a plain file name, so the platform " +
                    "resolves it against a directory the runtime never configured");
                Assert.IsNull(driveRelative, "a refused name must yield no path");
                StringAssert.Contains(failure, "rooted path",
                    "the ROOTED check must be what refuses it: relying on the containment check instead makes " +
                    "the verdict depend on where the process happens to be running from");
            }
        }

        [TestMethod]
        public void ANameWithACharacterAFileNameMayNotHave_IsRefused_ByTheShapeCheckAndNotByAccident()
        {
            Assert.IsFalse(RootedNames.TryResolve(Root, "pass\0word", "credential", out var path, out var failure),
                "an embedded null truncates the name at whatever layer reads it next, so the file actually " +
                "opened is not the file whose name was checked");
            Assert.IsNull(path, "a refused name must yield no path");
            StringAssert.Contains(failure, "character a file name may not have",
                "the INVALID-CHARACTER check must be what fires: leaving it to the platform's own exception " +
                "makes the refusal depend on which platform normalises the name, which is the bug this " +
                "primitive exists to remove");
        }

        [TestMethod]
        public void AnEmptyOrBlankName_IsRefused_RatherThanNamingTheDirectoryItself()
        {
            foreach (var name in new String[] { null, String.Empty, "   " })
            {
                Assert.IsFalse(RootedNames.TryResolve(Root, name, "credential", out var path, out var failure),
                    "an empty name would resolve to the mount DIRECTORY, and reading a directory is an error a " +
                    "reader would blame on the mount rather than on the job that named nothing");
                Assert.IsNull(path, "a refused name must yield no path");
                StringAssert.Contains(failure, "name is required",
                    "the refusal must say a name is missing, or whoever submitted the job goes looking for a " +
                    "broken mount instead of an empty field");
            }
        }

        [TestMethod]
        public void ABlankRoot_IsRefused_RatherThanResolvingAgainstTheWorkingDirectory()
        {
            foreach (var root in new String[] { null, String.Empty, "   " })
            {
                Assert.IsFalse(RootedNames.TryResolve(root, CredentialName, "credential", out var path, out var failure),
                    "with no configured directory a name would resolve against the process's working directory, " +
                    "so the runtime would read a file baked into the image and authenticate with whatever it says");
                Assert.IsNull(path, "a refused name must yield no path");
                StringAssert.Contains(failure, "directory is configured",
                    "the refusal must name the missing CONFIGURATION, because the fix is a mount and not a job field");
            }
        }

        [TestMethod]
        public void ASiblingDirectorySharingTheRootsCharacters_IsUnreachable()
        {
            // The case a prefix check alone would miss: /run/secrets-old starts with /run/secrets and is a
            // different directory, which is why the containment test compares against the root WITH a separator.
            var resolvedRoot = Path.GetFullPath(Root);
            var sibling = Path.GetFullPath(Root + "-old");

            Assert.IsTrue(sibling.StartsWith(resolvedRoot, StringComparison.Ordinal),
                "this test is only about anything if the sibling really does share the root's characters without " +
                "a separator, which is the shape a naive prefix check accepts");
            Assert.IsFalse(sibling.StartsWith(resolvedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                "the sibling must NOT be inside the root, or the fixture is not the case being tested");

            var name = sibling + Path.DirectorySeparatorChar + "steal.txt";

            Assert.IsFalse(RootedNames.TryResolve(Root, name, "credential", out var path, out _),
                "a name reaching a sibling directory whose name merely BEGINS with the root must be refused: an " +
                "operator who rotates by mounting /run/secrets-old next to /run/secrets would otherwise have " +
                "every retired credential readable by name from a job");
            Assert.IsNull(path, "a refused name must yield no path");
        }

        [TestMethod]
        public void ARootWrittenWithATrailingSeparator_ResolvesTheSameNameToTheSamePath()
        {
            Assert.IsTrue(RootedNames.TryResolve(Root, CredentialName, "credential", out var bare, out _),
                "the plain root must resolve, or the rest of this test proves nothing");
            Assert.IsTrue(RootedNames.TryResolve(Root + "/", CredentialName, "credential", out var slashed,
                    out var failure),
                "an operator writes the mount path in a compose file and may end it with a separator, so a root " +
                "spelled that way must not make every credential unreadable: " + failure);
            Assert.AreEqual(bare, slashed,
                "the two spellings of one directory must resolve to one file, or whether a credential is found " +
                "depends on a trailing character in configuration");
        }

        // ------------------------------------------------------------------------------------------------
        // Credential CONTENT rules, driven through FixtureCredentialStore because DirectoryCredentialStore
        // .TryAccept is internal to the runtime assembly; the fixture store routes through that same method.
        // ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void ExactlyOneTrailingLineEnding_IsStripped_SoPrintfEchoAndAProjectedSecretAgree()
        {
            Assert.IsTrue(TryReadContent("pw", out var bare, out _), "a file written with printf must be readable");
            Assert.AreEqual("pw", bare, "printf 'pw' > f writes no line ending, and the value is the file verbatim");

            Assert.IsTrue(TryReadContent("pw\n", out var unix, out _), "a file written with echo must be readable");
            Assert.AreEqual("pw", unix,
                "echo pw > f differs from printf by one byte, and keeping that byte produces an authentication " +
                "failure from somebody's controller with nothing in the report to explain it");

            Assert.IsTrue(TryReadContent("pw\r\n", out var windows, out _), "a CRLF file must be readable");
            Assert.AreEqual("pw", windows,
                "a credential file edited on Windows ends CRLF, and treating those two bytes as part of the " +
                "password sends the wrong value to the source");

            Assert.IsTrue(TryReadContent("pw\r", out var carriageReturn, out _), "a CR-only file must be readable");
            Assert.AreEqual("pw", carriageReturn,
                "a lone carriage return is a line ending too, and one left on the value is invisible in every " +
                "log line the operator would use to diagnose the rejection");
        }

        [TestMethod]
        public void ASecondTrailingLineEnding_IsPartOfTheCredential_BecauseContentIsOtherwiseVerbatim()
        {
            Assert.IsTrue(TryReadContent("pw\n\n", out var twoNewlines, out _), "the value is not empty");
            Assert.AreEqual("pw\n", twoNewlines,
                "only ONE trailing line ending is a file-format artefact; stripping more would silently rewrite " +
                "a credential whose real last character is a newline, and the source would reject it");

            Assert.IsTrue(TryReadContent("pw\r\n\r\n", out var twoCrLf, out _), "the value is not empty");
            Assert.AreEqual("pw\r\n", twoCrLf,
                "the same rule holds for CRLF: exactly one ending is removed, never all of them");
        }

        [TestMethod]
        public void SpacesAreUntouched_LeadingInternalAndTrailing_BecauseASpaceCanBePartOfAPassword()
        {
            Assert.IsTrue(TryReadContent("  pw\n", out var leading, out _), "the value is not empty");
            Assert.AreEqual("  pw", leading,
                "trimming a leading space would send a different password than the file holds, and the source " +
                "reports only that authentication failed");

            Assert.IsTrue(TryReadContent("pass word\n", out var internalSpace, out _), "the value is not empty");
            Assert.AreEqual("pass word", internalSpace, "an internal space is ordinary in a real password");

            Assert.IsTrue(TryReadContent("pw  \n", out var trailing, out _), "the value is not empty");
            Assert.AreEqual("pw  ", trailing,
                "a trailing space is the one an operator cannot see in an editor, so trimming it makes the file " +
                "look right and the run fail");
        }

        [TestMethod]
        public void AnEmptyCredentialFile_IsAFailure_NeverNoCredential()
        {
            foreach (var content in new[] { String.Empty, "\n", "\r\n" })
            {
                Assert.IsFalse(TryReadContent(content, out var value, out var failure),
                    "a rotation script that truncated a file must fail the job: read as 'no credential' the run " +
                    "reads what the source shows the public, declares that complete, and withdraws every claim " +
                    "the instance ever made");
                Assert.IsNull(value, "a failed read must hand back no value, or the run authenticates with nothing");
                Assert.IsFalse(String.IsNullOrWhiteSpace(failure),
                    "the failure must say why, because an unexplained credential failure is indistinguishable " +
                    "from a wrong password and sends the operator to the wrong system");
            }
        }

        [TestMethod]
        public void AWhitespaceOnlyCredentialFile_IsAFailure_ForTheSameReason()
        {
            foreach (var content in new[] { "   ", "\t", " \n", "\t \r\n" })
            {
                Assert.IsFalse(TryReadContent(content, out var value, out _),
                    "whitespace is what a half-written or projected-but-unpopulated file holds, and treating it " +
                    "as a credential produces a complete snapshot of what the source shows the public");
                Assert.IsNull(value, "a failed read must hand back no value");
            }
        }

        [TestMethod]
        public void ACredentialTheStoreDoesNotHave_Fails_WithAReasonAndNoValue()
        {
            var store = new FixtureCredentialStore(new Dictionary<String, String>(StringComparer.Ordinal));

            Assert.IsFalse(store.TryRead(CredentialName, out var value, out var failure),
                "a name nothing answers to is 'I could not look', which must never become 'there is nothing there'");
            Assert.IsNull(value, "a failed read must hand back no value");
            Assert.IsFalse(String.IsNullOrWhiteSpace(failure), "the failure must name what could not be read");
        }

        [TestMethod]
        public void ACredentialNameWithASeparator_IsRefusedByShape_BeforeTheStoreIsAskedForIt()
        {
            // The fixture DOES hold this key, so only the shape check can refuse it.
            var store = new FixtureCredentialStore(new Dictionary<String, String>(StringComparer.Ordinal)
            {
                { "sub/name", Secret },
            });

            Assert.IsFalse(store.TryRead("sub/name", out var value, out var failure),
                "the fixture store must route the name through the same primitive a real mount does, or the " +
                "conformance suite certifies a path-escape check the live path applies and the suite does not");
            Assert.IsNull(value, "a refused name must hand back no value");
            StringAssert.Contains(failure, "path separator", "the shape check must be what refused it");
        }

        // ------------------------------------------------------------------------------------------------
        // The real mount: one file per credential in a bind-mounted directory.
        // ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void TheMountedStore_ReadsTheFileTheNameNamesUnderTheConfiguredDirectory()
        {
            File.WriteAllText(Path.Combine(_mount, CredentialName), Secret + "\n");

            Assert.IsTrue(MountedStore().TryRead(CredentialName, out var value, out var failure),
                "the credential mount is the whole delivery mechanism, so a store that cannot read a file " +
                "written into the configured directory leaves every credentialed integration unusable: " + failure);
            Assert.AreEqual(Secret, value,
                "the value must come from the CONFIGURED directory on every run, because that is what makes " +
                "rotating a credential nothing more than overwriting a file, with no restart and nothing " +
                "re-entered");
        }

        [TestMethod]
        public void TheMountedStore_FailsForACredentialThatIsNotThere_RatherThanAnsweringNoCredential()
        {
            Assert.IsFalse(MountedStore().TryRead("absent-credential", out var value, out var failure),
                "a name with no file behind it is 'I could not look', and reading the source without the " +
                "credential produces a complete snapshot of what it shows the public");
            Assert.IsNull(value, "a failed read must hand back no value");
            Assert.IsFalse(String.IsNullOrWhiteSpace(failure),
                "the failure must say what could not be read, because the fix is a file in the mount and the " +
                "operator has to know which one");
        }

        [TestMethod]
        public void TheMountedStore_FailsForATruncatedFile_WhichIsWhatAHalfDoneRotationLeavesBehind()
        {
            File.WriteAllText(Path.Combine(_mount, CredentialName), String.Empty);

            Assert.IsFalse(MountedStore().TryRead(CredentialName, out var value, out _),
                "a rotation script that truncated the file must fail the job: read as 'no credential' the run " +
                "authenticates as nobody, sees what the source shows the public, declares that complete, and " +
                "withdraws every claim the instance ever made");
            Assert.IsNull(value, "a failed read must hand back no value");
        }

        [TestMethod]
        public void TheMountedStore_CannotReachASiblingDirectoryWhoseNameBeginsWithTheMount()
        {
            Directory.CreateDirectory(_retired);
            var retiredFile = Path.Combine(_retired, "retired-password");
            File.WriteAllText(retiredFile, "retired-value\n");

            var store = MountedStore();

            Assert.IsFalse(store.TryRead(
                    ".." + Path.DirectorySeparatorChar + Path.GetFileName(_retired) +
                    Path.DirectorySeparatorChar + "retired-password", out var byRelative, out _),
                "the file really is there and the process really can read it, so only the name check stops a " +
                "job from naming it: an operator who rotates by mounting a second directory alongside the " +
                "first would otherwise have every retired credential readable by whoever can submit a job");
            Assert.IsNull(byRelative, "a refused name must hand back no value");

            Assert.IsFalse(store.TryRead(retiredFile, out var byAbsolute, out _),
                "the absolute form is the one a naive prefix check on the resolved path accepts, because the " +
                "sibling shares the mount's characters without a separator");
            Assert.IsNull(byAbsolute, "a refused name must hand back no value");

            Assert.AreEqual("retired-value\n", File.ReadAllText(retiredFile),
                "this test only means anything while the retired credential is genuinely readable from disk");
        }

        // ------------------------------------------------------------------------------------------------
        // CredentialResolver and CredentialLease.
        // ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void AProviderNeedingNoCredential_GetsALeaseThatIsEmptyAndHasNoFingerprint()
        {
            var active = new ActiveCredentials();
            var resolver = ResolverOver(active);

            using var fromNull = resolver.Resolve(null);
            using var fromEmpty = resolver.Resolve(
                new Dictionary<String, CredentialSource>(StringComparer.OrdinalIgnoreCase));

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
        public void ANamedCredentialTheStoreDoesNotHave_Raises_NamingBothTheCredentialAndTheSetting()
        {
            var resolver = ResolverOver(new ActiveCredentials());

            var ex = Assert.ThrowsException<CredentialUnavailableException>(
                () => resolver.Resolve(Names(("password", "absent-credential"))),
                "an unreadable credential must fail the job BEFORE the provider is invoked: reading the source " +
                "without it produces what the source shows the public and a complete snapshot withdraws the rest");
            StringAssert.Contains(ex.Message, "absent-credential",
                "the failure must name the CREDENTIAL, because the fix is a file in the mount");
            StringAssert.Contains(ex.Message, "password",
                "the failure must name the SETTING too, because a job with two credentials otherwise leaves the " +
                "operator guessing which of them the mount is missing");
        }

        [TestMethod]
        public void ACredentialSettingNamingNothing_IsRefused_RatherThanTreatedAsNoCredential()
        {
            var resolver = ResolverOver(new ActiveCredentials(), (CredentialName, Secret));

            foreach (var named in new String[] { null, String.Empty, "   " })
            {
                var ex = Assert.ThrowsException<CredentialUnavailableException>(
                    () => resolver.Resolve(Names(("password", named))),
                    "a credential setting whose name is blank is a job that was half filled in, and running it " +
                    "anyway reads the source unauthenticated and declares that view complete");
                StringAssert.Contains(ex.Message, "password",
                    "the refusal must name the setting that was left empty, or the caller cannot fix the job");
            }
        }

        [TestMethod]
        public void ACredentialSettingKey_IsFoundWhateverCaseTheJobSpeltItIn()
        {
            var active = new ActiveCredentials();
            using var lease = ResolverOver(active, (CredentialName, Secret))
                .Resolve(Names(("Password", CredentialName)));

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
            var lease = ResolverOver(active, (CredentialName, Secret)).Resolve(Names(("password", CredentialName)));

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
        public void ACredentialSuppliedWithTheJob_IsLeasedAndHeldExactlyAsANamedOneIs()
        {
            var active = new ActiveCredentials();

            // No fixture credential at all: the store has nothing to offer, so if this resolves, it
            // resolved from the job.
            using (var lease = ResolverOver(active).Resolve(Supplied(("password", Secret))))
            {
                Assert.AreEqual(Secret, lease.Require("password"),
                    "a credential the caller supplied must reach the provider like any other, or the whole point " +
                    "of the second source is lost");
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
                "inline: no cache, no reuse by the next job, nothing to rotate");
        }

        [TestMethod]
        public void ACredentialSuppliedWithTheJob_ObeysTheSameContentRulesAsAFile()
        {
            var active = new ActiveCredentials();

            // One trailing newline dropped, and NOTHING else: a value pasted out of a console arrives with
            // one, and a space inside or around a real password has to survive.
            using (var lease = ResolverOver(active).Resolve(Supplied(("password", " pa ss \n"))))
            {
                Assert.AreEqual(" pa ss ", lease.Require("password"),
                    "exactly one trailing line ending is dropped and every other character is verbatim, or a " +
                    "credential that works from cron fails from a form and the symptom is an authentication " +
                    "failure with nothing to explain it");
            }

            foreach (var blank in new String[] { String.Empty, "   ", "\n" })
            {
                var ex = Assert.ThrowsException<CredentialUnavailableException>(
                    () => ResolverOver(active).Resolve(Supplied(("password", blank))),
                    "an empty supplied credential is a failure rather than 'no credential': submitting the form " +
                    "before pasting would otherwise read what the source shows the public, declare that " +
                    "complete, and withdraw every claim the instance ever made");
                StringAssert.Contains(ex.Message, "password",
                    "the refusal must name the setting, because that is the field to go back to");
            }
        }

        [TestMethod]
        public void ARefusedSuppliedCredential_IsNotQuotedInTheFailure_BecauseThatMessageIsReported()
        {
            var resolver = ResolverOver(new ActiveCredentials());

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
        public void ACredentialSource_NeverRendersTheSecretWhenFormatted()
        {
            var supplied = CredentialSource.Inline(Secret);
            var named = CredentialSource.Named(CredentialName);

            Assert.IsFalse(supplied.ToString().Contains(Secret, StringComparison.Ordinal),
                "an interpolation into a log line or an exception message is the one accident a secret-carrying " +
                "type suffers, and the guard belongs on the type rather than on every site that might format it");
            Assert.AreEqual(CredentialName, named.ToString(),
                "a NAME is not a secret and reads usefully in a message, which is the asymmetry worth keeping");
            Assert.IsTrue(supplied.IsInline, "a supplied value must declare itself as one");
            Assert.IsNull(supplied.Name, "a supplied value has no name to read");
            Assert.IsNull(named.InlineValue, "and a named credential carries no value until the store is asked");
        }

        [TestMethod]
        public void RequireForASettingNoCredentialWasSuppliedFor_NamesTheSetting()
        {
            var active = new ActiveCredentials();
            using var lease = ResolverOver(active, (CredentialName, Secret))
                .Resolve(Names(("password", CredentialName)));

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
                "a credential file replaced by MOVING a new file over it gives the file a new inode and a " +
                "bind-mounted container keeps reading the old one, so the job succeeds with the credential the " +
                "operator believes they revoked; a fingerprint that does not change after a rotation is the only " +
                "way that is ever seen");
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
        ///   Drives the credential CONTENT rules through the fixture store, which routes them through the same
        ///   <c>TryAccept</c> the real mount uses (that method is internal to the runtime assembly).
        /// </summary>
        private static Boolean TryReadContent(String content, out String value, out String failure)
        {
            var store = new FixtureCredentialStore(new Dictionary<String, String>(StringComparer.Ordinal)
            {
                { CredentialName, content },
            });

            return store.TryRead(CredentialName, out value, out failure);
        }

        /// <summary>The directory store over a real mount, configured exactly as the runtime configures it.</summary>
        private DirectoryCredentialStore MountedStore()
        {
            return new DirectoryCredentialStore(Options.Create(new IntegrationsOptions
            {
                Credentials = new CredentialsOptions { Directory = _mount },
            }));
        }

        private static CredentialResolver ResolverOver(ActiveCredentials active,
            params (String Name, String Value)[] offered)
        {
            var values = new Dictionary<String, String>(StringComparer.Ordinal);
            foreach (var pair in offered)
            {
                values[pair.Name] = pair.Value;
            }

            return new CredentialResolver(new FixtureCredentialStore(values), active);
        }

        /// <summary>Which credential each credential setting uses, by NAME, as a job supplies it.</summary>
        private static IReadOnlyDictionary<String, CredentialSource> Names(
            params (String SettingKey, String Name)[] pairs)
        {
            var names = new Dictionary<String, CredentialSource>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in pairs)
            {
                names[pair.SettingKey] = CredentialSource.Named(pair.Name);
            }

            return names;
        }

        /// <summary>The credential VALUES a job carries, keyed by credential setting.</summary>
        private static IReadOnlyDictionary<String, CredentialSource> Supplied(
            params (String SettingKey, String Value)[] pairs)
        {
            var supplied = new Dictionary<String, CredentialSource>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in pairs)
            {
                supplied[pair.SettingKey] = CredentialSource.Inline(pair.Value);
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
