// MIT License
//
// NlpClient.cs
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
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.App.Configuration;

namespace NoSQL.GraphDB.App.Ingestion
{
    #region wire DTOs

    public sealed class NlpEntity
    {
        [JsonPropertyName("text")] public String Text { get; set; }
        [JsonPropertyName("label")] public String Label { get; set; }
        [JsonPropertyName("start")] public Int32 Start { get; set; }
        [JsonPropertyName("end")] public Int32 End { get; set; }
    }

    public sealed class NlpEnrichedItem
    {
        [JsonPropertyName("id")] public String Id { get; set; }
        [JsonPropertyName("language")] public String Language { get; set; }
        [JsonPropertyName("entities")] public List<NlpEntity> Entities { get; set; } = new List<NlpEntity>();
        [JsonPropertyName("keyTerms")] public List<String> KeyTerms { get; set; } = new List<String>();
    }

    /// <summary>The sidecar is not configured, unreachable, timed out, or answered non-success.
    /// Enrichment is additive, so the pipeline treats this as "no entities", not a fault.</summary>
    public sealed class NlpUnavailableException : Exception
    {
        public NlpUnavailableException(String message, Exception inner = null) : base(message, inner)
        {
        }
    }

    #endregion

    /// <summary>Enrichment behind the fallen-8-nlp sidecar (feature semantic-layer). One
    /// implementation; the seam exists for the ingestion pipeline's tests.</summary>
    public interface INlpClient
    {
        Boolean Configured
        {
            get;
        }

        /// <summary>Enriches a batch. Throws <see cref="NlpUnavailableException"/> when the
        /// sidecar is unconfigured/unreachable; the caller decides that entities are simply
        /// empty (never fails the ingest).</summary>
        Task<IReadOnlyList<NlpEnrichedItem>> EnrichAsync(IReadOnlyList<(String Id, String Text)> items,
            String languageHint, CancellationToken cancellationToken);

        Task<Boolean> IsReachableAsync(CancellationToken cancellationToken);
    }

    /// <summary>The HTTP client for fallen-8-nlp: <c>POST /enrich</c> plus a cached
    /// <c>GET /health</c> probe for /status (mirrors <see cref="DoclingClient"/>).</summary>
    public sealed class NlpClient : INlpClient, IDisposable
    {
        private static readonly TimeSpan HealthCacheTtl = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan HealthProbeTimeout = TimeSpan.FromSeconds(5);

        private readonly HttpClient _client;
        private readonly ILogger<NlpClient> _logger;

        private Int64 _healthProbedAtTicks;
        private volatile Boolean _healthy;

        public NlpClient(IOptions<Fallen8NlpOptions> options, ILogger<NlpClient> logger,
            HttpMessageHandler handler = null)
        {
            _logger = logger;
            var nlp = options.Value ?? new Fallen8NlpOptions();
            if (!String.IsNullOrWhiteSpace(nlp.Endpoint))
            {
                var endpoint = nlp.Endpoint.EndsWith("/", StringComparison.Ordinal) ? nlp.Endpoint : nlp.Endpoint + "/";
                _client = new HttpClient(handler ?? new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2)
                }, disposeHandler: true);
                _client.BaseAddress = new Uri(endpoint);
                _client.Timeout = TimeSpan.FromSeconds(Math.Max(1, nlp.TimeoutSeconds));
            }
        }

        public Boolean Configured => _client != null;

        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
            Justification = "Serializes/deserializes the small NLP wire DTOs with default options; trimming is disabled for this application.")]
        public async Task<IReadOnlyList<NlpEnrichedItem>> EnrichAsync(
            IReadOnlyList<(String Id, String Text)> items, String languageHint, CancellationToken cancellationToken)
        {
            if (!Configured)
            {
                throw new NlpUnavailableException("No NLP endpoint is configured (Fallen8:Nlp:Endpoint).");
            }

            var body = new JsonObject_EnrichRequest
            {
                Items = new List<JsonObject_EnrichItem>(items.Count),
                LanguageHint = String.IsNullOrWhiteSpace(languageHint) ? null : languageHint
            };
            foreach (var item in items)
            {
                body.Items.Add(new JsonObject_EnrichItem { Id = item.Id, Text = item.Text });
            }

            try
            {
                using (var response = await _client.PostAsJsonAsync("enrich", body, cancellationToken))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new NlpUnavailableException(String.Format(
                            "The NLP sidecar answered {0}.", (Int32)response.StatusCode));
                    }

                    var parsed = await response.Content.ReadFromJsonAsync<EnrichResponseDto>(cancellationToken);
                    return parsed?.Items ?? new List<NlpEnrichedItem>();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is JsonException)
            {
                throw new NlpUnavailableException(String.Format("The NLP sidecar did not answer: {0}", ex.Message), ex);
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
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                _logger.LogDebug("NLP health probe failed: {Reason}", ex.Message);
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

        #region serialization DTOs

        private sealed class JsonObject_EnrichRequest
        {
            [JsonPropertyName("items")] public List<JsonObject_EnrichItem> Items { get; set; }
            [JsonPropertyName("languageHint")] public String LanguageHint { get; set; }
        }

        private sealed class JsonObject_EnrichItem
        {
            [JsonPropertyName("id")] public String Id { get; set; }
            [JsonPropertyName("text")] public String Text { get; set; }
        }

        private sealed class EnrichResponseDto
        {
            [JsonPropertyName("items")] public List<NlpEnrichedItem> Items { get; set; }
        }

        #endregion
    }
}
