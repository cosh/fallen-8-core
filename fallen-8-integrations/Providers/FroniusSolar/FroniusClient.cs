// MIT License
//
// FroniusClient.cs
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
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Integrations.Contract;

namespace NoSQL.GraphDB.Integrations.Providers.FroniusSolar
{
    /// <summary>
    ///   The three requests the <c>fronius-solar</c> provider issues, and the ONE place that decides
    ///   whether the device answered with data. It is deliberately the whole of this provider's contact
    ///   with the network, so "which requests does this integration make" is answered by reading one file.
    ///
    ///   <para><b>Three requests, and no others.</b> Ask where the API lives, read the inverter list, read
    ///   the logging device. ABSOLUTELY NO REALTIME REQUEST: power, current, voltage and energy counters
    ///   change between any two runs, so landing them would make every run a write and make the
    ///   zero-mutation invariant unobservable for the one provider whose source is never unchanged. What
    ///   this reads is shape and coarse state, which changes at dawn and dusk rather than every second.</para>
    ///
    ///   <para><b>Where a failure is decided.</b> Every reply but the first arrives in an envelope whose
    ///   <c>Head.Status.Code</c> is the device's own verdict, and FAILURE ARRIVES WITH HTTP 200. So a
    ///   non-success HTTP status and a non-zero status code are treated as the same kind of event - the
    ///   device answering "not data" - and only the network failing to answer at all is a different one.
    ///   Each of the three methods below then decides whether that is fatal, because the answer differs
    ///   per resource and nothing else in the runtime can know that.</para>
    ///
    ///   <para>Nothing here holds mutable state, and nothing on the handed-in <see cref="HttpClient"/> is
    ///   mutated: no base address, no default header. The client belongs to the run, a delegating handler
    ///   on it enforces the allowed-host list, and a provider that built its own would be reaching the
    ///   network behind every seam the runtime controls.</para>
    /// </summary>
    public sealed class FroniusClient
    {
        /// <summary>The version probe, at a fixed path because it is what reports where the rest lives.</summary>
        public const String ApiVersionResource = "solar_api/GetAPIVersion.cgi";

        /// <summary>The inverter list, under the root the device reported.</summary>
        public const String InverterInfoResource = "GetInverterInfo.cgi";

        /// <summary>The logging device, under the root the device reported.</summary>
        public const String LoggerInfoResource = "GetLoggerInfo.cgi";

        /// <summary>How much of a device's own words a failure message quotes. Bounded because a 404 from
        /// a web interface answers with a whole HTML page, and an unbounded quote would bury the sentence
        /// the reader needs.</summary>
        private const Int32 QuotedBodyLimit = 200;

        /// <summary>
        ///   Web defaults for the camelCase-insensitive match, plus reading a number from a quoted
        ///   string: this API's own document declares types its platforms do not all send, and a quoted
        ///   number is the cheapest of those divergences to absorb. The two that cannot be absorbed this
        ///   way are read as raw elements on the DTO instead.
        /// </summary>
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };

        private readonly HttpClient _http;
        private readonly ILogger _logger;

        /// <param name="http">The run's client, from <c>ProviderContext.Http</c>.</param>
        /// <param name="logger">The run's logger, from <c>ProviderContext.Logger</c>.</param>
        public FroniusClient(HttpClient http, ILogger logger)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        ///   Reads the <c>baseUrl</c> setting as the device's address. A bare host is REFUSED rather than
        ///   repaired: guessing a scheme is guessing, and the refusal names both accepted forms instead.
        /// </summary>
        /// <param name="baseUrl">The setting's value.</param>
        /// <exception cref="ProviderConfigurationException">The value is not an absolute http or https URL,
        /// or carries a query or a fragment, neither of which a Solar API address has.</exception>
        public static Uri Root(String? baseUrl)
        {
            var text = (baseUrl ?? String.Empty).Trim();
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                String.IsNullOrEmpty(uri.Host))
            {
                throw new ProviderConfigurationException(String.Format(
                    "'{0}' is not a Solar API address. Give the device's own address with its scheme, " +
                    "'http://192.168.1.50' or 'https://192.168.1.50'; a bare host is refused rather than " +
                    "repaired, because guessing which one a device serves is guessing.", text));
            }

            if (!String.IsNullOrEmpty(uri.Query) || !String.IsNullOrEmpty(uri.Fragment))
            {
                throw new ProviderConfigurationException(String.Format(
                    "'{0}' carries a query or a fragment. A Solar API address is a device address, and " +
                    "every path this provider reads is one the device itself reports.", text));
            }

            return uri;
        }

        /// <summary>
        ///   Asks the device where its API lives. The root every other request hangs off is ASKED rather
        ///   than configured, because v0 versus v1 is a property of the device.
        /// </summary>
        /// <exception cref="ProviderSourceException">The device did not answer, answered a non-success
        /// status (a 404 being the switched-off Solar API, which the message says and quotes), or answered
        /// without a base URL to hang the other two requests off.</exception>
        public async Task<FroniusApiRoot> ReadApiRootAsync(Uri root, CancellationToken cancellationToken)
        {
            if (root == null)
            {
                throw new ArgumentNullException(nameof(root));
            }

            var url = Combine(root, ApiVersionResource);
            var answer = await AskAsync(url, cancellationToken).ConfigureAwait(false);
            if (!answer.Succeeded)
            {
                throw new ProviderSourceException(DeviceRefused(url, answer));
            }

            // The ONE reply with no envelope, so there is deliberately no status-code check here.
            var version = Deserialize<FroniusApiVersionDto>(url, answer.Body);
            if (version == null || String.IsNullOrWhiteSpace(version.BaseUrl))
            {
                throw new ProviderSourceException(String.Format(
                    "{0} answered without a BaseURL. The resource root is asked rather than configured, so " +
                    "there is nothing to hang the inverter and logger requests off, and a run that cannot " +
                    "look must not describe an empty installation. The device said: {1}",
                    url, Quote(answer.Body)));
            }

            // Composed onto the CONFIGURED root, with both sides' slashes trimmed, so a device-reported
            // base URL can extend the path and can never move the request to another authority.
            var resources = Combine(root, version.BaseUrl!);
            _logger.LogDebug(
                "Fronius Solar API version {ApiVersion} (compatibility {CompatibilityRange}) serves its " +
                "resources at {Resources}.",
                version.ApiVersion, version.CompatibilityRange, resources);

            return new FroniusApiRoot(version.ApiVersion, resources);
        }

        /// <summary>
        ///   Reads every inverter the device has seen in the last 24 hours, in a stable order.
        /// </summary>
        /// <exception cref="ProviderSourceException">The device did not answer, answered a failure of any
        /// kind (including one carrying HTTP 200), or reported no inverter at all. An empty list is a
        /// failure and never an empty installation, because a datamanager reports every inverter it has
        /// seen in the last 24 hours, and an empty complete snapshot withdraws everything this identity
        /// ever claimed.</exception>
        public async Task<IReadOnlyList<FroniusInverterEntry>> ReadInvertersAsync(Uri resources,
            CancellationToken cancellationToken)
        {
            if (resources == null)
            {
                throw new ArgumentNullException(nameof(resources));
            }

            var url = Combine(resources, InverterInfoResource);
            var answer = await AskAsync(url, cancellationToken).ConfigureAwait(false);
            if (!answer.Succeeded)
            {
                throw new ProviderSourceException(DeviceRefused(url, answer));
            }

            if (!TryReadBody<FroniusInverterInfoBodyDto>(url, answer.Body, out var body, out var refusal))
            {
                throw new ProviderSourceException(String.Format(
                    "{0} A run must not read that as an empty installation: it would declare a complete " +
                    "snapshot with nothing in it, withdraw every claim this identity ever made and delete " +
                    "what the source still has.", refusal));
            }

            var data = body!.Data;
            if (data == null || data.Count == 0)
            {
                throw new ProviderSourceException(String.Format(
                    "{0} answered OKAY and named no inverter. That is not an empty installation: the " +
                    "device reports every inverter it has seen in the last 24 hours, so a run cannot tell " +
                    "an empty list from an unreadable one, and a complete snapshot with no entities " +
                    "withdraws and deletes everything this identity claimed.", url));
            }

            var entries = new List<FroniusInverterEntry>(data.Count);
            foreach (var pair in data)
            {
                // A null map value is kept rather than dropped: it has no UniqueID, which is exactly what
                // the caller reports and skips it for, and keeping it makes the count of what the device
                // reported honest - the count that decides which device holds the address claim.
                entries.Add(new FroniusInverterEntry(pair.Key, pair.Value ?? new FroniusInverterDto()));
            }

            // The device ids arrive as a MAP, whose order no contract promises. Sorting ordinally is what
            // keeps two runs over one unchanged source byte-identical instead of merely equivalent.
            entries.Sort(static (left, right) => String.CompareOrdinal(left.DeviceId, right.DeviceId));
            return entries;
        }

        /// <summary>
        ///   Reads the logging device that fronts the Solar API, and is THE ONE TOLERATED CALL:
        ///   <c>GetLoggerInfo</c> fails by design on a GEN24, Tauro and Verto, where the inverter itself
        ///   serves the API. That is a fact about the device, not a failed run.
        ///
        ///   <para>The line drawn here: whatever the DEVICE answered - a non-success status, a non-zero
        ///   Solar API status code, or a success carrying no logger object - is tolerated and described,
        ///   because all three are the device saying there is no separate logging device here. The network
        ///   failing to answer at all, or answering something that is not JSON, is NOT tolerated and fails
        ///   the run: continuing there would let a complete snapshot omit a logging device that exists,
        ///   which withdraws it and every <c>loggedBy</c> edge and then deletes it.</para>
        ///
        ///   <para>A 404 here is also NOT the switched-off Solar API: <c>GetAPIVersion</c> answered on this
        ///   same address moments ago, so the API is on and this one resource is simply not served.</para>
        /// </summary>
        /// <exception cref="ProviderSourceException">The device could not be reached, or answered something
        /// that is not JSON.</exception>
        public async Task<FroniusLoggerReading> ReadLoggerAsync(Uri resources,
            CancellationToken cancellationToken)
        {
            if (resources == null)
            {
                throw new ArgumentNullException(nameof(resources));
            }

            var url = Combine(resources, LoggerInfoResource);
            var answer = await AskAsync(url, cancellationToken).ConfigureAwait(false);
            if (!answer.Succeeded)
            {
                return FroniusLoggerReading.Absent(String.Format(
                    "{0} answered HTTP {1}, which is how a GEN24, Tauro or Verto says the inverter itself " +
                    "serves the Solar API and there is no separate logging device. That is a fact about " +
                    "the device rather than a failed run, so the run continues without one. The device " +
                    "said: {2}", url, answer.Status.ToString(CultureInfo.InvariantCulture),
                    Quote(answer.Body)));
            }

            if (!TryReadBody<FroniusLoggerInfoBodyDto>(url, answer.Body, out var body, out var refusal))
            {
                return FroniusLoggerReading.Absent(String.Format(
                    "{0} The run continues without a logging device, because that is the device answering " +
                    "rather than the run failing.", refusal));
            }

            var info = body!.LoggerInfo;
            if (info == null)
            {
                return FroniusLoggerReading.Absent(String.Format(
                    "{0} answered OKAY with no LoggerInfo, so this device describes no logging device.",
                    url));
            }

            return FroniusLoggerReading.Found(info);
        }

        /// <summary>
        ///   Composes a request URL by trimming both sides' slashes and joining. Deliberately not
        ///   <see cref="Uri"/> relative resolution: the device REPORTS its base url, and a reported value
        ///   that happened to be rooted or absolute would silently discard a reverse proxy's path prefix
        ///   or move the request off the configured device. Trimming and joining onto the configured left
        ///   part makes the authority impossible to change.
        /// </summary>
        private static Uri Combine(Uri root, String relative)
        {
            var text = root.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/" + relative.Trim().Trim('/');
            if (!Uri.TryCreate(text, UriKind.Absolute, out var url))
            {
                throw new ProviderSourceException(String.Format(
                    "'{0}' is not a URL this run can request. Every path here is composed from the base " +
                    "url the device itself reported, so the device answered something unusable.", text));
            }

            return url;
        }

        /// <summary>
        ///   Issues one GET and returns what came back. Throws only when the network did not answer,
        ///   because that is the one failure no resource can interpret: everything the device itself said,
        ///   at any status, is returned for the caller to judge.
        /// </summary>
        private async Task<FroniusAnswer> AskAsync(Uri url, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new ProviderSourceException(String.Format(
                    "The Fronius Solar API at {0} did not answer: {1}. The run fails and withdraws " +
                    "nothing, because \"I could not look\" must never become \"there is nothing there\".",
                    url, ex.Message), ex);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ProviderSourceException(String.Format(
                    "The Fronius Solar API at {0} did not answer in time. The run fails and withdraws " +
                    "nothing.", url), ex);
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return new FroniusAnswer((Int32)response.StatusCode, body);
            }
        }

        /// <summary>
        ///   Reads the envelope and applies the rule the whole vendor document turns on: only
        ///   <c>Head.Status.Code</c> 0 means <see cref="FroniusEnvelope{TBody}.Body"/> is data.
        /// </summary>
        /// <returns>
        ///   False with a sentence naming what the device said, in the device's own vocabulary - a
        ///   missing status, a translated failure code, or an OKAY with no body. Each caller decides
        ///   whether that is fatal for its own resource.
        /// </returns>
        /// <exception cref="ProviderSourceException">The reply is not JSON at all, which no resource can
        /// interpret.</exception>
        private static Boolean TryReadBody<TBody>(Uri url, String body, out TBody? payload,
            out String? refusal)
        {
            payload = default;
            refusal = null;

            var envelope = Deserialize<FroniusEnvelope<TBody>>(url, body);
            var code = envelope?.Head?.Status?.Code;
            if (code == null)
            {
                refusal = String.Format(
                    "{0} answered with no Head.Status.Code. Only code 0 means the body is data, so a " +
                    "reply without one cannot be read as data at all. The device said: {1}",
                    url, Quote(body));
                return false;
            }

            if (code.Value != FroniusStatusCodes.Okay)
            {
                refusal = String.Format(
                    "{0} answered Solar API status code {1} ({2}){3}. Failure arrives with HTTP 200 on " +
                    "this API: the HTTP status said success and the device said it has no data.",
                    url, code.Value.ToString(CultureInfo.InvariantCulture),
                    FroniusStatusCodes.Describe(code.Value),
                    Said(envelope!.Head!.Status!));
                return false;
            }

            if (envelope!.Body == null)
            {
                refusal = String.Format("{0} answered status code 0 (OKAY) and no body.", url);
                return false;
            }

            payload = envelope.Body;
            return true;
        }

        /// <summary>Deserializes a reply, turning a body that is not JSON into a named source failure
        /// rather than an exception whose message is about brackets.</summary>
        private static T? Deserialize<T>(Uri url, String body)
        {
            try
            {
                return JsonSerializer.Deserialize<T>(body, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new ProviderSourceException(String.Format(
                    "{0} answered something that is not the JSON this API documents, so the run fails " +
                    "rather than describing what it could not read. The device said: {1}",
                    url, Quote(body)), ex);
            }
        }

        /// <summary>
        ///   The sentence for a non-success HTTP status. A 404 gets the specific one, because a GEN24,
        ///   Tauro or Verto delivered at bundle version 1.14.1 or higher (or factory reset at it) has the
        ///   Solar API OFF by default, and a reader who is not told that has no way to guess it.
        /// </summary>
        private static String DeviceRefused(Uri url, FroniusAnswer answer)
        {
            if (answer.Status == 404)
            {
                return String.Format(
                    "{0} answered HTTP 404. A GEN24, Tauro or Verto delivered at software bundle version " +
                    "1.14.1 or higher, or factory reset at it, has the Solar API switched OFF by default " +
                    "and answers 404 with \"Solar API disabled by customer config\". Switch it on in the " +
                    "inverter's own web interface, under Communication and then Solar API. The device " +
                    "said: {1}", url, Quote(answer.Body));
            }

            return String.Format(
                "{0} answered HTTP {1}. The Solar API is unauthenticated local HTTP, so this is the " +
                "device or the network rather than a credential. The device said: {2}",
                url, answer.Status.ToString(CultureInfo.InvariantCulture), Quote(answer.Body));
        }

        /// <summary>The device's own reason and user message, when it sent either, so a failure carries
        /// the vendor's words next to the transcribed code.</summary>
        private static String Said(FroniusStatusDto status)
        {
            var reason = String.IsNullOrWhiteSpace(status.Reason) ? null : status.Reason!.Trim();
            var message = String.IsNullOrWhiteSpace(status.UserMessage) ? null : status.UserMessage!.Trim();
            if (reason == null && message == null)
            {
                return String.Empty;
            }

            return ": " + String.Join(" ", reason ?? String.Empty, message ?? String.Empty).Trim();
        }

        /// <summary>What the device said, bounded and on one line: a 404 from a web interface is a whole
        /// HTML page, and an unbounded quote buries the sentence a reader needs.</summary>
        private static String Quote(String body)
        {
            var text = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (text.Length == 0)
            {
                return "(nothing)";
            }

            if (text.Length > QuotedBodyLimit)
            {
                text = text.Substring(0, QuotedBodyLimit) + " [...]";
            }

            return "\"" + text + "\"";
        }

        /// <summary>One HTTP reply, before anything decides what it means.</summary>
        private sealed class FroniusAnswer
        {
            internal FroniusAnswer(Int32 status, String body)
            {
                Status = status;
                Body = body;
            }

            internal Int32 Status { get; }

            internal String Body { get; }

            internal Boolean Succeeded => Status >= 200 && Status <= 299;
        }
    }

    /// <summary>
    ///   Where this device says its API lives, which is asked rather than configured.
    /// </summary>
    public sealed class FroniusApiRoot
    {
        /// <param name="apiVersion">The version the device reported, when it reported one.</param>
        /// <param name="resources">The root the other two requests hang off.</param>
        public FroniusApiRoot(Int32? apiVersion, Uri resources)
        {
            ApiVersion = apiVersion;
            Resources = resources ?? throw new ArgumentNullException(nameof(resources));
        }

        /// <summary>The API version, or null when the device did not say. Null rather than a default,
        /// because a version this run invented is worse than one it does not report.</summary>
        public Int32? ApiVersion { get; }

        /// <summary>The absolute root every other request is composed onto.</summary>
        public Uri Resources { get; }
    }

    /// <summary>
    ///   The logging device, or the sentence saying why there is none. Two states rather than a nullable,
    ///   because "no logging device" is a statement this run must report and pass to the address rule, not
    ///   a value it may quietly skip.
    /// </summary>
    public sealed class FroniusLoggerReading
    {
        private FroniusLoggerReading(FroniusLoggerDto? logger, String? absentBecause)
        {
            Logger = logger;
            AbsentBecause = absentBecause;
        }

        /// <summary>What the device said about its logging device, or null when it described none.</summary>
        public FroniusLoggerDto? Logger { get; }

        /// <summary>Why there is none, in a sentence a reader can act on, or null when there is one. It
        /// becomes the <c>loggerInfoUnavailable</c> diagnostic's message.</summary>
        public String? AbsentBecause { get; }

        /// <summary>The device described a logging device.</summary>
        public static FroniusLoggerReading Found(FroniusLoggerDto logger)
        {
            return new FroniusLoggerReading(logger ?? throw new ArgumentNullException(nameof(logger)), null);
        }

        /// <summary>The device described none, and this is why.</summary>
        public static FroniusLoggerReading Absent(String because)
        {
            return new FroniusLoggerReading(null, because);
        }
    }
}
