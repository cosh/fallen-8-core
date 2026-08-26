// MIT License
//
// UnifiClient.cs
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
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Integrations.Contract;

namespace NoSQL.GraphDB.Integrations.Providers.UnifiNetwork
{
    /// <summary>
    ///   The whole of how <see cref="UnifiNetworkProvider"/> touches the network: two request shapes, both
    ///   GET, over the <see cref="System.Net.Http.HttpClient"/> the runtime handed it. It never constructs
    ///   a client, a handler or a socket, because the runtime's delegating handler on that client is what
    ///   enforces the allowed-host list on the way out, and a provider that opened its own connection would
    ///   walk somebody's console credential past it.
    ///
    ///   <para>READ-ONLY BY CONSTRUCTION. The vendor's contract has verbs that restart devices and rewrite
    ///   firewall policy, and because the OpenAPI document declares no security scheme there is no
    ///   published way to scope a key, so the key must be assumed authorised for everything. The defence is
    ///   therefore structural rather than configured: <see cref="HttpMethod.Get"/> is named exactly once in
    ///   this file, and nothing here composes a request body.</para>
    ///
    ///   <para>One instance per run. It holds the run's retry budget, which is why it is created inside
    ///   <c>ObserveAsync</c> and never on the provider, and its requests are issued one at a time, so the
    ///   budget needs no interlocking.</para>
    /// </summary>
    public sealed class UnifiClient
    {
        /// <summary>
        ///   The page size asked for. The vendor caps <c>limit</c> at 200 (spec section 14, from the whole
        ///   document), so asking for more would be answered with an unannounced smaller page - which is
        ///   exactly the answer the loop must not trust anyway.
        /// </summary>
        public const Int32 PageSize = 200;

        /// <summary>
        ///   The page-count backstop, the fourth paging defence: a console that reports more than it will
        ///   serve, or that ignores <c>offset</c> and serves page one forever, ends the run with a failure
        ///   instead of a growing list. At <see cref="PageSize"/> this bounds one list at 102,400 items,
        ///   which is orders of magnitude above any console's site, device or client count.
        /// </summary>
        public const Int32 PageLimit = 512;

        /// <summary>
        ///   How many times a run will honour a 429 before giving up. Counted per RUN and not per request:
        ///   a per-request budget lets a console answering 429 to every request keep a run alive
        ///   indefinitely.
        /// </summary>
        public const Int32 RetryBudget = 3;

        /// <summary>
        ///   The longest <c>Retry-After</c> this provider will wait out. Anything longer fails the run
        ///   rather than sleeping: a caller is waiting on the job, and a snapshot either describes the
        ///   whole console or must not claim to.
        /// </summary>
        public static readonly TimeSpan LongestRetryAfter = TimeSpan.FromSeconds(5);

        /// <summary>
        ///   The authentication header.
        ///
        ///   <para>The Network OpenAPI document declares NO security scheme at all, so this name does not come
        ///   from it. It comes from the vendor's Site Manager document, whose one security scheme is
        ///   <c>{"in": "header", "name": "X-API-Key", "type": "apiKey"}</c> applied globally, and whose getting
        ///   started page shows <c>-H 'X-API-KEY: YOUR_API_KEY'</c>. The value is the raw key: no scheme word,
        ///   no encoding. The vendor's own two spellings differ in case and HTTP header names are
        ///   case-insensitive, so this follows the example rather than the schema.</para>
        ///
        ///   <para>The distinction is kept here, at the constant, so the Network document's silence is never
        ///   read as permission - neither permission to send no credential, nor permission to assume a key is
        ///   scoped to reads. Sources: https://developer.ui.com/site-manager/v1.0.0/openapi.json and
        ///   https://developer.ui.com/site-manager/v1.0.0/gettingstarted.</para>
        /// </summary>
        public const String ApiKeyHeader = "X-API-KEY";

        /// <summary>The two published base-URL forms, named by the refusal so nobody has to guess.</summary>
        private const String LocalConsoleForm = "https://{consoleIP}/proxy/network/integration";

        /// <summary>The cloud connector form of the same base URL.</summary>
        private const String CloudConnectorForm =
            "https://api.ui.com/v1/connector/consoles/{consoleId}/proxy/network/integration";

        /// <summary>
        ///   THE TWO FORMS TAKE DIFFERENT KEYS, which is the mistake a 401 here is usually reporting.
        ///
        ///   <para>The vendor's own getting-started page renders its key instructions against a Remote/Local
        ///   toggle: the local console's key is created in the Network application's Integrations section,
        ///   while the cloud connector is the remote case and takes a Site Manager key created at
        ///   unifi.ui.com under Settings and then API Keys. Same header, different issuer, and the key from
        ///   one front door is simply unknown at the other.</para>
        ///
        ///   <para>Recorded here rather than left to a reader because it is not discoverable from a 401: the
        ///   response says nothing about which key it wanted. Sources:
        ///   https://developer.ui.com/network/v10.4.57/gettingstarted (its Remote and Local branches) and
        ///   https://developer.ui.com/site-manager/v1.0.0/gettingstarted.</para>
        /// </summary>
        private const String KeyIssuers =
            "a local console's key is created in the Network application under Settings and then " +
            "Integrations, and the cloud connector takes a Site Manager key created at unifi.ui.com under " +
            "Settings and then API Keys";

        // Resource paths, from the vendor's machine-readable developer index (llms.txt), which lists
        // GET /v1/sites "List Local Sites", GET /v1/sites/{siteId}/devices "List Adopted Devices",
        // GET /v1/sites/{siteId}/devices/{deviceId} "Get Adopted Device Details" and
        // GET /v1/sites/{siteId}/clients "List Connected Clients". They hang off whichever of the two
        // published servers the operator configured, both of which are just a base URL.
        private const String SitesPath = "/v1/sites";
        private const String DevicesPathFormat = "/v1/sites/{0}/devices";
        private const String DeviceDetailsPathFormat = "/v1/sites/{0}/devices/{1}";
        private const String ClientsPathFormat = "/v1/sites/{0}/clients";

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly HttpClient _http;
        private readonly String _baseUrl;
        private readonly String _apiKey;
        private readonly ILogger _logger;

        private Int32 _retriesLeft = RetryBudget;

        /// <summary>Builds the run's reader.</summary>
        /// <param name="http">The runtime's client, whose handler enforces the allowed-host list.</param>
        /// <param name="baseUrl">A base URL already through <see cref="RequireIntegrationBaseUrl"/>.</param>
        /// <param name="apiKey">The run's leased key value.</param>
        /// <param name="logger">The provider's logger, every sink of which is behind the redaction wrap.</param>
        /// <exception cref="ProviderConfigurationException">The key cannot be sent as a header.</exception>
        public UnifiClient(HttpClient http, Uri baseUrl, String apiKey, ILogger logger)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            if (baseUrl == null)
            {
                throw new ArgumentNullException(nameof(baseUrl));
            }

            // Trailing slash removed once, here, so a path is composed by concatenation exactly the same
            // way whether the operator pasted one or not. Uri-relative composition is deliberately not
            // used: it would silently replace the console's "/proxy/network/integration" prefix with the
            // rooted resource path and send every request to the console's web UI instead.
            _baseUrl = baseUrl.AbsoluteUri.TrimEnd('/');
            _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));

            foreach (var character in _apiKey)
            {
                // The resolver already refused a control or non-ASCII character, eagerly, and owns that rule
                // and its reason. This stays as an assertion at the site that composes the header rather than
                // as a second opinion: the setter below does not validate, so a value carrying a line break
                // would inject a header, and a boundary that must be safe does not trust its input.
                if (Char.IsControl(character) || character > 127)
                {
                    // Refused BEFORE the header is composed, and the value is deliberately not quoted in
                    // the message: the validating header setter puts the offending value into its own
                    // FormatException message, and that message would travel into the job report and the
                    // container log. The credential itself is what would be quoted there.
                    throw new ProviderConfigurationException(String.Format(
                        "The credential supplied for setting '{0}' contains a character that cannot be sent as " +
                        "the {1} header: it is either a control character or not ASCII. Its value is " +
                        "deliberately not quoted here. A key is taken verbatim except for one trailing line " +
                        "ending, so look for what a copy brought along with it: a line break, a non-breaking " +
                        "space, or a quotation mark an editor turned into a curly one.",
                        UnifiNetworkProvider.ApiKeySetting, ApiKeyHeader));
                }
            }
        }

        /// <summary>
        ///   What a refused key is told, and it is the message a person acts on: which front door refused,
        ///   which of the two things went wrong, and what to go and look at.
        ///
        ///   <para>401 and 403 are separated because they narrow to different things: 401 is "not a key I
        ///   accept", where the usual cause is a key issued for the OTHER published front door, since the local
        ///   console and the cloud connector each issue their own; 403 is "not a read I allow", which is
        ///   usually a permission on the key.</para>
        ///
        ///   <para>Neither message asserts that the credential was VALIDATED. It cannot know that: an
        ///   authorization layer in front of a console answers either status without ever looking at the
        ///   header, and this client cannot tell that apart. So it says what happened and lists the
        ///   candidates, in the same spirit as the refusal below - this vendor documents no failure status at
        ///   all, and an unfounded certainty about the one thing a reader will act on is worse than a list.</para>
        ///
        ///   <para>The credential is never quoted, its length is never given, and the response body is never
        ///   echoed: this message travels to the job report and to every log sink. The content TYPE is
        ///   reported, because an answer that is not JSON is the one cheap sign that whatever refused is not
        ///   the integration API.</para>
        /// </summary>
        private static String RefusedCredentialMessage(HttpResponseMessage response, String url)
        {
            var host = Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed.Host : "the console";
            var reason = response.ReasonPhrase ?? "no reason given";

            var mediaType = response.Content?.Headers?.ContentType?.MediaType;
            var notJson = String.IsNullOrEmpty(mediaType) ||
                          mediaType!.IndexOf("json", StringComparison.OrdinalIgnoreCase) < 0;
            var aside = notJson
                ? String.Format(
                    " The answer was {0} rather than JSON, which the integration API would have sent, so " +
                    "consider that something else refused it: the path of the base URL, or a proxy, portal or " +
                    "gateway in front of the console.",
                    String.IsNullOrEmpty(mediaType) ? "sent with no content type" : "'" + mediaType + "'")
                : String.Empty;

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return String.Format(
                    "{0} answered 403 ({1}) to GET {2}, refusing the READ rather than the key. The key was " +
                    "sent as the {3} header. The candidates, in order: the permissions on the key; on the " +
                    "cloud connector, a console that is not the key owner's, since the vendor documents a key " +
                    "that is not an organization key as reaching its owner's consoles only; and an " +
                    "authorization layer in front of the console that never looked at the key.{4} Nothing was " +
                    "withdrawn.",
                    host, reason, url, ApiKeyHeader, aside);
            }

            return String.Format(
                "{0} answered 401 ({1}) to GET {2}, refusing the credential. The key was sent as the {3} " +
                "header, so start with the key rather than the network. The two published base URLs are two " +
                "different front doors and each issues its OWN key: {4}. So check which of the two this base " +
                "URL names, that the key came from that one, and that it has not been revoked.{5} Nothing was " +
                "withdrawn.",
                host, reason, url, ApiKeyHeader, KeyIssuers, aside);
        }

        /// <summary>
        ///   Validates the <c>baseUrl</c> setting and REFUSES a bare host rather than repairing it, naming
        ///   both published forms in the refusal.
        ///
        ///   <para>Guessing is the failure worth preventing: a bare console address with a guessed path
        ///   appended sends an API key to the console's own web UI, which is a login form on the same host
        ///   and the same certificate. The path is not required to END in the published suffix, because a
        ///   self-hosted install that serves the integration API needs no code from this provider, only the
        ///   right base URL, which only its operator can discover.</para>
        /// </summary>
        /// <param name="value">The setting as the job supplied it.</param>
        /// <returns>The absolute base URL every request hangs off.</returns>
        /// <exception cref="ProviderConfigurationException">It is not an absolute URL, it names a host with
        /// no integration API path, it names a scheme other than http or https, or it carries a query or a
        /// fragment.</exception>
        public static Uri RequireIntegrationBaseUrl(String value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
                String.IsNullOrEmpty(parsed.AbsolutePath) || parsed.AbsolutePath == "/")
            {
                throw new ProviderConfigurationException(String.Format(
                    "Setting '{0}' must be the FULL integration API base URL, and '{1}' names no path. It is " +
                    "refused rather than repaired, because guessing the path wrong sends the API key to the " +
                    "console's web UI. The two published forms are '{2}' for a local console and '{3}' for " +
                    "the cloud connector.",
                    UnifiNetworkProvider.BaseUrlSetting, value, LocalConsoleForm, CloudConnectorForm));
            }

            if (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
            {
                // Refused here only because anything else is not a request this client can make at all (it
                // would surface as an unhandled NotSupportedException rather than as a named failure).
                // Whether plain http is acceptable is NOT decided here: that is the runtime's outbound
                // guard, which refuses it for a credentialed run with loopback excepted, and one home for
                // that rule is what keeps it enforceable for providers that never read it.
                throw new ProviderConfigurationException(String.Format(
                    "Setting '{0}' names the scheme '{1}', and this integration speaks only http and https. " +
                    "Supply '{2}' or '{3}'.",
                    UnifiNetworkProvider.BaseUrlSetting, parsed.Scheme, LocalConsoleForm, CloudConnectorForm));
            }

            if (!String.IsNullOrEmpty(parsed.Query) || !String.IsNullOrEmpty(parsed.Fragment))
            {
                throw new ProviderConfigurationException(String.Format(
                    "Setting '{0}' carries a query string or a fragment. Every request appends its own " +
                    "offset and limit, so a base URL that already carries one cannot be extended; supply " +
                    "'{1}' or '{2}' instead.",
                    UnifiNetworkProvider.BaseUrlSetting, LocalConsoleForm, CloudConnectorForm));
            }

            if (!String.IsNullOrEmpty(parsed.UserInfo))
            {
                // A SECRET IN A SETTING, and the one shape a person actually types. It would be unredactable:
                // the lease holds credentials, and this value never went through it, so every failure message
                // quoting the composed URL would carry it to the job report, to the container log and into an
                // OTLP span attribute. The API key belongs in the credential setting, which is leased.
                throw new ProviderConfigurationException(String.Format(
                    "Setting '{0}' carries a user name or password in the URL itself. That value is not a " +
                    "credential this runtime can hold or redact, so it would appear in every failure message " +
                    "this run reports and logs. Put the address here and the key in '{1}'.",
                    UnifiNetworkProvider.BaseUrlSetting, UnifiNetworkProvider.ApiKeySetting));
            }

            return parsed;
        }

        /// <summary>Reads every site of the console, following the list to its end.</summary>
        /// <param name="cancellationToken">The run's token.</param>
        /// <returns>Every site the console served.</returns>
        public Task<IReadOnlyList<UnifiSite>> ReadSitesAsync(CancellationToken cancellationToken)
        {
            return ReadPagedAsync<UnifiSite>(SitesPath, cancellationToken);
        }

        /// <summary>Reads every adopted device of one site, following the list to its end.</summary>
        /// <param name="siteId">The site's UUID, as the console reported it.</param>
        /// <param name="cancellationToken">The run's token.</param>
        /// <returns>Every device the console served for that site.</returns>
        public Task<IReadOnlyList<UnifiDevice>> ReadDevicesAsync(String siteId, CancellationToken cancellationToken)
        {
            return ReadPagedAsync<UnifiDevice>(
                String.Format(DevicesPathFormat, Segment(siteId)), cancellationToken);
        }

        /// <summary>Reads every connected client of one site, following the list to its end.</summary>
        /// <param name="siteId">The site's UUID, as the console reported it.</param>
        /// <param name="cancellationToken">The run's token.</param>
        /// <returns>Every client the console served for that site.</returns>
        public Task<IReadOnlyList<UnifiConnectedClient>> ReadClientsAsync(String siteId,
            CancellationToken cancellationToken)
        {
            return ReadPagedAsync<UnifiConnectedClient>(
                String.Format(ClientsPathFormat, Segment(siteId)), cancellationToken);
        }

        /// <summary>
        ///   Reads one device's details, the second request shape, and the only one that tolerates a 404:
        ///   a device the list named and that is gone by the time it is asked about was removed mid-run.
        ///   ANY other failure on this read fails the run, because a 500 does not mean the device is gone,
        ///   and treating it as gone would omit the device from a complete snapshot and delete it.
        /// </summary>
        /// <param name="siteId">The site's UUID.</param>
        /// <param name="deviceId">The device's UUID.</param>
        /// <param name="cancellationToken">The run's token.</param>
        /// <returns>The details, or null when the console answered 404.</returns>
        public Task<UnifiDeviceDetails?> ReadDeviceDetailsAsync(String siteId, String deviceId,
            CancellationToken cancellationToken)
        {
            return SendGetAsync<UnifiDeviceDetails>(
                Url(String.Format(DeviceDetailsPathFormat, Segment(siteId), Segment(deviceId))),
                true,
                cancellationToken);
        }

        /// <summary>
        ///   The paging loop, and the place where a wrong answer DELETES data: this provider declares a
        ///   complete snapshot, so a list that stops early does not miss devices, it withdraws them and, on
        ///   their last claim, deletes them. Three defences, each load-bearing on its own:
        ///
        ///   <para>(a) the offset advances by the number of items ACTUALLY RETURNED, never by the size
        ///   asked for and never by what the envelope says it returned, so a console that serves a shorter
        ///   page than requested cannot make the loop skip the items in between;</para>
        ///
        ///   <para>(b) the loop stops when a page returns nothing, rather than when the collected count
        ///   reaches <c>totalCount</c>, so a console whose total is stale or wrong still gets read to its
        ///   real end. It costs exactly one extra request per list;</para>
        ///
        ///   <para>(c) the run is REFUSED if the loop ended with fewer items than the console promised.
        ///   The promise compared against is the LOWEST <c>totalCount</c> any page reported: a list that
        ///   churns while it is paged reports a different total per page in both directions, and a run that
        ///   saw everything the console claimed throughout must not fail, while a console that reports 500
        ///   and serves 200 must.</para>
        /// </summary>
        private async Task<IReadOnlyList<TItem>> ReadPagedAsync<TItem>(String resourcePath,
            CancellationToken cancellationToken)
            where TItem : class
        {
            var items = new List<TItem>();
            var offset = 0;
            var pages = 0;
            Int32? promised = null;

            while (true)
            {
                if (pages >= PageLimit)
                {
                    throw new ProviderSourceException(String.Format(
                        "The console served {0} pages of '{1}' without ever returning an empty page. The list " +
                        "has no end this run can see, so the snapshot cannot claim to describe the whole " +
                        "console.",
                        pages, resourcePath));
                }

                pages++;
                var page = await SendGetAsync<UnifiPage<TItem>>(
                    String.Format("{0}?offset={1}&limit={2}",
                        Url(resourcePath),
                        offset.ToString(CultureInfo.InvariantCulture),
                        PageSize.ToString(CultureInfo.InvariantCulture)),
                    false,
                    cancellationToken).ConfigureAwait(false);

                if (page!.TotalCount.HasValue &&
                    (!promised.HasValue || page.TotalCount.Value < promised.Value))
                {
                    promised = page.TotalCount.Value;
                }

                var returned = page.Data?.Count ?? 0;
                if (returned == 0)
                {
                    // Defence (b).
                    break;
                }

                items.AddRange(page.Data!);

                // Defence (a).
                offset += returned;
            }

            if (!promised.HasValue)
            {
                throw new ProviderSourceException(String.Format(
                    "No page of '{0}' carried the required totalCount, so there is no promise to check this " +
                    "list against. The check is the only thing that makes a complete declaration honest here, " +
                    "so its absence is a failure rather than a shrug.",
                    resourcePath));
            }

            if (items.Count < promised.Value)
            {
                // Defence (c).
                throw new ProviderSourceException(String.Format(
                    "The console promised {0} items of '{1}' and served {2}. The run is refused rather than " +
                    "described as complete: the {3} items it did not serve would be withdrawn from the graph " +
                    "and, on their last claim, deleted.",
                    promised.Value, resourcePath, items.Count, promised.Value - items.Count));
            }

            return items;
        }

        /// <summary>
        ///   The single HTTP path: one GET, the key header, the defensive 429 handling, and a deserialized
        ///   document. Returns null only for a tolerated 404, so a caller can tell "gone" from "empty".
        /// </summary>
        private async Task<TDocument?> SendGetAsync<TDocument>(String url, Boolean tolerateNotFound,
            CancellationToken cancellationToken)
            where TDocument : class
        {
            while (true)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);

                // Set without validation, on purpose: the validating setter reports a rejected value by
                // quoting it in a FormatException, and the value here is somebody's console credential.
                // The constructor already refused a value carrying a control or non-ASCII character, so
                // nothing can be smuggled into the header block.
                request.Headers.TryAddWithoutValidation(ApiKeyHeader, _apiKey);

                // Every official example sends it and the document requires it nowhere, which is exactly
                // when to send it anyway: it costs one header and removes a difference between what this
                // client does and what the vendor's own curl line does.
                request.Headers.TryAddWithoutValidation("Accept", "application/json");

                var response = await ProviderRequest
                    .SendAsync(_http, request, "The console", cancellationToken).ConfigureAwait(false);

                using (response)
                {
                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        await WaitOutRateLimitAsync(response, url, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (tolerateNotFound && response.StatusCode == HttpStatusCode.NotFound)
                    {
                        return null;
                    }

                    // A refused key is its OWN failure kind, before the catch-all below. Reported as
                    // 'source' it would send a reader to the network, which is the one place the answer is
                    // not: something answered, promptly, that this request may not proceed. Which of the two
                    // published front doors was asked is the first thing to check, because each issues its
                    // own key and a key for one is not a key for the other.
                    if (response.StatusCode == HttpStatusCode.Unauthorized ||
                        response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        throw new ProviderCredentialRejectedException(RefusedCredentialMessage(response, url));
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        throw new ProviderSourceException(String.Format(
                            "The console answered {0} ({1}) to GET {2}. The document declares no response " +
                            "other than 200 and 201 anywhere, so nothing here can be read as expected.",
                            (Int32)response.StatusCode, response.ReasonPhrase ?? "no reason given", url));
                    }

                    TDocument? document;
                    try
                    {
                        document = await response.Content
                            .ReadFromJsonAsync<TDocument>(JsonOptions, cancellationToken).ConfigureAwait(false);
                    }
                    catch (JsonException exception)
                    {
                        throw new ProviderSourceException(String.Format(
                            "The console answered GET {0} with a body this contract cannot read: {1}",
                            url, exception.Message), exception);
                    }

                    // An empty body is an unusable answer, not an empty resource, and it must not be
                    // confused with the tolerated 404 above.
                    return document ?? throw new ProviderSourceException(String.Format(
                        "The console answered GET {0} with success and no document.", url));
                }
            }
        }

        /// <summary>
        ///   Everything this provider does about 429 is DEFENSIVE, and says so here because the ground for
        ///   it is an absence rather than a contract: 429 is undocumented, and confirmed undocumented in
        ///   three vendor sources rather than one. The OpenAPI document declares no 429 and no rate limit
        ///   (its only documented responses anywhere are 200 and 201, and its Error Message schema is
        ///   referenced from no response at all); the vendor's Postman collection carries no rate limit and
        ///   no auth; and the vendor's Error Handling page gives the error shape and says nothing about
        ///   rate limiting.
        ///
        ///   <para>So: a SHORT <c>Retry-After</c> is honoured a bounded number of times, and anything
        ///   longer, a 429 with no guidance at all, or an exhausted budget fails the run naming what the
        ///   console asked for. Never a partial success, because the snapshot either describes the whole
        ///   console or must not claim to, and a short list here deletes data.</para>
        /// </summary>
        private async Task WaitOutRateLimitAsync(HttpResponseMessage response, String url,
            CancellationToken cancellationToken)
        {
            var asked = AskedToWaitFor(response);

            if (asked == null)
            {
                throw new ProviderSourceException(String.Format(
                    "The console answered 429 to GET {0} with no Retry-After to act on. Rate limiting is " +
                    "undocumented for this API, so there is no published interval to fall back on, and " +
                    "guessing one would make the snapshot's completeness a guess too.", url));
            }

            if (asked.Value > LongestRetryAfter)
            {
                throw new ProviderSourceException(String.Format(
                    "The console answered 429 to GET {0} asking for {1} seconds, longer than the {2} seconds " +
                    "this run will wait. A caller is waiting on this job, and the snapshot either describes " +
                    "the whole console or must not claim to.",
                    url,
                    asked.Value.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture),
                    LongestRetryAfter.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)));
            }

            if (_retriesLeft <= 0)
            {
                throw new ProviderSourceException(String.Format(
                    "The console has answered 429 more than {0} times this run, most recently to GET {1}. " +
                    "It is rate limiting harder than a single snapshot can be read through.",
                    RetryBudget, url));
            }

            _retriesLeft--;
            _logger.LogWarning(
                "The UniFi console asked for {RetryAfterSeconds}s after a 429; waiting and retrying " +
                "({RetriesLeft} of {RetryBudget} retries left). Rate limiting is undocumented for this API.",
                asked.Value.TotalSeconds, _retriesLeft, RetryBudget);

            await Task.Delay(asked.Value < TimeSpan.Zero ? TimeSpan.Zero : asked.Value, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        ///   What the console asked to wait for, from either form of <c>Retry-After</c>, or null when it
        ///   offered no guidance. The date form is the one place this provider reads a clock, and only
        ///   because a date cannot become a wait without one; UTC, since a local clock would make the wait
        ///   depend on the container's time zone.
        /// </summary>
        private static TimeSpan? AskedToWaitFor(HttpResponseMessage response)
        {
            var retryAfter = response.Headers.RetryAfter;
            if (retryAfter == null)
            {
                return null;
            }

            if (retryAfter.Delta.HasValue)
            {
                return retryAfter.Delta.Value;
            }

            return retryAfter.Date.HasValue ? retryAfter.Date.Value - DateTimeOffset.UtcNow : (TimeSpan?)null;
        }

        /// <summary>The absolute URL of one resource path under the configured base URL.</summary>
        private String Url(String resourcePath)
        {
            return String.Concat(_baseUrl, resourcePath);
        }

        /// <summary>
        ///   Makes an id the SOURCE reported safe to be a path segment. The ids are UUIDs in the published
        ///   contract, but they arrive from the console rather than from this code, and an unescaped
        ///   separator in one would steer the next request off the resource it names.
        ///
        ///   <para>Escaping alone is not enough, which is why the dot segments are refused rather than
        ///   escaped: both of their characters are unreserved, so escaping leaves them intact and the
        ///   request URI then normalises the segment away and reads a different resource with the same
        ///   key.</para>
        /// </summary>
        private static String Segment(String id)
        {
            if (id == "." || id == "..")
            {
                throw new ProviderSourceException(String.Format(
                    "The console reported '{0}' as an id. It is refused rather than requested, because as a " +
                    "path segment it would silently address a different resource.", id));
            }

            return Uri.EscapeDataString(id);
        }
    }
}
