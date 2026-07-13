// MIT License
//
// OpenApiDocumentTest.cs
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
    /// Verifies that the OpenAPI document still generates correctly after the openapi-10 feature
    /// bumped <c>Microsoft.AspNetCore.OpenApi</c> to 10.x, removed the hand-written
    /// <c>XmlDocumentationOperationTransformer</c>, and started relying on .NET 10's native
    /// XML-doc reading.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The test boots the real application through <see cref="WebApplicationFactory{TEntryPoint}"/>
    /// (so the real <c>Program.cs</c> wiring and the real <c>AddOpenApi("v0.1")</c> call run) and
    /// performs an HTTP GET against the framework-served <c>/openapi/v0.1.json</c> endpoint. This
    /// exercises the genuine end-to-end document-generation and serialization path, not a stand-in.
    /// </para>
    /// <para>
    /// It asserts three things the feature promises: the document generates without error, it reports
    /// OpenAPI <c>3.1.x</c> (the accepted upgrade from 3.0.1), and it carries controller XML
    /// <c>&lt;summary&gt;</c>/<c>&lt;remarks&gt;</c> content as operation summaries/descriptions —
    /// proving the native XML reader now does what the deleted transformer used to do.
    /// </para>
    /// </remarks>
    [TestClass]
    public class OpenApiDocumentTest
    {
        private const String DocumentPath = "/openapi/v0.1.json";

        private static readonly String[] OperationMethods =
        {
            "get", "put", "post", "delete", "patch", "options", "head", "trace"
        };

        /// <summary>
        /// Boots the app in the Development environment (the OpenAPI + Scalar endpoints are only
        /// mapped there) and keeps every other part of the real pipeline intact.
        /// </summary>
        private sealed class DevelopmentApiFactory : WebApplicationFactory<Program>
        {
            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseEnvironment("Development");
            }
        }

        [TestMethod]
        public async Task OpenApiDocument_GeneratesAs31_WithNativeXmlContent()
        {
            using var factory = new DevelopmentApiFactory();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync(DocumentPath);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                "The framework should serve the OpenAPI document at " + DocumentPath + " in Development.");

            var json = await response.Content.ReadAsStringAsync();
            Assert.IsFalse(String.IsNullOrWhiteSpace(json), "The OpenAPI document body must not be empty.");

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            // (1) OpenAPI version: 10.x native path emits 3.1.x (accepted upgrade from 3.0.1).
            Assert.IsTrue(root.TryGetProperty("openapi", out var versionElement),
                "The document must carry an 'openapi' version field.");
            var version = versionElement.GetString();
            StringAssert.StartsWith(version, "3.1",
                "Expected OpenAPI 3.1.x from the .NET 10 native OpenAPI path, but got: " + version);

            // (2) There must be operations to document.
            Assert.IsTrue(root.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Object,
                "The document must contain a non-empty 'paths' object.");

            var summaries = new List<String>();
            var descriptions = new List<String>();
            CollectOperationText(paths, summaries, descriptions);

            Assert.IsTrue(summaries.Count > 0,
                "At least one operation must carry a summary sourced from a controller <summary> comment.");

            // (3) The native XML reader must surface a known controller <summary> as an operation
            //     summary. GraphController.AddVertex documents "Creates a new vertex in the graph".
            //     This is precisely the job the deleted transformer used to perform.
            var hasKnownSummary = summaries.Exists(
                s => s != null && s.Contains("Creates a new vertex in the graph", StringComparison.Ordinal));
            Assert.IsTrue(hasKnownSummary,
                "A known controller <summary> ('Creates a new vertex in the graph') must flow into the " +
                "document as an operation summary via the native XML reader. Summaries seen: " +
                String.Join(" | ", summaries));

            // Richer content: <remarks> flow through as operation descriptions. AddVertex's remarks
            // include a "Sample request:" block. This proves the enriched 3.1 output, not just summaries.
            var hasKnownDescription = descriptions.Exists(
                d => d != null && d.Contains("Sample request", StringComparison.Ordinal));
            Assert.IsTrue(hasKnownDescription,
                "A controller <remarks> block ('Sample request') must flow into the document as an " +
                "operation description via the native XML reader.");
        }

        private static void CollectOperationText(JsonElement paths, List<String> summaries, List<String> descriptions)
        {
            foreach (var pathItem in paths.EnumerateObject())
            {
                if (pathItem.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var method in OperationMethods)
                {
                    if (!pathItem.Value.TryGetProperty(method, out var operation) ||
                        operation.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (operation.TryGetProperty("summary", out var summary) &&
                        summary.ValueKind == JsonValueKind.String)
                    {
                        summaries.Add(summary.GetString());
                    }

                    if (operation.TryGetProperty("description", out var description) &&
                        description.ValueKind == JsonValueKind.String)
                    {
                        descriptions.Add(description.GetString());
                    }
                }
            }
        }
    }
}
