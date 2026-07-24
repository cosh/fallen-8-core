// MIT License
//
// McpTestSupport.cs
//
// Copyright (c) 2026 Henning Rauch
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
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.Mcp.Bridge;
using NoSQL.GraphDB.Mcp.Configuration;
using NoSQL.GraphDB.Mcp.Tools;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Shared fixtures for the fallen-8-mcp tests: a message handler that runs a lambda, a
    ///   one-handler <see cref="IHttpClientFactory"/> so a <see cref="Fallen8RestClient"/> can be
    ///   pointed at either a stub or a hosted apiApp (spec §3.11), and a catalog builder.
    /// </summary>
    internal static class McpTestSupport
    {
        internal sealed class LambdaHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

            public LambdaHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            {
                _responder = responder;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(_responder(request));
            }
        }

        internal sealed class SingleClientFactory : IHttpClientFactory
        {
            private readonly HttpMessageHandler _handler;
            private readonly Uri _baseAddress;

            public SingleClientFactory(HttpMessageHandler handler, Uri baseAddress)
            {
                _handler = handler;
                _baseAddress = baseAddress;
            }

            public HttpClient CreateClient(String name)
            {
                // disposeHandler:false so the reusable handler survives Fallen8RestClient's per-call
                // 'using var client'.
                return new HttpClient(_handler, disposeHandler: false) { BaseAddress = _baseAddress };
            }
        }

        internal static Fallen8RestClient Bridge(HttpMessageHandler handler, String baseAddress = "http://localhost")
        {
            return new Fallen8RestClient(new SingleClientFactory(handler, new Uri(baseAddress)));
        }

        internal static ToolCatalog Catalog(McpToolsOptions tools, IEnumerable<IMcpTool> registeredTools)
        {
            return new ToolCatalog(
                registeredTools,
                Options.Create(new McpOptions { Tools = tools }),
                TestLoggerFactory.Create().CreateLogger<ToolCatalog>());
        }
    }
}
