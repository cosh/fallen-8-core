// MIT License
//
// Fallen8RestClient.cs
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
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NoSQL.GraphDB.Mcp.Bridge.Dto;

namespace NoSQL.GraphDB.Mcp.Bridge
{
    /// <summary>
    ///   The one home for talking to a downstream Fallen-8 over its REST API. It is a REST
    ///   bridge, not an engine embedding (spec §2) — it never references the engine or the
    ///   apiApp, only their public HTTP contract. It owns two cross-cutting concerns so no tool
    ///   re-implements them: <see cref="UrlSafety">URL-safe route construction</see> and the
    ///   three-rule error mapping (problem+json → title/detail; other 4xx/5xx string body →
    ///   detail; 204/200-null → soft-not-found) into <see cref="BridgeError"/> (spec §3.2).
    ///   Obtains its <see cref="HttpClient"/> from the factory so the primary handler is
    ///   overridable in tests (the walking-skeleton harness injects the apiApp's in-memory
    ///   handler — spec §3.11).
    /// </summary>
    public sealed class Fallen8RestClient
    {
        /// <summary>The named <see cref="HttpClient"/> the host configures (base URL + api-key
        /// header) and tests re-point.</summary>
        public const String HttpClientName = "fallen8";

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly IHttpClientFactory _factory;

        public Fallen8RestClient(IHttpClientFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        /// <summary>The connection probe used at startup and by <c>/healthz</c>. Returns the
        /// default namespace's status; throws <see cref="BridgeError"/> when unreachable.</summary>
        public Task<StatusDto> GetStatusAsync(String? @namespace, CancellationToken cancellationToken)
        {
            var path = NamespaceScoped(@namespace, "status", out var error);
            if (path is null)
            {
                throw new BridgeError(400, "Invalid namespace", error);
            }

            return GetJsonAsync<StatusDto>(path, cancellationToken)!;
        }

        /// <summary>Lists the target's namespaces (Fallen-8-level; always the bare route).</summary>
        public Task<NamespacesDto> ListNamespacesAsync(CancellationToken cancellationToken)
        {
            return GetJsonAsync<NamespacesDto>("ns", cancellationToken)!;
        }

        /// <summary>
        ///   Builds a namespace-scoped relative path: the bare <c>{suffix}</c> for the reserved
        ///   default, or <c>ns/{encoded}/{suffix}</c> otherwise, with the namespace validated and
        ///   percent-encoded (spec §3.9). Returns null (with <paramref name="error"/>) for an
        ///   invalid namespace so the caller can surface a clean tool error.
        /// </summary>
        private static String? NamespaceScoped(String? @namespace, String suffix, out String error)
        {
            error = String.Empty;
            if (UrlSafety.IsDefault(@namespace))
            {
                return suffix;
            }

            if (!UrlSafety.TryEncodeNamespace(@namespace, out var encoded, out error))
            {
                return null;
            }

            return $"ns/{encoded}/{suffix}";
        }

        private async Task<T?> GetJsonAsync<T>(String relativePath, CancellationToken cancellationToken)
        {
            using var client = _factory.CreateClient(HttpClientName);
            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync(relativePath, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new BridgeError(503, "Fallen-8 unreachable", ex.Message, retryable: true);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new BridgeError(504, "Fallen-8 timeout", "The downstream request timed out.", retryable: true);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw await MapErrorAsync(response, cancellationToken).ConfigureAwait(false);
                }

                // Soft-not-found: 204, or 200 with a literal null body (the getters' convention).
                if (response.StatusCode == HttpStatusCode.NoContent)
                {
                    return default;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (String.IsNullOrWhiteSpace(body) || body.Trim() == "null")
                {
                    return default;
                }

                return JsonSerializer.Deserialize<T>(body, JsonOptions);
            }
        }

        /// <summary>
        ///   The three-rule error mapping (spec §3.2). Never reads request headers, so the API
        ///   key cannot leak into the mapped error.
        /// </summary>
        private static async Task<BridgeError> MapErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            var status = (Int32)response.StatusCode;
            var retryable = status == 429 || status == 503;
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var mediaType = response.Content.Headers.ContentType?.MediaType;

            if (String.Equals(mediaType, "application/problem+json", StringComparison.OrdinalIgnoreCase) &&
                !String.IsNullOrWhiteSpace(body))
            {
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    var title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
                    var detail = root.TryGetProperty("detail", out var d) ? d.GetString() : null;
                    return new BridgeError(
                        status,
                        title ?? response.ReasonPhrase ?? "Error",
                        detail ?? body,
                        retryable);
                }
                catch (JsonException)
                {
                    // Fall through to the string-body rule.
                }
            }

            var reason = response.ReasonPhrase ?? "Error";
            var stringDetail = String.IsNullOrWhiteSpace(body) ? reason : body.Trim();
            return new BridgeError(status, reason, stringDetail, retryable);
        }
    }
}
