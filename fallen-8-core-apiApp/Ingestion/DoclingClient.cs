// MIT License
//
// DoclingClient.cs
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
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.App.Configuration;

namespace NoSQL.GraphDB.App.Ingestion
{
    /// <summary>
    ///   The docling-serve HTTP client: multipart <c>POST /v1/convert/file</c> asking for
    ///   <c>json</c> AND <c>md</c> (structured chunking primary, markdown fallback), plus a
    ///   cached <c>GET /health</c> probe for the /status block.
    /// </summary>
    public sealed class DoclingClient : IDoclingConverter, IDisposable
    {
        private static readonly TimeSpan HealthCacheTtl = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan HealthProbeTimeout = TimeSpan.FromSeconds(5);

        private readonly HttpClient _client;
        private readonly ILogger<DoclingClient> _logger;

        /// <summary>Monotonic-clock cache for the health probe (0 ticks = never probed).</summary>
        private Int64 _healthProbedAtTicks;
        private volatile Boolean _healthy;

        public DoclingClient(Microsoft.Extensions.Options.IOptions<Fallen8IngestionOptions> options,
            ILogger<DoclingClient> logger, HttpMessageHandler handler = null)
        {
            _logger = logger;

            var docling = options.Value.Docling ?? new Fallen8IngestionOptions.DoclingOptions();
            if (!String.IsNullOrWhiteSpace(docling.Endpoint))
            {
                var endpoint = docling.Endpoint.EndsWith("/", StringComparison.Ordinal)
                    ? docling.Endpoint
                    : docling.Endpoint + "/";
                _client = handler == null ? new HttpClient() : new HttpClient(handler);
                _client.BaseAddress = new Uri(endpoint);
                _client.Timeout = TimeSpan.FromSeconds(Math.Max(1, docling.TimeoutSeconds));
            }
        }

        public Boolean Configured => _client != null;

        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
            Justification = "Deserializes the pinned DoclingModels DTO subset with default options; trimming is disabled for this application.")]
        public async Task<DoclingConversionResult> ConvertAsync(Byte[] fileBytes, String fileName,
            CancellationToken cancellationToken)
        {
            if (!Configured)
            {
                throw new DoclingUnavailableException("No docling endpoint is configured (Fallen8:Ingestion:Docling:Endpoint).");
            }

            using (var content = new MultipartFormDataContent())
            {
                var filePart = new ByteArrayContent(fileBytes);
                content.Add(filePart, "files", fileName);
                // Structured output primary, markdown fallback - one conversion, both formats.
                content.Add(new StringContent("json"), "to_formats");
                content.Add(new StringContent("md"), "to_formats");

                HttpResponseMessage response;
                try
                {
                    response = await _client.PostAsync("v1/convert/file", content, cancellationToken);
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
                {
                    // TaskCanceledException covers the HttpClient timeout; a caller-cancelled token
                    // surfaces the same way and is equally a "no conversion happened".
                    throw new DoclingUnavailableException(
                        String.Format("The docling sidecar did not answer: {0}", ex.Message), ex);
                }

                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new DoclingUnavailableException(String.Format(
                            "The docling sidecar answered {0} for '{1}'.", (Int32)response.StatusCode, fileName));
                    }

                    DoclingConvertResponse parsed;
                    try
                    {
                        using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
                        {
                            parsed = await JsonSerializer.DeserializeAsync<DoclingConvertResponse>(stream,
                                cancellationToken: cancellationToken);
                        }
                    }
                    catch (JsonException ex)
                    {
                        throw new DoclingUnavailableException(
                            String.Format("The docling sidecar answered non-JSON for '{0}': {1}", fileName, ex.Message), ex);
                    }

                    var document = parsed?.Document?.JsonContent;
                    var pages = document?.Pages;
                    return new DoclingConversionResult
                    {
                        Markdown = parsed?.Document?.MdContent,
                        Document = document,
                        PageCount = pages != null && pages.Count > 0 ? pages.Count : (Int32?)null
                    };
                }
            }
        }

        public async Task<Boolean> IsReachableAsync(CancellationToken cancellationToken)
        {
            if (!Configured)
            {
                return false;
            }

            var now = Environment.TickCount64;
            var probedAt = Interlocked.Read(ref _healthProbedAtTicks);
            if (probedAt != 0 && now - probedAt < (Int64)HealthCacheTtl.TotalMilliseconds)
            {
                return _healthy;
            }

            Boolean healthy;
            try
            {
                using (var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    probeCts.CancelAfter(HealthProbeTimeout);
                    using (var response = await _client.GetAsync("health", probeCts.Token))
                    {
                        healthy = response.IsSuccessStatusCode;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                _logger.LogDebug("Docling health probe failed: {Reason}", ex.Message);
                healthy = false;
            }

            _healthy = healthy;
            Interlocked.Exchange(ref _healthProbedAtTicks, now);
            return healthy;
        }

        public void Dispose()
        {
            _client?.Dispose();
        }
    }
}
