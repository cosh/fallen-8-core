// MIT License
//
// AdminController.cs
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
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.App.Namespaces;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.App.Interfaces;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Algorithms.Analytics;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.Helper;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Plugin;
using NoSQL.GraphDB.Core.Plugins;
using NoSQL.GraphDB.Core.Serializer;
using NoSQL.GraphDB.Core.Service;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.App.Controllers
{
    [ApiController]
    [Route("api/v{version:apiVersion}/[controller]")]
    [ApiVersion("0.1")]
    public class AdminController : ControllerBase, IRESTService
    {
        #region Data

        /// <summary>
        ///   The internal Fallen-8 instance
        /// </summary>
        private readonly IFallen8 _fallen8;

        /// <summary>
        /// The Fallen-8 save path
        /// </summary>
        private readonly String _savePath;

        /// <summary>
        /// The Fallen-8 save file
        /// </summary>
        private readonly String _saveFile;

        /// <summary>
        /// The optimal number of partitions
        /// </summary>
        private readonly int _optimalNumberOfPartitions;

        private readonly ILogger<AdminController> _logger;

        /// <summary>
        /// The save-game metadata registry (feature save-games): records every save and auto-registers
        /// an unknown checkpoint on load.
        /// </summary>
        private readonly Services.SaveGameRegistry _saveGames;

        /// <summary>
        /// Whether an API key is configured, reported by /status (see StatusREST.ApiKeyRequired).
        /// </summary>
        private readonly Boolean _apiKeyConfigured;

        /// <summary>
        /// The embedding provider whose identity /status reports (see StatusREST.Embedding);
        /// null under direct unit construction.
        /// </summary>
        private readonly Embedding.Fallen8EmbeddingProvider _embeddingProvider;

        /// <summary>
        /// The chat gateway whose state /status and /config report (feature instance-config);
        /// null under direct unit construction.
        /// </summary>
        private readonly Chat.Fallen8ChatProvider _chatProvider;

        /// <summary>
        /// The observability posture reported read-only by /config (feature instance-config).
        /// </summary>
        private readonly Fallen8ObservabilityOptions _observability;

        /// <summary>
        /// The embedding config (feature instance-config): supplies the Ollama endpoint/model for
        /// the /config residency probe. Null under direct unit construction.
        /// </summary>
        private readonly Fallen8EmbeddingOptions _embeddingOptions;

        /// <summary>The ingestion pieces surfaced on /status (feature unstructured-ingestion);
        /// null under direct unit construction.</summary>
        private readonly Ingestion.IDoclingConverter _doclingConverter;
        private readonly Fallen8IngestionOptions _ingestionOptions;

        /// <summary>The NLP enrichment pieces surfaced on /status (feature semantic-layer);
        /// null under direct unit construction.</summary>
        private readonly Ingestion.INlpClient _nlpClient;
        private readonly Fallen8NlpOptions _nlpOptions;

        #endregion

        /// <summary>The namespace collection (feature graph-namespaces); null under direct unit
        /// construction, where every operation targets the one supplied engine.</summary>
        private readonly Fallen8Namespaces _namespaces;

        public AdminController(ILogger<AdminController> logger, IFallen8 fallen8, IOptions<Fallen8SecurityOptions> security,
            Services.SaveGameRegistry saveGames, Embedding.Fallen8EmbeddingProvider embeddingProvider = null,
            Fallen8Namespaces namespaces = null, IOptions<Fallen8DurabilityOptions> durability = null,
            Chat.Fallen8ChatProvider chatProvider = null, IOptions<Fallen8ObservabilityOptions> observability = null,
            IOptions<Fallen8EmbeddingOptions> embeddingOptions = null,
            Ingestion.IDoclingConverter doclingConverter = null, IOptions<Fallen8IngestionOptions> ingestionOptions = null,
            Ingestion.INlpClient nlpClient = null, IOptions<Fallen8NlpOptions> nlpOptions = null)
        {
            _embeddingProvider = embeddingProvider;
            _chatProvider = chatProvider;
            _observability = observability?.Value ?? new Fallen8ObservabilityOptions();
            _embeddingOptions = embeddingOptions?.Value;
            _doclingConverter = doclingConverter;
            _ingestionOptions = ingestionOptions?.Value;
            _nlpClient = nlpClient;
            _nlpOptions = nlpOptions?.Value;

            _namespaces = namespaces;

            _logger = logger;

            _fallen8 = fallen8;

            // The default save location honours Fallen8:Durability (StorageDirectory + CheckpointBaseName),
            // so an interactive PUT /save lands where the durability lifecycle saves and loads - on the
            // mounted data volume in a container, not the app's binary directory (which does not survive a
            // container recreation). Null under direct unit construction falls back to the historical
            // base-directory default.
            var durabilityOptions = durability?.Value ?? new Fallen8DurabilityOptions();
            _savePath = durabilityOptions.ResolveCheckpointPath();
            _saveFile = System.IO.Path.GetFileName(_savePath);

            _optimalNumberOfPartitions = Convert.ToInt32(Environment.ProcessorCount * 3 / 2);

            var securityOptions = security?.Value ?? new Fallen8SecurityOptions();
            _apiKeyConfigured = !String.IsNullOrWhiteSpace(securityOptions.ApiKey);

            _saveGames = saveGames;
        }

        #region IDisposable Members

        public void Dispose()
        {
        }

        #endregion

        /// <summary>
        /// Gets the current status of the Fallen-8 database
        /// </summary>
        /// <returns>Status information including counts, the current index inventory, available plugins and memory usage</returns>
        /// <response code="200">Returns the database status information</response>
        [HttpGet("/status")]
        [AllowAnonymous]
        [Produces("application/json")]
        [ProducesResponseType(typeof(StatusREST), StatusCodes.Status200OK)]
        public async Task<StatusREST> Status()
        {
            // WorkingSet64 (physical RAM in use), NOT VirtualMemorySize64: modern .NET reserves a
            // huge virtual address space (GC regions), so VirtualMemorySize64 reported hundreds of
            // GiB as "used memory" - meaningless. The working set is what an operator reads as the
            // process's memory (and matches /statistics' processWorkingSetBytes).
            var totalBytesOfMemoryUsed = Process.GetCurrentProcess().WorkingSet64;

            var vertexCount = _fallen8.VertexCount;
            var edgeCount = _fallen8.EdgeCount;

            IEnumerable<String> availableIndices;
            PluginFactory.TryGetAvailablePlugins<IIndex>(out availableIndices);

            // Built-in Path/Analytics names via the shared contract->interface home
            // (consolidation-audit CA-13); unioned with the registry's runtime plugins below.
            // Index and Service are not PluginContract members and stay generic.
            IEnumerable<String> availablePathAlgos = PluginFactory.AvailableBuiltInNames(PluginContract.Path);
            IEnumerable<String> availableAnalyticsAlgos = PluginFactory.AvailableBuiltInNames(PluginContract.Analytics);

            IEnumerable<String> availableServices;
            PluginFactory.TryGetAvailablePlugins<IService>(out availableServices);

            // Union the addressed namespace's runtime-registered algorithm plugins (feature
            // plugin-registration §4.4): a registered Path/Analytics plugin resolves by name, so it
            // must also be DISCOVERABLE in the available-plugin lists, not just invocable. Index has no
            // user-registrable category and functions have their own surface, so only these two lists
            // union. Capture the registry once (Dispose may null it under a concurrent teardown).
            var pluginRegistry = _fallen8.Plugins;
            if (pluginRegistry != null)
            {
                availablePathAlgos = availablePathAlgos
                    .Concat(pluginRegistry.NamesForContract(PluginContract.Path)).Distinct().ToList();
                availableAnalyticsAlgos = availableAnalyticsAlgos
                    .Concat(pluginRegistry.NamesForContract(PluginContract.Analytics)).Distinct().ToList();
            }

            // Read-locked snapshot (id -> index); O(#indices) plus the per-index counts
            // (see IndexDescriptionREST.Values), no graph pass. A bound vector index
            // (feature element-embeddings) also reports its embedding binding + model
            // identity so a client can mark it a self-maintained projection.
            var indices = new List<IndexDescriptionREST>();
            foreach (var kv in _fallen8.IndexFactory.GetNamedIndicesSnapshot())
            {
                var description = new IndexDescriptionREST
                {
                    IndexId = kv.Key,
                    PluginType = kv.Value?.PluginName,
                    Capabilities = IndexCapabilities.Describe(kv.Value),
                    Keys = NonNegativeCount(kv.Value?.CountOfKeys()),
                    Values = NonNegativeCount(kv.Value?.CountOfValues()),
                };
                if (kv.Value is NoSQL.GraphDB.Core.Index.Vector.IVectorIndex vectorIndex)
                {
                    description.EmbeddingName = vectorIndex.EmbeddingName;
                    description.Model = vectorIndex.Model;
                }
                indices.Add(description);
            }

            return new StatusREST
            {
                Indices = indices,
                AvailableIndexPlugins = new List<String>(availableIndices),
                AvailablePathPlugins = new List<String>(availablePathAlgos),
                // Subgraph algorithms are now discoverable too (feature: /status discovery parity with
                // path/analytics/index). The factory accessor unions the built-ins with the registry.
                AvailableSubGraphPlugins = new List<String>(_fallen8.SubGraphFactory.GetAvailableSubGraphPlugins()),
                AvailableAnalyticsPlugins = new List<String>(availableAnalyticsAlgos),
                AvailableServicePlugins = new List<String>(availableServices),
                EdgeCount = edgeCount,
                VertexCount = vertexCount,
                UsedMemory = totalBytesOfMemoryUsed,
                ApiKeyRequired = _apiKeyConfigured,
                Authenticated = HttpContext?.User?.Identity?.IsAuthenticated == true,
                Embedding = EmbeddingProviderStatsREST.From(_embeddingProvider),
                Chat = ChatProviderStatsREST.From(_chatProvider),
                Ingestion = await IngestionStatsREST.From(_ingestionOptions, _doclingConverter,
                    HttpContext?.RequestAborted ?? System.Threading.CancellationToken.None),
                Nlp = await NlpStatsREST.From(_nlpOptions, _nlpClient,
                    HttpContext?.RequestAborted ?? System.Threading.CancellationToken.None),
                Durability = DurabilityBlock(_fallen8.Durability),
            };
        }

        /// <summary>
        ///   Projects the engine's durability/recovery state onto the wire (feature
        ///   platform-integrity-audit W5). A straight field copy: the engine composes the state, and
        ///   duplicating any of its reasoning here would give the same question two answers.
        /// </summary>
        private static DurabilityStatusREST DurabilityBlock(DurabilityState state)
        {
            if (state == null)
            {
                return null;
            }

            return new DurabilityStatusREST
            {
                WalEnabled = state.WalEnabled,
                Degraded = state.Degraded,
                RecoveryRan = state.RecoveryRan,
                LastRecoveryTruncated = state.LastRecoveryTruncated,
                LastRecoveryReplayedEntries = state.LastRecoveryReplayedEntries,
                LastCheckpointDroppedIndices = state.LastCheckpointDroppedIndices,
            };
        }

        /// <summary>
        /// Gets the instance's read-only configuration (semantic providers + observability)
        /// </summary>
        /// <param name="cancellationToken">Aborts the best-effort GPU probe</param>
        /// <returns>The configuration view: embedding + chat providers and the observability posture</returns>
        /// <remarks>The operator view behind the Studio Configuration section (feature
        /// instance-config). Fallen-8-level and API-key gated like /statistics; config is
        /// startup-bound, so this is display-only. Secrets are never emitted - only the boolean
        /// apiKeyRequired reports the security posture, and the OTLP endpoint (operator config, not
        /// a secret) is shown as configured.</remarks>
        /// <response code="200">The configuration view</response>
        /// <response code="401">No valid credential was supplied (when an API key is configured)</response>
        [HttpGet("/config")]
        [Fallen8Level]
        [Produces("application/json")]
        [ProducesResponseType(typeof(ConfigREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public async Task<ConfigREST> Config(CancellationToken cancellationToken)
        {
            var embedding = EmbeddingProviderStatsREST.From(_embeddingProvider);
            var chat = ChatProviderStatsREST.From(_chatProvider);

            // Best-effort model-residency probe for Ollama-backed providers (feature instance-config):
            // is the configured model actually loaded in the sidecar right now? It uses a TRANSIENT
            // client (never the providers' lazy backends), so reading config never loads a model or
            // flips "loaded"; it is bounded so a hung/absent sidecar answers "unknown" (null) within
            // the timeout and never fails the read. Both providers probe concurrently.
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(3));

                async Task ProbeAsync(String endpoint, String model, Action<Boolean?, Boolean?> assign)
                {
                    if (String.IsNullOrWhiteSpace(endpoint) || String.IsNullOrWhiteSpace(model))
                    {
                        return;
                    }

                    var state = await Chat.OllamaModelProbe.ProbeAsync(endpoint, model, cts.Token);
                    assign(state?.Resident, state?.Gpu);
                }

                var probes = new System.Collections.Generic.List<Task>();
                if (chat != null && chat.Enabled && _chatProvider != null)
                {
                    probes.Add(ProbeAsync(_chatProvider.OllamaEndpoint, _chatProvider.OllamaModel,
                        (resident, gpu) => { chat.Resident = resident; chat.Gpu = gpu; }));
                }
                if (embedding != null && embedding.Enabled && _embeddingOptions != null &&
                    String.Equals(embedding.Backend, "Ollama", StringComparison.OrdinalIgnoreCase))
                {
                    probes.Add(ProbeAsync(_embeddingOptions.Ollama?.Endpoint, _embeddingOptions.Ollama?.Model,
                        (resident, gpu) => { embedding.Resident = resident; embedding.Gpu = gpu; }));
                }

                await Task.WhenAll(probes);
            }

            return new ConfigREST
            {
                Semantic = new SemanticConfigREST
                {
                    Embedding = embedding,
                    Chat = chat,
                },
                Observability = ObservabilityConfigREST.From(_observability),
                ApiKeyRequired = _apiKeyConfigured,
            };
        }

        /// <summary>
        /// An engine count sentinel (negative = "not supported", e.g. the spatial R-Tree's
        /// CountOfKeys) surfaces as null on the inventory, never as a fake count.
        /// </summary>
        private static Int32? NonNegativeCount(Int32? count)
        {
            return count >= 0 ? count : null;
        }

        /// <summary>The namespace this request addresses: the "ns" route value on a twin route,
        /// "default" on a bare one (feature graph-namespaces).</summary>
        private String AddressedNamespaceName()
        {
            return HttpContext?.Request.RouteValues[NamespaceRouteConvention.RouteParameterName] as String
                   ?? Fallen8Namespaces.DefaultName;
        }

        /// <summary>The addressed namespace's immutable id (what save-game members are keyed by);
        /// the default's stable id under direct unit construction.</summary>
        private String AddressedNamespaceId()
        {
            return _namespaces != null && _namespaces.TryGet(AddressedNamespaceName(), out var ns)
                ? ns.Id
                : Fallen8Namespaces.DefaultId;
        }

        /// <summary>
        /// The addressed namespace's default save location: the legacy path for "default" (and
        /// under direct unit construction), the id-keyed namespace directory otherwise.
        /// </summary>
        private String DefaultSavePath()
        {
            if (_namespaces == null || !_namespaces.TryGet(AddressedNamespaceName(), out var ns)
                || ReferenceEquals(ns, _namespaces.Default))
            {
                return _savePath;
            }

            return System.IO.Path.Combine(EnsuredNamespaceDirectory(ns), _saveFile);
        }

        private String EnsuredNamespaceDirectory(Namespaces.Namespace ns)
        {
            var directory = _namespaces.DirectoryFor(ns);
            System.IO.Directory.CreateDirectory(directory);
            return directory;
        }

        /// <summary>
        /// Trims the database, releasing unused memory
        /// </summary>
        /// <response code="200">Trim operation successfully enqueued (this void action returns 200 with an empty body, not 204)</response>
        [HttpHead("/trim")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public void Trim()
        {
            TrimTransaction tx = new TrimTransaction();

            _fallen8.EnqueueTransaction(tx);
        }

        /// <summary>
        /// Loads a Fallen-8 database from a saved file
        /// </summary>
        /// <param name="definition">Load specification including file path and service start options</param>
        /// <remarks>
        /// Sample request:
        ///
        ///     PUT /load
        ///     {
        ///        "startServices": true,
        ///        "saveGameLocation": "C:/Fallen8/database.f8s"
        ///     }
        /// </remarks>
        /// <response code="204">Database loaded successfully</response>
        /// <response code="400">Invalid load specification or file not found</response>
        /// <response code="500">The load transaction was rolled back and did not complete</response>
        [HttpPut("/load")]
        [Consumes("application/json")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async System.Threading.Tasks.Task<IActionResult> Load([FromBody] LoadSpecification definition)
        {
            // Null-guard the body so a JSON `null` yields a 400 (matching the documented 400 and the
            // Save/AddVertex/... siblings) rather than an NRE surfaced as a 500.
            if (definition == null)
            {
                return ProblemResults.BadRequest("A load specification is required.");
            }

            // Pre-flight the checkpoint file exactly as the engine does (PersistencyFactory.Load
            // returns false for a missing path, which the load transaction does NOT turn into a
            // rollback). Without this, a typo'd path answered 204 and was then recorded as the
            // namespace's NEWEST save game below, which aborts the next startup. This is the
            // documented 400 "file not found", and the twin of the SaveGamesController pre-flight.
            if (String.IsNullOrWhiteSpace(definition.SaveGameLocation))
            {
                return ProblemResults.BadRequest("A save game location is required.");
            }

            if (!System.IO.File.Exists(definition.SaveGameLocation))
            {
                return ProblemResults.BadRequest(String.Format(
                    "The save game location \"{0}\" does not exist; nothing was loaded.",
                    definition.SaveGameLocation));
            }

            _logger.LogInformation(String.Format("Loading Fallen-8. Start services: {0}", definition.StartServices));

            LoadTransaction tx = new LoadTransaction();
            tx.Path = definition.SaveGameLocation;
            tx.StartServices = definition.StartServices;

            var transactionTask = _fallen8.EnqueueTransaction(tx);
            await transactionTask.Completion;

            // A rolled-back load must not be reported to the client as success (correctness-fixes B6).
            if (transactionTask.TransactionState == TransactionState.RolledBack)
            {
                return ProblemResults.InternalServerError(
                    "The load transaction was rolled back; the database was not loaded.");
            }

            // A checkpoint loaded from an arbitrary path that is not yet in the registry is recorded
            // now (feature save-games FR-7), so the historical record captures manually-loaded saves.
            try
            {
                _saveGames.RegisterImportIfUnknown(AddressedNamespaceName(), AddressedNamespaceId(), _fallen8,
                    definition.SaveGameLocation);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The loaded checkpoint could not be registered in the save-game registry.");
            }

            return NoContent();
        }

        /// <summary>
        /// Saves the current database state to a file
        /// </summary>
        /// <param name="definition">Save specification including file path and partition options (both optional)</param>
        /// <returns>The created save-game registry entry (including its path)</returns>
        /// <remarks>
        /// Sample request:
        ///
        ///     PUT /save
        ///     {
        ///        "saveGameLocation": "C:/Fallen8/database.f8s",
        ///        "savePartitions": 8
        ///     }
        ///
        /// Both parameters are optional. If not provided, the save goes to the configured durability
        /// storage directory (Fallen8:Durability:StorageDirectory; the app base directory when unset)
        /// using the CheckpointBaseName ("Temp.f8s" by default) and the optimal partition count.
        /// The save is recorded in the save-game registry (feature save-games); the response is the
        /// created entry, whose "location" field is the path the database was saved to.
        /// </remarks>
        /// <response code="200">Returns the created save-game registry entry</response>
        /// <response code="400">Invalid save specification</response>
        /// <response code="500">The save transaction was rolled back and did not complete</response>
        [HttpPut("/save")]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(SaveGameREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async System.Threading.Tasks.Task<IActionResult> Save([FromBody] SaveSpecification definition)
        {
            // Use provided path or fall back to the addressed namespace's default location
            string savePath = !string.IsNullOrWhiteSpace(definition?.SaveGameLocation)
                ? definition.SaveGameLocation
                : DefaultSavePath();

            // Use provided partitions or fall back to optimal
            int savePartitions = definition?.SavePartitions ?? _optimalNumberOfPartitions;

            SaveTransaction saveTx = new SaveTransaction() { Path = savePath, SavePartitions = savePartitions };
            var transactionTask = _fallen8.EnqueueTransaction(saveTx);
            await transactionTask.Completion;

            // A rolled-back save must not be reported to the client as success (correctness-fixes B6).
            if (transactionTask.TransactionState == TransactionState.RolledBack)
            {
                return ProblemResults.InternalServerError(
                    "The save transaction was rolled back; the database was not saved.");
            }

            // Record the successful save in the registry and return the entry (feature save-games FR-4).
            // The checkpoint is already physically written; a registry failure must NOT turn a
            // successful save into a 500. Fall back to a best-effort entry describing the save.
            try
            {
                return Ok(_saveGames.Register(AddressedNamespaceName(), AddressedNamespaceId(), _fallen8,
                    saveTx.ActualPath, "api"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The save to \"{Path}\" succeeded but could not be registered in the save-game registry.", saveTx.ActualPath);
                return Ok(new SaveGameREST
                {
                    SavedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    Trigger = "api",
                    Location = saveTx.ActualPath,
                });
            }
        }

        /// <summary>
        /// Erases the addressed namespace's data (the namespace stays registered, empty)
        /// </summary>
        /// <remarks>
        /// Bare /tabularasa erases the "default" namespace; /ns/{ns}/tabularasa erases that
        /// namespace. The Fallen-8-wide factory reset is HEAD /tabularasa/all.
        /// </remarks>
        /// <response code="200">Namespace clear successfully enqueued (this void action returns 200 with an empty body, not 204)</response>
        [HttpHead("/tabularasa")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public void TabulaRasa()
        {
            TabulaRasaTransaction tx = new TabulaRasaTransaction();

            _fallen8.EnqueueTransaction(tx);
        }

        /// <summary>
        /// Factory reset: drops every non-default namespace and erases "default" (Fallen-8-level — all namespaces)
        /// </summary>
        /// <remarks>
        /// Irreversible. Dropped namespaces lose their on-disk data; save-game entries remain
        /// valid restore points. Afterwards only an empty "default" namespace exists.
        /// </remarks>
        /// <response code="204">All namespaces erased</response>
        /// <response code="429">The sensitive-endpoint rate limit was exceeded</response>
        [Fallen8Level]
        [HttpHead("/tabularasa/all")]
        [EnableRateLimiting(Fallen8SecurityOptions.SensitiveRateLimitPolicy)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public IActionResult TabulaRasaAll()
        {
            foreach (var ns in _namespaces.Snapshot())
            {
                if (!ReferenceEquals(ns, _namespaces.Default))
                {
                    _namespaces.TryDrop(ns.Name, out _);
                }
            }

            _namespaces.Default.Engine.EnqueueTransaction(new TabulaRasaTransaction());
            return NoContent();
        }

        /// <summary>
        /// Saves every namespace into one save-game entry (Fallen-8-level — all namespaces)
        /// </summary>
        /// <returns>The created save-game registry entry spanning all namespaces</returns>
        /// <remarks>
        /// One consistent restore point for the whole Fallen-8: each namespace is checkpointed to
        /// its own default location and the registry records a single entry whose "namespaces"
        /// manifest lists every member. Restore the whole entry - or a single namespace out of it -
        /// via PUT /savegames/{id}/load.
        /// </remarks>
        /// <response code="200">Returns the created save-game registry entry</response>
        /// <response code="429">The sensitive-endpoint rate limit was exceeded</response>
        /// <response code="500">At least one namespace's save failed (the body names it; successfully saved namespaces are still registered)</response>
        [Fallen8Level]
        [HttpPut("/save/all")]
        [EnableRateLimiting(Fallen8SecurityOptions.SensitiveRateLimitPolicy)]
        [ProducesResponseType(typeof(SaveGameREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async System.Threading.Tasks.Task<IActionResult> SaveAll()
        {
            var members = new List<(String Name, String Id, IFallen8 Engine, String Location)>();
            var failed = new List<String>();

            foreach (var ns in _namespaces.Snapshot())
            {
                var savePath = ReferenceEquals(ns, _namespaces.Default)
                    ? _savePath
                    : System.IO.Path.Combine(EnsuredNamespaceDirectory(ns), _saveFile);

                // Per-namespace containment (mirrors the shutdown save): a namespace dropped
                // mid-loop throws on its disposed engine, and one failure must neither abort the
                // sweep nor un-register the checkpoints already written.
                try
                {
                    var saveTx = new SaveTransaction { Path = savePath, SavePartitions = _optimalNumberOfPartitions };
                    var task = ns.Engine.EnqueueTransaction(saveTx);
                    await task.Completion;

                    if (task.TransactionState == TransactionState.RolledBack)
                    {
                        _logger.LogError(task.Error, "The save of namespace \"{Namespace}\" rolled back during PUT /save/all.", ns.Name);
                        failed.Add(ns.Name);
                    }
                    else
                    {
                        members.Add((ns.Name, ns.Id, ns.Engine, saveTx.ActualPath ?? savePath));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "The save of namespace \"{Namespace}\" threw during PUT /save/all (dropped mid-sweep?).", ns.Name);
                    failed.Add(ns.Name);
                }
            }

            SaveGameREST entry = null;
            if (members.Count > 0)
            {
                try
                {
                    entry = _saveGames.RegisterAll(members, "api");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "The save/all checkpoints were written but could not be registered in the save-game registry.");
                }
            }

            if (failed.Count > 0)
            {
                return Helper.ProblemResults.Create(StatusCodes.Status500InternalServerError, "Save incomplete",
                    "The save transaction rolled back for: " + String.Join(", ", failed) +
                    ". Successfully saved namespaces were registered" + (entry != null ? " as " + entry.Id : "") + ".",
                    p => p.Extensions["failedNamespaces"] = failed);
            }

            return Ok(entry);
        }

        /// <summary>
        /// Gets the total number of vertices in the database
        /// </summary>
        /// <returns>Count of vertices in the database</returns>
        /// <response code="200">Returns the number of vertices</response>
        [HttpGet("/vertex/count")]
        [AllowAnonymous]
        [Produces("application/json")]
        [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
        public int VertexCount()
        {
            return _fallen8.VertexCount;
        }

        /// <summary>
        /// Gets the total number of edges in the database
        /// </summary>
        /// <returns>Count of edges in the database</returns>
        /// <response code="200">Returns the number of edges</response>
        [HttpGet("/edge/count")]
        [AllowAnonymous]
        [Produces("application/json")]
        [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
        public int EdgeCount()
        {
            return _fallen8.EdgeCount;
        }

        /// <summary>
        /// Creates a new service based on the specified plugin
        /// </summary>
        /// <param name="definition">Plugin specification including type, ID and options</param>
        /// <returns>True if service was successfully created, false otherwise</returns>
        /// <response code="200">Returns true if the service was created; false if the plugin type is unknown or the id already exists</response>
        /// <response code="400">The request body was missing or malformed</response>
        [HttpPost("/service")]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public bool CreateService([FromBody] PluginSpecification definition)
        {
            IService service;
            return _fallen8.ServiceFactory.TryAddService(out service, definition.PluginType, definition.UniqueId, ServiceHelper.CreatePluginOptions(definition.PluginOptions));
        }

        /// <summary>
        /// Deletes a service with the specified key
        /// </summary>
        /// <param name="key">The unique identifier of the service to delete</param>
        /// <returns>True if the service was successfully deleted, false if it wasn't found</returns>
        /// <response code="200">Returns whether the service was successfully deleted</response>
        [HttpDelete("/service/{key}")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(bool), StatusCodes.Status200OK)]
        public bool DeleteService([FromRoute] string key)
        {
            // The factory owns the stop-then-remove sequence and the write lock that every other
            // mutation of its dictionary takes; see ServiceFactory.TryRemoveService.
            return _fallen8.ServiceFactory.TryRemoveService(key);
        }

        // The runtime plugin-DLL upload endpoint (PUT /plugin) was REMOVED (feature
        // plugin-registration): loading an opaque external assembly in-process was the most dangerous
        // surface in the product. It is replaced by typed, source-based, namespace-scoped registration
        // under /plugins/* (PluginsController) - the server now compiles and contract-validates C#
        // source instead of loading an unvalidated binary. See docs/plugin-registration.md.

        #region private helper

        // Checkpoint discovery moved to NoSQL.GraphDB.App.Helper.CheckpointDiscovery and is now driven
        // by the hosted DurabilityLifecycleService (feature hosted-durability-lifecycle). The former
        // private FindLatestFallen8 was dead code (never called) and has been removed.

        #endregion

        #region not implemented

        [NonAction]
        public void Save(SerializationWriter writer)
        {
        }

        [NonAction]
        public void Load(SerializationReader reader, IFallen8 fallen8)
        {
        }

        [NonAction]
        public void Shutdown()
        {
        }

        #endregion
    }
}
