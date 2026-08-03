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
        /// empty (never fails the ingest). The sidecar is English-only (feature nlp-gpu-tier).</summary>
        Task<IReadOnlyList<NlpEnrichedItem>> EnrichAsync(IReadOnlyList<(String Id, String Text)> items,
            CancellationToken cancellationToken);

        Task<Boolean> IsReachableAsync(CancellationToken cancellationToken);
    }

    /// <summary>The HTTP client for fallen-8-nlp: <c>POST /enrich</c> plus a cached
    /// <c>GET /health</c> probe for /status (mirrors <see cref="DoclingClient"/>).</summary>
    public sealed class NlpClient : SidecarHttpClient, INlpClient
    {
        // The base owns the HttpClient, endpoint normalization, the cached /health probe, Configured
        // and Dispose (consolidation-audit CA-10); this client keeps only POST /enrich. The base
        // takes an already-computed timeout: NLP floors its configured seconds at 1 (docling clamps
        // differently), so the formula stays here.
        public NlpClient(IOptions<Fallen8NlpOptions> options, ILogger<NlpClient> logger,
            HttpMessageHandler handler = null)
            : base(Resolve(options).Endpoint,
                   TimeSpan.FromSeconds(Math.Max(1, Resolve(options).TimeoutSeconds)),
                   logger, "NLP", handler)
        {
        }

        private static Fallen8NlpOptions Resolve(IOptions<Fallen8NlpOptions> options)
            => options.Value ?? new Fallen8NlpOptions();

        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
            Justification = "Serializes/deserializes the small NLP wire DTOs with default options; trimming is disabled for this application.")]
        public async Task<IReadOnlyList<NlpEnrichedItem>> EnrichAsync(
            IReadOnlyList<(String Id, String Text)> items, CancellationToken cancellationToken)
        {
            if (!Configured)
            {
                throw new NlpUnavailableException("No NLP endpoint is configured (Fallen8:Nlp:Endpoint).");
            }

            var body = new JsonObject_EnrichRequest
            {
                Items = new List<JsonObject_EnrichItem>(items.Count)
            };
            foreach (var item in items)
            {
                body.Items.Add(new JsonObject_EnrichItem { Id = item.Id, Text = item.Text });
            }

            try
            {
                using (var response = await Http.PostAsJsonAsync("enrich", body, cancellationToken))
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

        #region serialization DTOs

        private sealed class JsonObject_EnrichRequest
        {
            [JsonPropertyName("items")] public List<JsonObject_EnrichItem> Items { get; set; }
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
