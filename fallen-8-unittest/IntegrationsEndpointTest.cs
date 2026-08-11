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
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.Integrations.Conformance;
using NoSQL.GraphDB.Integrations.Configuration;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    /// Pins the HTTP surface of the integrations feature through both hosted pipelines: the
    /// fallen-8-integrations runtime's own four routes plus its health probe, and the apiApp's
    /// authenticated proxy over them. Both halves run in process, the runtime under
    /// WebApplicationFactory over its deliberately namespaced entry point.
    /// </summary>
    [TestClass]
    public class IntegrationsEndpointTest
    {
        #region routes and shipped ids

        private const String HealthRoute = "/health";
        private const String RuntimeProvidersRoute = "/integration/providers";
        private const String RuntimeVocabularyRoute = "/integration/vocabulary";
        private const String RuntimeValidateRoute = "/integration/snapshot/validate";
        private const String RuntimeJobRoute = "/integration/job";

        private const String ProxyProvidersRoute = "/integrations/providers";
        private const String ProxyVocabularyRoute = "/integrations/vocabulary";
        private const String ProxyValidateRoute = "/integrations/snapshot/validate";
        private const String ProxyJobRoute = "/integrations/job";

        private const String CsvProviderId = "csv-device-list";
        private const String UnifiProviderId = "unifi-network";
        private const String FroniusProviderId = "fronius-solar";

        #endregion

        #region the runtime's host

        /// <summary>
        /// The runtime, hosted over its own entry point. Every configured value carries the marker
        /// below so the health test can assert that none of them reaches the probe's body: a probe
        /// disclosing which integrations exist, where the credentials are mounted or which graph is
        /// written into would be a disclosure surface on the one container that can read mounted
        /// third-party credentials.
        /// </summary>
        private sealed class RuntimeFactory : WebApplicationFactory<NoSQL.GraphDB.Integrations.Program>
        {
            internal const String Marker = "must-not-be-disclosed";
            internal const String CredentialDirectory = "/mnt/f8i-credentials-" + Marker;
            internal const String FilesDirectory = "/mnt/f8i-files-" + Marker;
            internal const String AllowedHost = "console." + Marker + ".invalid";
            internal const String SelfSignedHost = "inverter." + Marker + ".invalid";
            internal const String TargetBaseUrl = "http://graph." + Marker + ".invalid:19999/";

            protected override void ConfigureWebHost(IWebHostBuilder builder)
            {
                builder.UseSetting("Integrations:Credentials:Directory", CredentialDirectory);
                builder.UseSetting("Integrations:Credentials:AllowedHosts", AllowedHost);
                builder.UseSetting("Integrations:FilesDirectory", FilesDirectory);
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
            String credentials = null, String credentialValues = null)
        {
            return "{\"providerId\":\"" + providerId + "\",\"integrationInstanceId\":\"" + instanceId +
                   "\",\"settings\":" + (settings ?? "{}") +
                   (credentials == null ? String.Empty : ",\"credentials\":" + credentials) +
                   (credentialValues == null ? String.Empty : ",\"credentialValues\":" + credentialValues) +
                   "}";
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
            Assert.AreEqual(RuntimeFactory.CredentialDirectory, configured.Credentials.Directory,
                "this host did not take the marked credential directory, so the disclosure check below " +
                "would pass over a probe that leaks the real one");
            Assert.AreEqual(RuntimeFactory.FilesDirectory, configured.FilesDirectory,
                "this host did not take the marked files directory, so the disclosure check below would " +
                "pass over a probe that leaks the real one");

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
                "every field beyond 'status' is a disclosure on the one container that can read mounted " +
                "third-party credentials, and this route is reachable without authentication");
            Assert.AreEqual("ok", Text(probe, "status"),
                "the probe's verdict is what makes the runtime count as reachable at all");

            var lowered = body.ToLowerInvariant();
            Assert.IsFalse(lowered.Contains(RuntimeFactory.Marker),
                "the probe leaked a configured path, host or target URL, which tells an unauthenticated " +
                "caller where this container's credentials are mounted and which graph it writes into");

            foreach (var providerId in new[] { CsvProviderId, UnifiProviderId, FroniusProviderId })
            {
                Assert.IsFalse(lowered.Contains(providerId),
                    "the probe named " + providerId + ", so an unauthenticated caller learns which " +
                    "third-party systems this container holds credentials for");
            }
        }

        /// <summary>
        /// Three providers ship, because three is the smallest number that measures the contract.
        /// </summary>
        [TestMethod]
        public async Task TheProviderCatalogListsExactlyTheThreeShippedIntegrations()
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

            CollectionAssert.AreEquivalent(new List<String> { CsvProviderId, UnifiProviderId, FroniusProviderId },
                ids,
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
            var kinds = new List<String> { "Text", "Number", "Boolean", "Url", "Credential" };

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
        public async Task TheVocabularyPublishesTheElevenIdentifierTypes_WithStrengthScopeCanonicalAndAccept()
        {
            var expected = new List<String>
            {
                "mac", "serial", "imei", "ipv4", "ipv6", "hostname",
                "unifi-site-id", "unifi-device-id", "unifi-client-id",
                "fronius-unique-id", "fronius-logger-id",
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
        /// Two sources for one credential setting is a job nobody can read, so it is refused rather than
        /// resolved by precedence.
        /// </summary>
        [TestMethod]
        public async Task AJobSupplyingBothACredentialNameAndACredentialValue_IsRefused()
        {
            const String Supplied = "sk-pasted-into-the-form-9c1f";

            using var factory = new RuntimeFactory();
            using var client = factory.CreateClient();

            using var response = await client.PostAsync(RuntimeJobRoute, Json(JobBody(UnifiProviderId,
                "home-unifi",
                "{\"baseUrl\":\"https://" + RuntimeFactory.AllowedHost + "/proxy/network/integration\"}",
                "{\"apiKey\":\"console-key\"}",
                "{\"apiKey\":\"" + Supplied + "\"}")));

            var body = await ReadText(response);

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode,
                "a job naming a credential AND carrying one for the same setting was admitted, so the run " +
                "authenticated with whichever source the runtime happened to read second and no report says " +
                "which. A caller who filled in a form and left a stale name behind cannot tell a working " +
                "credential from a working one they meant to replace");

            var problem = JsonDocument.Parse(body).RootElement;
            StringAssert.Contains(Text(problem, "detail"), "apiKey",
                "the refusal must name the setting with two sources, because a provider with several " +
                "credential settings otherwise leaves the caller to find it");
            Assert.AreEqual("configuration", Text(problem, "errorKind"),
                "nothing was read and nothing written, so this is the caller's job to fix rather than a " +
                "credential to rotate or a source to retry");
            Assert.IsFalse(body.Contains(Supplied, StringComparison.Ordinal),
                "the refusal quoted the supplied credential. A value rejected BEFORE the lease exists is a " +
                "value redaction knows nothing about, and this body is reported and logged");
        }

        /// <summary>
        /// The second credential source, end to end: a required credential supplied as a VALUE gets a run
        /// past the pre-flight and out to the source, and appears nowhere on the report.
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
                null,
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
                "'credential' would mean the resolver went looking in the mount for one it was handed");
            Assert.IsFalse(String.IsNullOrEmpty(Text(report, "credentialFingerprint")),
                "a supplied credential is fingerprinted like any other, which is how a caller who pasted a " +
                "stale value sees the report change once they paste the new one");
            Assert.IsFalse(body.Contains(Supplied, StringComparison.Ordinal),
                "the supplied credential reached the report. The report is the ONE thing a run hands back, " +
                "and the source's own failure message is quoted onto it");
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
        /// The apiApp, whose four /integrations routes proxy the runtime. Volatile durability so
        /// booting the host writes no checkpoint into the test bin.
        /// </summary>
        private sealed class ProxyFactory : WebApplicationFactory<NoSQL.GraphDB.App.Program>
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
                builder.ConfigureLogging(logging => logging.AddProvider(Sink));
                builder.UseEnvironment("Development");
                builder.UseSetting("Fallen8:Durability:Volatile", "true");
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
            };

            return answers;
        }

        private static readonly String[] ProxyRoutes =
        {
            ProxyProvidersRoute, ProxyVocabularyRoute, ProxyValidateRoute, ProxyJobRoute,
        };

        /// <summary>
        /// Off is the default, and that 403 IS the opt-out a client gates the feature on. The caller is
        /// authenticated here, which is the api-security-boundary posture: the capability answers 403 to
        /// a caller the instance knows, and an unauthenticated one is challenged first (below).
        /// </summary>
        [TestMethod]
        public async Task WithTheCapabilityOff_AllFourProxyRoutesAnswer403ToAnAuthenticatedCaller()
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
        public async Task WithNoEndpointConfigured_AllFourProxyRoutesAnswer503()
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
                null,
                "{\"apiKey\":\"" + Supplied + "\"}")));

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode,
                "the unreachable runtime must be a 503, or this test is asserting about some other path");

            // Without this the loop below is a no-op dressed as a guarantee: a sink the host never wired
            // captures nothing, and "nothing contains the credential" is true of an empty list.
            Assert.IsTrue(factory.Sink.Lines.Length > 0,
                "this sink captured no line at all, so it is not attached to the apiApp's logging and the " +
                "check below proves nothing about what this process writes");

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
        public async Task WithAnEndpointNothingListensOn_AllFourProxyRoutesAnswer503RatherThan500()
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
        /// The four routes are Fallen-8-level: one runtime serves the whole instance and a job names
        /// the namespace it writes into.
        /// </summary>
        [TestMethod]
        public async Task TheFourProxyRoutesAreNotTwinnedUnderNs()
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
