// MIT License
//
// CodeQualityTest.cs
//
// Copyright (c) 2025 Henning Rauch
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
        private static readonly string[] _allProjects = { "fallen-8-core", "fallen-8-core-apiApp", "fallen-8-unittest" };
        private static readonly string[] _productProjects = { "fallen-8-core", "fallen-8-core-apiApp" };

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
            // stdout belongs to the host. Tests and benchmarks may print - product code not.
            var violations = new List<string>();
            foreach (var file in SourceFiles(_productProjects))
            {
                if (CodeLines(file).Any(l => l.Contains("Console.Write", StringComparison.Ordinal)))
                {
                    violations.Add(file);
                }
            }

            AssertNoViolations(violations, "no Console.Write* in product code");
        }

        [TestMethod]
        public void ProductCode_UsesNoLocalClock_OutsideTheDocumentedAllowlist()
        {
            // DateTime.Now is local and DST-sensitive; new code uses UtcNow (or stays off wall
            // clocks entirely). DateHelper is the documented exception: its epoch semantics are
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

                if (CodeLines(file).Any(l => Regex.IsMatch(l, @"\bDateTime\.Now\b")))
                {
                    violations.Add(file);
                }
            }

            AssertNoViolations(violations, "no DateTime.Now in product code (allowlist: DateHelper.cs)");
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
    }
}
