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
    ///   apiApp, only their public HTTP contract. It owns three cross-cutting concerns so no
    ///   tool re-implements them: <see cref="UrlSafety">URL-safe route construction</see>,
    ///   namespace scoping (<c>/ns/{ns}/…</c> vs the bare default route), and the three-rule
    ///   error mapping (problem+json → title/detail; other 4xx/5xx string body → detail;
    ///   204/200-null → soft-not-found) into <see cref="BridgeError"/> (spec §3.2). Obtains its
    ///   <see cref="HttpClient"/> from the factory so the primary handler is overridable in tests.
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

        // --- Namespace-scoped, typed helpers -------------------------------------------------

        /// <summary>GET a namespace-scoped resource and deserialize it (null on soft-not-found).</summary>
        public Task<T?> GetAsync<T>(String? @namespace, String suffix, CancellationToken cancellationToken)
        {
            return SendJsonAsync<T>(HttpMethod.Get, Scoped(@namespace, suffix), null, cancellationToken);
        }

        /// <summary>POST a JSON body to a namespace-scoped resource and deserialize the reply.</summary>
        public Task<T?> PostAsync<T>(String? @namespace, String suffix, Object body, CancellationToken cancellationToken)
        {
            return SendJsonAsync<T>(HttpMethod.Post, Scoped(@namespace, suffix), body, cancellationToken);
        }

        /// <summary>The connection probe used by <c>f8_overview</c> and <c>/healthz</c>.</summary>
        public Task<StatusDto?> GetStatusAsync(String? @namespace, CancellationToken cancellationToken)
        {
            return GetAsync<StatusDto>(@namespace, "status", cancellationToken);
        }

        /// <summary>Lists the target's namespaces (Fallen-8-level; always the bare route).</summary>
        public async Task<NamespacesDto> ListNamespacesAsync(CancellationToken cancellationToken)
        {
            return await SendJsonAsync<NamespacesDto>(HttpMethod.Get, "ns", null, cancellationToken).ConfigureAwait(false)
                   ?? new NamespacesDto();
        }

        /// <summary>Fetches one element's full JSON (the single getter already carries properties +
        /// grouped adjacency), returning null for the getter's 204/200-null soft-not-found.</summary>
        public Task<JsonElement?> GetElementAsync(String? @namespace, String kind, Int32 id, CancellationToken cancellationToken)
        {
            var suffix = String.Equals(kind, "edge", StringComparison.OrdinalIgnoreCase)
                ? $"edge/{id}"
                : $"vertex/{id}";
            return SendRawAsync(HttpMethod.Get, Scoped(@namespace, suffix), null, cancellationToken);
        }

        /// <summary>Fetches an element by id without knowing its kind (<c>GET /graphelement/{id}</c>),
        /// used to enrich search hits whose ids may be vertices or edges. Null on soft-not-found.</summary>
        public Task<JsonElement?> GetGraphElementAsync(String? @namespace, Int32 id, CancellationToken cancellationToken)
        {
            return SendRawAsync(HttpMethod.Get, Scoped(@namespace, $"graphelement/{id}"), null, cancellationToken);
        }

        /// <summary>A namespace-scoped request that returns the raw JSON reply (or null on
        /// soft-not-found) — for write endpoints whose response is a rich, forward-compatible
        /// document the bridge passes through rather than re-models (save-game, subgraph summary,
        /// namespace entry).</summary>
        public Task<JsonElement?> RequestRawAsync(HttpMethod method, String? @namespace, String suffix, Object? body, CancellationToken cancellationToken)
        {
            return SendRawAsync(method, Scoped(@namespace, suffix), body, cancellationToken);
        }

        /// <summary>A namespace-scoped request that expects a 2xx and no meaningful body (the
        /// write endpoints' 202/204). Throws a mapped <see cref="BridgeError"/> on failure.</summary>
        public async Task RequestVoidAsync(HttpMethod method, String? @namespace, String suffix, Object? body, CancellationToken cancellationToken)
        {
            await SendAsync(method, Scoped(@namespace, suffix), body, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        ///   Builds a namespace-scoped relative path: the bare <c>{suffix}</c> for the reserved
        ///   default, or <c>ns/{encoded}/{suffix}</c> otherwise, with the namespace validated and
        ///   percent-encoded (spec §3.9). Throws a clean <see cref="BridgeError"/> for an invalid
        ///   namespace so the tool surfaces it as an error rather than issuing a bad request.
        /// </summary>
        private static String Scoped(String? @namespace, String suffix)
        {
            if (UrlSafety.IsDefault(@namespace))
            {
                return suffix;
            }

            if (!UrlSafety.TryEncodeNamespace(@namespace, out var encoded, out var error))
            {
                throw new BridgeError(400, "Invalid namespace", error);
            }

            return $"ns/{encoded}/{suffix}";
        }

        // --- Core send + error mapping -------------------------------------------------------

        private async Task<T?> SendJsonAsync<T>(HttpMethod method, String relativePath, Object? body, CancellationToken cancellationToken)
        {
            var text = await SendAsync(method, relativePath, body, cancellationToken).ConfigureAwait(false);
            return text is null ? default : JsonSerializer.Deserialize<T>(text, JsonOptions);
        }

        private async Task<JsonElement?> SendRawAsync(HttpMethod method, String relativePath, Object? body, CancellationToken cancellationToken)
        {
            var text = await SendAsync(method, relativePath, body, cancellationToken).ConfigureAwait(false);
            return text is null ? null : JsonSerializer.Deserialize<JsonElement>(text, JsonOptions);
        }

        /// <summary>
        ///   The single HTTP send + status handling. Returns the response body text, or null for
        ///   the soft-not-found convention (204, or 200 with a literal <c>null</c> body); throws a
        ///   mapped <see cref="BridgeError"/> for a 4xx/5xx or a transport failure.
        /// </summary>
        private async Task<String?> SendAsync(HttpMethod method, String relativePath, Object? body, CancellationToken cancellationToken)
        {
            using var client = _factory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(method, relativePath);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, mediaType: null, JsonOptions);
            }

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
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

                if (response.StatusCode == HttpStatusCode.NoContent)
                {
                    return null;
                }

                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return String.IsNullOrWhiteSpace(text) || text.Trim() == "null" ? null : text;
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
