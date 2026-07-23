// MIT License
//
// Fallen8Namespaces.cs
//
// Copyright (c) 2025 Henning Rauch
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.ChangeFeed;

namespace NoSQL.GraphDB.App.Namespaces
{
    /// <summary>
    ///   The entire collection of namespaces behind one endpoint — the Fallen-8 itself (feature
    ///   graph-namespaces; terminology is spec §1). It owns one Fallen-8 engine per namespace and
    ///   always holds the reserved <see cref="DefaultName"/> namespace, which lives on the legacy
    ///   storage paths and is what bare (un-prefixed) URLs address. Namespacing is a hosting
    ///   concern: each engine is constructed exactly the way the previous single-engine factory
    ///   built the one engine, and the engine itself is unchanged.
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

        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<Fallen8Namespaces> _logger;
        private readonly Fallen8DurabilityOptions _durability;
        private readonly ChangeFeedOptions _changeFeedOptions;
        private readonly Int32 _storedQueryMaxCount;

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
            IOptions<Fallen8MetadataOptions> metadata)
        {
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<Fallen8Namespaces>();
            _durability = durability.Value;
            _changeFeedOptions = changeFeed.Value.ToEngineOptions();
            _storedQueryMaxCount = storedQueries.Value.MaxCount;
            MaxNamespaces = namespaces.Value.MaxNamespaces;

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

            Default = new Namespace(DefaultName, NewId(DateTime.UtcNow), CreateEngine(defaultWalPath), DateTime.UtcNow);
            _byName[DefaultName] = Default;

            // Boot every cataloged namespace eagerly, each on its id-keyed directory: its engine
            // constructor replays that namespace's unanchored WAL exactly like the single engine
            // always has; checkpoint loading follows in DurabilityLifecycleService.StartAsync.
            foreach (var entry in LoadCatalog().Namespaces)
            {
                Directory.CreateDirectory(ResolveNamespaceDirectory(entry.Id));
                var createdAt = DateTime.TryParseExact(entry.CreatedAt, CreatedAtFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var stamp)
                    ? stamp
                    : DateTime.UtcNow;
                var ns = new Namespace(entry.Name, entry.Id, CreateEngine(ResolveNamespaceWalPath(entry.Id)), createdAt);
                _byName[entry.Name] = ns;
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
        ///   Validates a namespace name: <c>^[a-z0-9-]{1,63}$</c>, compared ordinally. The
        ///   restriction makes every name a safe URL path segment; on-disk locations use the
        ///   immutable namespace id instead, so names never become filesystem paths.
        /// </summary>
        public static Boolean IsValidName(String name)
        {
            if (String.IsNullOrEmpty(name) || name.Length > MaxNameLength)
            {
                return false;
            }

            foreach (var c in name)
            {
                var valid = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-';
                if (!valid)
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

                ns = new Namespace(name, id, CreateEngine(walPath), createdAt);
                _byName[name] = ns;

                try
                {
                    WriteCatalogUnlocked();
                }
                catch
                {
                    // Memory must not claim a namespace disk truth does not know: undo and rethrow.
                    _byName.TryRemove(name, out _);
                    ns.Engine.Dispose();
                    ns = null;
                    throw;
                }
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
            ns = null;

            if (!IsValidName(newName))
            {
                failure = NamespaceFailure.InvalidName;
                return false;
            }

            if (String.Equals(name, DefaultName, StringComparison.Ordinal))
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

                if (_byName.ContainsKey(newName))
                {
                    ns = null;
                    failure = NamespaceFailure.Conflict;
                    return false;
                }

                // Register the new name before retiring the old one so a concurrent reader never
                // sees the namespace vanish mid-rename (a brief both-names window is harmless).
                _byName[newName] = ns;
                ns.Name = newName;
                _byName.TryRemove(name, out _);

                try
                {
                    WriteCatalogUnlocked();
                }
                catch
                {
                    ns.Name = name;
                    _byName[name] = ns;
                    _byName.TryRemove(newName, out _);
                    ns = null;
                    throw;
                }
            }

            _logger.LogInformation("Renamed namespace \"{OldName}\" to \"{NewName}\" ({Id}).", name, ns.Name, ns.Id);
            failure = NamespaceFailure.None;
            return true;
        }

        /// <summary>
        ///   Drops a namespace irreversibly: it leaves the collection first (new requests 404
        ///   immediately), then its engine is disposed (the engine's teardown is reader-safe: a
        ///   racing reader observes an empty snapshot, never null), then its on-disk data is
        ///   deleted. <see cref="DefaultName"/> cannot be dropped (<see cref="NamespaceFailure.Reserved"/>).
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
                    WriteCatalogUnlocked();
                }
                catch
                {
                    _byName[name] = ns;
                    throw;
                }
            }

            ns.Engine.Dispose();

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
                    ns.Engine.Dispose();
                }
            }
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

            if (String.IsNullOrWhiteSpace(text))
            {
                return new NamespaceCatalogDocument();
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
        private void WriteCatalogUnlocked()
        {
            if (_catalogPath == null)
            {
                return;
            }

            var document = new NamespaceCatalogDocument();
            foreach (var ns in _byName.Values.OrderBy(n => n.Name, StringComparer.Ordinal))
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
                });
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_catalogPath));
            var temp = _catalogPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(document, _json));

            // Atomic replace: a crash mid-write leaves the previous catalog intact (temp is orphaned).
            if (File.Exists(_catalogPath))
            {
                File.Replace(temp, _catalogPath, null);
            }
            else
            {
                File.Move(temp, _catalogPath);
            }
        }

        private Fallen8 CreateEngine(String walPath)
        {
            Fallen8 engine;
            if (walPath == null)
            {
                engine = new Fallen8(_loggerFactory, _changeFeedOptions)
                {
                    StoredQueryCompiler = new StoredQueryCompiler()
                };
            }
            else
            {
                engine = new Fallen8(_loggerFactory,
                    new WriteAheadLogOptions(walPath),
                    new RecipeSubGraphCompiler(),
                    new StoredQueryCompiler(),
                    _changeFeedOptions);
            }

            // Stored query library: apply the configured registration ceiling (per namespace).
            engine.StoredQueries.MaxCount = _storedQueryMaxCount;

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

    /// <summary>Why a namespace create/rename/drop was rejected (mapped to HTTP by the controller).</summary>
    public enum NamespaceFailure
    {
        /// <summary>The operation succeeded.</summary>
        None,

        /// <summary>The name does not match <c>^[a-z0-9-]{1,63}$</c> (400).</summary>
        InvalidName,

        /// <summary>The name is already in use (409).</summary>
        Conflict,

        /// <summary>The configured <c>Fallen8:Namespaces:MaxNamespaces</c> ceiling is reached (422).</summary>
        QuotaExceeded,

        /// <summary>No namespace is registered under the name (404).</summary>
        NotFound,

        /// <summary>The reserved default namespace cannot be renamed or dropped (409).</summary>
        Reserved
    }
}
