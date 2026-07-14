// MIT License
//
// DelegateValidationEndpointTest.cs
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
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Pipeline tests for POST /delegates/validate (feature web-ui, gap G-2), through the real
    /// ASP.NET pipeline via WebApplicationFactory: every delegate kind validates its canonical
    /// fragment, diagnostic positions are asserted in fragment coordinates (not just non-empty),
    /// empty/oversized fragments and unknown kinds behave per contract, and the endpoint sits
    /// behind the same auth + dynamic-code gates as the query endpoints. Also pins the
    /// Authorization: Bearer fallback of the API-key handler (feature web-ui, lightweight auth).
    /// </summary>
    [TestClass]
    public class DelegateValidationEndpointTest
    {
        private const string ApiKey = "delegate-validation-test-key";

        private sealed class ValidationFactory : WebApplicationFactory<Program>
        {
            private readonly bool _enableCode;
            private readonly bool _withApiKey;

            public ValidationFactory(bool enableCode = true, bool withApiKey = true)
            {
                _enableCode = enableCode;
                _withApiKey = withApiKey;
            }

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                // Volatile durability so booting the host writes no checkpoint/WAL.
                builder.UseSetting("Fallen8:Durability:Volatile", "true");
                builder.UseSetting("Fallen8:Security:EnableDynamicCodeExecution", _enableCode ? "true" : "false");
                if (_withApiKey)
                {
                    builder.UseSetting("Fallen8:Security:ApiKey", ApiKey);
                }
            }
        }

        private sealed class ValidationResponse
        {
            public bool Valid
            {
                get; set;
            }
            public List<DiagnosticResponse> Diagnostics { get; set; } = new List<DiagnosticResponse>();
        }

        private sealed class DiagnosticResponse
        {
            public int Line
            {
                get; set;
            }
            public int Column
            {
                get; set;
            }
            public int EndLine
            {
                get; set;
            }
            public int EndColumn
            {
                get; set;
            }
            public string Id
            {
                get; set;
            }
            public string Message
            {
                get; set;
            }
            public string Severity
            {
                get; set;
            }
        }

        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private static HttpClient Client(ValidationFactory factory, bool withKey = true)
        {
            var client = factory.CreateClient();
            if (withKey)
            {
                client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
            }
            return client;
        }

        private static async Task<HttpResponseMessage> PostValidateRaw(HttpClient client, string delegateKind, string fragment)
        {
            var payload = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["delegateKind"] = delegateKind,
                ["fragment"] = fragment
            });
            return await client.PostAsync("/delegates/validate",
                new StringContent(payload, Encoding.UTF8, "application/json"));
        }

        private static async Task<ValidationResponse> PostValidate(HttpClient client, string delegateKind, string fragment)
        {
            var response = await PostValidateRaw(client, delegateKind, fragment);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                "Expected 200 from /delegates/validate for kind '" + delegateKind + "'.");
            var body = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<ValidationResponse>(body, _jsonOptions);
        }

        #region valid fragments per kind

        [TestMethod]
        [DataRow("VertexFilter", "return (v) => v.Label == \"person\";")]
        [DataRow("EdgeFilter", "return (e) => e.EdgePropertyId == \"knows\";")]
        [DataRow("EdgePropertyFilter", "return (p) => p.StartsWith(\"k\");")]
        [DataRow("VertexCost", "return (v) => (double)v.GetOutDegree();")]
        [DataRow("EdgeCost", "return (e) => 1.0;")]
        [DataRow("GraphElementFilter", "return (ge) => ge.Label != null;")]
        public async Task ValidFragment_PerKind_ReturnsValid(string kind, string fragment)
        {
            using var factory = new ValidationFactory();
            using var client = Client(factory);

            var result = await PostValidate(client, kind, fragment);

            Assert.IsTrue(result.Valid, "Fragment for kind '" + kind + "' should be valid. Diagnostics: "
                + string.Join(" | ", result.Diagnostics.Select(d => d.Id + " " + d.Message)));
            Assert.IsFalse(result.Diagnostics.Any(d => d.Severity == "error"));
        }

        [TestMethod]
        public async Task TryGetPropertyIdiom_IsValid()
        {
            using var factory = new ValidationFactory();
            using var client = Client(factory);

            var result = await PostValidate(client, "VertexFilter",
                "return (v) => v.TryGetProperty(out int age, \"age\") && age > 30;");

            Assert.IsTrue(result.Valid);
        }

        [TestMethod]
        public async Task KindIsCaseInsensitive()
        {
            using var factory = new ValidationFactory();
            using var client = Client(factory);

            var result = await PostValidate(client, "vertexfilter", "return (v) => true;");

            Assert.IsTrue(result.Valid);
        }

        #endregion

        #region diagnostics and position mapping

        [TestMethod]
        public async Task SyntaxError_MissingSemicolon_PositionIsInFragmentCoordinates()
        {
            using var factory = new ValidationFactory();
            using var client = Client(factory);

            // "return (v) => true" is 18 chars; the missing ';' is expected at column 19 of line 1.
            var result = await PostValidate(client, "VertexFilter", "return (v) => true");

            Assert.IsFalse(result.Valid);
            var semicolon = result.Diagnostics.FirstOrDefault(d => d.Id == "CS1002");
            Assert.IsNotNull(semicolon, "Expected CS1002 (; expected). Got: "
                + string.Join(" | ", result.Diagnostics.Select(d => d.Id)));
            Assert.AreEqual(1, semicolon.Line, "Line must be mapped to fragment coordinates.");
            Assert.AreEqual(19, semicolon.Column, "Column must be unchanged by the wrapper (no reindentation).");
        }

        [TestMethod]
        public async Task SemanticError_OnSecondFragmentLine_LineAndColumnAreMapped()
        {
            using var factory = new ValidationFactory();
            using var client = Client(factory);

            // The unknown identifier 'zzz' sits on fragment line 2, column 26.
            var fragment = "var threshold = 30;\nreturn (v) => v.Label == zzz;";
            var result = await PostValidate(client, "VertexFilter", fragment);

            Assert.IsFalse(result.Valid);
            var unknown = result.Diagnostics.FirstOrDefault(d => d.Id == "CS0103");
            Assert.IsNotNull(unknown, "Expected CS0103 (name does not exist). Got: "
                + string.Join(" | ", result.Diagnostics.Select(d => d.Id)));
            Assert.AreEqual(2, unknown.Line);
            Assert.AreEqual(26, unknown.Column);
            Assert.AreEqual(2, unknown.EndLine);
            Assert.AreEqual(29, unknown.EndColumn);
        }

        [TestMethod]
        public async Task UnknownMember_IsSemanticError()
        {
            using var factory = new ValidationFactory();
            using var client = Client(factory);

            var result = await PostValidate(client, "VertexFilter", "return (v) => v.DoesNotExist;");

            Assert.IsFalse(result.Valid);
            var missing = result.Diagnostics.FirstOrDefault(d => d.Id == "CS1061");
            Assert.IsNotNull(missing);
            Assert.AreEqual(1, missing.Line);
            Assert.AreEqual("error", missing.Severity);
        }

        [TestMethod]
        public async Task WrongParameterTypeForKind_IsInvalid()
        {
            using var factory = new ValidationFactory();
            using var client = Client(factory);

            // EdgePropertyId is an EdgeModel member; a VertexFilter's parameter is a VertexModel.
            var result = await PostValidate(client, "VertexFilter", "return (v) => v.EdgePropertyId == \"x\";");

            Assert.IsFalse(result.Valid);
            Assert.IsTrue(result.Diagnostics.Any(d => d.Id == "CS1061"));
        }

        [TestMethod]
        public async Task UnbalancedBrace_DiagnosticIsClampedIntoFragment()
        {
            using var factory = new ValidationFactory();
            using var client = Client(factory);

            // The stray '{' makes Roslyn report trouble at/after the wrapper's trailer; the
            // mapped position must still land inside the two fragment lines.
            var fragment = "return (v) => {\nreturn true;";
            var result = await PostValidate(client, "VertexFilter", fragment);

            Assert.IsFalse(result.Valid);
            Assert.IsTrue(result.Diagnostics.Count > 0);
            foreach (var diagnostic in result.Diagnostics)
            {
                Assert.IsTrue(diagnostic.Line >= 1 && diagnostic.Line <= 2,
                    "Diagnostic line " + diagnostic.Line + " must be clamped into the fragment (" + diagnostic.Id + ").");
            }
        }

        [TestMethod]
        public async Task Warning_DoesNotBlockValidity_ButIsReported()
        {
            using var factory = new ValidationFactory();
            using var client = Client(factory);

            // CS0219: assigned but never used - a warning, not an error.
            var fragment = "int unused = 1;\nreturn (v) => true;";
            var result = await PostValidate(client, "VertexFilter", fragment);

            Assert.IsTrue(result.Valid, "Warnings must not invalidate a fragment.");
            var warning = result.Diagnostics.FirstOrDefault(d => d.Id == "CS0219");
            Assert.IsNotNull(warning, "The CS0219 warning should be reported.");
            Assert.AreEqual("warning", warning.Severity);
            Assert.AreEqual(1, warning.Line);
        }

        #endregion

        #region empty, oversized, unknown kind

        [TestMethod]
        public async Task EmptyFragment_IsValid()
        {
            using var factory = new ValidationFactory();
            using var client = Client(factory);

            var result = await PostValidate(client, "VertexFilter", "");

            Assert.IsTrue(result.Valid, "An empty fragment means 'match everything' and is valid.");
            Assert.AreEqual(0, result.Diagnostics.Count);
        }

        [TestMethod]
        public async Task MissingFragmentProperty_IsValid()
        {
            using var factory = new ValidationFactory();
            using var client = Client(factory);

            var response = await client.PostAsync("/delegates/validate",
                new StringContent("{\"delegateKind\":\"EdgeCost\"}", Encoding.UTF8, "application/json"));

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var body = JsonSerializer.Deserialize<ValidationResponse>(
                await response.Content.ReadAsStringAsync(), _jsonOptions);
            Assert.IsTrue(body.Valid);
        }

        [TestMethod]
        public async Task OversizedFragment_IsRejectedWithoutCompiling()
        {
            using var factory = new ValidationFactory();
            using var client = Client(factory);

            var oversized = "return (v) => true; //" + new string('x', 100_001);
            var result = await PostValidate(client, "VertexFilter", oversized);

            Assert.IsFalse(result.Valid);
            Assert.AreEqual(1, result.Diagnostics.Count);
            Assert.AreEqual("F8LIMIT", result.Diagnostics[0].Id);
            Assert.AreEqual("error", result.Diagnostics[0].Severity);
            Assert.AreEqual(1, result.Diagnostics[0].Line);
        }

        [TestMethod]
        public async Task UnknownKind_Returns400()
        {
            using var factory = new ValidationFactory();
            using var client = Client(factory);

            var response = await PostValidateRaw(client, "LabelFilter", "return (l) => true;");

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            StringAssert.Contains(body, "LabelFilter");
        }

        [TestMethod]
        public async Task MissingKind_Returns400()
        {
            using var factory = new ValidationFactory();
            using var client = Client(factory);

            var response = await client.PostAsync("/delegates/validate",
                new StringContent("{\"fragment\":\"return (v) => true;\"}", Encoding.UTF8, "application/json"));

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        }

        #endregion

        #region security posture

        [TestMethod]
        public async Task Anonymous_Returns401()
        {
            using var factory = new ValidationFactory();
            using var client = Client(factory, withKey: false);

            var response = await PostValidateRaw(client, "VertexFilter", "return (v) => true;");

            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [TestMethod]
        public async Task DynamicCodeDisabled_Returns403()
        {
            using var factory = new ValidationFactory(enableCode: false);
            using var client = Client(factory);

            var response = await PostValidateRaw(client, "VertexFilter", "return (v) => true;");

            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        }

        [TestMethod]
        public async Task BearerAuthorization_IsAccepted()
        {
            using var factory = new ValidationFactory();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", "Bearer " + ApiKey);

            var result = await PostValidate(client, "VertexFilter", "return (v) => true;");

            Assert.IsTrue(result.Valid);
        }

        [TestMethod]
        public async Task BearerAuthorization_WrongKey_Returns401()
        {
            using var factory = new ValidationFactory();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", "Bearer not-the-key");

            var response = await PostValidateRaw(client, "VertexFilter", "return (v) => true;");

            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        #endregion
    }
}
