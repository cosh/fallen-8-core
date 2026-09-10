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
        ///   brace. See the remarks at the use site for why both anchors matter.
        /// </summary>
        private static readonly string[] RoutePatterns =
        {
            "\"(?<value>[a-zA-Z0-9_./-]+)\"",
            "\\$@?\"(?<value>[a-zA-Z0-9_./-]+)\\{",
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
                                violations.Add(relative + ":" + lineNumber + ": " + value);
                            }
                        }
                    }
                }
            }

            AssertNoViolations(violations,
                "fallen-8-agents calls only the chat gateway (/chat, /chat/models); every graph "
                + "capability arrives as an MCP tool, whose server-side tiers are the bound");
        }
    }
}
