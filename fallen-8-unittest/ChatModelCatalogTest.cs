// MIT License
//
// ChatModelCatalogTest.cs
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
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NoSQL.GraphDB.App.Chat;
using NoSQL.GraphDB.App.Configuration;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The chat model catalog (feature chat-model-catalog): the Ollama-protocol tags + show
    ///   fan-out, the two remote protocols, and the four guarantees the read makes - one shared
    ///   budget, a fan-out that is concurrent but capped, per-entry degradation instead of a dropped
    ///   name, and the caller's cancellation as the only failure that is not swallowed. The endpoint
    ///   half (gate, fault statuses and the 200 shape) is at the bottom, over a real loopback
    ///   backend.
    /// </summary>
    [TestClass]
    public class ChatModelCatalogTest
    {
        #region fixtures

        private const String NahilHost = "https://api.nahil.invalid";

        private const String NahilKey = "nahil-test-credential";

        private const String SidecarHost = "http://localhost:11434";

        private const String Model = "phi4-f8-mini:latest";

        private static Fallen8ChatOptions NahilOptions()
        {
            return new Fallen8ChatOptions
            {
                Enabled = true,
                Backend = "Nahil",
                Nahil = new Fallen8ChatOptions.NahilOptions
                {
                    Endpoint = NahilHost,
                    ApiKey = NahilKey,
                    Model = Model
                }
            };
        }

        private static Fallen8ChatOptions SidecarOptions()
        {
            return new Fallen8ChatOptions
            {
                Enabled = true,
                Backend = "Ollama",
                Ollama = new Fallen8ChatOptions.OllamaOptions
                {
                    Endpoint = SidecarHost,
                    Model = Model
                }
            };
        }

        private static Fallen8ChatOptions OpenAIOptions()
        {
            return new Fallen8ChatOptions
            {
                Enabled = true,
                Backend = "OpenAI",
                OpenAI = new Fallen8ChatOptions.OpenAIOptions
                {
                    Endpoint = RemoteModelWire.OpenAIHost,
                    ApiKey = RemoteModelWire.OpenAIKey,
                    Model = RemoteModelWire.OpenAIModel
                }
            };
        }

        private static Fallen8ChatOptions AnthropicOptions()
        {
            return new Fallen8ChatOptions
            {
                Enabled = true,
                Backend = "Anthropic",
                Anthropic = new Fallen8ChatOptions.AnthropicOptions
                {
                    Endpoint = RemoteModelWire.AnthropicHost,
                    ApiKey = RemoteModelWire.AnthropicKey,
                    Model = RemoteModelWire.AnthropicModel
                }
            };
        }

        /// <summary>
        ///   A tags body shaped like the live one, extra fields included: <c>digest</c> and the
        ///   per-entry <c>nahil_class</c> are deliberately NOT read here (the class is a show value,
        ///   so a model whose show fails reports none), and this body is what would catch it if they
        ///   ever were.
        /// </summary>
        private static String TagsJson(params String[] names)
        {
            var entries = names.Select(name =>
                "{\"name\":\"" + name + "\",\"model\":\"" + name + "\",\"digest\":\"" + new String('a', 64)
                + "\",\"nahil_class\":\"ZZ\",\"details\":{\"family\":\"\"}}");
            return "{\"models\":[" + String.Join(",", entries) + "]}";
        }

        /// <summary>One show body; every field is optional on the wire, which is the point.</summary>
        private static String ShowJson(String capability = null, Boolean? routable = null, String modelClass = null,
            String extraCapability = null)
        {
            var parts = new List<String>();
            if (capability != null)
            {
                parts.Add("\"capabilities\":[\"" + capability + "\""
                    + (extraCapability == null ? String.Empty : ",\"" + extraCapability + "\"") + "]");
            }

            if (routable.HasValue)
            {
                parts.Add("\"nahil_routable_now\":" + (routable.Value ? "true" : "false"));
            }

            if (modelClass != null)
            {
                parts.Add("\"nahil_class\":\"" + modelClass + "\"");
            }

            parts.Add("\"details\":{\"family\":\"\",\"parameter_size\":\"\"}");
            return "{" + String.Join(",", parts) + "}";
        }

        /// <summary>The model name out of a show request body, so a stub can answer per model.</summary>
        private static String ModelOf(String body)
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.GetProperty("model").GetString();
        }

        /// <summary>A stub that answers the tags call from a fixed body and each show call from
        /// <paramref name="show" />, keyed by the model the request asked about.</summary>
        private static CatalogHandler OllamaStub(String tagsJson, Func<String, HttpResponseMessage> show)
        {
            return new CatalogHandler((path, body) => path.EndsWith("/api/tags", StringComparison.Ordinal)
                ? RemoteModelWire.Json(tagsJson)
                : show(ModelOf(body)));
        }

        /// <summary>
        ///   Records every outbound call and answers per REQUEST rather than per call NUMBER, which is
        ///   what the shared <see cref="RecordingHandler" /> cannot do: the show fan-out is concurrent,
        ///   so the only way to answer "this model's show" is to read the body. Its lists are guarded
        ///   for the same reason.
        /// </summary>
        private sealed class CatalogHandler : HttpMessageHandler
        {
            private readonly Func<String, String, CancellationToken, Task<HttpResponseMessage>> _respond;
            private readonly Object _lock = new Object();

            internal CatalogHandler(Func<String, String, HttpResponseMessage> respond)
                : this((path, body, _) => Task.FromResult(respond(path, body)))
            {
            }

            internal CatalogHandler(Func<String, String, CancellationToken, Task<HttpResponseMessage>> respond)
            {
                _respond = respond;
            }

            internal List<String> Paths { get; } = new List<String>();

            internal List<String> Bodies { get; } = new List<String>();

            /// <summary>One entry per call; <c>null</c> when the request carried no
            /// <c>Authorization</c> header, which is the sidecar claim under test.</summary>
            internal List<String> Authorizations { get; } = new List<String>();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var body = request.Content == null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                var path = request.RequestUri?.AbsolutePath;

                lock (_lock)
                {
                    Paths.Add(path);
                    Bodies.Add(body);
                    Authorizations.Add(request.Headers.Authorization?.ToString());
                }

                return await _respond(path, body, cancellationToken);
            }
        }

        /// <summary>
        ///   How many show calls are inside the stub at once, and the highest that count ever
        ///   reached. The PEAK is the whole assertion of the fan-out test, so it is tracked with a
        ///   compare-exchange loop rather than "if greater then assign": the calls arrive on many
        ///   threads at once and that is exactly when the plain version loses a peak.
        /// </summary>
        private sealed class ShowMeter
        {
            private Int32 _inFlight;

            private Int32 _peak;

            internal Int32 Peak => Volatile.Read(ref _peak);

            /// <summary>Records one call entering, and answers how many are now inside.</summary>
            internal Int32 Enter()
            {
                var now = Interlocked.Increment(ref _inFlight);
                var seen = Volatile.Read(ref _peak);
                while (now > seen)
                {
                    var previous = Interlocked.CompareExchange(ref _peak, now, seen);
                    if (previous == seen)
                    {
                        break;
                    }

                    seen = previous;
                }

                return now;
            }

            internal void Exit()
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        private static HttpResponseMessage Failed(HttpStatusCode status = HttpStatusCode.InternalServerError)
        {
            return new HttpResponseMessage(status)
            {
                Content = new StringContent("{\"error\":\"no\"}", Encoding.UTF8, "application/json")
            };
        }

        #endregion

        #region the Ollama-protocol arm

        /// <summary>
        ///   The documented response shape out of one tags call plus the show fan-out, and the
        ///   ordinal sort that makes the list a stable contract. The names are chosen so a
        ///   culture-aware sort would order them DIFFERENTLY (uppercase before lowercase, and
        ///   <c>-</c> before <c>:</c>), because that is the mistake a later "tidy up" would make.
        /// </summary>
        [TestMethod]
        public async Task TagsAndShow_ProduceTheDocumentedShape_SortedOrdinally()
        {
            var stub = OllamaStub(
                TagsJson("phi4-f8:latest", "Zeta:latest", "bge-m3:latest", "phi4-f8-mini:latest"),
                model => model switch
                {
                    "bge-m3:latest" => RemoteModelWire.Json(ShowJson("embedding", true, "C2")),
                    "phi4-f8:latest" => RemoteModelWire.Json(ShowJson("completion", true, "S2")),
                    "phi4-f8-mini:latest" => RemoteModelWire.Json(ShowJson("completion", true, "S1")),
                    _ => RemoteModelWire.Json(ShowJson("completion", false, "S1"))
                });

            var models = await ChatModelCatalog.ReadAsync(NahilOptions(), CancellationToken.None, stub);

            Assert.IsNotNull(models);
            CollectionAssert.AreEqual(
                new[] { "Zeta:latest", "bge-m3:latest", "phi4-f8-mini:latest", "phi4-f8:latest" },
                models.Select(m => m.Name).ToArray(),
                "ordinal by name: 'Z' before 'b', and 'phi4-f8-mini' before 'phi4-f8:' because '-' < ':'");

            var embedder = models.Single(m => m.Name == "bge-m3:latest");
            Assert.AreEqual("embedding", embedder.Capability);
            Assert.AreEqual(true, embedder.Available, "nahil_routable_now is what available means on Nahil");
            Assert.AreEqual("C2", embedder.ModelClass, "the class is a verbatim passthrough");

            var cold = models.Single(m => m.Name == "Zeta:latest");
            Assert.AreEqual("completion", cold.Capability);
            Assert.AreEqual(false, cold.Available, "a model no worker can serve right now says so");

            Assert.AreEqual(5, stub.Paths.Count, "one tags call plus one show per listed model");
            Assert.AreEqual(1, stub.Paths.Count(p => p.EndsWith("/api/tags", StringComparison.Ordinal)));
            Assert.AreEqual(4, stub.Paths.Count(p => p.EndsWith("/api/show", StringComparison.Ordinal)));
        }

        /// <summary>
        ///   A capability list carrying more than the model KIND (current Ollama reports
        ///   <c>tools</c>, <c>vision</c> and <c>insert</c> beside it) still maps to one of the two
        ///   values the contract publishes.
        /// </summary>
        [TestMethod]
        public async Task ACapabilityListWithExtras_StillMapsToTheModelKind()
        {
            var stub = OllamaStub(TagsJson(Model),
                _ => RemoteModelWire.Json(ShowJson("completion", extraCapability: "tools")));

            var models = await ChatModelCatalog.ReadAsync(SidecarOptions(), CancellationToken.None, stub);

            Assert.AreEqual("completion", models.Single().Capability);
        }

        /// <summary>
        ///   ONE model's show call failing degrades THAT entry and nothing else - and above all does
        ///   not drop it. A dropped name is the worst outcome available here: the picker would
        ///   silently omit a model the operator has, and nothing would say why.
        /// </summary>
        [TestMethod]
        public async Task AShowFailure_DegradesOneEntry_AndKeepsItInTheList()
        {
            var stub = OllamaStub(TagsJson("a:latest", "b:latest", "c:latest"),
                model => model == "b:latest"
                    ? Failed()
                    : RemoteModelWire.Json(ShowJson("completion", true, "S1")));

            var models = await ChatModelCatalog.ReadAsync(NahilOptions(), CancellationToken.None, stub);

            Assert.IsNotNull(models);
            CollectionAssert.AreEqual(new[] { "a:latest", "b:latest", "c:latest" },
                models.Select(m => m.Name).ToArray(), "the failed entry keeps its place in the list");

            var degraded = models.Single(m => m.Name == "b:latest");
            Assert.IsNull(degraded.Capability);
            Assert.IsNull(degraded.Available);
            Assert.IsNull(degraded.ModelClass);

            foreach (var healthy in models.Where(m => m.Name != "b:latest"))
            {
                Assert.AreEqual("completion", healthy.Capability, "one failure does not degrade its neighbours");
                Assert.AreEqual(true, healthy.Available);
                Assert.AreEqual("S1", healthy.ModelClass);
            }
        }

        /// <summary>
        ///   An older Ollama sidecar answers show WITHOUT a capabilities field, and a sidecar model
        ///   can also fail its show entirely. Either way the name survives with capability unknown,
        ///   and availability stays true: on a sidecar it is TAGS that establishes the model is on
        ///   disk, so a failed capability probe is not a reason to call it unavailable.
        /// </summary>
        [TestMethod]
        public async Task AnOlderSidecar_ReportsNoCapability_ButStaysAvailable()
        {
            var stub = OllamaStub(TagsJson("old:latest", "gone:latest"),
                model => model == "gone:latest"
                    ? Failed(HttpStatusCode.NotFound)
                    : RemoteModelWire.Json(ShowJson()));

            var models = await ChatModelCatalog.ReadAsync(SidecarOptions(), CancellationToken.None, stub);

            Assert.IsNotNull(models);
            Assert.AreEqual(2, models.Count);
            foreach (var model in models)
            {
                Assert.IsNull(model.Capability, "an unknown capability is reported as unknown, not guessed");
                Assert.IsNull(model.ModelClass, "the class is a Nahil field; a sidecar has none");
                Assert.AreEqual(true, model.Available, "a sidecar's tags entry is on disk");
            }
        }

        /// <summary>
        ///   Nahil authenticates EVERY route, so the bearer has to be on the show calls too - a
        ///   credential on tags alone would produce a catalogue of names with every capability
        ///   "unknown" and a 401 nobody sees.
        /// </summary>
        [TestMethod]
        public async Task EveryNahilCall_CarriesTheBearer_IncludingEachShow()
        {
            var stub = OllamaStub(TagsJson("a:latest", "b:latest"),
                _ => RemoteModelWire.Json(ShowJson("completion", true, "S1")));

            await ChatModelCatalog.ReadAsync(NahilOptions(), CancellationToken.None, stub);

            Assert.AreEqual(3, stub.Authorizations.Count);
            foreach (var authorization in stub.Authorizations)
            {
                Assert.AreEqual("Bearer " + NahilKey, authorization);
            }
        }

        /// <summary>The local sidecar is unauthenticated, and a credential must never be offered to
        /// it: on any call, including the fan-out.</summary>
        [TestMethod]
        public async Task NoSidecarCall_CarriesAnAuthorizationHeader()
        {
            var stub = OllamaStub(TagsJson("a:latest", "b:latest"),
                _ => RemoteModelWire.Json(ShowJson("completion")));

            await ChatModelCatalog.ReadAsync(SidecarOptions(), CancellationToken.None, stub);

            Assert.AreEqual(3, stub.Authorizations.Count);
            foreach (var authorization in stub.Authorizations)
            {
                Assert.IsNull(authorization, "a sidecar gets no Authorization header, ever");
            }
        }

        /// <summary>
        ///   A backend that never answers the tags call costs the shared budget and then reports no
        ///   catalog. The transport carries no deadline of its own, so this bound is the linked
        ///   budget's alone: if it were ever dropped, this test would hang instead of failing.
        /// </summary>
        [TestMethod]
        public async Task AHungBackend_AnswersWithinTheSharedBudget()
        {
            var stub = new CatalogHandler(async (_, _, cancellationToken) =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return null;
            });

            var clock = Stopwatch.StartNew();
            var models = await ChatModelCatalog.ReadAsync(NahilOptions(), CancellationToken.None, stub);
            clock.Stop();

            Assert.IsNull(models, "no answer within the budget is no catalog, which the route reports as 503");
            Assert.IsTrue(clock.Elapsed >= TimeSpan.FromSeconds(4),
                "the read is bounded by the budget rather than failing early for another reason");
            Assert.IsTrue(clock.Elapsed < ChatModelCatalog.Budget + TimeSpan.FromSeconds(15),
                "the budget bounds the whole read; it took " + clock.Elapsed);
        }

        /// <summary>
        ///   The budget covers the fan-out too, and spending it there degrades rather than failing:
        ///   the names arrived, so the names are returned. A picker with names and no labels is
        ///   useful; a 503 is not.
        /// </summary>
        [TestMethod]
        public async Task HungShowCalls_StillYieldTheNames_WithinTheSharedBudget()
        {
            var stub = new CatalogHandler(async (path, _, cancellationToken) =>
            {
                if (path.EndsWith("/api/tags", StringComparison.Ordinal))
                {
                    return RemoteModelWire.Json(TagsJson("a:latest", "b:latest"));
                }

                await Task.Delay(Timeout.Infinite, cancellationToken);
                return null;
            });

            var clock = Stopwatch.StartNew();
            var models = await ChatModelCatalog.ReadAsync(NahilOptions(), CancellationToken.None, stub);
            clock.Stop();

            Assert.IsNotNull(models);
            CollectionAssert.AreEqual(new[] { "a:latest", "b:latest" }, models.Select(m => m.Name).ToArray());
            Assert.IsTrue(models.All(m => m.Capability == null && m.Available == null && m.ModelClass == null),
                "a show that never answered leaves the metadata unknown");
            Assert.IsTrue(clock.Elapsed < ChatModelCatalog.Budget + TimeSpan.FromSeconds(15),
                "the fan-out shares the one budget; it took " + clock.Elapsed);
        }

        /// <summary>
        ///   The same degradation for a show that never even STARTED: with more names than the cap and
        ///   every show hung, the calls queued behind the gate are still waiting when the budget goes,
        ///   so their wait is what gets cancelled rather than their request. That is a different code
        ///   path from the test above (which cancels an in-flight read, and which cannot reach this one
        ///   because it lists fewer names than <see cref="ChatModelCatalog.MaxConcurrentShows" />, so
        ///   nothing ever queues). It is the ONLY thing standing between the documented degraded 200
        ///   and a 503: let the cancelled wait escape instead of degrading, and the whole fan-out
        ///   faults, the read reports a wholesale failure, and a picker that should show every name
        ///   with no labels shows nothing at all with an "unavailable" caption.
        /// </summary>
        [TestMethod]
        public async Task ShowCallsStillQueuedBehindTheCapWhenTheBudgetGoes_DegradeRatherThanFailTheRead()
        {
            var names = Enumerable.Range(0, ChatModelCatalog.MaxConcurrentShows + 4)
                .Select(i => "m" + i.ToString(CultureInfo.InvariantCulture) + ":latest").ToArray();

            var stub = new CatalogHandler(async (path, _, cancellationToken) =>
            {
                if (path.EndsWith("/api/tags", StringComparison.Ordinal))
                {
                    return RemoteModelWire.Json(TagsJson(names));
                }

                // Never released, so the cap stays saturated and the surplus never gets a slot: the
                // budget is what ends their wait.
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return null;
            });

            var clock = Stopwatch.StartNew();
            var models = await ChatModelCatalog.ReadAsync(NahilOptions(), CancellationToken.None, stub);
            clock.Stop();

            Assert.IsNotNull(models,
                "a wait cancelled behind the cap is a degradation, not a wholesale failure");
            // Ordinal, because that is the contract: sorted by name, which for these generated names
            // is NOT generation order ("m10:latest" sorts before "m1:latest", '0' being below ':').
            CollectionAssert.AreEqual(names.OrderBy(n => n, StringComparer.Ordinal).ToArray(),
                models.Select(m => m.Name).ToArray(),
                "every listed name survives, including the ones whose show never got a slot");
            Assert.IsTrue(models.All(m => m.Capability == null && m.Available == null && m.ModelClass == null),
                "a show that never ran leaves the metadata unknown");
            Assert.IsTrue(clock.Elapsed < ChatModelCatalog.Budget + TimeSpan.FromSeconds(15),
                "the queued waits end with the shared budget; it took " + clock.Elapsed);
        }

        /// <summary>
        ///   The fan-out is CONCURRENT and no wider than the cap, which is one test because the two
        ///   claims are one loop. The stub holds every show inside itself until the cap is saturated,
        ///   so the peak it measures is what the implementation ALLOWS rather than what the machine
        ///   happened to overlap: a SEQUENTIAL fan-out never reaches the release condition and walks
        ///   the shared budget one call at a time (peak 1), an UNCAPPED one puts every listed model
        ///   in flight at once (peak = the whole catalogue). Both are real regressions this is the
        ///   only test that can see - the hung-show test above passes either way, because serialized
        ///   hung calls still end at the budget.
        /// </summary>
        [TestMethod]
        public async Task TheShowFanOut_IsConcurrent_AndNoWiderThanTheCap()
        {
            var cap = ChatModelCatalog.MaxConcurrentShows;
            var names = Enumerable.Range(0, cap + 4)
                .Select(i => "m" + i.ToString(CultureInfo.InvariantCulture) + ":latest").ToArray();
            var meter = new ShowMeter();
            var saturated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var stub = new CatalogHandler(async (path, _, cancellationToken) =>
            {
                if (path.EndsWith("/api/tags", StringComparison.Ordinal))
                {
                    return RemoteModelWire.Json(TagsJson(names));
                }

                if (meter.Enter() >= cap)
                {
                    // A settle window before the release, so an UNCAPPED fan-out has time to put the
                    // whole catalogue in flight and be SEEN doing it. Releasing the instant the cap
                    // is reached lets the first calls leave while the rest are still arriving, and
                    // the peak then reads as capped even when nothing capped it (measured: that
                    // version passed with the cap removed).
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                    saturated.TrySetResult();
                }

                try
                {
                    // The delay is the escape hatch, not the mechanism: a serialized fan-out would
                    // otherwise hang here forever waiting for a saturation that cannot happen, and
                    // instead it spends the shared budget and reports a peak of 1.
                    await Task.WhenAny(saturated.Task,
                        Task.Delay(TimeSpan.FromSeconds(3), cancellationToken));
                }
                finally
                {
                    meter.Exit();
                }

                return RemoteModelWire.Json(ShowJson("completion", true, "S1"));
            });

            var models = await ChatModelCatalog.ReadAsync(NahilOptions(), CancellationToken.None, stub);

            Assert.IsNotNull(models);
            Assert.AreEqual(names.Length, models.Count, "every listed name is returned");
            Assert.IsTrue(meter.Peak > 1,
                "the shows run concurrently rather than one after another; the observed peak was "
                + meter.Peak.ToString(CultureInfo.InvariantCulture));
            Assert.IsTrue(meter.Peak <= cap,
                "and never wider than MaxConcurrentShows (" + cap.ToString(CultureInfo.InvariantCulture)
                + "), because one catalog read must not dial a whole catalogue at once; the observed"
                + " peak was " + meter.Peak.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        ///   ONE budget for the whole read, not one per phase. A tags call that eats most of the
        ///   budget leaves the fan-out only what is LEFT of it, so a read whose tags call took 4s
        ///   answers at about the budget instead of at 4s plus a fresh one. A per-phase deadline
        ///   would double the configuration-page stall the feature caps at 5s and would still pass
        ///   every other test here, including the hung-show test above.
        /// </summary>
        [TestMethod]
        public async Task TheBudgetIsSharedAcrossTagsAndTheFanOut_RatherThanOnePerPhase()
        {
            var slowTags = TimeSpan.FromSeconds(4);
            var stub = new CatalogHandler(async (path, _, cancellationToken) =>
            {
                if (path.EndsWith("/api/tags", StringComparison.Ordinal))
                {
                    await Task.Delay(slowTags, cancellationToken);
                    return RemoteModelWire.Json(TagsJson("a:latest", "b:latest"));
                }

                await Task.Delay(Timeout.Infinite, cancellationToken);
                return null;
            });

            var clock = Stopwatch.StartNew();
            var models = await ChatModelCatalog.ReadAsync(NahilOptions(), CancellationToken.None, stub);
            clock.Stop();

            Assert.IsNotNull(models, "the names arrived, so the names are returned");
            CollectionAssert.AreEqual(new[] { "a:latest", "b:latest" }, models.Select(m => m.Name).ToArray());
            Assert.IsTrue(models.All(m => m.Capability == null && m.Available == null),
                "the fan-out ran out of what was left of the budget, so the metadata stays unknown");
            Assert.IsTrue(clock.Elapsed >= slowTags, "the tags call really did spend most of the budget");
            Assert.IsTrue(clock.Elapsed < ChatModelCatalog.Budget + TimeSpan.FromSeconds(2),
                "the fan-out inherits the REMAINDER of the one budget; a fresh budget per phase would"
                + " answer at about " + (slowTags + ChatModelCatalog.Budget) + ". It took " + clock.Elapsed);
        }

        /// <summary>A caller who cancels before the read starts gets a cancellation, not a null
        /// pretending the backend said nothing.</summary>
        [TestMethod]
        public async Task AnAlreadyCancelledCaller_Propagates()
        {
            var stub = OllamaStub(TagsJson("a:latest"), _ => RemoteModelWire.Json(ShowJson("completion")));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            await RemoteModelWire.AssertCancelled(
                () => ChatModelCatalog.ReadAsync(NahilOptions(), cancelled.Token, stub),
                "the caller's cancellation is the one failure this read does not swallow");
        }

        /// <summary>
        ///   The interesting half of the same rule: a caller who goes away DURING the fan-out. The
        ///   per-entry swallow cannot tell that cancellation from the budget's, so without the
        ///   caller-token re-check after the fan-out this would answer a degraded list to a caller
        ///   that is gone.
        /// </summary>
        [TestMethod]
        public async Task ACallerCancelledDuringTheFanOut_Propagates()
        {
            using var caller = new CancellationTokenSource();
            var stub = new CatalogHandler(async (path, _, cancellationToken) =>
            {
                if (path.EndsWith("/api/tags", StringComparison.Ordinal))
                {
                    return RemoteModelWire.Json(TagsJson("a:latest", "b:latest"));
                }

                await caller.CancelAsync();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return null;
            });

            await RemoteModelWire.AssertCancelled(
                () => ChatModelCatalog.ReadAsync(NahilOptions(), caller.Token, stub),
                "a list assembled after the caller went away is not an answer");
        }

        /// <summary>
        ///   An empty catalogue is an answer: 200 with an empty list, not the 503 a failed read
        ///   produces. And nothing is asked about a model that was not listed. This is one half of a
        ///   PAIR that must never collapse into one behaviour; the other half is
        ///   <see cref="A200BodyWithNoModelsField_IsAWholesaleFailure" />, where the field is absent
        ///   rather than empty and the read fails wholesale.
        /// </summary>
        [TestMethod]
        public async Task AnEmptyCatalog_IsAnEmptyListRatherThanAFailure()
        {
            var stub = OllamaStub("{\"models\":[]}", _ => Failed());

            var models = await ChatModelCatalog.ReadAsync(NahilOptions(), CancellationToken.None, stub);

            Assert.IsNotNull(models, "a backend that catalogues nothing said so, which is not a failure");
            Assert.AreEqual(0, models.Count);
            Assert.AreEqual(1, stub.Paths.Count, "no model, no fan-out");
        }

        /// <summary>
        ///   The other half of that pair: a 200 carrying well-formed JSON with NO models field is a
        ///   WHOLESALE failure, which the route reports as a 503. This is what an authenticating
        ///   reverse proxy, a captive-portal JSON page or a backend that renamed the field answers,
        ///   and calling it "an empty catalogue" would hand the operator an empty picker with nothing
        ///   naming the cause. The garbled-body case in
        ///   <see cref="AFailedTagsCall_IsAWholesaleFailure" /> does not cover this: HTML THROWS on
        ///   deserialization and takes the already-working exception path, while this body
        ///   deserializes cleanly.
        /// </summary>
        [TestMethod]
        public async Task A200BodyWithNoModelsField_IsAWholesaleFailure()
        {
            var proxy = new CatalogHandler((_, _) => RemoteModelWire.Json("{\"detail\":\"ok\"}"));
            Assert.IsNull(await ChatModelCatalog.ReadAsync(NahilOptions(), CancellationToken.None, proxy),
                "valid JSON of the wrong shape is no catalog, not an empty one");
            Assert.AreEqual(1, proxy.Paths.Count, "and nothing is asked about a model nobody listed");

            var bare = new CatalogHandler((_, _) => RemoteModelWire.Json("{}"));
            Assert.IsNull(await ChatModelCatalog.ReadAsync(NahilOptions(), CancellationToken.None, bare),
                "an empty object carries no models field either");

            var nulled = new CatalogHandler((_, _) => RemoteModelWire.Json("{\"models\":null}"));
            Assert.IsNull(await ChatModelCatalog.ReadAsync(NahilOptions(), CancellationToken.None, nulled),
                "and an explicit null is the same absence");
        }

        /// <summary>A tags call that fails is a WHOLESALE failure (no names, nothing to degrade),
        /// whether it fails by status or by answering something that is not the expected JSON.</summary>
        [TestMethod]
        public async Task AFailedTagsCall_IsAWholesaleFailure()
        {
            var status = new CatalogHandler((_, _) => Failed(HttpStatusCode.Unauthorized));
            Assert.IsNull(await ChatModelCatalog.ReadAsync(NahilOptions(), CancellationToken.None, status),
                "a 401 on tags is no catalog");

            var garbage = new CatalogHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>not ollama</html>", Encoding.UTF8, "text/html")
            });
            Assert.IsNull(await ChatModelCatalog.ReadAsync(NahilOptions(), CancellationToken.None, garbage),
                "a body that is not the expected JSON is no catalog either");
        }

        /// <summary>A model listed twice (Nahil publishes aliases) is one entry, so a picker does not
        /// show the same name twice.</summary>
        [TestMethod]
        public async Task ADuplicateName_IsListedOnce()
        {
            var stub = OllamaStub(TagsJson("a:latest", "a:latest"),
                _ => RemoteModelWire.Json(ShowJson("completion", true, "S1")));

            var models = await ChatModelCatalog.ReadAsync(NahilOptions(), CancellationToken.None, stub);

            Assert.AreEqual(1, models.Count);
            Assert.AreEqual(2, stub.Paths.Count, "and it is asked about once");
        }

        /// <summary>An unusable configuration is refused before anything is dialled: the reason
        /// belongs to the caller (ChatBackendFactory.Validate), not to a transport failure.</summary>
        [TestMethod]
        public async Task AnUnusableConfiguration_DialsNothing()
        {
            var stub = OllamaStub(TagsJson(Model), _ => RemoteModelWire.Json(ShowJson("completion")));

            var noKey = NahilOptions();
            noKey.Nahil.ApiKey = null;
            Assert.IsNull(await ChatModelCatalog.ReadAsync(noKey, CancellationToken.None, stub));

            var unknown = NahilOptions();
            unknown.Backend = "Nope";
            Assert.IsNull(await ChatModelCatalog.ReadAsync(unknown, CancellationToken.None, stub));

            Assert.IsNull(await ChatModelCatalog.ReadAsync(null, CancellationToken.None, stub));
            Assert.AreEqual(0, stub.Paths.Count, "nothing reachable was contacted");
        }

        // The "a catalog read never constructs the provider's lazy backend" rule is pinned ONCE, at
        // the route level, by TheRoute_Answers200_WithTheCatalog_AndLoadsNoBackend: only a request
        // through the controller reaches the provider that owns that Lazy. A function-level version
        // of the test used to sit here and was removed as tautological - ReadAsync is static and
        // takes no provider, so no implementation of it could have failed that assertion.

        #endregion

        #region the remote arms

        /// <summary>
        ///   OpenAI's catalogue: one authenticated GET on the models route, with the URL and the
        ///   credential built by the SDK (this asserts what actually went out, not what we intended).
        ///   Its entries carry no capability, and inventing one from a name would be a guess.
        /// </summary>
        [TestMethod]
        public async Task TheOpenAIArm_ReadsTheModelsRoute_AndReportsNamesOnly()
        {
            var stub = new RecordingHandler(_ => RemoteModelWire.Json(
                "{\"object\":\"list\",\"data\":["
                + "{\"id\":\"gpt-4o-mini\",\"object\":\"model\",\"created\":1700000000,\"owned_by\":\"openai\"},"
                + "{\"id\":\"babbage-002\",\"object\":\"model\",\"created\":1700000001,\"owned_by\":\"openai\"},"
                + "{\"id\":\"text-embedding-3-small\",\"object\":\"model\",\"created\":1700000002,"
                + "\"owned_by\":\"openai\"}]}"));

            var models = await ChatModelCatalog.ReadAsync(OpenAIOptions(), CancellationToken.None, stub);

            Assert.IsNotNull(models);
            CollectionAssert.AreEqual(new[] { "babbage-002", "gpt-4o-mini", "text-embedding-3-small" },
                models.Select(m => m.Name).ToArray(), "sorted ordinally, like every other backend's");
            Assert.IsTrue(models.All(m => m.Capability == null && m.Available == null && m.ModelClass == null),
                "OpenAI publishes no capability, availability or class");

            Assert.AreEqual(1, stub.Calls, "one page, one call");
            Assert.AreEqual(RemoteModelWire.OpenAIHost + "/v1/models", stub.Uris.Single());
            Assert.AreEqual("Bearer " + RemoteModelWire.OpenAIKey, stub.Headers.Single()["Authorization"]);
        }

        /// <summary>
        ///   Anthropic's catalogue: its own credential header plus the version header its API
        ///   requires, the first page at the maximum size, and <c>id</c> as the name (there is no
        ///   <c>name</c> field, and the display name is not what goes into configuration).
        /// </summary>
        [TestMethod]
        public async Task TheAnthropicArm_SendsItsOwnHeaders_AndUsesTheIdAsTheName()
        {
            var stub = new RecordingHandler(_ => RemoteModelWire.Json(
                "{\"data\":["
                + "{\"id\":\"claude-opus-5\",\"type\":\"model\",\"display_name\":\"Claude Opus 5\","
                + "\"created_at\":\"2026-01-01T00:00:00Z\"},"
                + "{\"id\":\"claude-haiku-4-5\",\"type\":\"model\",\"display_name\":\"Claude Haiku 4.5\","
                + "\"created_at\":\"2025-10-01T00:00:00Z\"}],"
                + "\"has_more\":false,\"first_id\":\"claude-opus-5\",\"last_id\":\"claude-haiku-4-5\"}"));

            var models = await ChatModelCatalog.ReadAsync(AnthropicOptions(), CancellationToken.None, stub);

            Assert.IsNotNull(models);
            CollectionAssert.AreEqual(new[] { "claude-haiku-4-5", "claude-opus-5" },
                models.Select(m => m.Name).ToArray(), "the id is the name, sorted ordinally");
            Assert.IsTrue(models.All(m => m.Capability == null && m.Available == null && m.ModelClass == null));

            Assert.AreEqual(1, stub.Calls, "the first page only; pagination is deliberately not followed");
            var uri = stub.Uris.Single();
            StringAssert.StartsWith(uri, RemoteModelWire.AnthropicHost + "/v1/models",
                "the configured host root, with the SDK's own route appended");
            StringAssert.Contains(uri, "limit=1000", "the maximum page size, so one page is as complete as it gets");

            var headers = stub.Headers.Single();
            Assert.AreEqual(RemoteModelWire.AnthropicKey, headers["x-api-key"]);
            Assert.IsFalse(String.IsNullOrWhiteSpace(headers["anthropic-version"]),
                "the version header this API requires on every route");
            Assert.IsFalse(headers.ContainsKey("Authorization"), "Anthropic does not take a bearer");
        }

        /// <summary>A remote provider that refuses the read is no catalog, and the refusal never
        /// escapes as an exception.</summary>
        [TestMethod]
        public async Task ARefusedRemoteRead_IsNoCatalog()
        {
            var openAi = new RecordingHandler(_ => RemoteModelWire.Status(HttpStatusCode.Unauthorized));
            Assert.IsNull(await ChatModelCatalog.ReadAsync(OpenAIOptions(), CancellationToken.None, openAi));

            var anthropic = new RecordingHandler(_ => RemoteModelWire.Status(HttpStatusCode.Forbidden));
            Assert.IsNull(await ChatModelCatalog.ReadAsync(AnthropicOptions(), CancellationToken.None, anthropic));
        }

        /// <summary>A remote provider that never answers is bounded by the same budget as the
        /// Ollama-protocol arm, and a cancelled caller still propagates through an SDK.</summary>
        [TestMethod]
        public async Task ARemoteReadIsBounded_AndACancelledCallerPropagates()
        {
            var hung = new RecordingHandler(async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return null;
            });

            var clock = Stopwatch.StartNew();
            Assert.IsNull(await ChatModelCatalog.ReadAsync(OpenAIOptions(), CancellationToken.None, hung));
            clock.Stop();
            Assert.IsTrue(clock.Elapsed >= TimeSpan.FromSeconds(4), "bounded by the budget, not by an SDK default");
            Assert.IsTrue(clock.Elapsed < ChatModelCatalog.Budget + TimeSpan.FromSeconds(15),
                "the SDK's own 100s deadline is disarmed; it took " + clock.Elapsed);

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var stub = new RecordingHandler(_ => RemoteModelWire.Json("{\"object\":\"list\",\"data\":[]}"));
            await RemoteModelWire.AssertCancelled(
                () => ChatModelCatalog.ReadAsync(OpenAIOptions(), cancelled.Token, stub),
                "the caller's cancellation is not swallowed on the remote arms either");
        }

        #endregion

        #region the endpoint

        /// <summary>
        ///   A loopback server speaking just enough of the Ollama protocol for one catalog read, so
        ///   the endpoint tests can prove the 200 shape END TO END (route, gate, serializer) rather
        ///   than stopping at the catalog function. Raw sockets rather than HttpListener, which needs
        ///   a URL reservation on Windows - the same choice IntegrationsEndpointTest made - and every
        ///   answer closes its connection, so there is no keep-alive bookkeeping here.
        /// </summary>
        private sealed class LoopbackOllama : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly String _tagsJson;
            private readonly Func<String, String> _show;
            private readonly Object _lock = new Object();

            internal LoopbackOllama(String tagsJson, Func<String, String> show)
            {
                _tagsJson = tagsJson;
                _show = show;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Endpoint = "http://127.0.0.1:"
                    + ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);
                _ = Task.Run(AcceptAsync);
            }

            /// <summary>The host root to configure, exactly as an operator would write it.</summary>
            internal String Endpoint
            {
                get;
            }

            /// <summary>One entry per served request; <c>null</c> where none arrived.</summary>
            internal List<String> Authorizations { get; } = new List<String>();

            internal List<String> Paths { get; } = new List<String>();

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

                    // Each request is served on its own task: the show fan-out is concurrent, so a
                    // sequential accept loop would serialize what the read does in parallel.
                    _ = Task.Run(() => ServeAsync(socket));
                }
            }

            private async Task ServeAsync(TcpClient socket)
            {
                using (socket)
                {
                    try
                    {
                        var stream = socket.GetStream();
                        var head = await ReadHeadAsync(stream);
                        if (head == null)
                        {
                            return;
                        }

                        var body = await ReadBodyAsync(stream, ContentLengthOf(head));
                        var path = RequestPathOf(head);

                        lock (_lock)
                        {
                            Paths.Add(path);
                            Authorizations.Add(HeaderOf(head, "authorization"));
                        }

                        var json = path.EndsWith("/api/tags", StringComparison.Ordinal)
                            ? _tagsJson
                            : _show(ModelOf(body));
                        var payload = Encoding.UTF8.GetBytes(json);
                        var header = Encoding.ASCII.GetBytes(
                            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "
                            + payload.Length.ToString(CultureInfo.InvariantCulture)
                            + "\r\nConnection: close\r\n\r\n");

                        await stream.WriteAsync(header, 0, header.Length);
                        await stream.WriteAsync(payload, 0, payload.Length);
                        await stream.FlushAsync();

                        // Half-close rather than slam the socket shut: a plain Dispose can reach the
                        // client as a reset before it has read the body it was just sent.
                        socket.Client.Shutdown(SocketShutdown.Send);
                    }
                    catch (Exception)
                    {
                        // A client that hung up mid-exchange is not this fixture's problem.
                    }
                }
            }

            /// <summary>The request line and headers, byte by byte to the blank line. Small requests
            /// only, and clarity beats throughput in a fixture.</summary>
            private static async Task<String> ReadHeadAsync(NetworkStream stream)
            {
                var head = new StringBuilder();
                var one = new Byte[1];
                while (!EndsWithBlankLine(head))
                {
                    var read = await stream.ReadAsync(one, 0, 1);
                    if (read == 0)
                    {
                        return null;
                    }

                    head.Append((Char)one[0]);
                }

                return head.ToString();
            }

            private static Boolean EndsWithBlankLine(StringBuilder head)
            {
                return head.Length >= 4 && head[head.Length - 4] == '\r' && head[head.Length - 3] == '\n'
                    && head[head.Length - 2] == '\r' && head[head.Length - 1] == '\n';
            }

            private static async Task<String> ReadBodyAsync(NetworkStream stream, Int32 length)
            {
                if (length <= 0)
                {
                    return null;
                }

                var payload = new Byte[length];
                var got = 0;
                while (got < length)
                {
                    var read = await stream.ReadAsync(payload, got, length - got);
                    if (read == 0)
                    {
                        break;
                    }

                    got += read;
                }

                return Encoding.UTF8.GetString(payload, 0, got);
            }

            private static Int32 ContentLengthOf(String head)
            {
                var value = HeaderOf(head, "content-length");
                return value != null && Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var length)
                    ? length
                    : 0;
            }

            private static String HeaderOf(String head, String name)
            {
                foreach (var line in head.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries).Skip(1))
                {
                    var colon = line.IndexOf(':');
                    if (colon > 0 && String.Equals(line.Substring(0, colon).Trim(), name,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return line.Substring(colon + 1).Trim();
                    }
                }

                return null;
            }

            private static String RequestPathOf(String head)
            {
                var parts = head.Split(new[] { "\r\n" }, 2, StringSplitOptions.None)[0].Split(' ');
                return parts.Length > 1 ? parts[1] : String.Empty;
            }
        }

        /// <summary>A port nothing listens on: bound to learn a free one, then released.</summary>
        private static String ClosedLoopbackEndpoint()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
        }

        private const String EndpointApiKey = "catalog-test-key";

        private static VolatileAppFactory Host(Boolean enabled, String backend, String endpoint,
            Boolean withApiKey = false)
        {
            var settings = new Dictionary<String, String>
            {
                { "Fallen8:Chat:Enabled", enabled ? "true" : "false" },
                { "Fallen8:Chat:Backend", backend },
                { "Fallen8:Chat:Ollama:Model", Model },
                { "Fallen8:Chat:Nahil:Model", Model }
            };

            if (endpoint != null)
            {
                settings["Fallen8:Chat:Ollama:Endpoint"] = endpoint;
                settings["Fallen8:Chat:Nahil:Endpoint"] = endpoint;
            }

            if (withApiKey)
            {
                settings["Fallen8:Security:ApiKey"] = EndpointApiKey;
            }

            return new VolatileAppFactory(settings);
        }

        private static async Task<JsonElement> BodyOf(HttpResponseMessage response)
        {
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        }

        /// <summary>
        ///   The capability gate answers before anything is dialled: chat off is a 403 to an
        ///   AUTHENTICATED caller, exactly the posture POST /chat has (unauthenticated on a keyed
        ///   server is the 401 below, like everywhere else).
        /// </summary>
        [TestMethod]
        public async Task TheRoute_Answers403_WhenChatIsDisabled()
        {
            using var factory = Host(enabled: false, "Ollama", SidecarHost, withApiKey: true);
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Api-Key", EndpointApiKey);

            using var response = await client.GetAsync("/chat/models");

            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode,
                await response.Content.ReadAsStringAsync());
        }

        /// <summary>A keyed instance refuses an anonymous catalog read: the route reveals what the
        /// operator's credentialed backend catalogues, so it takes the instance's auth posture.</summary>
        [TestMethod]
        public async Task TheRoute_Answers401_WhenKeyedAndNoCredentialIsSupplied()
        {
            using var factory = Host(enabled: true, "Ollama", SidecarHost, withApiKey: true);
            using var client = factory.CreateClient();

            using var anonymous = await client.GetAsync("/chat/models");
            Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        /// <summary>
        ///   A backend that cannot be used at all answers 503 with the reason naming the KEY to fix,
        ///   and never the endpoint value: that sentence reaches an operator through a problem-detail
        ///   that is anonymous on a keyless instance, and an endpoint can carry a credential.
        /// </summary>
        [TestMethod]
        public async Task TheRoute_Answers503_NamingTheReason_AndNeverTheEndpoint()
        {
            using var unsupported = Host(enabled: true, "Nope", SidecarHost);
            using (var client = unsupported.CreateClient())
            {
                using var response = await client.GetAsync("/chat/models");
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                var detail = (await BodyOf(response)).GetProperty("detail").GetString();
                StringAssert.Contains(detail, "Fallen8:Chat:Backend is 'Nope'");
            }

            // Nahil with an endpoint but no credential: the reason names the missing key.
            using var noKey = Host(enabled: true, "Nahil", NahilHost);
            using (var client = noKey.CreateClient())
            {
                using var response = await client.GetAsync("/chat/models");
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                var detail = (await BodyOf(response)).GetProperty("detail").GetString();
                StringAssert.Contains(detail, "Fallen8:Chat:Nahil:ApiKey is required");
                Assert.IsFalse(detail.Contains("nahil.invalid", StringComparison.OrdinalIgnoreCase),
                    "no message quotes the endpoint value");
            }
        }

        /// <summary>
        ///   A configured but unreachable backend answers 503 too, and its detail names the
        ///   POSSIBILITIES rather than the actual transport fault: that fault can carry the endpoint
        ///   value or the credential, so none of it is repeated.
        /// </summary>
        [TestMethod]
        public async Task TheRoute_Answers503_WhenTheBackendIsUnreachable()
        {
            var endpoint = ClosedLoopbackEndpoint();
            using var factory = Host(enabled: true, "Ollama", endpoint);
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/chat/models");

            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var detail = (await BodyOf(response)).GetProperty("detail").GetString();
            StringAssert.Contains(detail, "returned no usable model catalog");
            StringAssert.Contains(detail, "within 5s", "the documented budget, named as one of the possibilities");
            Assert.IsFalse(detail.Contains("127.0.0.1", StringComparison.Ordinal),
                "the endpoint value stays out of the response");
        }

        /// <summary>
        ///   The whole route over a real backend: the documented body (backend name, sorted models,
        ///   the four per-model fields), no credential offered to a sidecar, and the chat provider's
        ///   backend still not constructed afterwards - a catalog read is not a chat call.
        ///   <para>
        ///     That last assertion is the SINGLE home of the never-construct-the-lazy-backend rule
        ///     (feature decision 4). It lives here rather than beside the catalog function because
        ///     the provider holding that Lazy is only reachable through the route: the function is
        ///     static and is handed options, never a provider. If the read ever went through the
        ///     provider's backend instead of a transient transport, <c>/status</c>'s
        ///     <c>chat.loaded</c> is what would turn true and fail here.
        ///   </para>
        /// </summary>
        [TestMethod]
        public async Task TheRoute_Answers200_WithTheCatalog_AndLoadsNoBackend()
        {
            using var backend = new LoopbackOllama(TagsJson("phi4-f8:latest", "bge-m3:latest"),
                model => model == "bge-m3:latest"
                    ? ShowJson("embedding")
                    : ShowJson("completion", extraCapability: "tools"));

            using var factory = Host(enabled: true, "Ollama", backend.Endpoint);
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/chat/models");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("application/json", response.Content.Headers.ContentType?.MediaType);

            var body = await BodyOf(response);
            Assert.AreEqual("Ollama", body.GetProperty("backend").GetString());

            var models = body.GetProperty("models").EnumerateArray().ToList();
            Assert.AreEqual(2, models.Count);
            Assert.AreEqual("bge-m3:latest", models[0].GetProperty("name").GetString(),
                "sorted ordinally by name");
            Assert.AreEqual("embedding", models[0].GetProperty("capability").GetString());
            Assert.IsTrue(models[0].GetProperty("available").GetBoolean(), "a sidecar's models are on disk");
            Assert.AreEqual(JsonValueKind.Null, models[0].GetProperty("class").ValueKind,
                "the class is a Nahil field, and the member is emitted even when null");
            Assert.AreEqual("phi4-f8:latest", models[1].GetProperty("name").GetString());
            Assert.AreEqual("completion", models[1].GetProperty("capability").GetString());

            Assert.AreEqual(3, backend.Paths.Count, "one tags call plus one show per model");
            foreach (var authorization in backend.Authorizations)
            {
                Assert.IsNull(authorization, "a sidecar gets no Authorization header, ever");
            }

            using var status = await client.GetAsync("/status");
            var chat = (await BodyOf(status)).GetProperty("chat");
            Assert.IsFalse(chat.GetProperty("loaded").GetBoolean(),
                "reading the catalog must not construct the chat backend");
        }

        #endregion
    }
}
