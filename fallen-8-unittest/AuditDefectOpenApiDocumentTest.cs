// MIT License
//
// AuditDefectOpenApiDocumentTest.cs
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
    /// Pins the two audit-defect fixes in the OpenAPI document transformer (fallen-8-core-apiApp/Program.cs):
    /// B34 - the info block carried the ASP.NET defaults (title = assembly name, version 1.0.0) instead of
    /// the product name and the API version every route is served under; B35 - the document described no
    /// security scheme at all, so neither Scalar nor a generated client had anywhere to put the API key.
    /// </summary>
    /// <remarks>
    /// The decided contract for B35: the API-key scheme AND a document-level requirement are declared
    /// ALWAYS, because only ENFORCEMENT is per-deployment (the handler demands a key solely when
    /// <c>Fallen8:Security:ApiKey</c> is configured) while the credential's shape never changes - a
    /// conditional declaration would be missing from the published reference and the pinned snapshot,
    /// both produced without a key. The <c>[AllowAnonymous]</c> operations override the requirement with
    /// an empty array so they are not misreported as secured.
    /// </remarks>
    [TestClass]
    public class AuditDefectOpenApiDocumentTest
    {
        private const String DocumentPath = "/openapi/v0.1.json";
        private const String ExpectedTitle = "Fallen-8 REST API";
        private const String ExpectedVersion = "0.1";
        private const String SchemeName = "ApiKey";
        private const String DefaultHeader = "X-Api-Key";
        private const String ApiKey = "audit-defect-openapi-key";

        private static readonly String[] OperationMethods =
        {
            "get", "put", "post", "delete", "patch", "options", "head", "trace"
        };

        /// <summary>
        /// The operations that opt out with <c>[AllowAnonymous]</c> (AdminController: /status,
        /// /vertex/count, /edge/count) - each also answering under its /ns/{ns} route twin, which is why
        /// the transformer reads the action's metadata instead of keeping a path list.
        /// </summary>
        private static readonly String[] AnonymousOperations =
        {
            "GET /edge/count",
            "GET /ns/{ns}/edge/count",
            "GET /ns/{ns}/status",
            "GET /ns/{ns}/vertex/count",
            "GET /status",
            "GET /vertex/count"
        };

        /// <summary>
        /// Boots the real application in Development (only there are /openapi and Scalar mapped) with a
        /// volatile engine, so generating the document writes no checkpoint or WAL.
        /// </summary>
        private sealed class DocumentFactory : WebApplicationFactory<Program>
        {
            private readonly IReadOnlyDictionary<String, String> _settings;

            public DocumentFactory(IReadOnlyDictionary<String, String> settings = null)
            {
                _settings = settings ?? new Dictionary<String, String>();
            }

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseEnvironment("Development");
                builder.UseSetting("Fallen8:Durability:Volatile", "true");
                foreach (var setting in _settings)
                {
                    builder.UseSetting(setting.Key, setting.Value);
                }
            }
        }

        /// <summary>
        /// Fetches the served document. <paramref name="credentialHeader"/> is needed only on an
        /// instance that configures a key: /openapi does not opt out of authorization, so it answers
        /// 401 without the credential.
        /// </summary>
        private static async Task<JsonDocument> ServedDocument(DocumentFactory factory, String credentialHeader = null)
        {
            using var client = factory.CreateClient();
            if (credentialHeader != null)
            {
                client.DefaultRequestHeaders.Add(credentialHeader, ApiKey);
            }

            using var response = await client.GetAsync(DocumentPath);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                "The framework must serve the OpenAPI document at " + DocumentPath + " in Development.");
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }

        #region B34 - the info block

        [TestMethod]
        public async Task Info_CarriesTheProductTitleAndTheApiVersion()
        {
            using var factory = new DocumentFactory();
            using var document = await ServedDocument(factory);

            var info = document.RootElement.GetProperty("info");

            // Previously: "fallen-8-core-apiApp | v0.1" (the assembly name) and "1.0.0".
            Assert.AreEqual(ExpectedTitle, info.GetProperty("title").GetString(),
                "The document must carry the product title, not the assembly name.");
            Assert.AreEqual(ExpectedVersion, info.GetProperty("version").GetString(),
                "The document version must be the API version every route is served under (0.1), not 1.0.0.");

            // Unchanged by the fix: the namespace-scheme description keeps its one home here.
            StringAssert.StartsWith(info.GetProperty("description").GetString(),
                "A Fallen-8 hosts isolated graph namespaces.",
                "Setting title/version must not disturb the document-level description.");
        }

        #endregion

        #region B35 - the API-key security scheme

        [TestMethod]
        public async Task SecurityScheme_IsDeclared_OnAnInstanceWithoutAKey()
        {
            // The keyless instance is the one that matters most: it is how the published reference and
            // the pinned snapshot are produced, and it is where the scheme used to be missing entirely.
            using var factory = new DocumentFactory();
            using var document = await ServedDocument(factory);

            AssertApiKeyScheme(document, DefaultHeader);
            AssertDocumentLevelRequirement(document);
        }

        [TestMethod]
        public async Task SecurityScheme_UsesTheConfiguredHeader_AndStaysDeclaredWhenAKeyIsConfigured()
        {
            var settings = new Dictionary<String, String>
            {
                ["Fallen8:Security:ApiKey"] = ApiKey,
                ["Fallen8:Security:ApiKeyHeader"] = "X-Fallen8-Audit-Key"
            };
            using var factory = new DocumentFactory(settings);
            using var document = await ServedDocument(factory, "X-Fallen8-Audit-Key");

            AssertApiKeyScheme(document, "X-Fallen8-Audit-Key");
            AssertDocumentLevelRequirement(document);

            // The override still applies on a key-secured instance - these operations really do answer
            // anonymously there (see ApiSecurityBoundaryTest).
            AssertEmptySecurity(document, "GET", "/status");
        }

        [TestMethod]
        public async Task SecurityScheme_FallsBackToTheDefaultHeader_WhenTheConfiguredOneIsBlank()
        {
            // A blank header must not produce a scheme with an empty "name": the runtime handler falls
            // back to X-Api-Key, so the document must say the same thing.
            var settings = new Dictionary<String, String>
            {
                ["Fallen8:Security:ApiKeyHeader"] = "   "
            };
            using var factory = new DocumentFactory(settings);
            using var document = await ServedDocument(factory);

            AssertApiKeyScheme(document, DefaultHeader);
        }

        [TestMethod]
        public async Task OperationSecurity_IsEmptyOnExactlyTheAnonymousOperations()
        {
            using var factory = new DocumentFactory();
            using var document = await ServedDocument(factory);

            var withOverride = new List<String>();
            foreach (var pathItem in document.RootElement.GetProperty("paths").EnumerateObject())
            {
                foreach (var method in OperationMethods)
                {
                    if (!pathItem.Value.TryGetProperty(method, out var operation) ||
                        operation.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (!operation.TryGetProperty("security", out var security))
                    {
                        // No override: the operation inherits the document-level API-key requirement.
                        continue;
                    }

                    Assert.AreEqual(JsonValueKind.Array, security.ValueKind,
                        "An operation-level 'security' must be an array: " + method + " " + pathItem.Name);
                    Assert.AreEqual(0, security.GetArrayLength(),
                        "The only operation-level override the transformer writes is the EMPTY requirement " +
                        "([AllowAnonymous]), but " + method.ToUpperInvariant() + " " + pathItem.Name +
                        " carries a non-empty one.");
                    withOverride.Add(method.ToUpperInvariant() + " " + pathItem.Name);
                }
            }

            withOverride.Sort(StringComparer.Ordinal);
            CollectionAssert.AreEqual(AnonymousOperations, withOverride,
                "Exactly the [AllowAnonymous] operations (both route twins of each) may carry an empty " +
                "'security' array; every other operation must inherit the document-level requirement. " +
                "Seen: " + String.Join(", ", withOverride));

            // A read and a write, spot-checked from the other side: no override, so both are described
            // as requiring the credential.
            AssertInheritsSecurity(document, "GET", "/graph");
            AssertInheritsSecurity(document, "PUT", "/edge");
        }

        #endregion

        #region The pinned snapshot must carry the same contract

        [TestMethod]
        public void PinnedSnapshot_CarriesTheInfoBlockAndTheSecurityScheme()
        {
            var snapshotFile = Path.Combine(TestRepo.Root(), "features", "done", "web-ui", "openapi-v0.1.json");
            Assert.IsTrue(File.Exists(snapshotFile), "pinned snapshot not found: " + snapshotFile);

            using var snapshot = JsonDocument.Parse(File.ReadAllText(snapshotFile));
            const String regenerate = " - regenerate it (pwsh scripts/update-openapi-snapshot.ps1) and review the diff.";

            var info = snapshot.RootElement.GetProperty("info");
            Assert.AreEqual(ExpectedTitle, info.GetProperty("title").GetString(),
                "The pinned snapshot's info.title is stale" + regenerate);
            Assert.AreEqual(ExpectedVersion, info.GetProperty("version").GetString(),
                "The pinned snapshot's info.version is stale" + regenerate);

            // The snapshot is regenerated without an API key, which is exactly why the scheme is
            // declared unconditionally: a conditional one would never reach the published reference.
            AssertApiKeyScheme(snapshot, DefaultHeader);
            AssertDocumentLevelRequirement(snapshot);
            AssertEmptySecurity(snapshot, "GET", "/status");
        }

        #endregion

        private static void AssertApiKeyScheme(JsonDocument document, String expectedHeader)
        {
            Assert.IsTrue(document.RootElement.TryGetProperty("components", out var components),
                "The document must carry a 'components' object.");
            Assert.IsTrue(components.TryGetProperty("securitySchemes", out var schemes),
                "The document must declare 'components.securitySchemes' - without it no consumer can " +
                "send the API key (audit defect B35).");
            Assert.IsTrue(schemes.TryGetProperty(SchemeName, out var scheme),
                "The API-key scheme must be named '" + SchemeName + "'.");

            Assert.AreEqual("apiKey", scheme.GetProperty("type").GetString(), "The scheme type must be apiKey.");
            Assert.AreEqual("header", scheme.GetProperty("in").GetString(), "The key travels in a header.");
            Assert.AreEqual(expectedHeader, scheme.GetProperty("name").GetString(),
                "The scheme's header name must follow Fallen8:Security:ApiKeyHeader (default " +
                DefaultHeader + ").");
            StringAssert.Contains(scheme.GetProperty("description").GetString(), "Fallen8:Security:ApiKey",
                "The scheme description must name the setting that turns enforcement on, since the " +
                "declaration itself is unconditional.");
        }

        private static void AssertDocumentLevelRequirement(JsonDocument document)
        {
            Assert.IsTrue(document.RootElement.TryGetProperty("security", out var security) &&
                security.ValueKind == JsonValueKind.Array,
                "The document must declare a document-level 'security' requirement so every operation " +
                "that does not opt out is described as needing the credential.");
            Assert.AreEqual(1, security.GetArrayLength(), "One alternative: the API key.");

            // Microsoft.OpenApi 2.x writes a requirement key as the JSON pointer to the scheme rather
            // than the bare scheme name, so accept either spelling: what is pinned here is OUR
            // contract (a document-level requirement naming the ApiKey scheme), not the library's
            // spelling of it.
            var names = security[0].EnumerateObject().Select(p => p.Name).ToList();
            Assert.AreEqual(1, names.Count, "The requirement must reference exactly one scheme.");
            Assert.IsTrue(names[0] == SchemeName || names[0] == "#/components/securitySchemes/" + SchemeName,
                "The requirement must reference the declared ApiKey scheme, but references: " + names[0]);
            Assert.AreEqual(JsonValueKind.Array, security[0].EnumerateObject().First().Value.ValueKind,
                "The scopes value must be an array (empty for an apiKey scheme).");
        }

        private static void AssertEmptySecurity(JsonDocument document, String method, String path)
        {
            var operation = document.RootElement.GetProperty("paths").GetProperty(path)
                .GetProperty(method.ToLowerInvariant());
            Assert.IsTrue(operation.TryGetProperty("security", out var security),
                method + " " + path + " is [AllowAnonymous] and must override the document-level " +
                "requirement with an empty 'security' array.");
            Assert.AreEqual(JsonValueKind.Array, security.ValueKind, "'security' must be an array.");
            Assert.AreEqual(0, security.GetArrayLength(),
                method + " " + path + " must carry an EMPTY 'security' array (no credential required).");
        }

        private static void AssertInheritsSecurity(JsonDocument document, String method, String path)
        {
            var operation = document.RootElement.GetProperty("paths").GetProperty(path)
                .GetProperty(method.ToLowerInvariant());
            Assert.IsFalse(operation.TryGetProperty("security", out _),
                method + " " + path + " is not [AllowAnonymous], so it must carry no " +
                "operation-level override and inherit the document-level API-key requirement.");
        }
    }
}
