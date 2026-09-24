// MIT License
//
// CodeQualityTest.cs
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
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Rest.Configuration;
using NoSQL.GraphDB.Rest;
using System.Reflection;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// CI-enforced repository conventions (feature code-quality) - the same philosophy as
    /// SkillLibraryTest and JsonSourceGenParityTest: a convention that matters is a failing
    /// test, not a prose rule. Each rule reports EVERY violating file, and comment lines are
    /// stripped before token checks so prose mentioning a banned token never trips a rule.
    /// </summary>
    [TestClass]
    public class CodeQualityTest
    {
        private static readonly string[] _allProjects = { "fallen-8-agents", "fallen-8-core", "fallen-8-core-apiApp", "fallen-8-integrations", "fallen-8-mcp", "fallen-8-rest-client", "fallen-8-unittest" };
        private static readonly string[] _productProjects = { "fallen-8-agents", "fallen-8-core", "fallen-8-core-apiApp", "fallen-8-integrations", "fallen-8-mcp", "fallen-8-rest-client" };

        private static IEnumerable<string> SourceFiles(params string[] projects)
        {
            var root = TestRepo.Root();
            foreach (var project in projects)
            {
                foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(root, file);
                    if (relative.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) ||
                        relative.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                    {
                        continue;
                    }
                    yield return file;
                }
            }
        }

        /// <summary>Strips line comments so a banned token in prose never trips a rule.</summary>
        private static IEnumerable<string> CodeLines(string file)
        {
            foreach (var line in File.ReadLines(file))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }
                yield return line;
            }
        }

        private static void AssertNoViolations(List<string> violations, string rule)
        {
            Assert.AreEqual(0, violations.Count,
                rule + " - violations:\n" + string.Join("\n", violations));
        }

        [TestMethod]
        public void EverySourceFile_StartsWithTheMitLicenseHeader()
        {
            var violations = new List<string>();
            foreach (var file in SourceFiles(_allProjects))
            {
                var head = string.Join(" ", File.ReadLines(file).Take(3));
                if (!head.Contains("MIT License", StringComparison.Ordinal))
                {
                    violations.Add(file);
                }
            }

            AssertNoViolations(violations,
                "every .cs file starts with the MIT license header (copy an existing file's, per CLAUDE.md)");
        }

        [TestMethod]
        public void ProductCode_WritesNoConsoleOutput()
        {
            // Output goes through ILogger (operational) or Debug.WriteLine (debug-only dumps);
            // stdout belongs to the host. Tests and benchmarks may print - product code not. The
            // pattern also catches the Console.Out.Write*/Console.Error.Write* spellings, not only the
            // bare Console.Write* one, so an alternate spelling cannot slip stdout/stderr past the gate.
            var violations = new List<string>();
            foreach (var file in SourceFiles(_productProjects))
            {
                if (CodeLines(file).Any(l => Regex.IsMatch(l, @"\bConsole\.(Out\.|Error\.)?Write")))
                {
                    violations.Add(file);
                }
            }

            AssertNoViolations(violations, "no Console.Write* / Console.Out.Write* / Console.Error.Write* in product code");
        }

        /// <summary>
        ///   Every <c>AThreadSafeElement</c> lock release sits in a <c>finally</c>, so a throw inside
        ///   a guarded region cannot skip it (platform-integrity audit, W8).
        ///
        ///   <para>
        ///     This is a rule rather than a review note because of what a leak costs and how little
        ///     it says while costing it: the release is a bare method call, a throw past it wedges
        ///     the element for the life of the process, acquisition then never returns at all, and
        ///     two of the callers that can trigger it swallow the throw. The full explanation has one
        ///     home, on <c>AThreadSafeElement._usingResource</c>. What a leak looks like from the
        ///     outside is pinned behaviourally by <c>IndexLockContainmentTest</c>; this gate is the
        ///     cheap structural half that covers every subclass at once.
        ///   </para>
        ///   <para>
        ///     The rule is on the RELEASE and not on the acquisition, which is the difference between
        ///     a gate and a style check. "The guarded region opens with try" was the first shape
        ///     tried and it reported <c>IndexFactory</c>, whose release is correctly in a finally
        ///     behind one local declaration that cannot throw. What actually matters is that no
        ///     release can be jumped over, and that is what this asserts.
        ///   </para>
        /// </summary>
        [TestMethod]
        public void EveryThreadSafeElementLock_ReleasesInAFinally()
        {
            // RTree is a KNOWN, MEASURED exemption and not an oversight: 15 of its 17 acquisitions
            // are unguarded, several of them around an injected IMetric and caller geometry, which
            // is the same defect as W8's and a larger instance of it. W8 scoped itself to
            // SingleValueIndex and the audit's architects never assessed this file, so widening the
            // mechanical fix into a 2,000-line spatial index is a decision to take deliberately
            // rather than to inherit from a gate. Recorded in
            // features/done/review-findings-2026-09-23/spec.md; the exemption goes when it is fixed.
            // The FILE, not its directory. Exempting Index/Spatial would hand the same pass to any
            // spatial index added later, which is the one thing an exemption must not do: RTree.cs
            // is the only file under there with a release today, and the gate should widen by itself
            // when that stops being true.
            var exempt = new[]
            {
                Path.Combine("fallen-8-core", "Index", "Spatial", "Implementation", "RTree", "RTree.cs"),
            };
            var root = TestRepo.Root();

            var violations = new List<string>();
            var guarded = 0;
            foreach (var file in SourceFiles(_productProjects))
            {
                var relative = Path.GetRelativePath(root, file);
                if (exempt.Any(e => relative.Equals(e, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                // The RAW lines, so a reported number is the one an editor shows. Comments and blanks
                // are stepped over while looking back instead.
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    // The CODE on the line, with any trailing comment removed first. Removing it is
                    // load-bearing twice over: a line that is only prose about a release must not be
                    // reported, and a trailing comment must not be allowed to satisfy the finally
                    // check below. "FinishWriteResource(); // not in a finally" outside a finally
                    // was counted as guarded before this, which is the exact shape this rule was
                    // widened to catch, one word away.
                    var trimmed = WithoutTrailingComment(lines[i]);

                    // ANYWHERE on the line, not the whole line. A whole-line pattern let
                    // "FinishWriteResource(); return;" through without counting or reporting it. The
                    // declarations in AThreadSafeElement carry no semicolon, so they still do not
                    // match.
                    if (!Regex.IsMatch(trimmed, @"Finish(Read|Write)Resource\(\)\s*;"))
                    {
                        continue;
                    }

                    // A one-line "finally { Finish...(); }" is guarded by the line it is on.
                    if (Regex.IsMatch(trimmed, @"\bfinally\b"))
                    {
                        guarded++;
                        continue;
                    }

                    // Back over the finally's own brace to the keyword itself.
                    var opener = PreviousStatement(lines, i - 1);
                    if (opener >= 0 && lines[opener].Trim() == "{")
                    {
                        opener = PreviousStatement(lines, opener - 1);
                    }

                    if (opener >= 0 && lines[opener].Trim() == "finally")
                    {
                        guarded++;
                        continue;
                    }

                    violations.Add(relative + ":" + (i + 1) + " " + trimmed
                        + " - a release outside a finally is skipped by any throw above it");
                }
            }

            // Paired with the rule, because a pattern that matched nothing would pass vacuously and
            // say the opposite of what it means. A FLOOR rather than an exact count, since the exact
            // number moves with ordinary edits: 65 releases across 7 files when this was written, so
            // 50 tolerates a normal change and still catches a pattern that has half stopped
            // matching. If this trips after a deliberate removal, re-measure and lower it.
            Assert.IsTrue(guarded >= 50,
                "this gate found only " + guarded + " releases in a finally, against 65 when it was "
                + "written, so its pattern has stopped matching the code it is meant to check");

            AssertNoViolations(violations,
                "every AThreadSafeElement lock release sits in a finally (one exempt file, see the comment)");
        }

        /// <summary>
        ///   A line's code, with any trailing <c>//</c> comment cut off and the result trimmed.
        ///   Naive about a <c>//</c> inside a string literal, which is correct for what it is used
        ///   for: no release line in this repository carries one, and treating such a line as
        ///   shorter code can only make the gate report MORE, never less.
        /// </summary>
        private static string WithoutTrailingComment(string line)
        {
            var comment = line.IndexOf("//", StringComparison.Ordinal);
            return (comment >= 0 ? line.Substring(0, comment) : line).Trim();
        }

        /// <summary>The previous line that is neither blank nor a comment, for the gate above.</summary>
        private static int PreviousStatement(string[] lines, int from)
        {
            while (from >= 0)
            {
                var trimmed = lines[from].Trim();
                if (trimmed.Length != 0 && !trimmed.StartsWith("//", StringComparison.Ordinal))
                {
                    return from;
                }

                from--;
            }

            return -1;
        }

        [TestMethod]
        public void ProductCode_UsesNoLocalClock_OutsideTheDocumentedAllowlist()
        {
            // DateTime.Now is local and DST-sensitive; new code uses UtcNow (or stays off wall
            // clocks entirely). DateTimeOffset.Now is the same local wall-clock read and is caught
            // too, so an alternate spelling cannot reintroduce the local-clock class the gate exists
            // to prevent. DateHelper is the documented exception: its epoch semantics are
            // load-bearing and consistently local - see the comment in the file and the
            // code-quality spec's non-goal (with its revisit trigger).
            var allowlist = new[] { Path.Combine("fallen-8-core", "Helper", "DateHelper.cs") };
            var root = TestRepo.Root();

            var violations = new List<string>();
            foreach (var file in SourceFiles(_productProjects))
            {
                var relative = Path.GetRelativePath(root, file);
                if (allowlist.Contains(relative, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (CodeLines(file).Any(l => Regex.IsMatch(l, @"\bDateTime(Offset)?\.Now\b")))
                {
                    violations.Add(file);
                }
            }

            AssertNoViolations(violations, "no DateTime.Now / DateTimeOffset.Now in product code (allowlist: DateHelper.cs)");
        }

        [TestMethod]
        public void ApiApp_AwaitsTransactionCompletion_InsteadOfBlocking()
        {
            // WaitUntilFinished() is a blocking Task.Wait: on an ASP.NET request path it pins a
            // thread-pool thread for the transaction's full queue latency (feature
            // async-completion-sweep; the awaitable is TransactionInformation.Completion). The
            // pattern regressed once after the write-path-throughput conversion, so it is a rule.
            // DurabilityLifecycleService is the documented exception: its waits run once at host
            // startup/shutdown, never on a request thread. Engine-internal waits (fallen-8-core)
            // are writer-thread mechanics and deliberately out of scope.
            var allowlist = new[] { Path.Combine("fallen-8-core-apiApp", "Services", "DurabilityLifecycleService.cs") };
            var root = TestRepo.Root();

            var violations = new List<string>();
            foreach (var file in SourceFiles("fallen-8-core-apiApp"))
            {
                var relative = Path.GetRelativePath(root, file);
                if (allowlist.Contains(relative, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (CodeLines(file).Any(l => l.Contains(".WaitUntilFinished(", StringComparison.Ordinal)))
                {
                    violations.Add(file);
                }
            }

            AssertNoViolations(violations,
                "no blocking WaitUntilFinished() in fallen-8-core-apiApp - await TransactionInformation.Completion instead (allowlist: DurabilityLifecycleService.cs)");
        }

        [TestMethod]
        public void ApiApp_MaySuppressIL2026_OnlyWhilePublishedUntrimmed()
        {
            // The apiApp suppresses IL2026 project-wide, and the whole justification is that this
            // service is published UNTRIMMED, so "you are calling something that needs unreferenced
            // code" tells nobody anything here. That pairing is a premise, not a fact of the build:
            // flipping PublishTrimmed to true would leave the suppression in place and silently drop
            // every one of those diagnostics from the one project whose reason for existing is to
            // expose exactly those features over REST. EnableTrimAnalyzer=true is the other half - the
            // remaining trim diagnostics (an annotation mismatch, an unannotated reflective
            // construction) must still fail the build here. Matched on the ELEMENT form, so the
            // justification comment naming IL2026 and PublishTrimmed cannot satisfy the rule.
            var root = TestRepo.Root();
            var csproj = Path.Combine(root, "fallen-8-core-apiApp", "fallen-8-core-apiApp.csproj");
            var project = File.ReadAllText(csproj);
            var violations = new List<string>();

            // Either spelling counts as suppression: NoWarn drops the diagnostic, WarningsNotAsErrors
            // demotes it to a warning under the repo's warnings-are-errors gate.
            if (Regex.IsMatch(project, @"<(NoWarn|WarningsNotAsErrors)>[^<]*IL2026", RegexOptions.IgnoreCase))
            {
                var relative = Path.GetRelativePath(root, csproj);

                if (!Regex.IsMatch(project, @"<PublishTrimmed>\s*false\s*</PublishTrimmed>", RegexOptions.IgnoreCase))
                {
                    violations.Add(relative + ": suppresses IL2026 without declaring <PublishTrimmed>false</PublishTrimmed>");
                }

                if (Regex.IsMatch(project, @"<PublishTrimmed>\s*true\s*</PublishTrimmed>", RegexOptions.IgnoreCase))
                {
                    violations.Add(relative + ": suppresses IL2026 while some PropertyGroup declares <PublishTrimmed>true</PublishTrimmed>");
                }

                if (!Regex.IsMatch(project, @"<EnableTrimAnalyzer>\s*true\s*</EnableTrimAnalyzer>", RegexOptions.IgnoreCase))
                {
                    violations.Add(relative + ": suppresses IL2026 without declaring <EnableTrimAnalyzer>true</EnableTrimAnalyzer>");
                }
            }

            AssertNoViolations(violations,
                "the apiApp may suppress IL2026 only while it is published untrimmed with the trim analyzer on - drop the suppression, or restore PublishTrimmed=false and EnableTrimAnalyzer=true");
        }

        [TestMethod]
        public void IntegrationsRuntime_ReadsAFormWithoutSpoolingItToDisk()
        {
            // The integrations runtime's published contract is that it mounts no directory for files and
            // opens nothing on disk: a file arrives with the job that needs it and is dropped when the run
            // ends. ReadFormAsync and the IFormFile family make that quietly false - the form reader spools
            // any part over 64 KiB (FormOptions.MemoryBufferThreshold) to a temp file, so a caller's extract
            // would be written into the container's filesystem by the transport rather than by any code
            // anyone reviewed. MultipartReader, which JobRequestReader uses, does not.
            //
            // The apiApp's integrations proxy is held to the same rule for a different reason: an IFormFile
            // parameter there would buffer the whole body in the one hop whose entire contract is not to
            // look at it.
            var root = TestRepo.Root();
            var banned = new Regex(@"\b(ReadFormAsync|IFormFileCollection|IFormFile|IFormCollection)\b");
            var violations = new List<string>();

            foreach (var file in SourceFiles("fallen-8-integrations"))
            {
                if (CodeLines(file).Any(l => banned.IsMatch(l)))
                {
                    violations.Add(Path.GetRelativePath(root, file));
                }
            }

            var proxy = Path.Combine(root, "fallen-8-core-apiApp", "Controllers", "IntegrationsController.cs");
            if (CodeLines(proxy).Any(l => banned.IsMatch(l)))
            {
                violations.Add(Path.GetRelativePath(root, proxy));
            }

            AssertNoViolations(violations,
                "no ReadFormAsync / IFormFile / IFormFileCollection / IFormCollection in fallen-8-integrations " +
                "or in the apiApp's integrations proxy - the form reader spools parts over 64 KiB to a temp " +
                "file, which would falsify the runtime's no-disk contract; read parts with MultipartReader");
        }

        [TestMethod]
        public void EveryPackageReference_PinsAnExactVersion()
        {
            // The repo's pin-everything rule, enforced: no floating ('1.*') or range versions,
            // and no version-less references - a build must resolve the same graph tomorrow.
            var root = TestRepo.Root();
            var violations = new List<string>();

            foreach (var project in _allProjects)
            {
                foreach (var csproj in Directory.EnumerateFiles(Path.Combine(root, project), "*.csproj", SearchOption.TopDirectoryOnly))
                {
                    foreach (var line in File.ReadLines(csproj))
                    {
                        if (!line.Contains("<PackageReference", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        var versionMatch = Regex.Match(line, "Version=\"([^\"]*)\"");
                        if (!versionMatch.Success)
                        {
                            violations.Add(csproj + ": version-less " + line.Trim());
                        }
                        else if (versionMatch.Groups[1].Value.Contains('*') ||
                                 versionMatch.Groups[1].Value.Contains('[') ||
                                 versionMatch.Groups[1].Value.Contains('('))
                        {
                            violations.Add(csproj + ": non-exact " + line.Trim());
                        }
                    }
                }
            }

            AssertNoViolations(violations, "every PackageReference pins an exact version");
        }

        [TestMethod]
        public void TheRestOnlyDeployables_ReferenceNeitherTheEngineNorTheApiApp()
        {
            // fallen-8-mcp, fallen-8-integrations and fallen-8-agents reach a graph over the public REST
            // contract ONLY: one is handed somebody's network-admin credential, one runs a model that
            // decides for itself what to call, and all must version independently against a boundary a
            // project reference would widen to the whole engine surface. That rule is what makes
            // fallen-8-rest-client (the seam they share) legal, so the seam is held to it as well - a
            // reference added THERE would reach every consumer transitively and nothing else would notice.
            var root = TestRepo.Root();
            var forbidden = new[] { "fallen-8-core.csproj", "fallen-8-core-apiApp.csproj" };
            var violations = new List<string>();

            foreach (var project in new[] { "fallen-8-mcp", "fallen-8-integrations", "fallen-8-agents", "fallen-8-rest-client" })
            {
                var csproj = Path.Combine(root, project, project + ".csproj");
                foreach (var line in File.ReadLines(csproj))
                {
                    if (!line.Contains("<ProjectReference", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    foreach (var banned in forbidden)
                    {
                        if (line.Contains(banned, StringComparison.OrdinalIgnoreCase))
                        {
                            violations.Add(project + ": " + line.Trim());
                        }
                    }
                }
            }

            AssertNoViolations(violations,
                "the REST-only deployables and the seam they share reference neither fallen-8-core nor fallen-8-core-apiApp");
        }

        /// <summary>
        ///   The two ways a REST path is written in this repository, each anchored at both ends: a
        ///   complete string literal, and the leading segment of an interpolated one up to its first
        ///   brace. A query string is tolerated before the closing anchor and is NOT part of the
        ///   captured value, so the value stays the path. See the remarks at the use site for why
        ///   both anchors matter.
        /// </summary>
        private static readonly string[] RoutePatterns =
        {
            "\"(?<value>[a-zA-Z0-9_./-]+)(\\?[^\"]*)?\"",
            "\\$@?\"(?<value>[a-zA-Z0-9_./-]+)(\\?[^\"{]*)?\\{",
        };

        [TestMethod]
        public void TheAgentHost_CallsTheChatGatewayAndNoOtherRestRoute()
        {
            // Feature agent-host, spec section 3.7. fallen-8-agents' REST surface is narrower than
            // its two sibling sidecars': the chat gateway, and nothing else. Everything about the
            // GRAPH arrives as an MCP tool, so the MCP server's read/write/admin tiers are the
            // whole of what an agent can reach - enforced server-side, where an agent cannot argue
            // with it. A graph call made directly from the host would route around those tiers
            // silently, which is why this is a test rather than a note.
            //
            // Pinned against the OpenAPI snapshot rather than a hand-written list, so a route
            // family added to the instance is covered the moment the snapshot is regenerated. The
            // check is on the FIRST path segment, which is what identifies a family: a literal
            // whose first segment is a known family and which is not the chat gateway is a
            // violation, and a literal that resembles no family at all is not this rule's business.
            var root = TestRepo.Root();
            var snapshot = File.ReadAllText(
                Path.Combine(root, "features", "done", "web-ui", "openapi-v0.1.json"));

            var families = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match path in Regex.Matches(snapshot, "\"/(?<path>[a-zA-Z0-9_./{}-]*)\"\\s*:"))
            {
                var segments = path.Groups["path"].Value.Split('/');
                if (segments.Length > 0 && segments[0].Length > 0 && !segments[0].StartsWith("{", StringComparison.Ordinal))
                {
                    families.Add(segments[0]);
                }
            }

            // Sanity: a regex that matched nothing would make this rule pass vacuously, which is
            // the one way a convention test can be worse than no test.
            Assert.IsTrue(families.Contains("chat"),
                "the OpenAPI snapshot should list the chat gateway; got " + families.Count + " families");

            // And the patterns themselves, against the shapes a graph call arrives in. The class
            // excluded "?" and "=", so a route carrying a query string matched NEITHER pattern and
            // passed the rule that the docs say it cannot escape.
            foreach (var (shape, expected) in new[]
            {
                ("\"graph/vertices\"", "graph/vertices"),
                ("\"graph/vertices?limit=10\"", "graph/vertices"),
                ("$\"graphelement/{id}\"", "graphelement/"),
                ("$\"graph/scan?op=eq&value={v}\"", "graph/scan"),
            })
            {
                // The captured VALUE, not a prefix of it: the doc above says the value stays the
                // path, and a pattern that swallowed a query string into the capture would still
                // start with "graph" and still split to the right family, so the boundary half of
                // that claim was pinned by nothing.
                Assert.IsTrue(
                    RoutePatterns.Any(pattern => Regex.Matches(shape, pattern)
                        .Any(m => String.Equals(m.Groups["value"].Value, expected,
                            StringComparison.Ordinal))),
                    "a graph route written as " + shape + " escapes this rule's own patterns, or "
                    + "the capture no longer stops where the path does");
            }

            var allowed = new HashSet<string>(StringComparer.Ordinal) { "chat", "chat/models" };
            var violations = new List<string>();

            foreach (var file in SourceFiles("fallen-8-agents"))
            {
                var relative = Path.GetRelativePath(root, file);
                var lineNumber = 0;
                foreach (var line in File.ReadLines(file))
                {
                    lineNumber++;
                    var code = line.TrimStart();
                    if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith("///", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // TWO shapes, because a graph call is written in whichever one fits, and both
                    // have to be anchored at BOTH ends. A path that is a whole literal is
                    // "graph/vertex"; one with a route value in it is $"graphelement/{id}", where the
                    // brace is the anchor. fallen-8-mcp writes every one of its graph calls the
                    // second way, so a pin that saw only the first would miss the shape a graph call
                    // actually arrives in.
                    //
                    // Anchoring at both ends is what keeps this a pin rather than a word search: an
                    // opening quote alone matches the second fragment of any concatenated sentence,
                    // which flagged the word "delegates" in a refusal message and the prefix
                    // "status:" in a posture value. Neither is a route.
                    //
                    // The closing anchor allows a QUERY STRING, and that is the hole this rule had:
                    // the path class excluded "?" and "=", so "graph/vertices?limit=10" matched
                    // neither shape and passed. The capture stays the path, so a query string
                    // cannot carry a second family past the family check either.
                    foreach (var pattern in RoutePatterns)
                    {
                        foreach (Match literal in Regex.Matches(line, pattern))
                        {
                            var value = literal.Groups["value"].Value.Trim('/');
                            if (value.Length == 0 || allowed.Contains(value))
                            {
                                continue;
                            }

                            if (families.Contains(value.Split('/')[0]))
                            {
                                // Both shapes now match an interpolated route that carries a query
                                // string, and one line is one violation.
                                var violation = relative + ":" + lineNumber + ": " + value;
                                if (!violations.Contains(violation))
                                {
                                    violations.Add(violation);
                                }
                            }
                        }
                    }
                }
            }

            AssertNoViolations(violations,
                "fallen-8-agents calls only the chat gateway (/chat, /chat/models); every graph "
                + "capability arrives as an MCP tool, whose server-side tiers are the bound");
        }

        /// <summary>
        /// The published security page is the one home for the capability posture, and it states a
        /// COUNT of switches in prose. A capability added to the authorization layer without
        /// touching that page leaves the number wrong, silently: it said four while the layer
        /// enforced six, through two features. The count is the only part of that page a test can
        /// hold, so this holds it.
        /// </summary>
        [TestMethod]
        public void TheSecurityPage_CountsEveryCapabilitySwitchTheLayerEnforces()
        {
            var page = File.ReadAllText(Path.Combine(
                TestRepo.Root(), "docs", "src", "content", "docs", "security.mdx"));
            var written = Regex.Match(
                page, @"one of (?<count>[a-z]+) operator \*\*capability\*\* switches");

            // A regex that stopped matching would make this rule pass vacuously, which is the one
            // way a convention test can be worse than no test. The sentence may be rewritten; it
            // may not lose its count.
            Assert.IsTrue(written.Success,
                "docs/src/content/docs/security.mdx no longer says how many capability switches "
                + "the authorization layer enforces. That sentence is what this test pins, so "
                + "keep a count in it (\"one of six operator **capability** switches\") or move "
                + "the pin to whatever replaced it.");

            var numbers = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6,
                ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10,
            };
            var word = written.Groups["count"].Value;
            Assert.IsTrue(numbers.TryGetValue(word, out var claimed),
                "security.mdx spells the capability count as \"" + word + "\", which this test "
                + "cannot read. Spell it as a word between two and ten.");

            var enforced = Enum.GetValues<
                NoSQL.GraphDB.App.Security.DynamicCapabilityRequirement.Capability>().Length;
            Assert.AreEqual(enforced, claimed,
                "The authorization layer enforces " + enforced + " capability switches and "
                + "security.mdx says " + claimed + ". Update the count, the switch table and the "
                + "per-capability row on that page: it is the one home for this posture, so a "
                + "reader has nowhere else to find the right number.");
        }

        // --- the sidecars share one seam, and these keep it that way (feature sidecar-shared-options) ---

        /// <summary>
        ///   The projects that consume the shared seam, DERIVED from the build graph rather than
        ///   listed: any project whose csproj takes a ProjectReference on fallen-8-rest-client. The
        ///   seam itself and the test project are excluded, the first because it is the thing being
        ///   consumed and the second because it is not a deployable.
        ///
        ///   <para>
        ///     This is the fix for a gate that could not fail. The first version of these three
        ///     tests hardcoded the three sidecars, so the ONE change they advertised catching - a
        ///     new deployable that copies instead of deriving - was outside their scan. A review
        ///     proved it by adding a fourth sidecar that copied the options class and watched all
        ///     three gates pass.
        ///   </para>
        /// </summary>
        private static IReadOnlyList<string> SeamConsumingProjects()
        {
            var projects = new List<string>();
            foreach (var directory in DotNetProjectDirectories())
            {
                var name = Path.GetFileName(directory);
                if (name == "fallen-8-rest-client" || name == "fallen-8-unittest")
                {
                    continue;
                }

                foreach (var csproj in Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly))
                {
                    var text = File.ReadAllText(csproj);
                    if (Regex.IsMatch(text, @"<ProjectReference[^>]*fallen-8-rest-client"))
                    {
                        projects.Add(name);
                        break;
                    }
                }
            }

            projects.Sort(StringComparer.Ordinal);
            Assert.IsTrue(projects.Count >= 3,
                "expected at least the three REST-only sidecars to consume the seam, found: "
                + string.Join(", ", projects) + ". If the seam is gone, these gates go with it - but "
                + "silently finding nothing is how a gate stops being one.");
            return projects;
        }

        /// <summary>
        ///   Every .NET project directory, which is the only kind these sweeps can read. Gated on a
        ///   csproj being present rather than on the fallen-8-* name alone: fallen-8-web-ui and
        ///   fallen-8-nlp match that name and are a Vite app and a Python service, and walking the
        ///   first one throws PathTooLongException on the recursive node_modules link its embed
        ///   fixture leaves behind.
        /// </summary>
        private static IEnumerable<string> DotNetProjectDirectories()
        {
            foreach (var directory in Directory.EnumerateDirectories(TestRepo.Root(), "fallen-8-*"))
            {
                if (Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly).Any())
                {
                    yield return directory;
                }
            }
        }

        /// <summary>Every project that wires OpenTelemetry, derived by looking for the call.</summary>
        private static IReadOnlyList<string> OpenTelemetryProjects()
        {
            var projects = new List<string>();
            foreach (var directory in DotNetProjectDirectories())
            {
                var name = Path.GetFileName(directory);
                if (name == "fallen-8-unittest")
                {
                    continue;
                }

                // CodeLines, so a doc comment MENTIONING AddOpenTelemetry does not enrol a project
                // that does not call it: the seam's own options type explains the wiring it
                // deliberately does not do.
                if (SourceFiles(name).Any(f => CodeLines(f).Any(l => l.Contains(".AddOpenTelemetry(", StringComparison.Ordinal))))
                {
                    projects.Add(name);
                }
            }

            projects.Sort(StringComparer.Ordinal);
            return projects;
        }

        /// <summary>The assembly a project's types live in, or null when this test run cannot see it.</summary>
        private static Assembly ProjectAssembly(string project)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, project, StringComparison.Ordinal));
        }

        /// <summary>
        ///   The member names a type DECLARES itself, which is what makes two types a copy of each
        ///   other rather than a coincidence. Inherited members are excluded on purpose: two classes
        ///   deriving from one seam base share everything it gives them, and that is the opposite of
        ///   the problem.
        ///
        ///   <para>
        ///     Accessors and backing fields are dropped so the set is the shape a reader would
        ///     recognise: naming &lt;Attempts&gt;k__BackingField beside Attempts says nothing extra
        ///     and makes the failure message harder to read than the defect it reports.
        ///   </para>
        /// </summary>
        private static SortedSet<string> DeclaredMembers(Type type)
        {
            const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            return new SortedSet<string>(
                type.GetMembers(Declared)
                    .Where(m => m.MemberType != MemberTypes.NestedType)
                    .Select(m => m.Name)
                    .Where(name => !name.StartsWith("get_", StringComparison.Ordinal)
                        && !name.StartsWith("set_", StringComparison.Ordinal)
                        && !name.StartsWith("<", StringComparison.Ordinal)),
                StringComparer.Ordinal);
        }

        [TestMethod]
        public void EverySeamConsumersSharedOptionsFamily_DerivesFromTheSeamsBase()
        {
            // Three families are the same question asked by every deployable beside a Fallen-8 -
            // which instance, whose identity, which collector - and each used to be answered by its
            // own copied class. The rule is a NAMING one over a DERIVED project list, so a fourth
            // deployable that copies the class instead of deriving fails here on the day it is
            // added, whether or not the test project references it.
            var families = new (string Suffix, string Base)[]
            {
                ("TargetOptions", nameof(AFallen8TargetOptions)),
                ("IdentityOptions", nameof(AFleetIdentityOptions)),
                ("ObservabilityOptions", nameof(AFleetObservabilityOptions)),
            };

            var violations = new List<string>();
            var found = new List<string>();

            foreach (var project in SeamConsumingProjects())
            {
                foreach (var file in SourceFiles(project))
                {
                    var relative = Path.GetRelativePath(TestRepo.Root(), file);
                    var lineNumber = 0;
                    // The RAW lines with comments skipped inline, not CodeLines: counting only the
                    // code lines makes the reported number drift by the length of the licence
                    // header, and a citation an operator cannot open is worse than none.
                    foreach (var raw in File.ReadLines(file))
                    {
                        lineNumber++;
                        var line = raw.TrimStart().StartsWith("//", StringComparison.Ordinal) ? string.Empty : raw;
                        foreach (var (suffix, expected) in families)
                        {
                            // The declaration, not a usage: "class Foo<Suffix>" optionally followed
                            // by ": Base". Text rather than reflection because a NEW project is not
                            // referenced by this test assembly and so cannot be reflected over at
                            // all - which is exactly the case that has to fail.
                            var match = Regex.Match(line,
                                @"\b(?:class|record)\s+(?<name>\w*" + suffix + @")\b\s*(?<bases>:[^{]*)?");
                            if (!match.Success)
                            {
                                continue;
                            }

                            found.Add(project + ":" + match.Groups["name"].Value);
                            if (!match.Groups["bases"].Value.Contains(expected, StringComparison.Ordinal))
                            {
                                violations.Add(relative + ":" + lineNumber + " declares "
                                    + match.Groups["name"].Value + " without deriving from " + expected
                                    + ", so it is another copy of what the seam owns");
                            }
                        }
                    }
                }
            }

            AssertNoViolations(violations,
                "a seam consumer's target/identity/observability options derive from fallen-8-rest-client's base");

            // Coverage, reported rather than asserted as a magic number: every consuming project
            // must contribute at least one of the families, or the sweep found nothing and is
            // passing vacuously.
            foreach (var project in SeamConsumingProjects())
            {
                Assert.IsTrue(found.Any(f => f.StartsWith(project + ":", StringComparison.Ordinal)),
                    project + " consumes the seam but declares none of the three shared option "
                    + "families. Either it gained a family under a name this rule does not match, "
                    + "or the sweep has gone blind. Found: " + string.Join(", ", found));
            }
        }

        [TestMethod]
        public void NoTypeIsCopiedBetweenTheSeamAndItsConsumers()
        {
            // Keyed on the SHAPE, not on the name, which is the difference between a gate and a
            // formality. The first version keyed on the type name AND required an exactly equal
            // member set, so it would not have caught the triplication it was written for: the
            // three *IdentityOptions differed in name, and the three same-named Fallen8TargetOptions
            // differed by one member each. A review proved both.
            //
            // The SEAM is in scope too, because the copy direction this feature fixed is a consumer
            // keeping its own copy of what the seam owns - re-adding fallen-8-mcp's own OptionBounds
            // passed the first version, since only one project declared the name.
            var assemblies = new List<Assembly>();
            foreach (var project in SeamConsumingProjects())
            {
                var assembly = ProjectAssembly(project);
                Assert.IsNotNull(assembly,
                    project + " consumes the seam but its assembly is not loaded here, so this gate "
                    + "cannot see it. Add a ProjectReference from fallen-8-unittest, or this project's "
                    + "copies are invisible.");
                assemblies.Add(assembly);
            }
            assemblies.Add(typeof(RestSeam).Assembly);

            // FOUR, and the number is measured rather than chosen. At three, the only two
            // cross-assembly matches on this tree are coincidences: the two Program entry points
            // (same member NAMES, and TransportBound holds 832 MiB in one and 2 MiB in the other),
            // and UnifiSite against IdentityLevel, which are both {Id, Name}. At four there are
            // none. A three-member copy therefore slips, and saying so is better than an allowlist
            // that grows until the gate proves nothing.
            const int MinimumShape = 4;

            var shapes = new List<(Assembly Assembly, Type Type, SortedSet<string> Shape)>();
            foreach (var assembly in assemblies)
            {
                foreach (var type in assembly.GetTypes())
                {
                    if (type.IsGenericParameter || type.Name.StartsWith("<", StringComparison.Ordinal))
                    {
                        continue;   // compiler-generated closures and iterator classes
                    }

                    var shape = DeclaredMembers(type);
                    if (shape.Count >= MinimumShape)
                    {
                        shapes.Add((assembly, type, shape));
                    }
                }
            }

            var violations = new List<string>();

            // RULE 1, and it is the one the shape rule cannot express: a consumer must not declare a
            // type whose NAME the seam already owns, at any size. The seam's names are few and
            // deliberate, so a second declaration of one is a fork rather than a coincidence - and
            // the shape rule misses it twice over, because only one project declares the name and
            // because the type that actually got forked in the review's mutant (OptionBounds, a
            // clamp with one constant and one method) is below any useful shape threshold. This is
            // the drift the clamp's own history is about: a host quietly forks it and then edits
            // only its own copy.
            var seamTypes = typeof(RestSeam).Assembly.GetTypes()
                .Where(t => !t.Name.StartsWith("<", StringComparison.Ordinal))
                .ToDictionary(t => t.Name, t => t.FullName, StringComparer.Ordinal);

            foreach (var assembly in assemblies.Where(a => a != typeof(RestSeam).Assembly))
            {
                foreach (var type in assembly.GetTypes())
                {
                    if (type.Name.StartsWith("<", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (seamTypes.TryGetValue(type.Name, out var seamType) && type.FullName != seamType)
                    {
                        violations.Add(type.FullName + " (" + assembly.GetName().Name
                            + ") re-declares a name the seam already owns (" + seamType
                            + "). Use the seam's, or give this one a name that says how it differs.");
                    }
                }
            }

            // RULE 2: a renamed copy, which no name-based rule can see.
            for (var i = 0; i < shapes.Count; i++)
            {
                for (var j = i + 1; j < shapes.Count; j++)
                {
                    var (left, right) = (shapes[i], shapes[j]);
                    if (left.Assembly == right.Assembly)
                    {
                        continue;
                    }

                    if (!left.Shape.SetEquals(right.Shape))
                    {
                        continue;
                    }

                    // Deriving from ONE seam base is the fix, not the defect: two consumers' own
                    // Fallen8TargetOptions are meant to be parallel and each adds only its own
                    // knobs. EverySeamConsumersSharedOptionsFamily_DerivesFromTheSeamsBase holds
                    // that half.
                    if (left.Type.BaseType != null && left.Type.BaseType == right.Type.BaseType
                        && left.Type.BaseType.Assembly == typeof(RestSeam).Assembly)
                    {
                        continue;
                    }

                    violations.Add(left.Type.Name + " (" + left.Assembly.GetName().Name + ") and "
                        + right.Type.Name + " (" + right.Assembly.GetName().Name
                        + ") declare the same " + left.Shape.Count + " members ("
                        + string.Join(", ", left.Shape)
                        + "). Give them one home in fallen-8-rest-client and derive, or make two "
                        + "genuinely different things genuinely different shapes.");
                }
            }

            AssertNoViolations(violations,
                "no type shape is copied between the seam and its consumers");
        }

        [TestMethod]
        public void EveryDeployablesServiceName_MatchesEverySelectorTheShippedDashboardUses()
        {
            // The pin derives its expectation from the CONSUMER. The per-tenant dashboard's log
            // panel selects streams by service_name, and Loki anchors its regexes fully, so
            // "fallen8.*" matched three of the four deployables and silently excluded
            // fallen-8-integrations - whose panel is described as "logs scoped to the selected
            // instance". Reading the selector out of the dashboard rather than restating it here
            // means a deliberate change to the convention has to change the dashboard first.
            //
            // EVERY selector, not the first one found: a review added an earlier panel selecting
            // {service_name=~".+"} and renamed a service, and the first-match version passed while
            // the real Logs panel still excluded it.
            var root = TestRepo.Root();
            var dashboard = Path.Combine(root, "observability", "grafana", "dashboards", "per-tenant.json");
            Assert.IsTrue(File.Exists(dashboard),
                "the per-tenant dashboard is where the service-name convention is enforced in anger; "
                + "if it moved, move this pin with it rather than deleting it");

            var selectors = Regex.Matches(File.ReadAllText(dashboard),
                    @"service_name\s*=~\s*\\?""(?<pattern>[^""\\]+)\\?""")
                .Select(m => m.Groups["pattern"].Value)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            Assert.IsTrue(selectors.Count > 0,
                "no {service_name=~\"...\"} selector found in " + Path.GetFileName(dashboard)
                + ". The dashboard is the source of this convention, so if it now selects logs "
                + "another way, this test has to learn the new way.");

            // A Grafana template variable is not a testable pattern: "$service" would compile to a
            // regex matching nothing and fail all four names with a message blaming the code.
            var testable = selectors.Where(s => !s.Contains('$', StringComparison.Ordinal)).ToList();
            Assert.IsTrue(testable.Count > 0,
                "every service_name selector in " + Path.GetFileName(dashboard) + " is a template "
                + "variable (" + string.Join(", ", selectors) + "), so none of them says what the "
                + "convention is. Keep one literal selector, or this gate has nothing to check.");

            // Per PROJECT, derived from which projects wire OpenTelemetry at all. The first version
            // asserted only a TOTAL of four literals, so a review deleted one project's
            // registration, added a second to another, and watched the gate pass while the MCP
            // server exported as "unknown_service:fallen-8-mcp".
            var declared = new List<(string Project, string Name)>();
            var otelProjects = OpenTelemetryProjects();
            Assert.IsTrue(otelProjects.Count >= 4,
                "expected the apiApp and the three sidecars to wire OpenTelemetry, found: "
                + string.Join(", ", otelProjects));

            foreach (var project in otelProjects)
            {
                foreach (var file in SourceFiles(project))
                {
                    foreach (var line in CodeLines(file))
                    {
                        foreach (Match call in Regex.Matches(line, @"AddService\(\s*""(?<name>[^""]+)"""))
                        {
                            declared.Add((project, call.Groups["name"].Value));
                        }
                    }
                }
            }

            var missing = otelProjects
                .Where(p => !declared.Any(d => d.Project == p))
                .Select(p => p + " wires OpenTelemetry but names no service, so it exports as the "
                    + "SDK's default (unknown_service:*) and its logs are absent from the panel")
                .ToList();
            AssertNoViolations(missing, "every OpenTelemetry-wiring project names its own service");

            var violations = new List<string>();
            foreach (var (project, name) in declared)
            {
                foreach (var pattern in testable)
                {
                    if (!Regex.IsMatch(name, "^(?:" + pattern + ")$", RegexOptions.CultureInvariant))
                    {
                        violations.Add(project + " declares service.name '" + name
                            + "', which the dashboard selector /" + pattern + "/ does not match, so "
                            + "its logs are absent from that panel");
                    }
                }
            }

            AssertNoViolations(violations,
                "every deployable's OTel service.name matches every literal stream selector the shipped dashboard uses");
        }
    }
}
