// MIT License
//
// GraphTargetFactory.cs
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
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.Integrations.Configuration;
using NoSQL.GraphDB.Integrations.Graph;

namespace NoSQL.GraphDB.Integrations.Run
{
    /// <summary>
    ///   Where a run's graph target comes from. A seam, because the conformance suite runs the real runner
    ///   against <c>InMemoryGraphTarget</c>: whole-suite offline operation is a requirement rather than a
    ///   convenience, since an author who needs a live graph to iterate will not iterate.
    /// </summary>
    public interface IGraphTargetFactory
    {
        /// <summary>Creates the target for one job. The runner disposes it, so a target owning a connection
        /// does not leak one per run.</summary>
        IGraphTarget Create(String? namespaceName);
    }

    /// <summary>
    ///   The live target: one <see cref="HttpClient"/> per run against the configured Fallen-8, carrying THIS
    ///   deployable's own api key. A caller's credential is never forwarded, so a job cannot escalate beyond
    ///   what this deployable may already do and a graph audit trail names one writer per sidecar instead of
    ///   whoever submitted a job.
    /// </summary>
    public sealed class GraphTargetFactory : IGraphTargetFactory
    {
        private readonly IOptions<Fallen8TargetOptions> _options;

        public GraphTargetFactory(IOptions<Fallen8TargetOptions> options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <inheritdoc />
        public IGraphTarget Create(String? namespaceName)
        {
            var options = _options.Value;
            var baseUrl = options.BaseUrl;
            if (String.IsNullOrWhiteSpace(baseUrl))
            {
                throw new GraphTargetException("No Fallen8Target:BaseUrl is configured, so there is no graph to write into.");
            }

            // Downstream TLS is validated normally, with no insecure-target escape hatch: it keeps the named
            // self-signed host list the feature's single reduction of trust.
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                AllowAutoRedirect = false,
            };

            var client = new HttpClient(handler, disposeHandler: true)
            {
                BaseAddress = new Uri(baseUrl.EndsWith("/", StringComparison.Ordinal) ? baseUrl : baseUrl + "/"),
                Timeout = TimeSpan.FromSeconds(120),
            };

            if (!String.IsNullOrEmpty(options.ApiKey))
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation(options.ApiKeyHeader, options.ApiKey);
            }

            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var target = String.IsNullOrWhiteSpace(namespaceName) ? options.DefaultNamespace : namespaceName;
            return new Fallen8RestTarget(client, target);
        }
    }
}
