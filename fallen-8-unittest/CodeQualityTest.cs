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

        /// <summary>The three deployables that may reach a Fallen-8 only over its public HTTP contract.</summary>
        private static IEnumerable<Assembly> SidecarAssemblies()
        {
            yield return typeof(NoSQL.GraphDB.Mcp.Configuration.McpOptions).Assembly;
            yield return typeof(NoSQL.GraphDB.Integrations.Configuration.IntegrationsOptions).Assembly;
            yield return typeof(NoSQL.GraphDB.Agents.Configuration.AgentsOptions).Assembly;
        }

        /// <summary>
        ///   The member names a type DECLARES itself, which is what makes two same-named types a copy
        ///   of each other rather than a coincidence. Inherited members are excluded on purpose: two
        ///   classes deriving from one seam base share everything it gives them, and that is the
        ///   opposite of the problem.
        /// </summary>
        private static SortedSet<string> DeclaredMembers(Type type)
        {
            const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            // Accessors and backing fields are dropped so the set is the shape a reader would
            // recognise: naming <Attempts>k__BackingField beside Attempts says nothing extra and
            // makes the failure message harder to read than the defect it reports.
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
        public void EverySidecarsSharedOptionsFamily_DerivesFromTheSeamsBase()
        {
            // Three families are the same question asked by every sidecar - which Fallen-8, whose
            // identity, which collector - and each used to be answered by its own copied class. The
            // rule is a NAMING one rather than a list, so a fourth deployable that copies the class
            // instead of deriving fails here on the day it is added.
            var families = new (string Suffix, Type Base)[]
            {
                ("TargetOptions", typeof(AFallen8TargetOptions)),
                ("IdentityOptions", typeof(AFleetIdentityOptions)),
                ("ObservabilityOptions", typeof(AFleetObservabilityOptions)),
            };

            var violations = new List<string>();
            var covered = 0;
            foreach (var assembly in SidecarAssemblies())
            {
                foreach (var type in assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract))
                {
                    foreach (var (suffix, expected) in families)
                    {
                        if (!type.Name.EndsWith(suffix, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        covered++;
                        if (!expected.IsAssignableFrom(type))
                        {
                            violations.Add(type.FullName + " (" + assembly.GetName().Name
                                + ") is named like the shared " + suffix + " family but does not derive from "
                                + expected.Name + ", so it is a fourth copy of it");
                        }
                    }
                }
            }

            AssertNoViolations(violations,
                "a sidecar's target/identity/observability options derive from fallen-8-rest-client's base");
            Assert.AreEqual(9, covered,
                "expected three families across three sidecars. If a deployable was added or removed, "
                + "this count moves with it - but check the new one actually derives rather than "
                + "just relaxing the number.");
        }

        [TestMethod]
        public void NoTypeIsCopiedBetweenTheSidecars()
        {
            // A COPY, not a name clash: fallen-8-mcp's McpOptions (the server's own transport, port
            // and tiers) and fallen-8-agents' nested McpOptions (how the host dials that server) share
            // a name and nothing else, and flagging that pair would make this gate a nuisance whose
            // allowlist grew until it proved nothing. So the test compares the members each type
            // DECLARES, which is what a copied class has in common with its original.
            var byName = new Dictionary<string, List<Type>>(StringComparer.Ordinal);
            foreach (var assembly in SidecarAssemblies())
            {
                foreach (var type in assembly.GetTypes())
                {
                    if (type.IsGenericParameter || type.Name.StartsWith("<", StringComparison.Ordinal))
                    {
                        continue;   // compiler-generated closures and iterator classes
                    }

                    if (!byName.TryGetValue(type.Name, out var list))
                    {
                        byName[type.Name] = list = new List<Type>();
                    }
                    list.Add(type);
                }
            }

            // One entry, and the reason is the SDK's rather than ours: an entry point per assembly is
            // what makes three processes three processes.
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "Program" };

            var violations = new List<string>();
            foreach (var (name, types) in byName.Where(kv => kv.Value.Count > 1))
            {
                if (allowed.Contains(name))
                {
                    continue;
                }

                for (var i = 0; i < types.Count; i++)
                {
                    for (var j = i + 1; j < types.Count; j++)
                    {
                        var (left, right) = (types[i], types[j]);

                        // Deriving from ONE seam base is the fix, not the defect: two sidecars' own
                        // Fallen8TargetOptions are meant to be parallel, and each adds only the knobs
                        // it has. EverySidecarsSharedOptionsFamily_DerivesFromTheSeamsBase is what
                        // holds that half.
                        if (left.BaseType != null && left.BaseType == right.BaseType
                            && left.BaseType.Assembly == typeof(RestSeam).Assembly)
                        {
                            continue;
                        }

                        var shape = DeclaredMembers(left);
                        if (shape.Count > 0 && shape.SetEquals(DeclaredMembers(right)))
                        {
                            violations.Add(name + " is declared identically in "
                                + left.Assembly.GetName().Name + " and " + right.Assembly.GetName().Name
                                + " (" + shape.Count + " members: " + string.Join(", ", shape)
                                + "). Give it one home in fallen-8-rest-client and derive, or make the "
                                + "two genuinely different things different types.");
                        }
                    }
                }
            }

            AssertNoViolations(violations, "no type is copied between the REST-only deployables");
        }

        [TestMethod]
        public void EveryDeployablesServiceName_MatchesTheSelectorTheShippedDashboardUses()
        {
            // The pin derives its expectation from the CONSUMER. The per-tenant dashboard's log panel
            // selects streams by service_name, and Loki anchors its regexes fully, so
            // "fallen8.*" matched three of the four deployables and silently excluded
            // fallen-8-integrations - whose panel was described as "logs scoped to the selected
            // instance". Reading the selector out of the dashboard rather than restating it here means
            // a deliberate change to the convention has to change the dashboard first, which is the
            // right order, and a fifth deployable that misses the panel fails the suite instead of
            // shipping invisible.
            var root = TestRepo.Root();
            var dashboard = Path.Combine(root, "observability", "grafana", "dashboards", "per-tenant.json");
            Assert.IsTrue(File.Exists(dashboard),
                "the per-tenant dashboard is where the service-name convention is enforced in anger; "
                + "if it moved, move this pin with it rather than deleting it");

            var selector = Regex.Match(File.ReadAllText(dashboard),
                @"service_name\s*=~\s*\\?""(?<pattern>[^""\\]+)\\?""");
            Assert.IsTrue(selector.Success,
                "no {service_name=~\"...\"} selector found in " + Path.GetFileName(dashboard)
                + ". The dashboard is the source of this convention, so if it now selects logs another "
                + "way, this test has to learn the new way.");

            // Loki anchors the whole value; .NET does not, so say so explicitly.
            var pattern = new Regex("^(?:" + selector.Groups["pattern"].Value + ")$", RegexOptions.CultureInvariant);

            var declared = new List<(string Project, string Name)>();
            foreach (var project in new[]
            {
                "fallen-8-core-apiApp", "fallen-8-mcp", "fallen-8-integrations", "fallen-8-agents",
            })
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

            Assert.AreEqual(4, declared.Count,
                "expected one OTel service name per deployable, found " + declared.Count + ": "
                + string.Join(", ", declared.Select(d => d.Project + " -> " + d.Name)));

            var violations = declared
                .Where(d => !pattern.IsMatch(d.Name))
                .Select(d => d.Project + " declares service.name '" + d.Name + "', which the dashboard's "
                    + "selector /" + pattern + "/ does not match, so its logs are absent from the panel")
                .ToList();

            AssertNoViolations(violations,
                "every deployable's OTel service.name matches the shipped dashboard's stream selector");
        }

    }
}
