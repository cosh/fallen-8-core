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
using System.IO;
using System.Linq;
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

        #endregion

        #region fields

        private readonly ConcurrentDictionary<String, Namespace> _byName =
            new ConcurrentDictionary<String, Namespace>(StringComparer.Ordinal);

        /// <summary>Serializes create/rename/drop so quota and conflict checks are atomic.</summary>
        private readonly Object _writeLock = new Object();

        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<Fallen8Namespaces> _logger;
        private readonly Fallen8DurabilityOptions _durability;
        private readonly ChangeFeedOptions _changeFeedOptions;
        private readonly Int32 _storedQueryMaxCount;

        #endregion

        #region constructor

        public Fallen8Namespaces(ILoggerFactory loggerFactory,
            IOptions<Fallen8DurabilityOptions> durability,
            IOptions<Fallen8StoredQueryOptions> storedQueries,
            IOptions<Fallen8ChangeFeedOptions> changeFeed,
            IOptions<Fallen8NamespacesOptions> namespaces)
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
            }

            Default = new Namespace(DefaultName, NewId(DateTime.UtcNow), CreateEngine(defaultWalPath), DateTime.UtcNow);
            _byName[DefaultName] = Default;
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
            }

            ns.Engine.Dispose();

            if (!_durability.Volatile)
            {
                var directory = ResolveNamespaceDirectory(ns.Id);
                try
                {
                    if (Directory.Exists(directory))
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                }
                catch (Exception ex)
                {
                    // The namespace is gone from the collection either way; leaking its directory
                    // is an operator-visible warning, not a failed drop.
                    _logger.LogWarning(ex, "Dropped namespace \"{Name}\" ({Id}) but could not delete its directory \"{Directory}\".",
                        name, ns.Id, directory);
                }
            }

            _logger.LogInformation("Dropped namespace \"{Name}\" ({Id}).", name, ns.Id);
            failure = NamespaceFailure.None;
            return true;
        }

        public void Dispose()
        {
            foreach (var ns in _byName.Values)
            {
                ns.Engine.Dispose();
            }
        }

        #endregion

        #region private helpers

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
            return Path.Combine(ResolveNamespaceDirectory(id), "fallen8.wal");
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
