// MIT License
//
// IntegrationsEndpointTest.cs
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
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Controllers;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Integrations;
using NoSQL.GraphDB.Integrations.Conformance;
using NoSQL.GraphDB.Integrations.Configuration;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Pins the HTTP surface of the integrations feature through both hosted pipelines: the
    /// fallen-8-integrations runtime's own seven routes plus its health probe, and the apiApp's
    /// authenticated proxy over them. Both halves run in process, the runtime under
    /// WebApplicationFactory over its deliberately namespaced entry point.
    /// </summary>
    [TestClass]
    public class IntegrationsEndpointTest
    {

        #region the run is observable while it happens, and knowable after it ends

        /// <summary>
        ///   The default shape: accepted, with the id and the place to watch it. The synchronous shape it
        ///   replaced could not survive a real source - the report is the only copy this runtime makes, the
        ///   proxy holds a connection for a bounded time, and the run is built to outlive its caller.
        /// </summary>
        [TestMethod]
        public async Task AnAcceptedJob_Answers202WithARunIdAndWhereToWatchIt()
        {
            using var factory = new RuntimeFactory();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync("/integration/job",
                Json(JobBody(FroniusProviderId, "watch-me", "{\"baseUrl\":\"http://127.0.0.1:1\"}")));

            Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode,
                "the job route waited for the run, which no real source survives: " + await ReadText(response));
            var body = await ReadJson(response);
            Assert.IsFalse(String.IsNullOrWhiteSpace(body.GetProperty("runId").GetString()),
                "an accepted run carries no id, so a caller cannot tell its own run from a later one");
            Assert.AreEqual("/integration/run/watch-me", body.GetProperty("progress").GetString(),
                "the answer does not say where to watch the run it just started");
        }

        /// <summary>
        ///   The point of the feature: the outcome outlives the request that started it. This source is a
        ///   closed port, so the run starts, fails at its source, and its FAILURE is what has to be
        ///   readable afterwards - the case where the old contract lost everything.
        /// </summary>
        [TestMethod]
        public async Task TheOutcomeIsReadableAfterTheRequestIsLongGone()
        {
            using var factory = new RuntimeFactory();
            using var client = factory.CreateClient();

            using (var accepted = await client.PostAsync("/integration/job",
                Json(JobBody(FroniusProviderId, "outcome", "{\"baseUrl\":\"http://127.0.0.1:1\"}"))))
            {
                Assert.AreEqual(HttpStatusCode.Accepted, accepted.StatusCode);
            }

            System.Text.Json.JsonElement state = default;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                using var polled = await client.GetAsync("/integration/run/outcome");
                Assert.AreEqual(HttpStatusCode.OK, polled.StatusCode,
                    "the run this process just accepted is not tracked");
                state = await ReadJson(polled);
                if (!state.GetProperty("running").GetBoolean())
                {
                    break;
                }

                await Task.Delay(50);
            }

            Assert.IsFalse(state.GetProperty("running").GetBoolean(), "the run never finished");
            Assert.IsTrue(state.TryGetProperty("report", out var report) &&
                          report.ValueKind != System.Text.Json.JsonValueKind.Null,
                "the run ended with no report anywhere, which is exactly the hole this feature closes");
            Assert.AreEqual("source", report.GetProperty("errorKind").GetString(),
                "a closed port is a source failure, and the report that says so has to survive the request");
        }

        /// <summary>
        ///   A run in flight reports a PHASE. Without this, "observe" - which parses a large extract for
        ///   minutes while writing nothing - is indistinguishable from a hang.
        /// </summary>
        [TestMethod]
        public async Task ARunInFlight_ReportsThePhaseItIsIn()
        {
            using var factory = new RuntimeFactory();
            using var client = factory.CreateClient();

            using (var accepted = await client.PostAsync("/integration/job",
                Json(JobBody(FroniusProviderId, "phased", "{\"baseUrl\":\"http://127.0.0.1:1\"}"))))
            {
                Assert.AreEqual(HttpStatusCode.Accepted, accepted.StatusCode);
            }

            using var polled = await client.GetAsync("/integration/run/phased");
            var state = await ReadJson(polled);

            // Either it is still observing, or it already finished - both are fine. What must never be true
            // is a tracked run that names no phase and carries no outcome.
            var running = state.GetProperty("running").GetBoolean();
            var phase = state.TryGetProperty("phase", out var p) ? p.GetString() : null;
            var hasOutcome = state.TryGetProperty("report", out var r) &&
                             r.ValueKind != System.Text.Json.JsonValueKind.Null;
            Assert.IsTrue(running ? phase != null : hasOutcome || state.GetProperty("error").ValueKind !=
                System.Text.Json.JsonValueKind.Null,
                "a tracked run reported neither a phase nor an outcome, so a reader learns nothing");
        }

        /// <summary>
        ///   A job the runtime refused never ran, so it must leave nothing behind. Otherwise a rejection
        ///   would appear in the tracker as a run that happened.
        /// </summary>
        [TestMethod]
        public async Task ARejectedJob_IsStillRefusedSynchronously_AndTracksNothing()
        {
            using var factory = new RuntimeFactory();
            using var client = factory.CreateClient();

            using (var refused = await client.PostAsync("/integration/job",
                Json(JobBody("no-such-provider", "never-ran", null))))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode,
                    "an unknown provider was ACCEPTED, so the caller is told to watch a run that cannot exist");
            }

            using var polled = await client.GetAsync("/integration/run/never-ran");
            Assert.AreEqual(HttpStatusCode.NotFound, polled.StatusCode,
                "a job that never ran is tracked as a run: " + await ReadText(polled));
        }

        /// <summary>
        ///   A run can END before it enters a phase: the credential-unusable class returns a REPORT rather
        ///   than throwing. Treating that as "started" answered 202 with a progress URL, while the report -
        ///   the only copy the runtime makes - was dropped on the floor and the poll 404'd forever. This is
        ///   asserted on the DEFAULT route, because that is the one every shipped client now uses.
        /// </summary>
        [TestMethod]
        public async Task ARunThatEndsBeforeItsFirstPhase_AnswersItsReportInline_NotA202()
        {
            using var factory = new RuntimeFactory();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync("/integration/job",
                Json("{\"providerId\":\"" + UnifiProviderId + "\",\"integrationInstanceId\":\"blank-key\"," +
                     "\"settings\":{\"baseUrl\":\"http://127.0.0.1:1\"}," +
                     "\"credentialValues\":{\"apiKey\":\"   \"}}"));

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                "a run that ended before it had a phase was accepted as though it were in flight, so its " +
                "report - the only copy this runtime makes - is unreachable: " + await ReadText(response));

            var body = await ReadJson(response);
            Assert.AreEqual("credential", body.GetProperty("errorKind").GetString(),
                "the report came back but not the reason, which is the one thing the caller can act on");
        }

        [TestMethod]
        public async Task AnUnknownIdentity_Is404_SayingWhyItMightBeMissing()
        {
            using var factory = new RuntimeFactory();
            using var client = factory.CreateClient();

            using var polled = await client.GetAsync("/integration/run/never-heard-of-it");

            Assert.AreEqual(HttpStatusCode.NotFound, polled.StatusCode);
            var text = await ReadText(polled);
            StringAssert.Contains(text, "restart",
                "the 404 does not explain that this runtime forgets runs on restart, so a reader assumes a bug: "
                + text);
        }

        [TestMethod]
        public async Task AFileSettingStillAcceptsASingleObjectOnTheWire()
        {
            // The compatibility half of multi-file, asserted where it actually has to hold: over real JSON.
            // A hand-built job never touches the converter, so only a posted body can say the object form
            // still parses. The CSV provider takes one file, so this is also the shape it must keep taking.
            using var factory = new RuntimeFactory();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync("/integration/job?wait=true",
                Json(JobBody(CsvProviderId, "single-shape", null, null,
                    "{\"file\":" + FileJson("devices.csv", "mac,name\nAA:BB:CC:DD:EE:01,Reception\n") + "}")));

            // A 400 would mean the SHAPE was refused, which is the regression this guards. What the run then
            // makes of the file is a different question and has its own tests.
            Assert.AreNotEqual(HttpStatusCode.BadRequest, response.StatusCode,
                "the single-object file shape stopped parsing, which breaks every caller written before " +
                "multi-file existed: " + await ReadText(response));
        }

        [TestMethod]
        public async Task AListOfFilesForASettingThatTakesOne_IsRefusedOverTheWire()
        {
            // The array form has to PARSE (or this test could not tell a rejected shape from an unparsed
            // one) and then be refused by the descriptor. The refusal is what protects a complete-snapshot
            // provider from reading one file of several and reporting the rest as deleted.
            using var factory = new RuntimeFactory();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync("/integration/job?wait=true",
                Json(JobBody(CsvProviderId, "list-shape", null, null,
                    "{\"file\":[" + FileJson("a.csv", "mac,name\n") + "," +
                    FileJson("b.csv", "mac,name\n") + "]}")));

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode,
                "a list sent to a single-file setting has to be refused, not silently read as its first " +
                "entry: " + await ReadText(response));
            var text = await ReadText(response);
            StringAssert.Contains(text, "ONE file",
                "and the refusal says what the setting takes, which is the only thing the caller can act " +
                "on: " + text);
        }

        [TestMethod]
        public async Task AListOfFilesForAMultipleSetting_IsAccepted_AndReachesTheProvider()
        {
            // The ARXML setting is the one shipped setting that takes several. These two files are not
            // AUTOSAR, so the RUN fails at its source - which is the proof that matters: a source failure
            // means the job's shape was accepted and the provider was reached with it, where a 400 would
            // mean the array never got past the front door.
            using var factory = new RuntimeFactory();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync("/integration/job?wait=true",
                Json(JobBody(ArxmlProviderId, "many-shape", null, null,
                    "{\"file\":[" + FileJson("chassis.arxml", "<not-autosar/>") + "," +
                    FileJson("body.arxml", "<not-autosar/>") + "]}")));

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                "the array shape was refused for a setting the descriptor declares multiple: " +
                await ReadText(response));
            var report = await ReadJson(response);
            Assert.AreEqual("source", report.GetProperty("errorKind").GetString(),
                "the run must have reached the provider and failed on the CONTENT, which is what says the " +
                "files arrived: " + await ReadText(response));
        }

        [TestMethod]
        public async Task CancellingAnIdentityWithNoRun_Is404_SayingThereIsNothingToStop()
        {
            using var factory = new RuntimeFactory();
            using var client = factory.CreateClient();

            using var cancelled = await client.PostAsync("/integration/run/never-ran/cancel", null);

            Assert.AreEqual(HttpStatusCode.NotFound, cancelled.StatusCode,
                "cancelling nothing must not answer 202: a client would believe it had stopped a run");
            var text = await ReadText(cancelled);
            StringAssert.Contains(text, "nothing to cancel",
                "and the 404 says why rather than leaving a reader to guess whether the route exists: " + text);
        }

        [TestMethod]
        public async Task CancellingARunThatAlreadyEnded_Is404()
        {
            // A closed port makes this deterministic: the run starts, fails at its source in milliseconds, and
            // the slot that survives it is a FINISHED one - which is exactly the state that must not be
            // cancellable, because nothing about it can still be prevented.
            using var factory = new RuntimeFactory();
            using var client = factory.CreateClient();

            using (var accepted = await client.PostAsync("/integration/job?wait=true",
                Json(JobBody(FroniusProviderId, "already-done", "{\"baseUrl\":\"http://127.0.0.1:1\"}"))))
            {
                Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode,
                    "the waited run has to have ENDED before this test asserts anything: " +
                    await ReadText(accepted));
            }

            using var cancelled = await client.PostAsync("/integration/run/already-done/cancel", null);

            Assert.AreEqual(HttpStatusCode.NotFound, cancelled.StatusCode,
                "a finished run answered as though it were still stoppable: " + await ReadText(cancelled));

            using var polled = await client.GetAsync("/integration/run/already-done");
            Assert.AreEqual(HttpStatusCode.OK, polled.StatusCode,
                "and the refusal must not have disturbed the slot, which is the only account of that run");
            var state = await ReadJson(polled);
            Assert.IsFalse(state.GetProperty("cancelRequested").GetBoolean(),
                "a refused cancel recorded a request against a run it did not touch");
            Assert.IsFalse(state.GetProperty("cancelled").GetBoolean(),
                "and it must not relabel a run that failed at its source as one somebody stopped");
        }

        #endregion

        #region routes and shipped ids

        private const String HealthRoute = "/health";
        private const String RuntimeProvidersRoute = "/integration/providers";
        private const String RuntimeVocabularyRoute = "/integration/vocabulary";
        private const String RuntimeValidateRoute = "/integration/snapshot/validate";
        // WAITED on purpose. Every test using this constant asserts what a run OUTCOME was - the report, its
        // errorKind, which failure a status maps to - and the waited shape is the one that answers with a
        // report. The route's default is now to accept and return a run id, and that behaviour has tests of
        // its own further down; pointing these at ?wait=true keeps each set testing one thing.
        private const String RuntimeJobRoute = "/integration/job?wait=true";

        private const String ProxyProvidersRoute = "/integrations/providers";
        private const String ProxyVocabularyRoute = "/integrations/vocabulary";
        private const String ProxyValidateRoute = "/integrations/snapshot/validate";
        private const String ProxyJobRoute = "/integrations/job";
        private const String ProxyRunsRoute = "/integrations/run";
        private const String ProxyRunRoute = "/integrations/run/garage";
        private const String ProxyCancelRoute = "/integrations/run/garage/cancel";

        private const String CsvProviderId = "csv-device-list";
        private const String UnifiProviderId = "unifi-network";
        private const String FroniusProviderId = "fronius-solar";
        private const String ArxmlProviderId = "autosar-arxml";

        /// <summary>
        ///   Every shipped provider id, in registration order, which is the order the descriptor snapshot
        ///   pins. One list, so a new provider is added once rather than in each assertion that enumerates
        ///   them and silently stops covering the newest one.
        /// </summary>
        private static readonly String[] ShippedProviderIds =
        {
            CsvProviderId, UnifiProviderId, FroniusProviderId, ArxmlProviderId,
        };

        #endregion

        #region the runtime's host

        /// <summary>
        /// The runtime, hosted over its own entry point. Every configured value carries the marker
        /// below so the health test can assert that none of them reaches the probe's body: a probe
        /// disclosing which integrations exist, which hosts they talk to or which graph is written into
        /// would be a disclosure surface on the one container that jobs hand third-party credentials to.
        /// </summary>
        private sealed class RuntimeFactory : WebApplicationFactory<NoSQL.GraphDB.Integrations.Program>
        {
            internal const String Marker = "must-not-be-disclosed";
            /// <summary>A marked, deliberately odd ceiling, so the binding assertion below proves this host
            /// really took the configured value rather than the shipped default.</summary>
            internal const Int64 MaxFileBytes = 12_345_678;
            internal const String AllowedHost = "console." + Marker + ".invalid";
            internal const String SelfSignedHost = "inverter." + Marker + ".invalid";
            internal const String TargetBaseUrl = "http://graph." + Marker + ".invalid:19999/";

            private readonly String _allowedHosts;

            /// <param name="allowedHosts">Overrides the allowed-host list. A test that needs a credentialed
            /// run to reach a fixture on loopback passes the empty string, which the guard documents as no
            /// restriction: loopback is not a name that can be put on a host list.</param>
            public RuntimeFactory(String allowedHosts = AllowedHost)
            {
                _allowedHosts = allowedHosts;
            }

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseSetting("Integrations:Credentials:AllowedHosts", _allowedHosts);
                builder.UseSetting("Integrations:MaxFileBytes", MaxFileBytes.ToString(CultureInfo.InvariantCulture));
                builder.UseSetting("Integrations:SelfSignedHosts", SelfSignedHost);
                builder.UseSetting("Fallen8Target:BaseUrl", TargetBaseUrl);
            }
        }

        #endregion

        #region helpers

        private static StringContent Json(String body)
        {
            return new StringContent(body, Encoding.UTF8, "application/json");
        }

        private static async Task<String> ReadText(HttpResponseMessage response)
        {
            return await response.Content.ReadAsStringAsync();
        }

        private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
        {
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        }

        private static String Text(JsonElement owner, String name)
        {
            return owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        /// <summary>Every diagnostic code in a validate verdict, so a test can name the one it wants.</summary>
        private static List<String> DiagnosticCodes(JsonElement verdict)
        {
            var codes = new List<String>();
            foreach (var diagnostic in verdict.GetProperty("diagnostics").EnumerateArray())
            {
                codes.Add(Text(diagnostic, "code"));
            }

            return codes;
        }

        /// <summary>One snapshot document, complete unless <paramref name="completeness"/> is null.</summary>
        private static String SnapshotBody(String completeness)
        {
            var declaration = completeness == null
                ? String.Empty
                : "\"completeness\":\"" + completeness + "\",";

            return "{\"schemaVersion\":1,\"providerId\":\"" + CsvProviderId + "\"," +
                   "\"integrationInstanceId\":\"office-list\"," + declaration +
                   "\"entities\":[{\"kind\":\"device\"," +
                   "\"claims\":[{\"type\":\"mac\",\"value\":\"44:D2:44:AA:BB:CC\"}]," +
                   "\"properties\":{\"csv.name\":\"Reception AP\"},\"relations\":[]}]}";
        }

        private static String JobBody(String providerId, String instanceId, String settings,
            String credentialValues = null, String files = null)
        {
            return "{\"providerId\":\"" + providerId + "\",\"integrationInstanceId\":\"" + instanceId +
                   "\",\"settings\":" + (settings ?? "{}") +
                   (credentialValues == null ? String.Empty : ",\"credentialValues\":" + credentialValues) +
                   (files == null ? String.Empty : ",\"files\":" + files) +
                   "}";
        }

        /// <summary>One file as the wire spells it, base64 of the text.</summary>
        private static String FileJson(String name, String text)
        {
            return "{\"name\":\"" + name + "\",\"contentBase64\":\"" +
                   Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) + "\"}";
        }

        /// <summary>
        /// A loopback socket that answers every request with one fixed HTTP response, so a status the
        /// runtime has to classify can be produced without a console and without the network. Raw sockets
        /// rather than HttpListener, which needs a URL reservation on Windows.
        /// </summary>
        private sealed class FixedAnswerListener : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly Byte[] _response;

            public FixedAnswerListener(Int32 statusCode, String reason)
            {
                _response = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 " + statusCode.ToString(CultureInfo.InvariantCulture) + " " + reason + "\r\n" +
                    "Content-Length: 0\r\nConnection: close\r\n\r\n");

                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

                _ = Task.Run(AcceptAsync);
            }

            public Int32 Port { get; }

            public void Dispose()
            {
                _listener.Stop();
            }

            private async Task AcceptAsync()
            {
                while (true)
                {
                    TcpClient socket;
                    try
                    {
                        socket = await _listener.AcceptTcpClientAsync();
                    }
                    catch (Exception)
                    {
                        return;
                    }

                    using (socket)
                    {
                        try
                        {
                            var stream = socket.GetStream();

                            // The request is read before answering: closing on an unread request gives the
                            // client a connection reset, which would surface as a transport failure instead
                            // of the status this fixture exists to produce. ONE read is enough, because a
                            // GET's request line and headers arrive in the first segment and this fixture
                            // answers the same thing whatever they say. The count is used only to notice a
                            // client that hung up before sending anything.
                            var buffer = new Byte[4096];
                            var read = await stream.ReadAsync(buffer, 0, buffer.Length);
                            if (read > 0)
                            {
                                await stream.WriteAsync(_response, 0, _response.Length);
                                await stream.FlushAsync();
                            }
                        }
                        catch (Exception)
                        {
                            // A client that hung up mid-exchange is not this fixture's problem.
                        }
                    }
                }
            }
        }

        /// <summary>A port nothing listens on: bound to learn a free one, then released.</summary>
        private static Int32 ClosedLoopbackPort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        #endregion

        #region A. the runtime's own routes

        /// <summary>
        /// The probe the apiApp's cached reachability check calls. It answers, and it says nothing
        /// else: exactly one field, and not one of the values this process was configured with.
        /// </summary>
        [TestMethod]
        public async Task TheHealthProbeAnswers200_AndDisclosesNothingAboutConfiguration()
        {
            using var factory = new RuntimeFactory();
            using var client = factory.CreateClient();

            // The marker check below is only worth anything if this process really was configured with
            // the marked values, so that is established first rather than assumed.
            var configured = factory.Services.GetRequiredService<IOptions<IntegrationsOptions>>().Value;
            Assert.AreEqual(RuntimeFactory.MaxFileBytes, configured.MaxFileBytes,
                "this host did not take the marked file ceiling, so Integrations:* is not binding here at " +
                "all and the disclosure check below would pass over a probe that leaks the real values");

            using var response = await client.GetAsync(HealthRoute);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                "the apiApp's /status block and its cached reachability probe read this route, so a " +
                "runtime that does not answer it reads as unreachable and the Studio screen disappears");

            var body = await ReadText(response);
            var probe = JsonDocument.Parse(body).RootElement;

            var fields = new List<String>();
            foreach (var field in probe.EnumerateObject())
            {
                fields.Add(field.Name);
            }

            CollectionAssert.AreEqual(new List<String> { "status" }, fields,
                "every field beyond 'status' is a disclosure on the one container that jobs hand third-party " +
                "credentials to, and this route is reachable without authentication");
            Assert.AreEqual("ok", Text(probe, "status"),
                "the probe's verdict is what makes the runtime count as reachable at all");

            var lowered = body.ToLowerInvariant();
            Assert.IsFalse(lowered.Contains(RuntimeFactory.Marker),
                "the probe leaked a configured host or target URL, which tells an unauthenticated caller " +
                "which systems this container talks to and which graph it writes into");

            foreach (var providerId in ShippedProviderIds)
            {
                Assert.IsFalse(lowered.Contains(providerId),
                    "the probe named " + providerId + ", so an unauthenticated caller learns which " +
                    "third-party systems this container holds credentials for");
            }
        }

        /// <summary>
        /// Each shipped provider measures something the others do not: one with no credential, no paging
        /// and one entity kind; one with many entity kinds, paging and topology; one with no strong
        /// identifier overlap at all; one whose source is a published standard.
        /// </summary>
        [TestMethod]
        public async Task TheProviderCatalogListsExactlyTheShippedIntegrations()
        {
            using var factory = new RuntimeFactory();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync(RuntimeProvidersRoute);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                "this route is the only way a client learns what it may run, so a failure here leaves " +
                "the Studio screen with an empty provider list and no way to submit a job");

            var ids = new List<String>();
            foreach (var descriptor in (await ReadJson(response)).EnumerateArray())
            {
                ids.Add(Text(descriptor, "id"));
            }

            CollectionAssert.AreEquivalent(ShippedProviderIds.ToList(), ids,
                "a provider id appears inside every provider-scoped claim key, so an id that appeared, " +
                "vanished or was renamed here renames every identity that provider ever asserted");
        }

        /// <summary>
        /// "Adding a provider requires zero Studio code change" is only true if the descriptor carries
        /// everything a form needs.
        /// </summary>
        [TestMethod]
        public async Task EverySettingTheCatalogPublishesCarriesWhatAFormNeedsToRenderIt()
        {
            var kinds = new List<String> { "Text", "Number", "Boolean", "Url", "Credential", "File" };

            using var factory = new RuntimeFactory();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync(RuntimeProvidersRoute);
            var descriptors = await ReadJson(response);

            var settingsSeen = 0;
            foreach (var descriptor in descriptors.EnumerateArray())
            {
                var id = Text(descriptor, "id");
                Assert.IsFalse(String.IsNullOrWhiteSpace(Text(descriptor, "displayName")),
                    id + " publishes no displayName, so a person picking an integration reads a blank row");
                Assert.IsFalse(String.IsNullOrWhiteSpace(Text(descriptor, "description")),
                    id + " publishes no description, so nothing tells a user what it reads or from where");
                Assert.IsTrue(descriptor.GetProperty("entityKinds").GetArrayLength() > 0,
                    id + " declares no entity kind, so nothing says what labels its run writes into the graph");

                foreach (var setting in descriptor.GetProperty("settings").EnumerateArray())
                {
                    settingsSeen++;
                    var key = Text(setting, "key");
                    Assert.IsFalse(String.IsNullOrWhiteSpace(key),
                        id + " publishes a setting with no key, so a job cannot name the value at all");
                    Assert.IsFalse(String.IsNullOrWhiteSpace(Text(setting, "label")),
                        id + "'s setting '" + key + "' publishes no label, so the form renders an unnamed field");
                    CollectionAssert.Contains(kinds, Text(setting, "kind"),
                        id + "'s setting '" + key + "' publishes a kind no form can render, which makes this " +
                        "provider need its own client component - a contract failure, not a UI task");
                    var required = setting.GetProperty("required").ValueKind;
                    Assert.IsTrue(required == JsonValueKind.True || required == JsonValueKind.False,
                        id + "'s setting '" + key + "' publishes no boolean 'required', so a form cannot tell " +
                        "a value a run cannot start without from an optional one");
                    Assert.IsFalse(String.IsNullOrWhiteSpace(Text(setting, "help")),
                        id + "'s setting '" + key + "' publishes no help. Help is where to find the value in " +
                        "the source system, which is the difference between a setting a user fills in and one " +
                        "they give up on");
                }
            }

            Assert.IsTrue(settingsSeen >= 4,
                "the shipped catalog published almost no settings, so this check would pass over an empty " +
                "descriptor surface and prove nothing about what a form can render");
        }

        /// <summary>
        /// A credential setting is published as its own kind, and never carries a default: a default is
        /// the one thing that WOULD travel as an ordinary setting.
        /// </summary>
        [TestMethod]
        public async Task ACredentialSettingIsPublishedAsACredentialKind_AndNeverCarriesADefaultValue()
        {
            using var factory = new RuntimeFactory();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync(RuntimeProvidersRoute);
            var descriptors = await ReadJson(response);

            var credentialSettings = 0;
            foreach (var descriptor in descriptors.EnumerateArray())
            {
                foreach (var setting in descriptor.GetProperty("settings").EnumerateArray())
                {
                    if (!String.Equals("Credential", Text(setting, "kind"), StringComparison.Ordinal))
                    {
                        continue;
                    }

                    credentialSettings++;
                    Assert.IsTrue(String.IsNullOrEmpty(Text(setting, "defaultValue")),
                        Text(descriptor, "id") + "'s credential setting '" + Text(setting, "key") +
                        "' publishes a default value. A default is neither leased nor redacted, so it would " +
                        "be logged and reported like any other value");
                }
            }

            Assert.AreEqual(1, credentialSettings,
                "the shipped catalog publishes exactly one credential setting (unifi-network's apiKey); " +
                "a credential setting that stopped being published as the Credential kind would be " +
                "rendered as an ordinary text field and its value submitted as a setting, which is " +
                "neither leased nor redacted");
        }

        /// <summary>
        /// The vocabulary as data a provider author reads: which types exist, which may resolve, in
        /// which uniqueness domain, and what each accepts.
        /// </summary>
        [TestMethod]
        public async Task TheVocabularyPublishesEveryIdentifierType_WithStrengthScopeCanonicalAndAccept()
        {
            var expected = new List<String>
            {
                "mac", "serial", "imei", "ipv4", "ipv6", "hostname",
                "unifi-site-id", "unifi-device-id", "unifi-client-id",
                "fronius-unique-id", "fronius-logger-id",
                "arxml-path",
            };

            using var factory = new RuntimeFactory();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync(RuntimeVocabularyRoute);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                "an author who cannot read the vocabulary declares a claim type by guessing, and a wrongly " +
                "scoped one composes one key for two installations' different devices");

            var vocabulary = await ReadJson(response);
            Assert.AreEqual(1, vocabulary.GetProperty("schemaVersion").GetInt32(),
                "a document from another contract version would be read with the fields this reader happens " +
                "to recognise");

            var types = new List<String>();
            var scopes = new List<String>();
            foreach (var entry in vocabulary.GetProperty("identifiers").EnumerateArray())
            {
                var type = Text(entry, "type");
                types.Add(type);
                scopes.Add(Text(entry, "scope"));

                CollectionAssert.Contains(new List<String> { "weak", "strong" }, Text(entry, "strength"),
                    type + " publishes no usable strength. Only strong may resolve, so an author who cannot " +
                    "read it relies on an identifier that resolves nothing and duplicates its devices every run");
                CollectionAssert.Contains(new List<String> { "global", "provider", "instance" }, Text(entry, "scope"),
                    type + " publishes no usable scope, and equal keys in the wrong uniqueness domain advertise " +
                    "an overlap that does not exist");
                Assert.IsFalse(String.IsNullOrWhiteSpace(Text(entry, "canonical")),
                    type + " publishes no canonicaliser, so an author cannot tell which values converge on one " +
                    "key and which fork into two elements");
                Assert.IsFalse(String.IsNullOrWhiteSpace(Text(entry, "accept")),
                    type + " publishes no accept pattern, so a value that will be dropped as invalid looks " +
                    "acceptable to whoever writes the provider");
            }

            CollectionAssert.AreEqual(expected, types,
                "the vocabulary is the closed set resolution is decided by: a type that vanished makes every " +
                "claim of it unknown and duplicates its elements on every run, and one that appeared was " +
                "never reviewed for strength or scope");

            foreach (var scope in new[] { "global", "provider", "instance" })
            {
                CollectionAssert.Contains(scopes, scope,
                    "no entry publishes " + scope + " scope, so the route is not projecting the file's own " +
                    "scope column and an author cannot see which keys are only unique inside one installation");
            }
        }

        /// <summary>
        /// The verdict an author wants before wiring a source to a provider.
        /// </summary>
        [TestMethod]
        public async Task AValidSnapshotDocument_IsAcceptedAtTheEnvelope_WithNoDiagnostics()
        {
            using var factory = new RuntimeFactory();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(RuntimeValidateRoute, Json(SnapshotBody("complete")));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                "a document this route cannot judge leaves an author guessing, and a provider written by " +
                "guessing lands duplicates nothing can remove");

            var verdict = await ReadJson(response);
            Assert.IsTrue(verdict.GetProperty("envelopeAccepted").GetBoolean(),
                "a document that satisfies the envelope was refused, which would tell an author to change a " +
                "provider that is already correct");
            Assert.AreEqual(1, verdict.GetProperty("acceptedEntities").GetInt32(),
                "the one identifiable entity was not accepted, so a run over this source would claim nothing " +
                "and a complete snapshot would withdraw whatever it claimed before");
            Assert.AreEqual(0, verdict.GetProperty("skippedEntities").GetInt32(),
                "an entity carrying a strong MAC claim and a prefixed property was skipped, and a skipped " +
                "entity in a complete snapshot is withdrawn and then deleted");
            CollectionAssert.AreEqual(new List<String>(), DiagnosticCodes(verdict),
                "a clean document produced a diagnostic, which trains an author to read the list as noise - " +
                "and the list is also where a claim that will silently never resolve is reported");
        }

        /// <summary>
        /// The route judges a DOCUMENT, so a bad document is a verdict rather than a transport error.
        /// </summary>
        [TestMethod]
        public async Task ADocumentWithNoCompleteness_IsAVerdictRatherThanA400()
        {
            using var factory = new RuntimeFactory();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(RuntimeValidateRoute, Json(SnapshotBody(null)));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                "a document with a broken envelope is what this route exists to describe, so answering a " +
                "transport error instead leaves an author with a status code and no named reason");

            var verdict = await ReadJson(response);
            Assert.IsFalse(verdict.GetProperty("envelopeAccepted").GetBoolean(),
                "a document with no completeness declaration was accepted, and completeness is the one field " +
                "that licenses withdrawal: acting on it would delete what the source still has");
            CollectionAssert.Contains(DiagnosticCodes(verdict), "missingCompleteness",
                "the verdict does not name missingCompleteness, so an author is told the document is wrong " +
                "without being told which field to add");
            Assert.AreEqual(0, verdict.GetProperty("acceptedEntities").GetInt32(),
                "entities were accepted out of a document whose envelope was refused, and applying part of " +
                "such a document is guessing");
        }

        /// <summary>
        /// An absent body is not a document, and there is nothing to judge.
        /// </summary>
        [TestMethod]
        public async Task AnAbsentSnapshotBody_IsA400_BecauseThereIsNoDocumentToJudge()
        {
            using var factory = new RuntimeFactory();
            using var client = factory.CreateClient();

            using (var empty = await client.PostAsync(RuntimeValidateRoute, Json(String.Empty)))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, empty.StatusCode,
                    "an empty body was answered as though it were a document, so a caller whose request body " +
                    "never arrived reads a verdict about nothing as a verdict about their snapshot");
            }

            using (var missing = await client.PostAsync(RuntimeValidateRoute, Json("null")))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, missing.StatusCode,
                    "a JSON null body was answered as though it were a document, so a caller reads a verdict " +
                    "about nothing as a verdict about their snapshot");
                StringAssert.Contains(await ReadText(missing), "snapshot document is required",
                    "the refusal does not say what was missing, and the apiApp passes this body through " +
                    "untouched to whoever is authoring the provider");
            }
        }

        /// <summary>
        /// A job naming a provider that does not exist could not be run at all.
        /// </summary>
        [TestMethod]
        public async Task AJobNamingAnUnknownProvider_IsA400ProblemJson_NamingTheCatalogRoute()
        {
            using var factory = new RuntimeFactory();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(RuntimeJobRoute,
                Json(JobBody("no-such-provider", "garage", null)));

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode,
                "a job that could not be run at all answered like one that ran, so a caller reads a report " +
                "for a run that never happened and believes their source was observed");
            Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType,
                "the runtime's own failure shape is what the apiApp passes through untouched, so a body that " +
                "is not problem+json reaches the operator as an opaque string");

            var problem = await ReadJson(response);
            StringAssert.Contains(Text(problem, "detail"), "no provider with id",
                "the refusal does not name the missing provider, and the runtime's own message is the one " +
                "thing a person configuring an integration has to go on");
            Assert.AreEqual("configuration", Text(problem, "errorKind"),
                "a job that cannot be run as written must name the system to look at, or 'the mount is " +
                "broken', 'the password is wrong' and 'the console will not answer' all read the same");
        }

        /// <summary>
        /// A required credential, end to end: supplied as a value it gets a run past the pre-flight and
        /// out to the source.
        /// </summary>
        [TestMethod]
        public async Task ARequiredCredentialSuppliedAsAValue_RunsTheJob_AndNeverAppearsOnTheReport()
        {
            const String Supplied = "sk-pasted-into-the-form-9c1f";

            using var factory = new RuntimeFactory();
            using var client = factory.CreateClient();

            // The console is the ALLOWED host, so the guard permits the request and the address does not
            // resolve: the run gets as far as the source and fails there. Any other outcome means it never
            // got past its own configuration or its credential, which is what this test is about.
            using var response = await client.PostAsync(RuntimeJobRoute, Json(JobBody(UnifiProviderId,
                "home-unifi",
                "{\"baseUrl\":\"https://" + RuntimeFactory.AllowedHost + "/proxy/network/integration\"}",
                "{\"apiKey\":\"" + Supplied + "\"}")));

            var body = await ReadText(response);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                "a required credential supplied as a value was refused as a job that could not be run at " +
                "all, so the only caller who has the secret in hand and nowhere to put it - a person at a " +
                "form - cannot run an integration: " + body);

            var report = JsonDocument.Parse(body).RootElement;
            Assert.AreEqual("source", Text(report, "errorKind"),
                "the run must have reached the console and failed there. 'configuration' would mean the " +
                "pre-flight did not count a supplied value as satisfying a required credential setting, and " +
                "'credential' would mean the runtime refused the value it was handed");
            Assert.IsFalse(String.IsNullOrEmpty(Text(report, "credentialFingerprint")),
                "a supplied credential is fingerprinted like any other, which is how a caller who pasted a " +
                "stale value sees the report change once they paste the new one");
            Assert.IsFalse(body.Contains(Supplied, StringComparison.Ordinal),
                "the supplied credential reached the report. The report is the ONE thing a run hands back, " +
                "and the source's own failure message is quoted onto it");
        }

        /// <summary>
        /// A source that REFUSES the credential is a credential failure, not a source failure. The whole
        /// point of errorKind is that "the password is wrong" and "the console will not answer" send a
        /// reader to different places, and a 401 reported as 'source' sends them to the network.
        /// </summary>
        [TestMethod]
        public async Task AConsoleThatAnswers401_IsReportedAsACredentialFailure_NotASourceFailure()
        {
            const String Supplied = "sk-pasted-into-the-form-9c1f";

            using var console = new FixedAnswerListener(401, "Unauthorized");

            // No allowed-host list, so a credentialed run may reach the fixture on loopback. Loopback is
            // exempt from the no-plain-http rule and is not a name that can go on a host list.
            using var factory = new RuntimeFactory(allowedHosts: String.Empty);
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(RuntimeJobRoute, Json(JobBody(UnifiProviderId,
                "home-unifi",
                "{\"baseUrl\":\"http://127.0.0.1:" + console.Port.ToString(CultureInfo.InvariantCulture) +
                "/proxy/network/integration\"}",
                "{\"apiKey\":\"" + Supplied + "\"}")));

            var body = await ReadText(response);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                "a run that reached its source and was refused RAN, so it answers 200 with the failure on " +
                "its report: " + body);

            var report = JsonDocument.Parse(body).RootElement;
            Assert.AreEqual("credential", Text(report, "errorKind"),
                "a 401 was reported as '" + Text(report, "errorKind") + "'. Reported as 'source' it sends " +
                "the reader to the network, which is the one place the answer is not: the front door " +
                "answered, promptly and correctly, that it does not accept this key");

            var error = Text(report, "error");
            StringAssert.Contains(error, "X-API-KEY",
                "the failure must name the header the key was sent as, or a reader cannot tell 'my key is " +
                "wrong' from 'this integration never sent it'");
            StringAssert.Contains(error, "proxy/network/integration",
                "the failure must name the published base-URL forms, because a key issued for the other " +
                "front door is the mistake this refusal most often reports");
            Assert.IsFalse(error.Contains(Supplied, StringComparison.Ordinal),
                "the failure quoted the credential. This message is the report and the log line");

            Assert.AreEqual(0, report.GetProperty("claimsWithdrawn").GetInt32(),
                "a refused key must withdraw nothing: 'I could not look' is not 'there is nothing there'");
            Assert.AreEqual(0, report.GetProperty("elementsDeleted").GetInt32(),
                "and it must delete nothing, for the same reason");
        }

        /// <summary>
        /// The 403 arm is its own message, so it is its own test: it must NOT claim the key authenticated
        /// (an authorization layer in front of a console answers 403 without ever reading the header), and
        /// it must still be a credential failure, because the key's permissions are what to go and look at.
        /// </summary>
        [TestMethod]
        public async Task AConsoleThatAnswers403_IsACredentialFailure_AboutTheReadRatherThanTheKey()
        {
            using var console = new FixedAnswerListener(403, "Forbidden");

            using var factory = new RuntimeFactory(allowedHosts: String.Empty);
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(RuntimeJobRoute, Json(JobBody(UnifiProviderId,
                "home-unifi",
                "{\"baseUrl\":\"http://127.0.0.1:" + console.Port.ToString(CultureInfo.InvariantCulture) +
                "/proxy/network/integration\"}",
                "{\"apiKey\":\"sk-whatever\"}")));

            var body = await ReadText(response);
            var report = JsonDocument.Parse(body).RootElement;

            Assert.AreEqual("credential", Text(report, "errorKind"),
                "a 403 sends a reader to the key's permissions, which is the credential and not the " +
                "network: " + body);

            var error = Text(report, "error");
            StringAssert.Contains(error, "403",
                "the failure must name the status, or a reader cannot tell this from a refused key");
            StringAssert.Contains(error, "refusing the READ",
                "the 403 message must be the one about the read rather than the 401 message about the key, " +
                "or the two arms are indistinguishable to whoever has to act on them");
            Assert.IsFalse(error.Contains("authenticated", StringComparison.Ordinal),
                "the message must not claim the key authenticated. Nothing here knows that: a reverse " +
                "proxy, portal or WAF answers 403 without ever looking at the header, and an unfounded " +
                "certainty about the one thing a reader will act on is worse than a list of candidates");
        }

        /// <summary>
        /// The other half of the split: a status that is NOT about the credential stays a source failure,
        /// or the new kind has swallowed the old one.
        /// </summary>
        [TestMethod]
        public async Task AConsoleThatAnswers500_IsStillReportedAsASourceFailure()
        {
            using var console = new FixedAnswerListener(500, "Internal Server Error");

            using var factory = new RuntimeFactory(allowedHosts: String.Empty);
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(RuntimeJobRoute, Json(JobBody(UnifiProviderId,
                "home-unifi",
                "{\"baseUrl\":\"http://127.0.0.1:" + console.Port.ToString(CultureInfo.InvariantCulture) +
                "/proxy/network/integration\"}",
                "{\"apiKey\":\"sk-whatever\"}")));

            var body = await ReadText(response);
            var report = JsonDocument.Parse(body).RootElement;

            Assert.AreEqual("source", Text(report, "errorKind"),
                "a 500 is the console being unwell and has nothing to do with the key. If every failed " +
                "status now reads as 'credential', the split it was added for is gone and everybody is " +
                "sent to check a key that is fine: " + body);
            StringAssert.Contains(Text(report, "error"), "answered 500",
                "'source' is also the runner's catch-all, so this must pin the answer rather than the kind: " +
                "a fixture that never bound, wedged or reset the connection reports the same kind with a " +
                "message saying the console did not answer, and would pass this test green");
        }

        /// <summary>
        /// The instance id's shape is an allow-list, checked before anything runs.
        /// </summary>
        [TestMethod]
        public async Task AJobWhoseIdentityCarriesAColon_IsRefusedBeforeTheRunStarts()
        {
            using var factory = new RuntimeFactory();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(RuntimeJobRoute,
                Json(JobBody(CsvProviderId, "garage:one", "{\"file\":\"devices.csv\"}")));

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode,
                "an identity carrying a colon was admitted. The value is substituted into a claim key whose " +
                "segments are joined with a colon and an at sign, so two identities can compose one identical " +
                "key and one run then resolves into and reconciles away another integration's elements");
            Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType,
                "the runtime's own failure shape is what the proxy hands back, so a non-problem body reaches " +
                "the caller as an opaque string");

            var problem = await ReadJson(response);
            StringAssert.Contains(Text(problem, "detail"), "integrationInstanceId",
                "the refusal does not name the field that is wrong, so the caller cannot tell it from a " +
                "refusal about a setting");
            Assert.AreEqual("configuration", Text(problem, "errorKind"),
                "a rejection kind is how a caller knows nothing was read and nothing written, so there is " +
                "something to fix rather than a source to retry");

            using var polled = await client.GetAsync("/integration/run/garage:one");
            Assert.AreEqual(HttpStatusCode.NotFound, polled.StatusCode,
                "refused BEFORE the run started is the half of this rule the status code cannot show: a " +
                "tracked slot would mean the identity was admitted far enough to be watched, and it would " +
                "supersede the slot of whatever is legitimately running under it: " + await ReadText(polled));
        }

        /// <summary>
        /// One job at a time per identity, refused rather than queued. The source is a socket that
        /// accepts and never answers, so the first run is provably still inside the gate.
        /// </summary>
        [TestMethod]
        public async Task TheSameIdentityRunningTwiceAtOnce_IsRefusedWithAConflict()
        {
            using var factory = new RuntimeFactory();
            using var client = factory.CreateClient();

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var contacted = new TaskCompletionSource<Boolean>(TaskCreationOptions.RunContinuationsAsynchronously);
            var held = new List<TcpClient>();
            var accepting = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient socket;
                    try
                    {
                        socket = await listener.AcceptTcpClientAsync();
                    }
                    catch (Exception)
                    {
                        return;
                    }

                    lock (held)
                    {
                        held.Add(socket);
                    }

                    contacted.TrySetResult(true);
                }
            });

            var body = JobBody(FroniusProviderId, "conflict-fixture",
                "{\"baseUrl\":\"http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "\"}");
            var first = client.PostAsync(RuntimeJobRoute, Json(body));

            try
            {
                var arrived = await Task.WhenAny(contacted.Task, Task.Delay(TimeSpan.FromSeconds(30)));
                Assert.AreSame(contacted.Task, arrived,
                    "the first run never reached the fixture source, so this test cannot say whether a second " +
                    "run under the same identity is refused while the first is in flight");

                using var second = await client.PostAsync(RuntimeJobRoute, Json(body));
                Assert.AreEqual(HttpStatusCode.Conflict, second.StatusCode,
                    "a second run under the same identity was admitted while the first was still reading its " +
                    "source. Both resolve against the graph as it was before either wrote, so both create the " +
                    "elements the other is creating: every device duplicated, with no index ever going missing");

                var problem = await ReadJson(second);
                Assert.AreEqual("conflict", Text(problem, "errorKind"),
                    "the refusal does not say it is a conflict, so a caller cannot tell 'nothing was read or " +
                    "written, wait and retry' from a job it has to fix");
            }
            finally
            {
                listener.Stop();
                lock (held)
                {
                    foreach (var socket in held)
                    {
                        socket.Close();
                    }
                }

                using var completed = await first;
                Assert.AreEqual(HttpStatusCode.OK, completed.StatusCode,
                    "the run that HELD the gate is the one that ran, and a job that ran and failed carries " +
                    "its failure on a report rather than being refused");
                await Task.WhenAny(accepting, Task.Delay(TimeSpan.FromSeconds(5)));
            }
        }

        /// <summary>
        /// Nothing on the runtime is authenticated, and nothing needs to be: the container's port is
        /// not published, so the only way in is through the apiApp.
        /// </summary>
        [TestMethod]
        public async Task NoRouteOnTheRuntimeAsksForAuthentication_BecauseTheApiAppIsTheOnlyWayIn()
        {
            using var factory = new RuntimeFactory();
            using var client = factory.CreateClient();

            var answered = new List<KeyValuePair<String, HttpStatusCode>>();

            using (var health = await client.GetAsync(HealthRoute))
            {
                answered.Add(new KeyValuePair<String, HttpStatusCode>(HealthRoute, health.StatusCode));
                Assert.AreEqual(HttpStatusCode.OK, health.StatusCode, HealthRoute + " did not answer");
            }

            using (var providers = await client.GetAsync(RuntimeProvidersRoute))
            {
                answered.Add(new KeyValuePair<String, HttpStatusCode>(RuntimeProvidersRoute, providers.StatusCode));
                Assert.AreEqual(HttpStatusCode.OK, providers.StatusCode, RuntimeProvidersRoute + " did not answer");
            }

            using (var vocabulary = await client.GetAsync(RuntimeVocabularyRoute))
            {
                answered.Add(new KeyValuePair<String, HttpStatusCode>(RuntimeVocabularyRoute, vocabulary.StatusCode));
                Assert.AreEqual(HttpStatusCode.OK, vocabulary.StatusCode, RuntimeVocabularyRoute + " did not answer");
            }

            using (var validate = await client.PostAsync(RuntimeValidateRoute, Json(SnapshotBody("complete"))))
            {
                answered.Add(new KeyValuePair<String, HttpStatusCode>(RuntimeValidateRoute, validate.StatusCode));
                Assert.AreEqual(HttpStatusCode.OK, validate.StatusCode, RuntimeValidateRoute + " did not answer");
            }

            using (var job = await client.PostAsync(RuntimeJobRoute, Json(JobBody("no-such-provider", "garage", null))))
            {
                answered.Add(new KeyValuePair<String, HttpStatusCode>(RuntimeJobRoute, job.StatusCode));
                Assert.AreEqual(HttpStatusCode.BadRequest, job.StatusCode,
                    RuntimeJobRoute + " answered neither a report nor a refusal");
            }

            foreach (var probe in answered)
            {
                Assert.AreNotEqual(HttpStatusCode.Unauthorized, probe.Value,
                    probe.Key + " asked for a credential. A second auth story on this container is a second " +
                    "thing to get wrong, and the apiApp - already the authenticated front door - has no " +
                    "credential of the runtime's to send");
                Assert.AreNotEqual(HttpStatusCode.Forbidden, probe.Value,
                    probe.Key + " refused an unauthenticated caller, which makes the whole proxy answer 403 " +
                    "and the Studio integrations screen disappear on a correctly configured instance");
            }
        }

        #endregion

        #region B. the apiApp's proxy

        /// <summary>
        /// The apiApp, whose four /integrations routes proxy the runtime.
        /// </summary>
        private sealed class ProxyFactory : VolatileAppFactory
        {
            internal const String ApiKey = "integrations-proxy-test-key";

            private readonly String _enabled;
            private readonly String _endpoint;
            private readonly Boolean _withApiKey;

            public ProxyFactory(String enabled = null, String endpoint = null, Boolean withApiKey = false)
            {
                _enabled = enabled;
                _endpoint = endpoint;
                _withApiKey = withApiKey;
            }

            /// <summary>Everything this apiApp logged, for the one test that asserts what it did NOT log.</summary>
            internal CapturingLoggerProvider Sink { get; } = new CapturingLoggerProvider();

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                base.ConfigureWebHost(builder);

                // Trace, because the sidecar client base logs its failures at DEBUG: at the app's
                // configured Information level a body logged there would never reach this sink and the
                // no-leak check below would pass over exactly the line it exists to catch.
                builder.ConfigureLogging(logging =>
                {
                    logging.SetMinimumLevel(LogLevel.Trace);
                    logging.AddProvider(Sink);
                });
                builder.UseEnvironment("Development");
                if (_enabled != null)
                {
                    builder.UseSetting("Fallen8:Integrations:Enabled", _enabled);
                }

                if (_endpoint != null)
                {
                    builder.UseSetting("Fallen8:Integrations:Endpoint", _endpoint);
                }

                if (_withApiKey)
                {
                    builder.UseSetting("Fallen8:Security:ApiKey", ApiKey);
                }

                // One second, so an unreachable runtime is an answer rather than the caller's patience.
                builder.UseSetting("Fallen8:Integrations:TimeoutSeconds", "1");
            }

            /// <summary>A client that carries this instance's api key, when one is configured.</summary>
            internal HttpClient CreateAuthenticatedClient()
            {
                var client = CreateClient();
                if (_withApiKey)
                {
                    client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
                }

                return client;
            }
        }

        private static async Task<List<HttpResponseMessage>> CallEveryProxyRoute(HttpClient client)
        {
            var answers = new List<HttpResponseMessage>
            {
                await client.GetAsync(ProxyProvidersRoute),
                await client.GetAsync(ProxyVocabularyRoute),
                await client.PostAsync(ProxyValidateRoute, Json(SnapshotBody("complete"))),
                await client.PostAsync(ProxyJobRoute, Json(JobBody(CsvProviderId, "garage", null))),
                await client.GetAsync(ProxyRunsRoute),
                await client.GetAsync(ProxyRunRoute),
                await client.PostAsync(ProxyCancelRoute, null),
            };

            return answers;
        }

        private static readonly String[] ProxyRoutes =
        {
            ProxyProvidersRoute, ProxyVocabularyRoute, ProxyValidateRoute, ProxyJobRoute,
            ProxyRunsRoute, ProxyRunRoute, ProxyCancelRoute,
        };

        #region the instance serves the ceilings a caller has to respect

        /// <summary>
        ///   The shipped defaults, pinned here rather than only in the options type. A changed default has
        ///   to fail a test, because these three numbers are published in the docs, drawn in a screenshot
        ///   and used by Studio to refuse a job before uploading it: changing one quietly makes all three
        ///   of those wrong at once.
        /// </summary>
        [TestMethod]
        public void TheShippedFileCeilings_AreTheOnesEveryClientAndDocumentAssumes()
        {
            var options = new IntegrationsOptions();

            Assert.AreEqual(134_217_728L, options.MaxFileBytes, "Integrations:MaxFileBytes changed");
            Assert.AreEqual(536_870_912L, options.MaxJobFileBytes, "Integrations:MaxJobFileBytes changed");
            Assert.AreEqual(256, options.MaxJobFiles, "Integrations:MaxJobFiles changed");
        }

        /// <summary>
        ///   The runtime reports what it is CONFIGURED with, not a constant. This host sets a deliberately
        ///   odd per-file ceiling, so the assertion below cannot pass on a route that returns the shipped
        ///   default while ignoring the operator's configuration entirely.
        /// </summary>
        [TestMethod]
        public async Task TheRuntimeLimitsRoute_ReportsTheConfiguredNumbersRatherThanConstants()
        {
            using var factory = new RuntimeFactory();
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/integration/limits");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await ReadText(response));

            var body = await ReadJson(response);
            Assert.AreEqual(RuntimeFactory.MaxFileBytes, body.GetProperty("maxFileBytes").GetInt64(),
                "the runtime reported a per-file ceiling that is not the one this host configured, so the " +
                "route is answering from a constant and an operator's setting would never reach a client");
            Assert.AreEqual(536_870_912L, body.GetProperty("maxJobFileBytes").GetInt64(),
                "the job-total ceiling is not the shipped default this host leaves alone");
            Assert.AreEqual(256, body.GetProperty("maxJobFiles").GetInt32(),
                "the file count ceiling is not the shipped default this host leaves alone");
        }

        /// <summary>
        ///   The proxy answers the ceiling that BINDS, which is the smaller of the runtime's own and this
        ///   proxy's transport bound. A caller told four gigabytes and then refused at 768 MiB has been
        ///   given the wrong answer to the only question they asked, and a caller that has to combine two
        ///   ceilings itself is the shape that let Studio carry one BELOW the runtime's.
        ///
        ///   <para>Asserted on the action with a stub client, because this is the one integrations route
        ///   whose answer the proxy computes: what is under test is the arithmetic, not the hop.</para>
        /// </summary>
        [TestMethod]
        public async Task TheProxyLimitsRoute_LowersARuntimeCeilingAboveItsOwnTransportBound()
        {
            // Both byte ceilings far above the proxy's 768 MiB, and the COUNT switched off.
            var controller = new IntegrationsController(new CannedLimitsClient(
                "{\"maxFileBytes\":4294967296,\"maxJobFileBytes\":4294967296,\"maxJobFiles\":0}"))
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };

            var result = await controller.Limits(CancellationToken.None) as OkObjectResult;
            Assert.IsNotNull(result, "the limits route did not answer 200 with a body");
            var limits = (IntegrationLimitsREST)result.Value;

            var expected = ProxyJobTransportLimit - 1_048_576;
            Assert.AreEqual(expected, limits.MaxFileBytes,
                "a runtime per-file ceiling above the proxy's transport bound was passed through, so a " +
                "caller is promised more than this instance can carry");
            Assert.AreEqual(expected, limits.MaxJobFileBytes,
                "a runtime job-total ceiling above the proxy's transport bound was passed through");
            Assert.AreEqual(0, limits.MaxJobFiles,
                "the count is the one number this proxy does not bound, so a switched-off count has to " +
                "survive as zero rather than being replaced by a byte figure");
        }

        /// <summary>
        ///   A ceiling the proxy CAN carry is passed through untouched. Without this the test above would
        ///   also pass on a proxy that ignored the runtime and always answered its own bound, which would
        ///   hide the operator's configuration just as thoroughly.
        /// </summary>
        [TestMethod]
        public async Task TheProxyLimitsRoute_PassesThroughACeilingItCanCarry()
        {
            var controller = new IntegrationsController(new CannedLimitsClient(
                "{\"maxFileBytes\":134217728,\"maxJobFileBytes\":536870912,\"maxJobFiles\":256}"))
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };

            var result = await controller.Limits(CancellationToken.None) as OkObjectResult;
            Assert.IsNotNull(result);
            var limits = (IntegrationLimitsREST)result.Value;

            Assert.AreEqual(134_217_728L, limits.MaxFileBytes,
                "the shipped per-file ceiling was altered, so the proxy is answering its own bound rather " +
                "than the runtime's configuration");
            Assert.AreEqual(536_870_912L, limits.MaxJobFileBytes, "the shipped job-total ceiling was altered");
            Assert.AreEqual(256, limits.MaxJobFiles, "the shipped count ceiling was altered");
        }

        /// <summary>
        ///   A runtime too old to serve this route, or answering something else: the proxy says it could not
        ///   read the limits rather than inventing ceilings a client would then trust and refuse against.
        /// </summary>
        [TestMethod]
        public async Task TheProxyLimitsRoute_RefusesToInventCeilingsItCouldNotRead()
        {
            var controller = new IntegrationsController(new CannedLimitsClient("<html>not this</html>"))
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };

            var result = await controller.Limits(CancellationToken.None) as ObjectResult;
            Assert.IsNotNull(result);
            Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, result.StatusCode,
                "an unreadable limits answer produced something other than 503, so a client may be given " +
                "numbers this instance never established");
        }

        [TestMethod]
        [DataRow("", DisplayName = "an empty body")]
        [DataRow("   ", DisplayName = "whitespace")]
        [DataRow("null", DisplayName = "a literal null")]
        public async Task TheProxyLimitsRoute_TreatsAnEmptyAnswerAsUnreadableRatherThanAsNoCeilings(
            String body)
        {
            // These three deserialize to nothing rather than to a failure, so the tempting shape is to
            // default them to an all-zero record. That would report this proxy's transport bound (the
            // substitution a zero triggers) as though the runtime had agreed to it, and a caller would
            // then stage a job against a ceiling nobody established.
            var controller = new IntegrationsController(new CannedLimitsClient(body))
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };

            var result = await controller.Limits(CancellationToken.None) as ObjectResult;
            Assert.IsNotNull(result);
            Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, result.StatusCode,
                "an empty limits answer was turned into ceilings instead of a refusal");
            Assert.IsNotInstanceOfType(result.Value, typeof(IntegrationLimitsREST),
                "the refusal carried a limits record, so a client could read ceilings off a failure");
        }

        /// <summary>A client that answers the limits route with one canned body and nothing else.</summary>
        private sealed class CannedLimitsClient : IIntegrationsClient
        {
            private readonly String _body;

            public CannedLimitsClient(String body)
            {
                _body = body;
            }

            public Boolean Configured => true;

            public Task<SidecarResponse> ForwardAsync(HttpMethod method, String path, String jsonBody,
                CancellationToken cancellationToken)
            {
                return Task.FromResult(new SidecarResponse(200, _body, "application/json"));
            }

            public Task<SidecarResponse> ForwardStreamAsync(HttpMethod method, String path, Stream body,
                String contentType, Int64? contentLength, CancellationToken cancellationToken)
            {
                throw new NotSupportedException("the limits route does not stream");
            }

            public Task<Boolean> IsReachableAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult(true);
            }
        }

        #endregion

        #region the job route refuses an oversized body itself, instead of blaming the runtime

        /// <summary>The bound on <c>POST /integrations/job</c>, mirrored from the controller's own private
        /// const so a change there fails a test instead of quietly moving what a caller may send.</summary>
        private const Int64 ProxyJobTransportLimit = 805_306_368;

        /// <summary>
        ///   A body whose DECLARED length is over the bound is refused here, with a 413, and the endpoint
        ///   points at a CLOSED port while it happens. That is what makes this a real assertion rather than
        ///   a restatement: nothing could have been forwarded, so a 413 can only have come from the header
        ///   check, and the 503 this used to answer was measured against a runtime that was serving
        ///   providers a second earlier (feature integration-file-transport, findings.md).
        ///
        ///   <para>The length is declared rather than sent. A test that really uploaded 768 MiB would
        ///   measure the machine, not the contract, and the contract is precisely that no upload happens.</para>
        /// </summary>
        [TestMethod]
        public async Task AJobBodyOverTheTransportBound_Answers413AndNeverBlamesTheRuntime()
        {
            using var factory = new ProxyFactory(enabled: "true", endpoint: "http://127.0.0.1:1");
            using var client = factory.CreateClient();

            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            content.Headers.ContentLength = ProxyJobTransportLimit + 1;

            using var answer = await client.PostAsync(ProxyJobRoute, content);
            var body = await ReadText(answer);

            Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, answer.StatusCode,
                "an oversized job body did not answer 413. If this is a 503, the refusal has gone back to " +
                "happening while the body is FORWARDED, which reports the caller's own request as a " +
                "runtime that did not answer and sends them to inspect a healthy sidecar: " + body);
            Assert.IsFalse(body.Contains("Integration runtime unavailable", StringComparison.Ordinal),
                "the 413 body still accuses the runtime, which was never contacted: " + body);
            StringAssert.Contains(body, ProxyJobTransportLimit.ToString(CultureInfo.InvariantCulture),
                "the refusal does not name the bound it broke, so the caller cannot tell how much to cut: " + body);
        }

        /// <summary>
        ///   No Content-Length, no judgement: a chunked body cannot be measured before it is read, and one
        ///   over the bound cannot be refused with a status that reliably reaches the caller at all. So it
        ///   is refused up front instead, which is the contract narrowing this feature took deliberately.
        ///
        ///   <para>Asserted on the ACTION rather than over HTTP because the in-memory test transport
        ///   always supplies a length, so it cannot express a chunked request at all. Testing the branch
        ///   directly also buys a stronger claim than a status: the client is one that fails the test if
        ///   it is called, which proves nothing was forwarded. The wire behaviour is verified live with
        ///   curl and recorded in the feature's findings.md.</para>
        /// </summary>
        [TestMethod]
        public async Task AJobBodyWithNoDeclaredLength_Answers411AndForwardsNothing()
        {
            var never = new NeverForwardsClient();
            var controller = new IntegrationsController(never)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };

            // DefaultHttpContext declares no Content-Length, which is exactly the chunked case.
            var result = await controller.Job(wait: null, CancellationToken.None) as ObjectResult;

            Assert.IsNotNull(result, "the 411 branch did not produce a problem result");
            Assert.AreEqual(StatusCodes.Status411LengthRequired, result.StatusCode,
                "a body with no declared length was not refused with 411, so an oversized one is judged " +
                "mid-upload again and its refusal may never reach the caller");
            StringAssert.Contains(((ProblemDetails)result.Value).Detail, "Content-Length",
                "the 411 does not say what is missing, which is the one thing a caller can act on");
            Assert.IsFalse(never.WasCalled,
                "the runtime was contacted for a request this proxy had already decided to refuse");
        }

        /// <summary>
        ///   The same claim for the oversized case, at the same level: refused without the runtime being
        ///   asked. The HTTP test above proves the status travels; this one proves no forward happened,
        ///   which is the property that makes the refusal cheap enough to be worth having.
        /// </summary>
        [TestMethod]
        public async Task AnOversizedJobBody_IsRefusedWithoutContactingTheRuntime()
        {
            var never = new NeverForwardsClient();
            var context = new DefaultHttpContext();
            context.Request.ContentLength = ProxyJobTransportLimit + 1;
            var controller = new IntegrationsController(never)
            {
                ControllerContext = new ControllerContext { HttpContext = context }
            };

            var result = await controller.Job(wait: null, CancellationToken.None) as ObjectResult;

            Assert.IsNotNull(result, "the 413 branch did not produce a problem result");
            Assert.AreEqual(StatusCodes.Status413PayloadTooLarge, result.StatusCode,
                "an oversized declared length was not refused with 413");
            Assert.IsFalse(never.WasCalled,
                "the runtime was contacted for an oversized body, which is how a caller's own request " +
                "came to be reported as a runtime that did not answer");
        }

        /// <summary>A client that fails the test if the proxy forwards anything to it.</summary>
        private sealed class NeverForwardsClient : IIntegrationsClient
        {
            internal Boolean WasCalled
            {
                get; private set;
            }

            public Boolean Configured => true;

            public Task<SidecarResponse> ForwardAsync(HttpMethod method, String path, String jsonBody,
                CancellationToken cancellationToken)
            {
                WasCalled = true;
                return Task.FromResult(new SidecarResponse(200, "{}", "application/json"));
            }

            public Task<SidecarResponse> ForwardStreamAsync(HttpMethod method, String path, Stream body,
                String contentType, Int64? contentLength, CancellationToken cancellationToken)
            {
                WasCalled = true;
                return Task.FromResult(new SidecarResponse(200, "{}", "application/json"));
            }

            public Task<Boolean> IsReachableAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult(true);
            }
        }

        /// <summary>
        ///   The pre-check must not swallow a legal job. A body inside the bound still reaches the forward,
        ///   which against a closed port is the honest 503 - and that 503 naming a connection is what
        ///   distinguishes "your request was too big" from "the runtime is not there", the two answers this
        ///   route used to give in the same words.
        /// </summary>
        [TestMethod]
        public async Task ALegalJobBody_StillReachesTheForwardAndFailsAsAnAbsentRuntime()
        {
            using var factory = new ProxyFactory(enabled: "true", endpoint: "http://127.0.0.1:1");
            using var client = factory.CreateClient();

            using var answer = await client.PostAsync(ProxyJobRoute,
                Json(JobBody(CsvProviderId, "legal-body", null)));
            var body = await ReadText(answer);

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, answer.StatusCode,
                "a legal job body no longer reaches the runtime, so the size pre-check is refusing " +
                "requests it was never meant to see: " + body);
            StringAssert.Contains(body, "Integration runtime unavailable",
                "an absent runtime stopped saying so, which is the half of this distinction that was " +
                "already correct: " + body);
        }

        #endregion

        /// <summary>
        /// Off is the default, and that 403 IS the opt-out a client gates the feature on. The caller is
        /// authenticated here, which is the api-security-boundary posture: the capability answers 403 to
        /// a caller the instance knows, and an unauthenticated one is challenged first (below).
        /// </summary>
        [TestMethod]
        public async Task WithTheCapabilityOff_EveryProxyRouteAnswers403ToAnAuthenticatedCaller()
        {
            // Nothing sets Fallen8:Integrations:Enabled: OFF is the default, and no endpoint is
            // configured either, so a 403 here also proves no sidecar was contacted before the gate.
            using var factory = new ProxyFactory(withApiKey: true);
            using var client = factory.CreateAuthenticatedClient();

            var answers = await CallEveryProxyRoute(client);
            for (var i = 0; i < answers.Count; i++)
            {
                using var answer = answers[i];
                Assert.AreEqual(HttpStatusCode.Forbidden, answer.StatusCode,
                    ProxyRoutes[i] + " does not answer 403 with the capability off (the default). That 403 " +
                    "is what F8_INTEGRATIONS=false produces and is the single signal that makes the Studio " +
                    "integrations screen absent, so anything else either shows the screen on an instance " +
                    "with no runtime or contacts a sidecar the operator switched off");
            }
        }

        /// <summary>
        /// The same instance with no api key configured, which is what a bare <c>dotnet run</c> is: the
        /// shared capability policy challenges an anonymous caller before it reports the capability, so
        /// "integrations are off" arrives as 401 there and as 403 only once a key exists. Pinned because
        /// a client that reads only 403 as "absent" shows a broken screen on exactly that instance.
        /// </summary>
        [TestMethod]
        public async Task WithTheCapabilityOffAndNoApiKeyConfigured_TheProxyChallengesRatherThanForbidding()
        {
            using var factory = new ProxyFactory();
            using var client = factory.CreateClient();

            var answers = await CallEveryProxyRoute(client);
            for (var i = 0; i < answers.Count; i++)
            {
                using var answer = answers[i];
                Assert.AreEqual(HttpStatusCode.Unauthorized, answer.StatusCode,
                    ProxyRoutes[i] + " no longer answers 401 on an unsecured instance with integrations off. " +
                    "Either the shared capability posture changed - in which case every client keying the " +
                    "integrations screen's absence on a status code has to be re-checked - or this surface " +
                    "now contacts a sidecar an operator switched off");
            }
        }

        /// <summary>
        /// On, but no runtime configured: a bare dotnet run says so rather than timing out.
        /// </summary>
        [TestMethod]
        public async Task WithNoEndpointConfigured_EveryProxyRouteAnswers503()
        {
            using var factory = new ProxyFactory(enabled: "true");
            using var client = factory.CreateClient();

            var answers = await CallEveryProxyRoute(client);
            for (var i = 0; i < answers.Count; i++)
            {
                using var answer = answers[i];
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, answer.StatusCode,
                    ProxyRoutes[i] + " does not answer 503 when no runtime endpoint is configured, so an " +
                    "operator who switched integrations on and forgot the endpoint reads a 500 or waits out " +
                    "a timeout instead of being told which setting is missing");
                StringAssert.Contains(await ReadText(answer), "Endpoint",
                    ProxyRoutes[i] + " does not name the missing setting, which is the whole content of the " +
                    "answer for whoever is configuring the sidecar");
            }
        }

        /// <summary>
        /// A credential supplied as a value travels through this hop, so this hop must be silent about
        /// it. The runtime redacts what IT logs; the apiApp has no lease and no redaction, and its only
        /// protection is that it logs no request body at all.
        /// </summary>
        [TestMethod]
        public async Task TheProxyLogsNothingOfAJobBody_SoASuppliedCredentialSurvivesTheHop()
        {
            const String Supplied = "sk-pasted-into-the-form-9c1f";

            var endpoint = "http://127.0.0.1:" + ClosedLoopbackPort().ToString(CultureInfo.InvariantCulture) + "/";

            using var factory = new ProxyFactory(enabled: "true", endpoint: endpoint);
            using var client = factory.CreateClient();

            // The runtime is unreachable ON PURPOSE: the forwarding failure is the noisiest moment this hop
            // has, and an exception message quoting the request it sent is the ordinary way a body reaches
            // a log.
            using var response = await client.PostAsync(ProxyJobRoute, Json(JobBody(UnifiProviderId,
                "home-unifi",
                "{\"baseUrl\":\"https://console.invalid/proxy/network/integration\"}",
                "{\"apiKey\":\"" + Supplied + "\"}")));

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode,
                "the unreachable runtime must be a 503, or this test is asserting about some other path");

            // Without this the loop below is a no-op dressed as a guarantee: a sink the host never wired
            // captures nothing, and "nothing contains the credential" is true of an empty list.
            Assert.IsTrue(factory.Sink.Lines.Length > 0,
                "this sink captured no line at all, so it is not attached to the apiApp's logging and the " +
                "check below proves nothing about what this process writes");
            Assert.IsTrue(factory.Sink.Lines.Any(line =>
                    line.IndexOf("ntegration", StringComparison.Ordinal) >= 0),
                "the sink captured lines but none about THIS call, so it cannot speak for what the proxy " +
                "logs while forwarding a job: whatever level that happens at is the level this test has " +
                "to be able to see");

            foreach (var line in factory.Sink.Lines)
            {
                Assert.IsFalse(line.Contains(Supplied, StringComparison.Ordinal),
                    "the apiApp logged a job body carrying a credential. This process holds no lease and " +
                    "redacts nothing, so a body it logs is a third-party secret written to the instance's " +
                    "own log by the one hop that was only ever meant to forward it: " + line);
            }

            Assert.IsFalse((await ReadText(response)).Contains(Supplied, StringComparison.Ordinal),
                "the 503 body echoed the request. A proxy failure is reported to whoever called it, which " +
                "is not always whoever supplied the credential");
        }

        /// <summary>
        /// On, with an endpoint nothing listens on: unreachable becomes 503, not a 500 and not a hang.
        /// </summary>
        [TestMethod]
        public async Task WithAnEndpointNothingListensOn_EveryProxyRouteAnswers503RatherThan500()
        {
            var endpoint = "http://127.0.0.1:" + ClosedLoopbackPort().ToString(CultureInfo.InvariantCulture) + "/";

            using var factory = new ProxyFactory(enabled: "true", endpoint: endpoint);
            using var client = factory.CreateClient();

            var answers = await CallEveryProxyRoute(client);
            for (var i = 0; i < answers.Count; i++)
            {
                using var answer = answers[i];
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, answer.StatusCode,
                    ProxyRoutes[i] + " does not answer 503 when the runtime does not answer. A 500 reads as a " +
                    "fault in the graph rather than an absent sidecar, and a proxy that hangs instead holds " +
                    "the caller for its whole timeout with nothing to read");
                Assert.AreEqual("application/problem+json", answer.Content.Headers.ContentType?.MediaType,
                    ProxyRoutes[i] + "'s unreachable answer is not problem+json, so a client cannot read why " +
                    "the integrations surface is unavailable");
                var body = await ReadText(answer);
                StringAssert.Contains(body, "Integration runtime unavailable",
                    ProxyRoutes[i] + " does not say the runtime is the part that is unavailable, which sends " +
                    "the reader to the graph instead of to the sidecar");
                StringAssert.Contains(body, "did not answer",
                    ProxyRoutes[i] + " does not distinguish a runtime that was contacted and did not answer " +
                    "from one that was never configured, which are two different things to go and fix");
            }
        }

        /// <summary>
        /// Every proxy route is Fallen-8-level: one runtime serves the whole instance and a job names
        /// the namespace it writes into.
        /// </summary>
        [TestMethod]
        public async Task TheProxyRoutesAreNotTwinnedUnderNs()
        {
            var endpoint = "http://127.0.0.1:" + ClosedLoopbackPort().ToString(CultureInfo.InvariantCulture) + "/";

            using var factory = new ProxyFactory(enabled: "true", endpoint: endpoint, withApiKey: true);
            using var client = factory.CreateAuthenticatedClient();

            // Every bare route is REACHED in this configuration (503: the configured runtime does not
            // answer), which is what makes the check below a statement about the twin rather than about
            // the capability - and it is also the exact answer a twin would give if one existed.
            var bare = await CallEveryProxyRoute(client);
            for (var i = 0; i < bare.Count; i++)
            {
                using var answer = bare[i];
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, answer.StatusCode,
                    ProxyRoutes[i] + " is not reached in this configuration, so nothing below could tell an " +
                    "absent /ns twin from a route that answers nothing anywhere");
            }

            var twins = new List<HttpResponseMessage>
            {
                await client.GetAsync("/ns/default" + ProxyProvidersRoute),
                await client.GetAsync("/ns/default" + ProxyVocabularyRoute),
                await client.PostAsync("/ns/default" + ProxyValidateRoute, Json(SnapshotBody("complete"))),
                await client.PostAsync("/ns/default" + ProxyJobRoute, Json(JobBody(CsvProviderId, "garage", null))),
                await client.GetAsync("/ns/default" + ProxyRunsRoute),
                await client.GetAsync("/ns/default" + ProxyRunRoute),
                await client.PostAsync("/ns/default" + ProxyCancelRoute, null),
            };

            for (var i = 0; i < twins.Count; i++)
            {
                using var twin = twins[i];
                var route = "/ns/default" + ProxyRoutes[i];

                // Asserted as "the proxy was not reached" rather than as a bare 404, because an unmatched
                // path renders the Studio shell (200 text/html) on a build that has one in wwwroot and
                // 404s on one that does not. Reaching the proxy is unambiguous either way: only it
                // answers 503 with this title.
                Assert.AreNotEqual(HttpStatusCode.ServiceUnavailable, twin.StatusCode,
                    route + " reaches the integrations proxy, so the same instance-wide runtime can be " +
                    "addressed two ways. One runtime serves the whole instance and a job names the namespace " +
                    "it writes into, so a twin is a second way to say the same thing that can disagree with " +
                    "the first - and for the job route the URL and the job's own namespace field would then " +
                    "disagree about which graph a complete-snapshot run withdraws and deletes in");
                Assert.IsFalse((await ReadText(twin)).Contains("Integration runtime unavailable"),
                    route + " answers with the integrations proxy's own body, so the runtime is addressable " +
                    "per namespace as well as instance-wide");
            }
        }

        #endregion
    }
}
