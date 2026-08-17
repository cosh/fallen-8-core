// MIT License
//
// Fallen8Namespaces.cs
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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.ChangeFeed;
using NoSQL.GraphDB.Core.Persistency;

namespace NoSQL.GraphDB.App.Namespaces
{
    /// <summary>
    ///   The entire collection of namespaces behind one endpoint — the Fallen-8 itself (feature
    ///   graph-namespaces; the living doc is the feature README). Owns one Fallen-8 engine per
    ///   namespace, always holding the reserved <see cref="DefaultName"/> on the legacy storage
    ///   paths; namespacing is a hosting concern and the engine itself is unchanged.
    ///
    ///   Engine construction supplies both compilers AT CONSTRUCTION: an unanchored write-ahead
    ///   log replays during construction, so only compilers present then can recompile its
    ///   CreateSubGraph / RegisterStoredQuery entries. Volatile mode constructs the plain
    ///   in-memory engine.
    /// </summary>
    public sealed class Fallen8Namespaces : IDisposable
    {
        #region constants

        /// <summary>The reserved namespace bare URLs address; it cannot be renamed or dropped.</summary>
        public const String DefaultName = "default";

        /// <summary>
        ///   The default namespace's STABLE id. Every other namespace gets a generated immutable
        ///   id, but the default is reborn on every boot — a generated id would change across
        ///   restarts and break everything keyed by namespace id (the save-game boot chain, metric
        ///   continuity). "default" is system-chosen, so the no-user-input tag invariant holds.
        /// </summary>
        public const String DefaultId = "default";

        /// <summary>The maximum name length accepted by <see cref="IsValidName"/>.</summary>
        public const Int32 MaxNameLength = 63;

        /// <summary>
        ///   Directory (under the durability storage directory) that holds the per-namespace
        ///   storage, keyed by the immutable namespace id — never by the user-supplied name.
        /// </summary>
        private const String NamespacesDirectoryName = "namespaces";

        /// <summary>The catalog file inside the metadata directory.</summary>
        public const String CatalogFileName = "namespaces.json";

        /// <summary>The per-namespace write-ahead-log file name (the live state a drop deletes).</summary>
        private const String WalFileName = "fallen8.wal";

        #endregion

        #region fields

        private readonly ConcurrentDictionary<String, Namespace> _byName =
            new ConcurrentDictionary<String, Namespace>(StringComparer.Ordinal);

        /// <summary>Serializes create/rename/drop so quota and conflict checks are atomic.</summary>
        private readonly Object _writeLock = new Object();

        /// <summary>Serializes disposal against shutdown work (see <see cref="TryRunBeforeDispose"/>).</summary>
        private readonly Object _disposeGate = new Object();
        private Boolean _disposed;

        /// <summary>
        ///   One load gate per namespace ID (feature namespace-startup-load §4.8): the mutex
        ///   <see cref="ActivateAsync"/> holds across engine construction, restore and publication.
        ///   See that method for why the gate is per namespace and never <see cref="_writeLock"/>.
        ///   Keyed by the IMMUTABLE id, because the id - not the renameable name - is what the
        ///   on-disk write-ahead log is keyed by, and the log is what must never get a second
        ///   engine. An entry is removed ONLY by <see cref="TryDrop"/>, once that id is retired for
        ///   good; never for a live namespace, because removing one under contention would hand two
        ///   concurrent activations two different gate objects, which is the exact race this closes.
        ///   The bound that follows: at most one entry per namespace that has been activated and not
        ///   dropped, so the dictionary is capped by <see cref="MaxNamespaces"/> rather than by how
        ///   often anyone activates.
        ///   <para>A <see cref="SemaphoreSlim"/> rather than a <c>lock</c> because the critical
        ///   section AWAITS the restore: a monitor cannot be held across an await, and blocking a
        ///   request thread for a seconds-long load is exactly what the apiApp's
        ///   no-WaitUntilFinished rule exists to prevent.</para>
        /// </summary>
        private readonly ConcurrentDictionary<String, SemaphoreSlim> _loadGates =
            new ConcurrentDictionary<String, SemaphoreSlim>(StringComparer.Ordinal);

        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<Fallen8Namespaces> _logger;
        private readonly Fallen8DurabilityOptions _durability;
        private readonly ChangeFeedOptions _changeFeedOptions;
        private readonly Int32 _storedQueryMaxCount;
        private readonly Int32 _pluginMaxCount;

        /// <summary>The startup-load selection this boot ran with (feature namespace-startup-load).</summary>
        private readonly Boolean _loadOnStartupDefault;
        private readonly NamespaceStartupLoadMode _startupLoadMode;

        /// <summary>The catalog file path; null in volatile mode (nothing is cataloged).</summary>
        private readonly String _catalogPath;

        private static readonly JsonSerializerOptions _json = new JsonSerializerOptions
        {
            WriteIndented = true,
        };

        private const String CreatedAtFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";

        #endregion

        #region constructor

        public Fallen8Namespaces(ILoggerFactory loggerFactory,
            IOptions<Fallen8DurabilityOptions> durability,
            IOptions<Fallen8StoredQueryOptions> storedQueries,
            IOptions<Fallen8ChangeFeedOptions> changeFeed,
            IOptions<Fallen8NamespacesOptions> namespaces,
            IOptions<Fallen8MetadataOptions> metadata,
            IOptions<Fallen8PluginOptions> plugins)
        {
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<Fallen8Namespaces>();
            _durability = durability.Value;
            _changeFeedOptions = changeFeed.Value.ToEngineOptions();
            _storedQueryMaxCount = storedQueries.Value.MaxCount;
            _pluginMaxCount = plugins.Value.MaxCount;
            MaxNamespaces = namespaces.Value.MaxNamespaces;
            _loadOnStartupDefault = namespaces.Value.LoadOnStartup;
            _startupLoadMode = namespaces.Value.StartupLoadMode;

            // The default namespace boots eagerly on the LEGACY paths (the storage directory and
            // WAL location the single-engine host used), so existing deployments upgrade in place
            // with zero migration.
            String defaultWalPath = null;
            if (!_durability.Volatile)
            {
                // Ensure the storage directory exists BEFORE the engine opens the WAL there; a missing
                // or unwritable directory must fail loudly at startup, never silently degrade to volatile.
                Directory.CreateDirectory(_durability.ResolveStorageDirectory());
                defaultWalPath = _durability.ResolveWalPath();
                _catalogPath = Path.Combine(metadata.Value.ResolveDirectory(), CatalogFileName);
            }

            Default = new Namespace(DefaultName, DefaultId, CreateEngine(defaultWalPath, DefaultId), DateTime.UtcNow)
            {
                // The reserved default namespace can never be excluded (spec §4.9), by catalog or by
                // config: every bare URL aliases it. A fixed true, not an inherited null, so the REST
                // surface reports the policy actually in force rather than one it does not follow.
                LoadOnStartupEnabled = true,
            };
            _byName[DefaultName] = Default;

            // Boot every SELECTED cataloged namespace, each on its id-keyed directory: its engine
            // constructor replays that namespace's unanchored WAL exactly like the single engine
            // always has; checkpoint loading follows in DurabilityLifecycleService.StartAsync.
            // Semantically bad entries are SKIPPED LOUDLY (an unreadable catalog still throws): a
            // "default"-named entry would split-brain the bare alias against /ns/default, and a
            // duplicate/invalid name would silently overwrite (leaking an engine + WAL handle).
            var catalog = LoadCatalog();
            // The default namespace's plugin-registration override (feature plugin-registration) is
            // stored on the document, since default has no catalog entry.
            Default.PluginRegistrationEnabled = catalog.DefaultPluginRegistrationEnabled;

            var loaded = 0;
            var skipped = 0;
            foreach (var entry in catalog.Namespaces)
            {
                if (String.Equals(entry.Name, DefaultName, StringComparison.Ordinal)
                    || !IsValidName(entry.Name)
                    || String.IsNullOrEmpty(entry.Id)
                    || _byName.ContainsKey(entry.Name))
                {
                    _logger.LogError("The namespace catalog entry {{ id: \"{Id}\", name: \"{Name}\" }} is invalid " +
                        "(reserved/duplicate/malformed) and was SKIPPED; its on-disk data (if any) is untouched. " +
                        "Repair \"{CatalogPath}\" to restore it.", entry.Id, entry.Name, _catalogPath);
                    continue;
                }

                Directory.CreateDirectory(ResolveNamespaceDirectory(entry.Id));
                var createdAt = DateTime.TryParseExact(entry.CreatedAt, CreatedAtFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var stamp)
                    ? stamp
                    : DateTime.UtcNow;

                // THE BOOT DECISION (feature namespace-startup-load §4.3/§4.4): taken BEFORE
                // CreateEngine, because "do not load this namespace" reduces to "do not call new
                // Fallen8". A namespace that is not selected still enters the collection, with a null
                // engine - residency is a property of the ENTRY, never of membership (see the field
                // doc on Namespace._engine for what leaving the collection would destroy).
                var selected = IsSelectedForStartupLoad(entry, out var reason);
                var ns = new Namespace(entry.Name, entry.Id,
                    selected ? CreateEngine(ResolveNamespaceWalPath(entry.Id), entry.Id) : null, createdAt)
                {
                    PluginRegistrationEnabled = entry.PluginRegistrationEnabled,
                    LoadOnStartupEnabled = entry.LoadOnStartupEnabled,
                };
                _byName[entry.Name] = ns;

                if (selected)
                {
                    loaded++;
                    _logger.LogInformation("Namespace \"{Name}\" ({Id}) is LOADED at startup ({Reason}).",
                        entry.Name, entry.Id, reason);
                }
                else
                {
                    skipped++;
                    _logger.LogInformation("Namespace \"{Name}\" ({Id}) is NOT loaded at startup ({Reason}); its " +
                        "checkpoint and write-ahead log are left untouched and it stays cataloged - it answers the " +
                        "namespace management routes, and refuses data requests with 503 until it is loaded.",
                        entry.Name, entry.Id, reason);
                }
            }

            if (skipped > 0)
            {
                // A selection is never a silent no-op (spec §4.3). One that loads NOTHING but the
                // reserved default is the shape an operator gets wrong (a stale mode, an inverted
                // global default), so it is a warning rather than a note.
                _logger.Log(loaded == 0 ? LogLevel.Warning : LogLevel.Information,
                    "Fallen-8 startup load selected {Loaded} of {Total} cataloged namespaces ({Skipped} skipped) with " +
                    "Fallen8:Namespaces:StartupLoadMode={Mode} and LoadOnStartup={LoadOnStartup}. The reserved " +
                    "\"default\" namespace is always loaded. Set StartupLoadMode=All to load every cataloged " +
                    "namespace regardless of its own policy.",
                    loaded, loaded + skipped, skipped, _startupLoadMode, _loadOnStartupDefault);
            }
        }

        /// <summary>
        ///   Whether this boot loads <paramref name="entry"/>, and the reason in the operator's
        ///   words (feature namespace-startup-load §4.2). The mode is an escape hatch over the
        ///   persisted policy, never a second source of truth: it does not rewrite the catalog.
        /// </summary>
        private Boolean IsSelectedForStartupLoad(NamespaceCatalogEntry entry, out String reason)
        {
            switch (_startupLoadMode)
            {
                case NamespaceStartupLoadMode.All:
                    reason = "Fallen8:Namespaces:StartupLoadMode=All ignores every exclusion";
                    return true;

                case NamespaceStartupLoadMode.DefaultOnly:
                    reason = "Fallen8:Namespaces:StartupLoadMode=DefaultOnly loads nothing but \"default\"";
                    return false;

                default:
                    if (entry.LoadOnStartupEnabled.HasValue)
                    {
                        reason = "its own catalog policy loadOnStartupEnabled=" +
                            (entry.LoadOnStartupEnabled.Value ? "true" : "false");
                        return entry.LoadOnStartupEnabled.Value;
                    }

                    reason = "it inherits Fallen8:Namespaces:LoadOnStartup=" + (_loadOnStartupDefault ? "true" : "false");
                    return _loadOnStartupDefault;
            }
        }

        #endregion

        #region public properties

        /// <summary>The reserved default namespace (always present).</summary>
        public Namespace Default { get; }

        /// <summary>The configured namespace ceiling (includes <see cref="DefaultName"/>).</summary>
        public Int32 MaxNamespaces { get; }

        /// <summary>The number of namespaces, including <see cref="DefaultName"/>.</summary>
        public Int32 Count
        {
            get { return _byName.Count; }
        }

        #endregion

        #region public methods

        /// <summary>
        ///   Validates a namespace name. Names are permissive by design — any case, digits,
        ///   spaces, punctuation, and Unicode are allowed — because on-disk storage is keyed by
        ///   the immutable namespace id, not the name, so a name is only ever a display label, a
        ///   dictionary key, and a URL PATH SEGMENT (<c>/ns/{name}/…</c> and Studio's
        ///   <c>/q/{name}/…</c>). That last role fixes the only hard limits:
        ///   <list type="bullet">
        ///     <item><c>/</c> and <c>\</c> break segment boundaries — an encoded slash
        ///       (<c>%2F</c>) is rejected by Kestrel before routing, so it can never round-trip.</item>
        ///     <item>Control characters are never valid in a URL.</item>
        ///     <item><c>.</c> and <c>..</c> are path-traversal tokens: <c>/ns/..</c> normalizes to
        ///       <c>/ns</c> and would misroute, so the whole-name forms are rejected.</item>
        ///     <item>Leading/trailing whitespace is ambiguous (" x" vs "x") and is rejected;
        ///       length is capped at <see cref="MaxNameLength"/>.</item>
        ///   </list>
        ///   Everything else is accepted; comparison is ordinal (so names are case-sensitive,
        ///   matching URL-path semantics).
        /// </summary>
        public static Boolean IsValidName(String name)
        {
            if (String.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
            {
                return false;
            }

            if (name != name.Trim() || name == "." || name == "..")
            {
                return false;
            }

            foreach (var c in name)
            {
                if (c == '/' || c == '\\' || Char.IsControl(c))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Gets the namespace registered under <paramref name="name"/>.</summary>
        public Boolean TryGet(String name, out Namespace ns)
        {
            return _byName.TryGetValue(name, out ns);
        }

        /// <summary>A name-ordered snapshot of all namespaces.</summary>
        public List<Namespace> Snapshot()
        {
            return _byName.Values.OrderBy(n => n.Name, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        ///   Creates a new, empty namespace. Fails with <see cref="NamespaceFailure.InvalidName"/>,
        ///   <see cref="NamespaceFailure.Conflict"/> (name in use, including <see cref="DefaultName"/>),
        ///   or <see cref="NamespaceFailure.QuotaExceeded"/> (<see cref="MaxNamespaces"/> reached).
        /// </summary>
        public Boolean TryCreate(String name, out Namespace ns, out NamespaceFailure failure)
        {
            ns = null;

            if (!IsValidName(name))
            {
                failure = NamespaceFailure.InvalidName;
                return false;
            }

            lock (_writeLock)
            {
                if (_byName.ContainsKey(name))
                {
                    failure = NamespaceFailure.Conflict;
                    return false;
                }

                if (_byName.Count >= MaxNamespaces)
                {
                    failure = NamespaceFailure.QuotaExceeded;
                    return false;
                }

                var createdAt = DateTime.UtcNow;
                var id = NewId(createdAt);
                String walPath = null;
                if (!_durability.Volatile)
                {
                    Directory.CreateDirectory(ResolveNamespaceDirectory(id));
                    walPath = ResolveNamespaceWalPath(id);
                }

                ns = new Namespace(name, id, CreateEngine(walPath, id), createdAt);

                // Catalog BEFORE publishing: were the namespace routable first, a concurrent
                // request could commit writes into an engine a failing catalog write then
                // destroys. Disk truth leads; memory follows.
                try
                {
                    WriteCatalogUnlocked(_byName.Values.Concat(new[] { ns }));
                }
                catch
                {
                    DisposeEngineOnce(ns);
                    ns = null;
                    throw;
                }

                _byName[name] = ns;
            }

            _logger.LogInformation("Created namespace \"{Name}\" ({Id}).", ns.Name, ns.Id);
            failure = NamespaceFailure.None;
            return true;
        }

        /// <summary>
        ///   Renames a namespace — a pure metadata operation: the engine, id, and on-disk
        ///   locations are untouched. <see cref="DefaultName"/> cannot be renamed
        ///   (<see cref="NamespaceFailure.Reserved"/>).
        /// </summary>
        public Boolean TryRename(String name, String newName, out Namespace ns, out NamespaceFailure failure)
        {
            // This name check LOOKS redundant with the one inside TryUpdate, and is not: TryUpdate
            // treats a null-or-empty NewName as "leave the name alone", so an empty newName would
            // arrive there as an EMPTY update and be answered with a no-op success. A caller that
            // asked to rename must never be told it worked when nothing was renamed, so the invalid
            // name is rejected here, where "rename" is still the known intent.
            if (!IsValidName(newName))
            {
                ns = null;
                failure = NamespaceFailure.InvalidName;
                return false;
            }

            return TryUpdate(name, new NamespaceUpdate { NewName = newName }, out ns, out failure);
        }

        /// <summary>
        ///   Applies one <see cref="NamespaceUpdate"/> - a rename and/or either persisted override -
        ///   as a SINGLE atomic change: every field mutates under <see cref="_writeLock"/>, one
        ///   catalog write persists all of them, and a failed write rolls every field back together.
        ///   Field-at-a-time methods used to write the catalog once per field, so a second write
        ///   failing left the first field persisted while the caller was told the request was
        ///   rejected (audit-defects, the follow-up to B31).
        ///   <para>The reserved <c>default</c> namespace can be neither renamed nor excluded from the
        ///   startup load (<see cref="NamespaceFailure.Reserved"/>), but its plugin-registration
        ///   override IS settable - it persists on the catalog document rather than on an entry.</para>
        /// </summary>
        public Boolean TryUpdate(String name, NamespaceUpdate update, out Namespace ns, out NamespaceFailure failure)
        {
            ns = null;
            var renaming = !String.IsNullOrEmpty(update.NewName);

            if (renaming && !IsValidName(update.NewName))
            {
                failure = NamespaceFailure.InvalidName;
                return false;
            }

            if (String.Equals(name, DefaultName, StringComparison.Ordinal)
                && (renaming || update.LoadOnStartupSupplied))
            {
                failure = NamespaceFailure.Reserved;
                return false;
            }

            lock (_writeLock)
            {
                if (!_byName.TryGetValue(name, out ns))
                {
                    ns = null;
                    failure = NamespaceFailure.NotFound;
                    return false;
                }

                if (renaming && _byName.ContainsKey(update.NewName))
                {
                    ns = null;
                    failure = NamespaceFailure.Conflict;
                    return false;
                }

                if (update.IsEmpty)
                {
                    // Nothing to persist: never spend a durable catalog write on a no-op (the
                    // controller rejects an empty request; a programmatic caller gets the namespace).
                    failure = NamespaceFailure.None;
                    return true;
                }

                var previousPluginRegistration = ns.PluginRegistrationEnabled;
                var previousLoadOnStartup = ns.LoadOnStartupEnabled;

                if (renaming)
                {
                    // Register the new name before retiring the old one so a concurrent reader never
                    // sees the namespace vanish mid-rename (a brief both-names window is harmless).
                    _byName[update.NewName] = ns;
                    ns.Name = update.NewName;
                    _byName.TryRemove(name, out _);
                }

                if (update.PluginRegistrationSupplied)
                {
                    ns.PluginRegistrationEnabled = update.PluginRegistrationEnabled;
                }

                if (update.LoadOnStartupSupplied)
                {
                    ns.LoadOnStartupEnabled = update.LoadOnStartupEnabled;
                }

                try
                {
                    WriteCatalogUnlocked(_byName.Values);
                }
                catch
                {
                    ns.PluginRegistrationEnabled = previousPluginRegistration;
                    ns.LoadOnStartupEnabled = previousLoadOnStartup;
                    if (renaming)
                    {
                        ns.Name = name;
                        _byName[name] = ns;
                        _byName.TryRemove(update.NewName, out _);
                    }

                    ns = null;
                    throw;
                }
            }

            _logger.LogInformation("Updated namespace \"{Name}\" ({Id}): name={NewName}, pluginRegistration={PluginRegistration}, " +
                "loadOnStartup={LoadOnStartup}.", name, ns.Id,
                renaming ? ns.Name : "unchanged",
                update.PluginRegistrationSupplied ? Describe(update.PluginRegistrationEnabled) : "unchanged",
                update.LoadOnStartupSupplied ? Describe(update.LoadOnStartupEnabled) : "unchanged");
            failure = NamespaceFailure.None;
            return true;
        }

        /// <summary>An override's operator-facing spelling: "inherit" when it is cleared.</summary>
        private static String Describe(Boolean? value)
        {
            return value.HasValue ? (value.Value ? "enabled" : "disabled") : "inherit";
        }

        /// <summary>
        ///   Loads a cataloged-but-not-loaded namespace into THIS process (feature
        ///   namespace-startup-load §4.8): constructs its engine, hands it to <paramref name="load"/>
        ///   to restore, and only then publishes it - so a failed restore leaves the namespace
        ///   exactly as not-loaded as it was, and no request ever sees a half-loaded graph.
        ///   Idempotent by construction: an already-resident namespace answers
        ///   <see cref="NamespaceActivationOutcome.AlreadyLoaded"/>, not a conflict.
        ///
        ///   <para>THE GATE, and the reason this method exists on the collection rather than on a
        ///   service: engine construction plus restore runs under a PER-NAMESPACE mutex, never under
        ///   <see cref="_writeLock"/>. Not the write lock, because a load is seconds of I/O at scale
        ///   and that lock serializes every create, rename and drop in the whole Fallen-8. Per
        ///   namespace and not per process, because the damage is per write-ahead log: two engines
        ///   constructed on ONE log both adopt the same baseline id
        ///   (<c>WriteAheadLog.BaselineCurrentId</c>) and then append into it independently, and the
        ///   first <c>Fallen8.Save</c> rewrites that shared log to a bare header
        ///   (<c>WriteAheadLog.ResetToSnapshot</c>) while the other engine keeps appending onto a log
        ///   its snapshot no longer pairs with - so acknowledged commits become silently
        ///   non-durable, with a sticky failure fence as the only trace. Exactly one constructor per
        ///   id is therefore the invariant, which is why the residency re-check below is INSIDE the
        ///   gate rather than in front of it.</para>
        ///
        ///   <para>The persisted startup-load policy is deliberately untouched: activation answers
        ///   for this process, the policy answers for the next boot. A caller that means to change
        ///   both (the save-game restore does) makes that a separate, visible update.</para>
        /// </summary>
        /// <param name="name">The namespace to activate.</param>
        /// <param name="load">The restore routine (its one home is <c>NamespaceLoader</c>).</param>
        /// <returns>
        ///   The outcome, the entry and the detail. Not a <c>Try*</c> boolean: TWO of the four
        ///   outcomes are successes, and "already loaded" versus "just loaded" is exactly what the
        ///   caller reports - and an <c>out</c> parameter is not available to an async method anyway.
        /// </returns>
        public async Task<NamespaceActivation> ActivateAsync(String name, NamespaceLoadRoutine load)
        {
            if (!_byName.TryGetValue(name, out var ns))
            {
                return new NamespaceActivation(NamespaceActivationOutcome.NotFound, null, null);
            }

            var gate = _loadGates.GetOrAdd(ns.Id, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (ns.IsLoaded)
                {
                    return new NamespaceActivation(NamespaceActivationOutcome.AlreadyLoaded, ns,
                        "The namespace was already loaded in this process; nothing was restored.");
                }

                // The entry was resolved BEFORE this gate, so a TryDrop may have won the race and
                // retired it (deleting its write-ahead log and its directory) while we waited. Read
                // that here, before touching the filesystem: creating the directory and a fresh log
                // for a retired id resurrects on-disk state for a namespace nobody can address any
                // more. Same condition the publish path re-reads atomically at the end.
                if (IsRetired(ns))
                {
                    return new NamespaceActivation(NamespaceActivationOutcome.NotFound, null, null);
                }

                String walPath = null;
                if (!_durability.Volatile)
                {
                    Directory.CreateDirectory(ResolveNamespaceDirectory(ns.Id));
                    walPath = ResolveNamespaceWalPath(ns.Id);
                }

                // Construction is where an unanchored write-ahead log replays, exactly as at boot.
                var engine = CreateEngine(walPath, ns.Id);
                NamespaceRestoreOutcome outcome;
                String detail;
                try
                {
                    (outcome, detail) = await load(ns, engine).ConfigureAwait(false);
                }
                catch
                {
                    engine.Dispose();
                    throw;
                }

                if (outcome != NamespaceRestoreOutcome.Ready)
                {
                    // Neither a failed restore nor unregistered-checkpoint files may publish an
                    // engine; NamespaceLoader's contract says why the second one is the dangerous
                    // half. Dispose does NOT reset or truncate the log (only a Save does), so
                    // abandoning this engine costs nothing on disk - the namespace stays exactly as
                    // recoverable as it was before the attempt.
                    engine.Dispose();
                    return new NamespaceActivation(
                        outcome == NamespaceRestoreOutcome.UnregisteredCheckpoints
                            ? NamespaceActivationOutcome.UnregisteredCheckpoints
                            : NamespaceActivationOutcome.LoadFailed, ns, detail);
                }

                if (!TryPublishEngine(ns, engine))
                {
                    // Dropped, or the whole collection disposed, while we were loading: either way
                    // this engine has nothing to serve, and the caller is told the namespace does
                    // not exist, which by then is true. What the DROP case may leave on disk, stated
                    // rather than wished away: the drop deletes the write-ahead log, and the
                    // construction above may have re-created an empty one (plus the directory)
                    // after it. Those files hold no commit - this engine was never published, so
                    // nothing was ever appended - and a re-created namesake gets a fresh id, so
                    // nothing can ever read them again. They are named in the log rather than
                    // deleted here: cleaning up would mean telling this case apart from a
                    // collection that is merely disposing, where the very same files still belong
                    // to a live namespace, and a mistake in that direction destroys a real log.
                    engine.Dispose();
                    _logger.LogWarning("Namespace \"{Name}\" ({Id}) left the collection while it was being activated " +
                        "(dropped, or the server is shutting down); the engine that had just been loaded for it was " +
                        "discarded. If it was dropped, an empty write-ahead log may be left under \"{Directory}\": it " +
                        "holds no committed data, nothing can reach it, and it is safe to delete.",
                        name, ns.Id, _durability.Volatile ? "(volatile: nothing on disk)" : ResolveNamespaceDirectory(ns.Id));
                    return new NamespaceActivation(NamespaceActivationOutcome.NotFound, null, detail);
                }

                _logger.LogInformation("Namespace \"{Name}\" ({Id}) was ACTIVATED at runtime: {VertexCount} vertices, " +
                    "{EdgeCount} edges. Its startup-load policy is unchanged, so the next boot still follows it.",
                    name, ns.Id, engine.VertexCount, engine.EdgeCount);
                return new NamespaceActivation(NamespaceActivationOutcome.Activated, ns, detail);
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        ///   Whether this entry can still take an engine at all: it is retired
        ///   (<see cref="Namespace.EngineDisposed"/>, which <see cref="TryDrop"/> sets even for an
        ///   engine-less namespace) or the whole collection is disposed. Read under the dispose gate
        ///   because both flags are written under it.
        /// </summary>
        private Boolean IsRetired(Namespace ns)
        {
            lock (_disposeGate)
            {
                return _disposed || ns.EngineDisposed;
            }
        }

        /// <summary>
        ///   Publishes a freshly loaded engine onto its entry, or refuses because the entry retired
        ///   while the load ran. Under the dispose gate, so it cannot race the collection's own
        ///   disposal: either we publish first (and <see cref="Dispose"/> then disposes the engine
        ///   with the rest) or disposal wins and we are told to throw the engine away. It re-reads
        ///   the same condition <see cref="IsRetired"/> answers, atomically with the attach.
        /// </summary>
        private Boolean TryPublishEngine(Namespace ns, Fallen8 engine)
        {
            lock (_disposeGate)
            {
                if (_disposed || ns.EngineDisposed)
                {
                    return false;
                }

                ns.AttachEngine(engine);
                return true;
            }
        }

        /// <summary>
        ///   Drops a namespace irreversibly: it leaves the collection first (new requests 404
        ///   immediately), then its engine is disposed, then its live on-disk state (the WAL) is
        ///   deleted. A request already past the validation filter when the drop lands may fail —
        ///   there is deliberately no in-flight drain (the engine's element snapshot degrades
        ///   safely to empty; factory accesses can fault, surfaced as a 404/500 to that one
        ///   caller). <see cref="DefaultName"/> cannot be dropped (<see cref="NamespaceFailure.Reserved"/>).
        /// </summary>
        public Boolean TryDrop(String name, out NamespaceFailure failure)
        {
            if (String.Equals(name, DefaultName, StringComparison.Ordinal))
            {
                failure = NamespaceFailure.Reserved;
                return false;
            }

            Namespace ns;
            lock (_writeLock)
            {
                if (!_byName.TryRemove(name, out ns))
                {
                    failure = NamespaceFailure.NotFound;
                    return false;
                }

                try
                {
                    WriteCatalogUnlocked(_byName.Values);
                }
                catch
                {
                    _byName[name] = ns;
                    throw;
                }
            }

            DisposeEngineOnce(ns);

            // The id is retired for good now (ids are minted per creation, never reused), and the
            // entry is marked retired by the dispose above, so an activation still holding the old
            // gate object can only observe that and give up. Removing it keeps the gate dictionary
            // bounded by the LIVE namespaces (see the field doc) instead of by everything that ever
            // existed - the only reason it is safe here and nowhere else.
            _loadGates.TryRemove(ns.Id, out _);

            if (!_durability.Volatile)
            {
                var directory = ResolveNamespaceDirectory(ns.Id);
                try
                {
                    if (Directory.Exists(directory))
                    {
                        // Only the LIVE state (the WAL) dies with the namespace. Checkpoint files
                        // under the directory belong to save-game entries - they remain valid
                        // restore points (that is how a dropped namespace comes back) and are
                        // deleted through DELETE /savegames/{id}?deleteFiles=true, never by a drop.
                        foreach (var walFile in Directory.EnumerateFiles(directory, WalFileName + "*").ToList())
                        {
                            File.Delete(walFile);
                        }

                        if (!Directory.EnumerateFileSystemEntries(directory).Any())
                        {
                            Directory.Delete(directory);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // The namespace is gone from the collection either way; leaking its directory
                    // is an operator-visible warning, not a failed drop.
                    _logger.LogWarning(ex, "Dropped namespace \"{Name}\" ({Id}) but could not delete its write-ahead log under \"{Directory}\".",
                        name, ns.Id, directory);
                }
            }

            _logger.LogInformation("Dropped namespace \"{Name}\" ({Id}).", name, ns.Id);
            failure = NamespaceFailure.None;
            return true;
        }

        /// <summary>
        ///   Runs <paramref name="action"/> only if the collection is not yet disposed, and blocks
        ///   disposal until it finishes. This closes a host-teardown race: the container's disposal
        ///   of this singleton can run CONCURRENTLY with a hosted service's StopAsync (observed
        ///   under WebApplicationFactory, where factory disposal and the app's own Run loop both
        ///   drive shutdown), and a shutdown save must never lose engines mid-loop. Returns false
        ///   when disposal already happened - the caller skips its work (WAL durability holds).
        /// </summary>
        public Boolean TryRunBeforeDispose(Action action)
        {
            lock (_disposeGate)
            {
                if (_disposed)
                {
                    return false;
                }

                action();
                return true;
            }
        }

        public void Dispose()
        {
            lock (_disposeGate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                foreach (var ns in _byName.Values)
                {
                    DisposeEngineOnceUnderGate(ns);
                }
            }
        }

        /// <summary>
        ///   Disposes a namespace's engine exactly once, under the dispose gate: a drop, a failed
        ///   create's revert, and the collection's own disposal can all reach the same engine, and
        ///   <c>Fallen8.Dispose</c> is not idempotent.
        /// </summary>
        private void DisposeEngineOnce(Namespace ns)
        {
            lock (_disposeGate)
            {
                DisposeEngineOnceUnderGate(ns);
            }
        }

        private static void DisposeEngineOnceUnderGate(Namespace ns)
        {
            if (ns.EngineDisposed)
            {
                return;
            }

            // A namespace that is cataloged but not loaded has no engine to dispose (feature
            // namespace-startup-load). Marked disposed anyway so the bookkeeping stays single-valued.
            if (!ns.TryGetEngine(out var engine))
            {
                ns.EngineDisposed = true;
                return;
            }

            ns.EngineDisposed = true;
            engine.Dispose();
        }

        #endregion

        /// <summary>
        ///   The directory a namespace's durability artifacts live in: the legacy storage directory
        ///   for <see cref="DefaultName"/> (zero-migration upgrade), the id-keyed
        ///   <c>namespaces/{id}</c> directory for everything else. Used for default save paths and
        ///   the per-namespace boot discovery.
        /// </summary>
        public String DirectoryFor(Namespace ns)
        {
            return ReferenceEquals(ns, Default)
                ? _durability.ResolveStorageDirectory()
                : ResolveNamespaceDirectory(ns.Id);
        }

        #region private helpers

        [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
            Justification = "Trimming is disabled for this application; the catalog DTOs are simple and also registered in AppJsonContext.")]
        private NamespaceCatalogDocument LoadCatalog()
        {
            if (_catalogPath == null || !File.Exists(_catalogPath))
            {
                return new NamespaceCatalogDocument();
            }

            String text;
            try
            {
                text = File.ReadAllText(_catalogPath);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "The Fallen-8 namespace catalog at \"" + _catalogPath + "\" could not be read.", ex);
            }

            // A PRESENT-but-empty catalog is corruption, not "no namespaces", and is as loud as invalid
            // JSON below (platform-integrity-audit W1). An ABSENT file legitimately means "only the
            // default namespace has ever existed"; a present zero-length file means the list of every
            // non-default namespace was destroyed while their data directories and WALs are still on
            // disk - and starting empty would strand all of them unreachable. WriteCatalogUnlocked never
            // writes an empty file, so this is only reachable from a non-durable write by an older build
            // (now fixed below).
            if (String.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException(
                    "The Fallen-8 namespace catalog at \"" + _catalogPath + "\" is present but empty, which " +
                    "no write path produces - it is a corrupt or truncated catalog (for example a power loss " +
                    "during a write by an older build). Startup is aborted so a destroyed catalog is never " +
                    "mistaken for \"no namespaces\" and silently overwritten, which would strand every " +
                    "non-default namespace's data on disk. Restore the file, or DELETE it to start with only " +
                    "the default namespace.");
            }

            try
            {
                return JsonSerializer.Deserialize<NamespaceCatalogDocument>(text, _json) ?? new NamespaceCatalogDocument();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    "The Fallen-8 namespace catalog at \"" + _catalogPath + "\" is corrupt (invalid JSON); " +
                    "startup is aborted so a bad catalog is never silently overwritten. Fix or remove the " +
                    "file and restart.", ex);
            }
        }

        [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
            Justification = "Trimming is disabled for this application; the catalog DTOs are simple and also registered in AppJsonContext.")]
        private void WriteCatalogUnlocked(IEnumerable<Namespace> namespaces)
        {
            if (_catalogPath == null)
            {
                return;
            }

            var document = new NamespaceCatalogDocument
            {
                // The default namespace is implicit (no entry below), so its plugin-registration
                // override rides on the document itself (feature plugin-registration).
                DefaultPluginRegistrationEnabled = Default.PluginRegistrationEnabled,
            };
            foreach (var ns in namespaces.OrderBy(n => n.Name, StringComparer.Ordinal))
            {
                if (ReferenceEquals(ns, Default))
                {
                    continue; // default is implicit - it always exists.
                }

                document.Namespaces.Add(new NamespaceCatalogEntry
                {
                    Id = ns.Id,
                    Name = ns.Name,
                    CreatedAt = ns.CreatedAtUtc.ToString(CreatedAtFormat),
                    PluginRegistrationEnabled = ns.PluginRegistrationEnabled,
                    LoadOnStartupEnabled = ns.LoadOnStartupEnabled,
                });
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_catalogPath));

            // DURABLE and atomic (platform-integrity-audit W1), through the engine's DurableFileIo rather
            // than a private copy. This file is the only record of which namespaces exist, so losing it
            // strands every non-default namespace's data directory and WAL; the previous write-then-rename
            // was atomic for readers but not durable, so a power loss could publish it zero-length.
            DurableFileIo.ReplaceAllTextDurably(_catalogPath, JsonSerializer.Serialize(document, _json), _logger);
        }

        private Fallen8 CreateEngine(String walPath, String metricsScopeId)
        {
            Fallen8 engine;
            if (walPath == null)
            {
                engine = new Fallen8(_loggerFactory, _changeFeedOptions, metricsScopeId)
                {
                    StoredQueryCompiler = new StoredQueryCompiler(),
                    PluginCompiler = new PluginCompiler()
                };
            }
            else
            {
                engine = new Fallen8(_loggerFactory,
                    new WriteAheadLogOptions(walPath),
                    new RecipeSubGraphCompiler(),
                    new StoredQueryCompiler(),
                    _changeFeedOptions,
                    metricsScopeId,
                    new PluginCompiler());
            }

            // Stored query library: apply the configured registration ceiling (per namespace).
            engine.StoredQueries.MaxCount = _storedQueryMaxCount;

            // Plugin registry: apply the configured per-namespace registration ceiling.
            engine.Plugins.MaxCount = _pluginMaxCount;

            return engine;
        }

        private String ResolveNamespaceDirectory(String id)
        {
            return Path.Combine(_durability.ResolveStorageDirectory(), NamespacesDirectoryName, id);
        }

        private String ResolveNamespaceWalPath(String id)
        {
            return Path.Combine(ResolveNamespaceDirectory(id), WalFileName);
        }

        private static String NewId(DateTime createdAtUtc)
        {
            return "ns-" + createdAtUtc.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
        }

        #endregion
    }

    /// <summary>
    ///   One atomic namespace metadata change for <see cref="Fallen8Namespaces.TryUpdate"/>. Each
    ///   override carries its own <c>*Supplied</c> flag because both are TRI-state on the wire:
    ///   "clear it back to inherit" (null) and "leave it alone" are different requests, and a plain
    ///   nullable cannot say which one arrived.
    /// </summary>
    public sealed class NamespaceUpdate
    {
        /// <summary>The new name, or null/empty to leave the name unchanged.</summary>
        public String NewName { get; set; }

        /// <summary>Whether <see cref="PluginRegistrationEnabled"/> is part of this update.</summary>
        public Boolean PluginRegistrationSupplied { get; set; }

        /// <summary>The plugin-registration override to set (null = inherit the global default).</summary>
        public Boolean? PluginRegistrationEnabled { get; set; }

        /// <summary>Whether <see cref="LoadOnStartupEnabled"/> is part of this update.</summary>
        public Boolean LoadOnStartupSupplied { get; set; }

        /// <summary>The startup-load override to set (null = inherit the global default).</summary>
        public Boolean? LoadOnStartupEnabled { get; set; }

        /// <summary>
        ///   Whether this update asks for nothing at all - the one place the "supply at least one
        ///   field" question is answered, so a field added here cannot be forgotten by the guard.
        /// </summary>
        public Boolean IsEmpty =>
            String.IsNullOrEmpty(NewName) && !PluginRegistrationSupplied && !LoadOnStartupSupplied;
    }

    /// <summary>
    ///   Restores a freshly constructed engine for <paramref name="ns"/> (feature
    ///   namespace-startup-load §4.8), reporting how it went and what happened in the operator's
    ///   words. Takes the engine EXPLICITLY rather than reading <c>ns.Engine</c>, because during an
    ///   activation the engine is deliberately not published until this succeeds. Its
    ///   implementations live in <c>NamespaceLoader</c>, which the boot path shares - and which owns
    ///   the contract for what each caller does with the outcome.
    /// </summary>
    public delegate Task<(NamespaceRestoreOutcome Outcome, String Detail)> NamespaceLoadRoutine(
        Namespace ns, Fallen8 engine);

    /// <summary>
    ///   How a <see cref="NamespaceLoadRoutine"/> ended. Three values rather than a boolean because
    ///   the two non-success cases must be answered differently: a broken checkpoint is a failure,
    ///   while checkpoint files that no registered save game contains are a REFUSAL to publish an
    ///   engine at all (<c>NamespaceLoader</c> says why).
    /// </summary>
    public enum NamespaceRestoreOutcome
    {
        /// <summary>
        ///   The engine may be published: a checkpoint was restored, or there was legitimately
        ///   nothing to restore and this namespace is genuinely empty.
        /// </summary>
        Ready,

        /// <summary>
        ///   No registered save game contains this namespace, but unregistered checkpoint files sit
        ///   in its directory (save-games FR-11).
        /// </summary>
        UnregisteredCheckpoints,

        /// <summary>The restore itself failed; nothing was restored.</summary>
        Failed
    }

    /// <summary>
    ///   What one <see cref="Fallen8Namespaces.ActivateAsync"/> attempt did: the outcome a caller
    ///   maps to a status code, the entry it applies to (null when there is none), and the detail
    ///   naming what was restored or why it failed.
    /// </summary>
    public sealed class NamespaceActivation
    {
        internal NamespaceActivation(NamespaceActivationOutcome outcome, Namespace ns, String detail)
        {
            Outcome = outcome;
            Namespace = ns;
            Detail = detail;
        }

        /// <summary>How the attempt ended.</summary>
        public NamespaceActivationOutcome Outcome { get; }

        /// <summary>The namespace, or null when it is not (or no longer) in the collection.</summary>
        public Namespace Namespace { get; }

        /// <summary>What was restored, or why the restore failed. Free text, for an operator.</summary>
        public String Detail { get; }

        /// <summary>Whether the namespace is loaded now - by this call or already.</summary>
        public Boolean Succeeded =>
            Outcome == NamespaceActivationOutcome.Activated || Outcome == NamespaceActivationOutcome.AlreadyLoaded;
    }

    /// <summary>How one <see cref="Fallen8Namespaces.ActivateAsync"/> attempt ended.</summary>
    public enum NamespaceActivationOutcome
    {
        /// <summary>The namespace was loaded into this process by this call (200).</summary>
        Activated,

        /// <summary>It was already loaded; the call did nothing, which is a success (200).</summary>
        AlreadyLoaded,

        /// <summary>No namespace of that name is in the collection (404).</summary>
        NotFound,

        /// <summary>Its checkpoint could not be restored; it stays not loaded (500).</summary>
        LoadFailed,

        /// <summary>
        ///   It has checkpoint files that no registered save game contains, so activating it would
        ///   publish an empty engine beside real data; it stays not loaded (409).
        /// </summary>
        UnregisteredCheckpoints
    }

    /// <summary>Why a namespace create/rename/drop was rejected (mapped to HTTP by the controller).</summary>
    public enum NamespaceFailure
    {
        /// <summary>The operation succeeded.</summary>
        None,

        /// <summary>The name is empty/whitespace-padded, too long, "."/"..", or contains "/", "\", or a control char (400).</summary>
        InvalidName,

        /// <summary>The name is already in use (409).</summary>
        Conflict,

        /// <summary>The configured <c>Fallen8:Namespaces:MaxNamespaces</c> ceiling is reached (422).</summary>
        QuotaExceeded,

        /// <summary>No namespace is registered under the name (404).</summary>
        NotFound,

        /// <summary>
        ///   The reserved default namespace cannot be renamed, dropped, or excluded from the startup
        ///   load (409).
        /// </summary>
        Reserved
    }
}
