// MIT License
//
// DoclingClientTest.cs
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
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Ingestion;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Feature unstructured-ingestion FR-2: the docling-serve client against a fake HTTP
    ///   handler - request shape (multipart, both to_formats), response parsing, the
    ///   markdown-only fallback, and the unavailability fault mapping.
    /// </summary>
    [TestClass]
    public class DoclingClientTest
    {
        private sealed class FakeHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

            public HttpRequestMessage LastRequest
            {
                get; private set;
            }

            public String LastRequestBody
            {
                get; private set;
            }

            public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            {
                _responder = responder;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                LastRequest = request;
                LastRequestBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                return _responder(request);
            }
        }

        private static DoclingClient Client(FakeHandler handler, String endpoint = "http://docling.test:5001")
        {
            var options = new Fallen8IngestionOptions();
            options.Docling.Endpoint = endpoint;
            return new DoclingClient(Options.Create(options),
                TestLoggerFactory.Create().CreateLogger<DoclingClient>(), handler);
        }

        private static HttpResponseMessage Json(String body, HttpStatusCode status = HttpStatusCode.OK)
        {
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }

        [TestMethod]
        public async Task Convert_ParsesStructuredAndMarkdown_AndAsksForBothFormats()
        {
            var handler = new FakeHandler(_ => Json(@"{
              ""document"": {
                ""md_content"": ""# Hello"",
                ""json_content"": {
                  ""texts"": [ { ""self_ref"": ""#/texts/0"", ""label"": ""text"", ""text"": ""Hello"" } ],
                  ""body"": { ""children"": [ { ""$ref"": ""#/texts/0"" } ] },
                  ""pages"": { ""1"": {}, ""2"": {} }
                }
              },
              ""status"": ""success""
            }"));

            using var client = Client(handler);
            Assert.IsTrue(client.Configured);

            var result = await client.ConvertAsync(Encoding.UTF8.GetBytes("%PDF"), "spec.pdf", CancellationToken.None);

            Assert.AreEqual("# Hello", result.Markdown);
            Assert.IsNotNull(result.Document);
            Assert.AreEqual(1, result.Document.Texts.Count);
            Assert.AreEqual(2, result.PageCount);

            Assert.AreEqual("/v1/convert/file", handler.LastRequest.RequestUri.AbsolutePath);
            StringAssert.Contains(handler.LastRequestBody, "spec.pdf");
            Assert.AreEqual(2, CountOccurrences(handler.LastRequestBody, "to_formats"),
                "json AND md are requested in one conversion");
            StringAssert.Contains(handler.LastRequestBody, "json");
            StringAssert.Contains(handler.LastRequestBody, "md");
        }

        [TestMethod]
        public async Task Convert_WithoutJsonContent_FallsBackToMarkdownOnly()
        {
            var handler = new FakeHandler(_ => Json(@"{ ""document"": { ""md_content"": ""plain"" } }"));
            using var client = Client(handler);

            var result = await client.ConvertAsync(new Byte[] { 1 }, "a.docx", CancellationToken.None);

            Assert.AreEqual("plain", result.Markdown);
            Assert.IsNull(result.Document);
            Assert.IsNull(result.PageCount, "no structured document, no page count");
        }

        [TestMethod]
        public async Task Convert_NonSuccessStatus_ThrowsUnavailable()
        {
            var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
            using var client = Client(handler);

            await Assert.ThrowsExceptionAsync<DoclingUnavailableException>(
                () => client.ConvertAsync(new Byte[] { 1 }, "a.pdf", CancellationToken.None));
        }

        [TestMethod]
        public async Task Convert_NonJsonBody_ThrowsUnavailable()
        {
            var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>proxy error</html>", Encoding.UTF8, "text/html")
            });
            using var client = Client(handler);

            await Assert.ThrowsExceptionAsync<DoclingUnavailableException>(
                () => client.ConvertAsync(new Byte[] { 1 }, "a.pdf", CancellationToken.None));
        }

        [TestMethod]
        public async Task Convert_ConnectionFailure_ThrowsUnavailable()
        {
            var handler = new FakeHandler(_ => throw new HttpRequestException("connection refused"));
            using var client = Client(handler);

            await Assert.ThrowsExceptionAsync<DoclingUnavailableException>(
                () => client.ConvertAsync(new Byte[] { 1 }, "a.pdf", CancellationToken.None));
        }

        [TestMethod]
        public async Task Unconfigured_ThrowsOnConvert_ReportsUnreachable()
        {
            var options = new Fallen8IngestionOptions();  // no endpoint
            using var client = new DoclingClient(Options.Create(options),
                TestLoggerFactory.Create().CreateLogger<DoclingClient>());

            Assert.IsFalse(client.Configured);
            Assert.IsFalse(await client.IsReachableAsync(CancellationToken.None));
            await Assert.ThrowsExceptionAsync<DoclingUnavailableException>(
                () => client.ConvertAsync(new Byte[] { 1 }, "a.pdf", CancellationToken.None));
        }

        [TestMethod]
        public async Task Health_ReflectsSidecarAnswer()
        {
            var healthyHandler = new FakeHandler(request =>
                request.RequestUri.AbsolutePath == "/health"
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
            using (var client = Client(healthyHandler))
            {
                Assert.IsTrue(await client.IsReachableAsync(CancellationToken.None));
            }

            var downHandler = new FakeHandler(_ => throw new HttpRequestException("down"));
            using (var client = Client(downHandler))
            {
                Assert.IsFalse(await client.IsReachableAsync(CancellationToken.None));
            }
        }

        [TestMethod]
        public async Task Convert_CallerCancellation_PropagatesNotMislabelledAsSidecarFault()
        {
            // A caller-cancelled token (client disconnect / shutdown) must surface as a
            // cancellation, NOT a DoclingUnavailableException that would become a 503 + failed
            // Document. The handler would answer OK, but the pre-cancelled token trips first.
            var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
            using var client = Client(handler);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // TaskCanceledException derives from OperationCanceledException; the point is that it
            // is a cancellation, NOT a DoclingUnavailableException.
            await Assert.ThrowsExceptionAsync<TaskCanceledException>(
                () => client.ConvertAsync(new Byte[] { 1 }, "a.pdf", cts.Token));
        }

        [TestMethod]
        public async Task Health_IsCached_WithinTtl()
        {
            var calls = 0;
            var handler = new FakeHandler(_ =>
            {
                calls++;
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

            using var client = Client(handler);
            Assert.IsTrue(await client.IsReachableAsync(CancellationToken.None));
            Assert.IsTrue(await client.IsReachableAsync(CancellationToken.None));
            Assert.AreEqual(1, calls, "the second probe within the TTL is served from the cache");
        }

        private static Int32 CountOccurrences(String haystack, String needle)
        {
            var count = 0;
            var index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
        }
    }
}
