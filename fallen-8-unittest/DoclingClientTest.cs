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
using System.Collections.Generic;
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
    ///   Feature semantic-layer FR-4: the docling-serve client against a fake handler over the
    ///   ASYNC task API - submit -> poll (pending then done) -> result, conversion knobs in the
    ///   submit body, markdown-only fallback, task-failure and timeout mapping, cancellation.
    /// </summary>
    [TestClass]
    public class DoclingClientTest
    {
        private const String TaskId = "task-123";

        private sealed class FakeHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, String, HttpResponseMessage> _responder;
            public String LastSubmitBody
            {
                get; private set;
            }

            public FakeHandler(Func<HttpRequestMessage, String, HttpResponseMessage> responder)
            {
                _responder = responder;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var path = request.RequestUri.AbsolutePath;
                String body = null;
                if (request.Content != null)
                {
                    body = await request.Content.ReadAsStringAsync(cancellationToken);
                    if (path.Contains("/async"))
                    {
                        LastSubmitBody = body;
                    }
                }

                return _responder(request, path);
            }
        }

        private static DoclingClient Client(FakeHandler handler, Int32 timeoutSeconds = 30)
        {
            var options = new Fallen8IngestionOptions();
            options.Docling.Endpoint = "http://docling.test:5001";
            options.Docling.TimeoutSeconds = timeoutSeconds;
            options.Docling.PollIntervalSeconds = 1;
            return new DoclingClient(Options.Create(options),
                TestLoggerFactory.Create().CreateLogger<DoclingClient>(), handler);
        }

        private static HttpResponseMessage Json(String body, HttpStatusCode status = HttpStatusCode.OK)
            => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Submitted() => Json($"{{ \"task_id\": \"{TaskId}\", \"task_status\": \"pending\" }}");

        private static HttpResponseMessage PollStatus(String status) => Json($"{{ \"task_id\": \"{TaskId}\", \"task_status\": \"{status}\" }}");

        /// <summary>Routes the three async endpoints; the result payload is supplied per test.</summary>
        private static FakeHandler AsyncFlow(String resultJson, String pollStatus = "success")
        {
            return new FakeHandler((request, path) =>
            {
                if (path.EndsWith("/async"))
                {
                    return Submitted();
                }
                if (path.Contains("/status/poll/"))
                {
                    return PollStatus(pollStatus);
                }
                if (path.Contains("/result/"))
                {
                    return Json(resultJson);
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });
        }

        [TestMethod]
        public async Task Convert_ParsesStructuredAndMarkdown_AndSendsOptions()
        {
            var handler = AsyncFlow(@"{
              ""document"": {
                ""md_content"": ""# Hello"",
                ""json_content"": {
                  ""texts"": [ { ""self_ref"": ""#/texts/0"", ""label"": ""text"", ""text"": ""Hello"" } ],
                  ""body"": { ""children"": [ { ""$ref"": ""#/texts/0"" } ] },
                  ""pages"": { ""1"": {}, ""2"": {} }
                }
              }, ""status"": ""success"" }");

            using var client = Client(handler);
            var result = await client.ConvertAsync(Encoding.UTF8.GetBytes("%PDF"), "spec.pdf", CancellationToken.None);

            Assert.AreEqual("# Hello", result.Markdown);
            Assert.IsNotNull(result.Document);
            Assert.AreEqual(2, result.PageCount);
            // Options ride the submit body.
            StringAssert.Contains(handler.LastSubmitBody, "do_ocr");
            StringAssert.Contains(handler.LastSubmitBody, "table_mode");
            StringAssert.Contains(handler.LastSubmitBody, "spec.pdf");
        }

        [TestMethod]
        public async Task Convert_WithoutJsonContent_FallsBackToMarkdownOnly()
        {
            using var client = Client(AsyncFlow(@"{ ""document"": { ""md_content"": ""plain"" } }"));
            var result = await client.ConvertAsync(new Byte[] { 1 }, "a.docx", CancellationToken.None);
            Assert.AreEqual("plain", result.Markdown);
            Assert.IsNull(result.Document);
            Assert.IsNull(result.PageCount);
        }

        [TestMethod]
        public async Task Convert_PollPendingThenSuccess_Completes()
        {
            var polls = 0;
            var handler = new FakeHandler((request, path) =>
            {
                if (path.EndsWith("/async")) return Submitted();
                if (path.Contains("/status/poll/")) return PollStatus(++polls >= 2 ? "success" : "started");
                if (path.Contains("/result/")) return Json(@"{ ""document"": { ""md_content"": ""done"" } }");
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            using var client = Client(handler);
            var result = await client.ConvertAsync(new Byte[] { 1 }, "a.pdf", CancellationToken.None);
            Assert.AreEqual("done", result.Markdown);
            Assert.IsTrue(polls >= 2, "the loop polled until success");
        }

        [TestMethod]
        public async Task Convert_TaskFailure_ThrowsUnavailable()
        {
            using var client = Client(AsyncFlow("{}", pollStatus: "failure"));
            await Assert.ThrowsExceptionAsync<DoclingUnavailableException>(
                () => client.ConvertAsync(new Byte[] { 1 }, "a.pdf", CancellationToken.None));
        }

        [TestMethod]
        public async Task Convert_PollTimeout_ThrowsUnavailable()
        {
            // Never finishes; the 1s overall budget elapses in the poll loop.
            using var client = Client(AsyncFlow("{}", pollStatus: "started"), timeoutSeconds: 1);
            await Assert.ThrowsExceptionAsync<DoclingUnavailableException>(
                () => client.ConvertAsync(new Byte[] { 1 }, "a.pdf", CancellationToken.None));
        }

        [TestMethod]
        public async Task Convert_SubmitNonSuccess_ThrowsUnavailable()
        {
            var handler = new FakeHandler((request, path) => new HttpResponseMessage(HttpStatusCode.InternalServerError));
            using var client = Client(handler);
            await Assert.ThrowsExceptionAsync<DoclingUnavailableException>(
                () => client.ConvertAsync(new Byte[] { 1 }, "a.pdf", CancellationToken.None));
        }

        [TestMethod]
        public async Task Convert_ConnectionFailure_ThrowsUnavailable()
        {
            var handler = new FakeHandler((request, path) => throw new HttpRequestException("refused"));
            using var client = Client(handler);
            await Assert.ThrowsExceptionAsync<DoclingUnavailableException>(
                () => client.ConvertAsync(new Byte[] { 1 }, "a.pdf", CancellationToken.None));
        }

        [TestMethod]
        public async Task Convert_CallerCancellation_Propagates()
        {
            using var client = Client(AsyncFlow(@"{ ""document"": { ""md_content"": ""x"" } }"));
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsExceptionAsync<TaskCanceledException>(
                () => client.ConvertAsync(new Byte[] { 1 }, "a.pdf", cts.Token));
        }

        [TestMethod]
        public async Task Unconfigured_ThrowsOnConvert_ReportsUnreachable()
        {
            using var client = new DoclingClient(Options.Create(new Fallen8IngestionOptions()),
                TestLoggerFactory.Create().CreateLogger<DoclingClient>());
            Assert.IsFalse(client.Configured);
            Assert.IsFalse(await client.IsReachableAsync(CancellationToken.None));
            await Assert.ThrowsExceptionAsync<DoclingUnavailableException>(
                () => client.ConvertAsync(new Byte[] { 1 }, "a.pdf", CancellationToken.None));
        }

        [TestMethod]
        public async Task Health_ReflectsSidecarAnswer_AndIsCached()
        {
            var calls = 0;
            var handler = new FakeHandler((request, path) =>
            {
                if (path == "/health") { calls++; return new HttpResponseMessage(HttpStatusCode.OK); }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });
            using var client = Client(handler);
            Assert.IsTrue(await client.IsReachableAsync(CancellationToken.None));
            Assert.IsTrue(await client.IsReachableAsync(CancellationToken.None));
            Assert.AreEqual(1, calls, "the probe is cached within the TTL");
        }
    }
}
