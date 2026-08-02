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

        // Conversion knobs + async budget (feature semantic-layer).
        private readonly Boolean _doOcr;
        private readonly String _tableMode;
        private readonly String _ocrEngine;
        private readonly TimeSpan _overallTimeout;
        private readonly TimeSpan _pollInterval;

        /// <summary>Monotonic-clock cache for the health probe (0 ticks = never probed).</summary>
        private Int64 _healthProbedAtTicks;
        private volatile Boolean _healthy;

        public DoclingClient(Microsoft.Extensions.Options.IOptions<Fallen8IngestionOptions> options,
            ILogger<DoclingClient> logger, HttpMessageHandler handler = null)
        {
            _logger = logger;

            var docling = options.Value.Docling ?? new Fallen8IngestionOptions.DoclingOptions();
            _doOcr = docling.DoOcr;
            _tableMode = String.IsNullOrWhiteSpace(docling.TableMode) ? "fast" : docling.TableMode;
            _ocrEngine = docling.OcrEngine ?? String.Empty;
            _overallTimeout = TimeSpan.FromSeconds(Math.Max(1, docling.TimeoutSeconds));
            _pollInterval = TimeSpan.FromSeconds(Math.Max(1, docling.PollIntervalSeconds));
            if (!String.IsNullOrWhiteSpace(docling.Endpoint))
            {
                var endpoint = docling.Endpoint.EndsWith("/", StringComparison.Ordinal)
                    ? docling.Endpoint
                    : docling.Endpoint + "/";
                // This client is a singleton, so a raw HttpClient would pin its DNS resolution for
                // the process lifetime - if the docling container restarts with a new IP, every
                // later conversion fails until F8 restarts. A SocketsHttpHandler with a bounded
                // PooledConnectionLifetime recycles connections (and re-resolves DNS) periodically.
                // A test-supplied handler is used verbatim.
                _client = new HttpClient(handler ?? new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2)
                }, disposeHandler: true);
                _client.BaseAddress = new Uri(endpoint);
                // Per-request cap (submit/poll/result are each quick); the OVERALL budget is the
                // poll-loop deadline below, so a minutes-long conversion is bounded there, not here.
                _client.Timeout = TimeSpan.FromSeconds(Math.Min(120, Math.Max(30, docling.TimeoutSeconds)));
            }
        }

        public Boolean Configured => _client != null;

        /// <summary>
        ///   Converts one document via docling-serve's ASYNC task API: submit
        ///   (<c>/v1/convert/file/async</c>), poll (<c>/v1/status/poll/{id}</c>) until the task
        ///   finishes or the overall budget elapses, then fetch (<c>/v1/result/{id}</c>). Called
        ///   off-thread by the ingestion worker, so a minutes-long scanned-PDF conversion holds
        ///   no HTTP request open. Throws <see cref="DoclingUnavailableException"/> on any
        ///   non-success / timeout; propagates a caller cancellation unchanged.
        /// </summary>
        public async Task<DoclingConversionResult> ConvertAsync(Byte[] fileBytes, String fileName,
            CancellationToken cancellationToken)
        {
            if (!Configured)
            {
                throw new DoclingUnavailableException("No docling endpoint is configured (Fallen8:Ingestion:Docling:Endpoint).");
            }

            var taskId = await SubmitAsync(fileBytes, fileName, cancellationToken);
            await PollUntilDoneAsync(taskId, fileName, cancellationToken);
            return await FetchResultAsync(taskId, fileName, cancellationToken);
        }

        private async Task<String> SubmitAsync(Byte[] fileBytes, String fileName, CancellationToken cancellationToken)
        {
            using (var content = new MultipartFormDataContent())
            {
                content.Add(new ByteArrayContent(fileBytes), "files", fileName);
                // Structured output primary, markdown fallback - one conversion, both formats.
                content.Add(new StringContent("json"), "to_formats");
                content.Add(new StringContent("md"), "to_formats");
                // Conversion knobs (feature semantic-layer): OCR off by default is the big win.
                content.Add(new StringContent(_doOcr ? "true" : "false"), "do_ocr");
                content.Add(new StringContent(_tableMode), "table_mode");
                if (!String.IsNullOrWhiteSpace(_ocrEngine))
                {
                    content.Add(new StringContent(_ocrEngine), "ocr_engine");
                }

                var status = await SendForJsonAsync<DoclingTaskStatus>(
                    () => _client.PostAsync("v1/convert/file/async", content, cancellationToken),
                    fileName, cancellationToken);
                if (String.IsNullOrEmpty(status?.TaskId))
                {
                    throw new DoclingUnavailableException(
                        String.Format("The docling sidecar accepted '{0}' but returned no task id.", fileName));
                }

                return status.TaskId;
            }
        }

        private async Task PollUntilDoneAsync(String taskId, String fileName, CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow + _overallTimeout;
            while (true)
            {
                var status = await SendForJsonAsync<DoclingTaskStatus>(
                    () => _client.GetAsync($"v1/status/poll/{taskId}", cancellationToken), fileName, cancellationToken);
                var state = status?.TaskStatus?.ToLowerInvariant();
                if (state == "success")
                {
                    return;
                }

                if (state == "failure" || state == "error")
                {
                    throw new DoclingUnavailableException(
                        String.Format("The docling conversion of '{0}' failed ({1}).", fileName, status.TaskStatus));
                }

                if (DateTime.UtcNow >= deadline)
                {
                    throw new DoclingUnavailableException(String.Format(
                        "The docling conversion of '{0}' did not finish within {1}s.", fileName, (Int32)_overallTimeout.TotalSeconds));
                }

                await Task.Delay(_pollInterval, cancellationToken);
            }
        }

        private async Task<DoclingConversionResult> FetchResultAsync(String taskId, String fileName,
            CancellationToken cancellationToken)
        {
            var parsed = await SendForJsonAsync<DoclingConvertResponse>(
                () => _client.GetAsync($"v1/result/{taskId}", cancellationToken), fileName, cancellationToken);
            var document = parsed?.Document?.JsonContent;
            var pages = document?.Pages;
            return new DoclingConversionResult
            {
                Markdown = parsed?.Document?.MdContent,
                Document = document,
                PageCount = pages != null && pages.Count > 0 ? pages.Count : (Int32?)null
            };
        }

        /// <summary>Runs one request and deserializes its JSON body, mapping every fault to the
        /// same policy: a caller cancellation propagates; anything else (non-2xx, no answer,
        /// non-JSON) is a <see cref="DoclingUnavailableException"/>.</summary>
        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
            Justification = "Deserializes the pinned DoclingModels DTO subset with default options; trimming is disabled for this application.")]
        private async Task<T> SendForJsonAsync<T>(Func<Task<HttpResponseMessage>> send, String fileName,
            CancellationToken cancellationToken)
        {
            HttpResponseMessage response;
            try
            {
                response = await send();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                throw new DoclingUnavailableException(
                    String.Format("The docling sidecar did not answer for '{0}': {1}", fileName, ex.Message), ex);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new DoclingUnavailableException(String.Format(
                        "The docling sidecar answered {0} for '{1}'.", (Int32)response.StatusCode, fileName));
                }

                try
                {
                    using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
                    {
                        return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: cancellationToken);
                    }
                }
                catch (JsonException ex)
                {
                    throw new DoclingUnavailableException(
                        String.Format("The docling sidecar answered non-JSON for '{0}': {1}", fileName, ex.Message), ex);
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The CALLER cancelled (e.g. the /status request was aborted) - do NOT cache this
                // as unhealthy, or one aborted request would report docling down to everyone for
                // the full TTL. Leave the cache untouched and surface the cancellation.
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                // The probe's own timeout elapsed, or the sidecar is unreachable: genuinely down.
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
