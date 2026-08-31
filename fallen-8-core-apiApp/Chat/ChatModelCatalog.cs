// MIT License
//
// ChatModelCatalog.cs
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
using System.ClientModel;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Helper;

namespace NoSQL.GraphDB.App.Chat
{
    /// <summary>
    ///   ONE model as the configured backend catalogues it (feature chat-model-catalog). Every field
    ///   except the name is optional, because no backend publishes all of them; what a null means is
    ///   stated on the field.
    /// </summary>
    /// <remarks>
    ///   Public for the test project rather than because a caller outside this assembly needs it -
    ///   the repository adds no <c>InternalsVisibleTo</c>, the same reason
    ///   <see cref="OllamaConnection" /> is public.
    /// </remarks>
    public sealed class ChatCatalogModel
    {
        /// <summary>The name VERBATIM, as the backend spells it (tag included). This is the value an
        /// operator writes back into <c>Fallen8:Chat:&lt;Backend&gt;:Model</c>, so nothing here
        /// strips, appends or normalizes a <c>:tag</c>.</summary>
        public String Name
        {
            get; init;
        }

        /// <summary><c>completion</c>, <c>embedding</c>, or <c>null</c> when the backend does not say
        /// (OpenAI and Anthropic never do, an older Ollama sidecar omits it, and a failed
        /// <c>/api/show</c> leaves it unknown).</summary>
        public String Capability
        {
            get; init;
        }

        /// <summary>Whether the backend can serve this model right now; <c>null</c> when it reports
        /// nothing on the subject.</summary>
        public Boolean? Available
        {
            get; init;
        }

        /// <summary>Nahil's <c>nahil_class</c> verbatim, <c>null</c> everywhere else. It carries no
        /// published legend (observed: S1/S2 on completion models, C1/C2 on embedding ones), so it is
        /// passed through as a label rather than interpreted.</summary>
        public String ModelClass
        {
            get; init;
        }
    }

    /// <summary>
    ///   Reads what the configured chat backend catalogues (feature chat-model-catalog), so the
    ///   Configuration surface can offer real model names instead of a blank field. It follows
    ///   <see cref="OllamaModelProbe" />'s discipline, for the reasons stated there: a TRANSIENT
    ///   transport, so a catalog read never touches the chat provider's lazy backend (it must not
    ///   construct a chat client or flip its loaded flag); every failure degraded rather than
    ///   thrown; and the caller's cancellation as the ONE thing that propagates.
    ///   <para>
    ///     What it adds is a fan-out, and therefore a SHARED deadline: the Ollama-protocol catalog is
    ///     <c>/api/tags</c> plus one <c>/api/show</c> per listed model, and <see cref="Budget" />
    ///     bounds all of it together rather than each call separately. Degradation is per ENTRY: a
    ///     model whose <c>/api/show</c> fails keeps its name and loses only the metadata, because a
    ///     name with unknown capabilities is still a name an operator can configure.
    ///   </para>
    ///   <para>
    ///     The catalogue is deliberately not the whole RESOLVABLE set on Nahil: a name absent from
    ///     <c>/api/tags</c> can still resolve (<c>f8-delegate:latest</c> does), which is why the
    ///     affordance built on this stays a combobox over free text rather than a closed dropdown.
    ///   </para>
    /// </summary>
    /// <remarks>
    ///   Public for the test project rather than because a caller outside this assembly needs it -
    ///   see <see cref="ChatCatalogModel" />.
    /// </remarks>
    public static class ChatModelCatalog
    {
        /// <summary>
        ///   The whole read's stall bound: the tags call and the show fan-out together. A documented
        ///   constant and not a setting, because this read exists to fill a picker - a slow backend
        ///   may cost a configuration page a few seconds and no more. Longer than
        ///   <see cref="OllamaModelProbe" />'s bound because it covers 1 + N calls rather than one.
        /// </summary>
        public static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

        /// <summary>
        ///   How many <c>/api/show</c> calls may be in flight at once. It exists because ONE
        ///   authenticated catalog read becomes 1 + N outbound calls carrying the operator's
        ///   credential: the transport here is built with no deadline and no per-server connection
        ///   limit of its own, so an uncapped fan-out over a long catalogue would dial every model
        ///   simultaneously and invite a metered backend to answer 429 to the tail - which degrades
        ///   each of those entries to "capability unknown" and hands a picker embedding models it
        ///   cannot recognize as such. Small enough to stay a polite metadata read, wide enough that
        ///   a real catalogue still finishes inside <see cref="Budget" />.
        /// </summary>
        public const Int32 MaxConcurrentShows = 8;

        /// <summary>The two capability values this catalog publishes. Everything else a backend may
        /// list (current Ollama also reports <c>tools</c>, <c>vision</c>, <c>insert</c>) describes a
        /// feature rather than the model KIND, and is deliberately dropped.</summary>
        private const String CompletionCapability = "completion";

        private const String EmbeddingCapability = "embedding";

        /// <summary>Anthropic's maximum page size, so the one page this reads is as complete as one
        /// page gets.</summary>
        private const Int32 AnthropicPageSize = 1000;

        /// <summary>
        ///   The configured backend's catalogue, sorted ordinally by name; <c>null</c> when the read
        ///   failed WHOLESALE (an unusable configuration, no answer within <see cref="Budget" />, an
        ///   error status, or a body that is not the expected JSON), which the caller reports as a
        ///   503. An EMPTY list is a success: a backend with nothing catalogued said so.
        /// </summary>
        /// <param name="options">The bound chat options. The target is resolved through
        /// <see cref="ChatBackendFactory" />, so the catalog can never describe a backend a
        /// completion would not reach.</param>
        /// <param name="cancellationToken">The caller's; its cancellation propagates.</param>
        /// <param name="handler">A test-supplied transport handler; used verbatim when non-null.</param>
        public static async Task<IReadOnlyList<ChatCatalogModel>> ReadAsync(Fallen8ChatOptions options,
            CancellationToken cancellationToken, HttpMessageHandler handler = null)
        {
            if (options == null)
            {
                return null;
            }

            // ONE budget for the whole read, owned here because it spans 1 + N calls. Per the
            // deadline rule on OllamaHttpClientFactory, a caller that owns the deadline with a
            // linked CTS takes no transport deadline as well - which is why the clients below are
            // built without one.
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(Budget);

            try
            {
                IReadOnlyList<ChatCatalogModel> models;
                if (ChatBackendFactory.ResolveConnection(options) is { } connection)
                {
                    models = await ReadOllamaProtocolAsync(connection, budget.Token, handler);
                }
                else if (ChatBackendFactory.ResolveRemoteTarget(options) is { } target)
                {
                    // The selector is the switch key here exactly as it is in the backend factory;
                    // ResolveRemoteTarget answers non-null for these two names only.
                    models = options.Backend == "Anthropic"
                        ? await ReadAnthropicAsync(target, budget.Token, handler)
                        : await ReadOpenAIAsync(target, budget.Token, handler);
                }
                else
                {
                    // A selector this app does not have. The caller reports the reason from
                    // ChatBackendFactory.Validate, which is the one home for that sentence.
                    return null;
                }

                // The per-entry swallow in ShowAsync cannot tell whose cancellation it caught, so the
                // caller's token is re-checked here: a list assembled after the caller went away is
                // not an answer, it is a degraded read nobody is waiting for.
                cancellationToken.ThrowIfCancellationRequested();

                // ONE home for the ordinal-sort half of the response contract. Ordinal rather than
                // culture-aware because the names are identifiers an operator copies back into
                // configuration, and a culture-sensitive order would rank "phi4-f8-mini:latest" and
                // "phi4-f8:latest" differently on different machines.
                return models?.OrderBy(model => model.Name, StringComparer.Ordinal).ToList();
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Every backend failure, including this read's own budget, is "no catalog" rather
                // than an exception; the caller turns that into the documented 503.
                return null;
            }
            catch (OperationCanceledException)
            {
                // The caller's own. The filter above already took everybody else's.
                throw;
            }
            catch (Exception)
            {
                // Also the caller's, reported by a provider SDK as something else: a request
                // abandoned before it was sent surfaces from System.ClientModel as a closed-stream
                // fault rather than a cancellation. Normalized, so a caller has ONE thing to catch.
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
        }

        /// <summary>
        ///   Ollama and Nahil, which speak one protocol: <c>/api/tags</c> for the names, then one
        ///   concurrent <c>/api/show</c> per name for what tags does not carry (the capabilities and,
        ///   on Nahil, the per-model routability and class).
        /// </summary>
        private static async Task<IReadOnlyList<ChatCatalogModel>> ReadOllamaProtocolAsync(
            OllamaConnection connection, CancellationToken budget, HttpMessageHandler handler)
        {
            if (!connection.IsValid(out _))
            {
                return null;
            }

            // CreateForProbe rather than CreateForProvider because a metadata read must not wait out
            // a Nahil warm-up: names now beat names after a model pull, and waiting would spend the
            // whole shared budget on one call. The transport deadline is INFINITE here so that budget
            // stays the single owner of it. Disposed per call, like the residency probe: the
            // transport is transient by design, and OllamaSharp's leak lesson applies to any client
            // built here.
            using var http = OllamaHttpClientFactory.CreateForProbe(connection, Timeout.InfiniteTimeSpan, handler);

            var tags = await ReadJsonAsync<TagsWire>(http,
                new HttpRequestMessage(HttpMethod.Get, "api/tags"), budget);

            // No answer at all, and a 200 whose JSON carries NO models field, are the same wholesale
            // failure: both mean nothing catalogued a model. The second case is what an
            // authenticating reverse proxy, a captive-portal JSON page or a backend that renamed the
            // field answers, and it deserializes cleanly into a TagsWire with nothing in it -
            // reporting that as an empty catalogue would put an operator in front of an empty picker
            // with nothing naming the cause. A models field that IS present and empty is the other
            // thing entirely: a backend with nothing catalogued said so, which is a 200 empty list.
            if (tags?.Models == null)
            {
                return null;
            }

            var names = tags.Models
                .Select(model => model?.Name)
                .Where(name => !String.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            // Concurrent on purpose: sequential shows would spend the shared budget N times over, and
            // a real Nahil catalogue is already long enough for that to matter. Concurrent up to
            // MaxConcurrentShows and no further, for the reason stated there. Each result is kept by
            // INDEX rather than by completion order, so a show answer can only ever land on the name
            // it was asked about, whatever order the backend answers in.
            using var gate = new SemaphoreSlim(MaxConcurrentShows, MaxConcurrentShows);
            var shows = new Task<ShowWire>[names.Count];
            for (var i = 0; i < names.Count; i++)
            {
                shows[i] = ShowAsync(http, names[i], gate, budget);
            }

            // Every task has completed by the time this returns (ShowAsync throws nothing), which is
            // what makes disposing the gate at the end of this method safe.
            var details = await Task.WhenAll(shows);

            var models = new List<ChatCatalogModel>(names.Count);
            for (var i = 0; i < names.Count; i++)
            {
                var show = details[i];
                models.Add(new ChatCatalogModel
                {
                    Name = names[i],
                    Capability = CapabilityOf(show?.Capabilities),
                    // A sidecar's tags entry is on disk, and TAGS is what establishes that - so a
                    // sidecar model stays available even when its show call said nothing. Nahil's
                    // routability is per model and per moment, and only show knows it.
                    Available = connection.IsNahil ? show?.RoutableNow : true,
                    ModelClass = show?.NahilClass
                });
            }

            return models;
        }

        /// <summary>
        ///   One model's <c>/api/show</c>, or <c>null</c> when it did not answer usefully. This is the
        ///   per-entry degradation: an older sidecar has no <c>capabilities</c> field, a model can
        ///   stop resolving between the two calls, and neither is a reason to hide a name.
        ///   <para>
        ///     The gate is the fan-out's width (<see cref="MaxConcurrentShows" />), held for the
        ///     duration of this call and released on every exit path, including the one where the
        ///     budget expires while this call is still queued behind it.
        ///   </para>
        /// </summary>
        [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
            Justification = "Trimming is disabled for this application; the wire DTO is private and simple.")]
        private static async Task<ShowWire> ShowAsync(HttpClient http, String model, SemaphoreSlim gate,
            CancellationToken budget)
        {
            try
            {
                await gate.WaitAsync(budget);
            }
            catch (Exception)
            {
                // The budget went while this call was still queued behind the cap. That is the same
                // degradation a failed show is: the name stays, its metadata is unknown.
                return null;
            }

            var request = new HttpRequestMessage(HttpMethod.Post, "api/show")
            {
                Content = new StringContent(JsonSerializer.Serialize(new ShowRequestWire { Model = model }),
                    Encoding.UTF8, "application/json")
            };

            try
            {
                return await ReadJsonAsync<ShowWire>(http, request, budget);
            }
            catch (Exception)
            {
                // Unconditional, unlike the read-wide swallow: this one cannot tell the caller's
                // cancellation from the budget's, so ReadAsync re-checks the caller's token after the
                // fan-out instead of letting this decide.
                return null;
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>OpenAI, through its own SDK so the credential and the request URL keep their
        /// single author (the rule lives on <see cref="RemoteModelHttpClient" />). Its catalogue
        /// includes non-chat models and reports no capability, so every entry passes through as a
        /// name and nothing else.</summary>
        private static async Task<IReadOnlyList<ChatCatalogModel>> ReadOpenAIAsync(RemoteModelTarget target,
            CancellationToken budget, HttpMessageHandler handler)
        {
            if (!target.IsValid(out _))
            {
                return null;
            }

            // retry: null, for the same reason the Ollama arm composes no warm-up retry - waiting out
            // a 429 inside a 5s budget would spend it on one call and answer nothing.
            using var http = RemoteModelHttpClient.Create(retry: null, handler);
            var client = new OpenAI.Models.OpenAIModelClient(new ApiKeyCredential(target.ApiKey),
                RemoteModelHttpClient.OpenAIOptions(target, http));

            var models = await client.GetModelsAsync(budget);
            return NamesOnly(models?.Value?.Select(model => model.Id));
        }

        /// <summary>
        ///   Anthropic, through its own SDK (see <see cref="ReadOpenAIAsync" /> for why), composed
        ///   exactly as <see cref="AnthropicChatBackend" /> composes its client. First page only, at
        ///   the API's maximum size: a catalogue of a few dozen names does not need pagination, and
        ///   following pages inside a 5s budget would trade a complete answer for no answer.
        /// </summary>
        private static async Task<IReadOnlyList<ChatCatalogModel>> ReadAnthropicAsync(RemoteModelTarget target,
            CancellationToken budget, HttpMessageHandler handler)
        {
            if (!target.IsValid(out _))
            {
                return null;
            }

            using var http = RemoteModelHttpClient.Create(retry: null, handler);
            var client = new Anthropic.AnthropicClient
            {
                ApiKey = target.ApiKey,
                // The host root, unchanged: this SDK appends its own route. See EndpointRule.
                BaseUrl = target.Endpoint,
                MaxRetries = 0,
                Timeout = Timeout.InfiniteTimeSpan,
                HttpClient = http
            };

            var page = await client.Models.List(
                new Anthropic.Models.Models.ModelListParams { Limit = AnthropicPageSize }, budget);
            return NamesOnly(page?.Items?.Select(model => model.ID));
        }

        /// <summary>The mapping for a backend that publishes names and nothing else: capability,
        /// availability and class are all unknown, and saying so is more honest than inferring a kind
        /// from a name.</summary>
        private static IReadOnlyList<ChatCatalogModel> NamesOnly(IEnumerable<String> names)
        {
            return names?
                .Where(name => !String.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .Select(name => new ChatCatalogModel { Name = name })
                .ToList();
        }

        /// <summary>
        ///   The model KIND out of a backend's capability list. Embedding is tested first because it
        ///   is the value a picker filters on: an entry that somehow claimed both must not reach a
        ///   chat-model picker as a completion model.
        /// </summary>
        private static String CapabilityOf(List<String> capabilities)
        {
            if (capabilities == null)
            {
                return null;
            }

            if (capabilities.Any(c => String.Equals(c, EmbeddingCapability, StringComparison.OrdinalIgnoreCase)))
            {
                return EmbeddingCapability;
            }

            return capabilities.Any(c => String.Equals(c, CompletionCapability, StringComparison.OrdinalIgnoreCase))
                ? CompletionCapability
                : null;
        }

        /// <summary>One request, deserialized, or <c>null</c> for any non-success status. The request
        /// is disposed here because every caller builds it inline.</summary>
        [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
            Justification = "Trimming is disabled for this application; the wire DTOs are private and simple.")]
        private static async Task<T> ReadJsonAsync<T>(HttpClient http, HttpRequestMessage request,
            CancellationToken budget) where T : class
        {
            using (request)
            using (var response = await http.SendAsync(request, budget))
            {
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                using var stream = await response.Content.ReadAsStreamAsync(budget);
                return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: budget);
            }
        }

        #region the Ollama-protocol wire

        /// <summary>
        ///   The MINIMAL subset of <c>/api/tags</c> and <c>/api/show</c> this read consumes. Hand
        ///   written rather than taken from OllamaSharp because that library's model types carry
        ///   neither <c>nahil_class</c> nor <c>nahil_routable_now</c>, and those two are half of what
        ///   a picker has to show.
        /// </summary>
        private sealed class TagsWire
        {
            [JsonPropertyName("models")]
            public List<TagWire> Models
            {
                get; set;
            }
        }

        private sealed class TagWire
        {
            [JsonPropertyName("name")]
            public String Name
            {
                get; set;
            }
        }

        private sealed class ShowRequestWire
        {
            [JsonPropertyName("model")]
            public String Model
            {
                get; set;
            }
        }

        private sealed class ShowWire
        {
            /// <summary>Absent on an older Ollama sidecar, which is why capability stays nullable all
            /// the way out to the response.</summary>
            [JsonPropertyName("capabilities")]
            public List<String> Capabilities
            {
                get; set;
            }

            /// <summary>Nahil only: whether a worker can serve this model right now.</summary>
            [JsonPropertyName("nahil_routable_now")]
            public Boolean? RoutableNow
            {
                get; set;
            }

            /// <summary>Nahil only: an opaque class label (S1/S2/C1/C2 observed).</summary>
            [JsonPropertyName("nahil_class")]
            public String NahilClass
            {
                get; set;
            }
        }

        #endregion
    }
}
