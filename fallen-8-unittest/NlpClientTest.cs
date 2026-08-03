// MIT License
//
// NlpClientTest.cs
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
    ///   Feature semantic-layer FR-2: the fallen-8-nlp client against a fake handler - request
    ///   shape, response parsing, the additive-failure fault mapping, and cancellation.
    /// </summary>
    [TestClass]
    public class NlpClientTest
    {
        private sealed class FakeHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
            public String LastBody
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
                LastBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                return _responder(request);
            }
        }

        private static NlpClient Client(FakeHandler handler, String endpoint = "http://nlp.test:8100")
        {
            var options = new Fallen8NlpOptions { Endpoint = endpoint };
            return new NlpClient(Options.Create(options),
                TestLoggerFactory.Create().CreateLogger<NlpClient>(), handler);
        }

        private static HttpResponseMessage Json(String body, HttpStatusCode status = HttpStatusCode.OK)
            => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        [TestMethod]
        public async Task Enrich_ParsesEntitiesAndTerms_AndSendsItems()
        {
            var handler = new FakeHandler(_ => Json(@"{ ""items"": [
                { ""id"": ""c1"", ""language"": ""en"", ""entities"": [
                    { ""text"": ""Acme Corporation"", ""label"": ""ORG"", ""start"": 0, ""end"": 16 } ],
                  ""keyTerms"": [ ""checkout service"" ] } ] }"));
            using var client = Client(handler);

            var result = await client.EnrichAsync(new[] { ("c1", "Acme Corporation ships ...") }, CancellationToken.None);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("en", result[0].Language);
            Assert.AreEqual("Acme Corporation", result[0].Entities[0].Text);
            Assert.AreEqual("ORG", result[0].Entities[0].Label);
            CollectionAssert.AreEqual(new[] { "checkout service" }, result[0].KeyTerms);
            StringAssert.Contains(handler.LastBody, "\"id\":\"c1\"");
            // English-only: no languageHint is sent (feature nlp-gpu-tier).
            Assert.IsFalse(handler.LastBody.Contains("languageHint"), "the request must not carry a languageHint");
        }

        [TestMethod]
        public async Task Enrich_NonSuccess_ThrowsUnavailable()
        {
            using var client = Client(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));
            await Assert.ThrowsExceptionAsync<NlpUnavailableException>(
                () => client.EnrichAsync(new[] { ("c1", "x") }, CancellationToken.None));
        }

        [TestMethod]
        public async Task Enrich_ConnectionFailure_ThrowsUnavailable()
        {
            using var client = Client(new FakeHandler(_ => throw new HttpRequestException("refused")));
            await Assert.ThrowsExceptionAsync<NlpUnavailableException>(
                () => client.EnrichAsync(new[] { ("c1", "x") }, CancellationToken.None));
        }

        [TestMethod]
        public async Task Enrich_CallerCancellation_Propagates()
        {
            using var client = Client(new FakeHandler(_ => Json(@"{ ""items"": [] }")));
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsExceptionAsync<TaskCanceledException>(
                () => client.EnrichAsync(new[] { ("c1", "x") }, cts.Token));
        }

        [TestMethod]
        public async Task Unconfigured_IsNotReachable_AndThrowsOnEnrich()
        {
            using var client = new NlpClient(Options.Create(new Fallen8NlpOptions()),
                TestLoggerFactory.Create().CreateLogger<NlpClient>());
            Assert.IsFalse(client.Configured);
            Assert.IsFalse(await client.IsReachableAsync(CancellationToken.None));
            await Assert.ThrowsExceptionAsync<NlpUnavailableException>(
                () => client.EnrichAsync(new[] { ("c1", "x") }, CancellationToken.None));
        }

        [TestMethod]
        public async Task Health_ReflectsAnswer_AndCaches()
        {
            var calls = 0;
            var handler = new FakeHandler(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.OK); });
            using var client = Client(handler);
            Assert.IsTrue(await client.IsReachableAsync(CancellationToken.None));
            Assert.IsTrue(await client.IsReachableAsync(CancellationToken.None));
            Assert.AreEqual(1, calls, "the probe is cached within the TTL");
        }
    }
}
