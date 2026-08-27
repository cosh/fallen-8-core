// MIT License
//
// ModelPinTest.cs
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
    /// The NL-assist model defaults must name an explicit published VERSION, and the three files
    /// that carry them must agree.
    ///
    /// Why this is a test rather than a convention: the fine-tune pipeline republishes over the
    /// same ":latest" tag, so an unpinned default made every deployment silently follow whatever
    /// was published last, with an identical model name in the UI and nothing reporting the drift.
    /// Pinning fixes that - but the default is written in three places (compose, the sidecar's
    /// in-container fallback, and the offline pre-seed script), and compose cannot compute a value
    /// from a shared file, so a single literal home is not available. Bumping two of three would
    /// pin inconsistent models depending on how the stack was started, which is exactly the class
    /// of drift the pin exists to prevent. So the drift is asserted away instead.
    ///
    /// The pin may legitimately name an OLDER version than the newest release:
    /// scripts/tag-models.sh gives one version per distinct build, so a release that ships no
    /// retrained model mints no model tag at all. These assertions are deliberately offline, so a
    /// pin naming a version that was never published passes here and fails at pull time instead.
    /// </summary>
    [TestClass]
    public class ModelPinTest
    {
        /// <summary>Files that each declare a default for the model repositories.</summary>
        private static readonly string[] _files =
        {
            "docker-compose.yml",
            Path.Combine("scripts", "ollama-init.sh"),
            Path.Combine("scripts", "ensure-models.sh"),
        };

        private static readonly string[] _variables = { "F8_DELEGATE_REPO", "F8_PHI4F8_REPO" };

        /// <summary>Reads the ${VAR:-default} fallback for one variable out of one file.</summary>
        private static string DefaultFor(string relativePath, string variable)
        {
            var path = Path.Combine(TestRepo.Root(), relativePath);
            Assert.IsTrue(File.Exists(path), $"{relativePath} is missing; this test's file list is stale.");

            var matches = Regex.Matches(File.ReadAllText(path), @"\$\{" + variable + @":-([^}]+)\}");
            Assert.AreEqual(
                1,
                matches.Count,
                $"expected exactly one ${{{variable}:-...}} default in {relativePath}, found {matches.Count}. " +
                "If a second one is deliberate, teach this test which is authoritative.");

            return matches[0].Groups[1].Value.Trim();
        }

        [TestMethod]
        public void ModelDefaults_NameAnExplicitVersion()
        {
            foreach (var variable in _variables)
            {
                foreach (var file in _files)
                {
                    var value = DefaultFor(file, variable);

                    StringAssert.Contains(
                        value,
                        ":",
                        $"{file}: {variable} defaults to '{value}', which carries no tag and therefore resolves " +
                        "to :latest. The fine-tune pipeline republishes over :latest, so this would make the " +
                        "deployment follow whatever was published last. Pin a published version, e.g. " +
                        "'<namespace>/phi4-f8-mini:v0.0.35'.");

                    var tag = value.Substring(value.LastIndexOf(':') + 1);
                    Assert.AreNotEqual(
                        "latest",
                        tag,
                        $"{file}: {variable} pins ':latest' explicitly, which is the moving tag this pin exists " +
                        "to avoid. Name a version instead.");
                    Assert.IsTrue(
                        Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$"),
                        $"{file}: {variable} pins tag '{tag}', which is not a vX.Y.Z release version. " +
                        "scripts/tag-models.sh only creates tags in that shape.");
                }
            }
        }

        [TestMethod]
        public void ModelDefaults_AgreeAcrossEveryFileThatDeclaresThem()
        {
            foreach (var variable in _variables)
            {
                var byFile = _files.ToDictionary(file => file, file => DefaultFor(file, variable));
                var distinct = byFile.Values.Distinct(StringComparer.Ordinal).ToList();

                Assert.AreEqual(
                    1,
                    distinct.Count,
                    $"{variable} has {distinct.Count} different defaults, so which model you get depends on how " +
                    "the stack was started: " +
                    string.Join("; ", byFile.Select(pair => $"{pair.Key} = '{pair.Value}'")) +
                    ". Bump them together.");
            }
        }

        [TestMethod]
        public void ModelDefaults_PinTheSameVersionForBothVariants()
        {
            // Not strictly required - the two models could legitimately sit at different versions -
            // but they are published and tagged together by the release, so a mismatch is far more
            // likely to be a half-finished bump than a deliberate choice.
            var versions = _variables
                .Select(variable => DefaultFor(_files[0], variable))
                .Select(value => value.Substring(value.LastIndexOf(':') + 1))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            Assert.AreEqual(
                1,
                versions.Count,
                "the two model variants are pinned to different versions (" + string.Join(", ", versions) +
                "). They are tagged together by the release, so this is probably a partial bump. If it is " +
                "deliberate, relax this test and say why.");
        }
    }
}
