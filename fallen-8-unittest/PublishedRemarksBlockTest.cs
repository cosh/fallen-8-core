// MIT License
//
// PublishedRemarksBlockTest.cs
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
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Pins the one-<c>&lt;remarks&gt;</c>-element-per-doc-comment rule and the paragraphs it decides
    /// to publish: .NET 10's native XML-doc reader maps only the FIRST <c>&lt;remarks&gt;</c> element of
    /// a member to the operation description, so a second one is dropped in silence.
    /// <para>History (audit defect B07): the three code endpoints (POST /storedquery,
    /// POST /path/{from}/to/{to}, PUT /subgraph) each carried TWO of them, so the trust-boundary
    /// SECURITY paragraph, the semantic-traversal contract and the execution-budget rules never
    /// reached the published document, although the compiled XML file contained them.</para>
    /// </summary>
    /// <remarks>
    /// The fix merged each action's second block into its single <c>&lt;remarks&gt;</c> element, so the
    /// assertions below are made against the GENERATED document (fetched over HTTP from the real host),
    /// not against the source text: a source-level "one block per member" check alone would not prove
    /// that the merged sentences are actually published. The source-level guard is kept as a second,
    /// cheaper test so the trap cannot be re-entered by a future edit.
    /// </remarks>
    [TestClass]
    public class PublishedRemarksBlockTest
    {
        private const String DocumentPath = "/openapi/v0.1.json";

        /// <summary>
        /// Phrases from the FIRST remarks block (which always reached the document) plus the SECOND
        /// block (which was dropped). Both are asserted, so the merge cannot fix one by losing the other.
        /// Each phrase must be unique within its description, because the assertion counts occurrences.
        /// </summary>
        private static readonly String[] StoredQueryPhrases =
        {
            "Exactly one of",                                     // first block
            "Sample request",                                     // first block
            "SECURITY: registration compiles C# fragments",        // second block, was dropped
            "IN-PROCESS",
        };

        private static readonly String[] PathPhrases =
        {
            "dynamic filtering and cost calculation",             // first block
            "Sample request",                                      // first block
            "SEMANTIC TRAVERSAL",                                  // second block, was dropped
            "costBySimilarity",
            "SECURITY: inline filter/cost fragments",
            "EXECUTION BUDGET",
            "timeBudgetSeconds",
            "storedQuery",
        };

        private static readonly String[] SubGraphPhrases =
        {
            "A null/empty fragment matches everything",            // first block
            "Sample request",                                      // first block
            "SEMANTIC SUBGRAPHS",                                  // second block, was dropped
            "semanticMinScore",
            "SECURITY: inline filter/pattern fragments",
            "storedQuery",
        };

        /// <summary>
        /// Every route the three code actions answer under, including the <c>/ns/{ns}</c> twin (the same
        /// action, hence the same description, so a merge that only reached the bare route would be a bug).
        /// Declared after the phrase arrays: static field initializers run in textual order.
        /// </summary>
        private static readonly (String Path, String Method, String[] Phrases)[] ExpectedDescriptions =
        {
            ("/storedquery", "post", StoredQueryPhrases),
            ("/ns/{ns}/storedquery", "post", StoredQueryPhrases),
            ("/path/{from}/to/{to}", "post", PathPhrases),
            ("/ns/{ns}/path/{from}/to/{to}", "post", PathPhrases),
            ("/subgraph", "put", SubGraphPhrases),
            ("/ns/{ns}/subgraph", "put", SubGraphPhrases),
        };

        private static readonly String[] OperationMethods =
        {
            "get", "put", "post", "delete", "patch", "options", "head", "trace"
        };

        /// <summary>
        /// Boots the real application in Development (only there are /openapi and Scalar mapped) with a
        /// volatile engine, so generating the document writes no checkpoint or WAL.
        /// </summary>
        private sealed class DocumentHostFactory : WebApplicationFactory<Program>
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseEnvironment("Development");
                builder.UseSetting("Fallen8:Durability:Volatile", "true");
            }
        }

        /// <summary>
        /// The regression proper: the merged remarks must be present in the SERVED document's operation
        /// descriptions - the SECURITY / SEMANTIC / EXECUTION BUDGET sentences alongside the sentences
        /// that were already published, each exactly once.
        /// </summary>
        [TestMethod]
        public async Task CodeEndpointDescriptions_CarryTheMergedSecurityAndSemanticRemarks()
        {
            using var factory = new DocumentHostFactory();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync(DocumentPath);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                "The framework should serve the OpenAPI document at " + DocumentPath + " in Development.");

            var json = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(json);
            Assert.IsTrue(document.RootElement.TryGetProperty("paths", out var paths) &&
                          paths.ValueKind == JsonValueKind.Object,
                "The served document must contain a 'paths' object.");

            foreach (var expected in ExpectedDescriptions)
            {
                var description = Description(paths, expected.Path, expected.Method);
                Assert.IsFalse(String.IsNullOrWhiteSpace(description),
                    expected.Method.ToUpperInvariant() + " " + expected.Path +
                    " must carry an operation description sourced from its <remarks> block.");

                foreach (var phrase in expected.Phrases)
                {
                    var occurrences = Occurrences(description, phrase);
                    Assert.AreEqual(1, occurrences,
                        "The description of " + expected.Method.ToUpperInvariant() + " " + expected.Path +
                        " must contain \"" + phrase + "\" exactly once (a second <remarks> element is " +
                        "dropped by the XML-doc reader; a sloppy merge duplicates a paragraph). Found " +
                        occurrences + " occurrence(s) in:\n" + description);
                }
            }
        }

        /// <summary>
        /// Guards the trap itself: no action in the API app may declare two <c>&lt;remarks&gt;</c>
        /// elements in one doc-comment block, because only the first is published. A doc comment is the
        /// run of contiguous <c>///</c> lines preceding a member, so counting per run matches exactly
        /// what the compiler emits for that member.
        /// </summary>
        [TestMethod]
        public void NoControllerDocCommentBlock_DeclaresTwoRemarksElements()
        {
            var controllers = Path.Combine(TestRepo.Root(), "fallen-8-core-apiApp", "Controllers");
            Assert.IsTrue(Directory.Exists(controllers), "controller sources not found: " + controllers);

            var offenders = new List<String>();
            foreach (var file in Directory.EnumerateFiles(controllers, "*.cs", SearchOption.AllDirectories))
            {
                var lines = File.ReadAllLines(file);
                var blockStart = 0;
                var remarks = 0;
                var inBlock = false;

                for (var i = 0; i < lines.Length; i++)
                {
                    if (lines[i].TrimStart().StartsWith("///", StringComparison.Ordinal))
                    {
                        if (!inBlock)
                        {
                            inBlock = true;
                            blockStart = i + 1;
                            remarks = 0;
                        }

                        remarks += Occurrences(lines[i], "<remarks>");
                        continue;
                    }

                    if (inBlock)
                    {
                        if (remarks > 1)
                        {
                            offenders.Add(Path.GetFileName(file) + ":" + blockStart + " declares " + remarks +
                                          " <remarks> elements");
                        }

                        inBlock = false;
                    }
                }

                if (inBlock && remarks > 1)
                {
                    offenders.Add(Path.GetFileName(file) + ":" + blockStart + " declares " + remarks +
                                  " <remarks> elements");
                }
            }

            Assert.AreEqual(0, offenders.Count,
                "A doc-comment block may declare at most one <remarks> element: the XML-doc reader maps " +
                "only the first one to the OpenAPI operation description, silently dropping the rest. " +
                "Merge the paragraphs into one block.\n" + String.Join("\n", offenders));
        }

        /// <summary>Reads one operation's description, or null when the operation is absent.</summary>
        private static String Description(JsonElement paths, String path, String method)
        {
            if (!paths.TryGetProperty(path, out var pathItem) || pathItem.ValueKind != JsonValueKind.Object)
            {
                Assert.Fail("The served document does not describe the path " + path + ". Known paths: " +
                    String.Join(", ", paths.EnumerateObject().Select(p => p.Name)));
            }

            Assert.IsTrue(OperationMethods.Contains(method, StringComparer.Ordinal),
                "unknown HTTP method in the expectation table: " + method);

            if (!pathItem.TryGetProperty(method, out var operation) || operation.ValueKind != JsonValueKind.Object)
            {
                Assert.Fail("The served document does not describe " + method.ToUpperInvariant() + " " + path + ".");
            }

            return operation.TryGetProperty("description", out var description) &&
                   description.ValueKind == JsonValueKind.String
                ? description.GetString()
                : null;
        }

        /// <summary>Counts non-overlapping ordinal occurrences of <paramref name="needle"/>.</summary>
        private static Int32 Occurrences(String haystack, String needle)
        {
            if (String.IsNullOrEmpty(haystack) || String.IsNullOrEmpty(needle))
            {
                return 0;
            }

            var count = 0;
            var index = haystack.IndexOf(needle, StringComparison.Ordinal);
            while (index >= 0)
            {
                count++;
                index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
            }

            return count;
        }
    }
}
