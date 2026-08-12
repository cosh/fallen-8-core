// MIT License
//
// PluginRegistry.cs
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
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Core.Plugins
{
    /// <summary>
    ///   The per-namespace registry of runtime-registered plugins (feature plugin-registration): the
    ///   source-based, namespace-scoped replacement for the removed DLL-upload path. It lives on the
    ///   per-namespace <c>Fallen8</c> engine (one graph, one registry), exactly like
    ///   <c>StoredQueryLibrary</c>, so a plugin registered in one namespace is invisible in another.
    ///
    ///   <para>Concurrency model - the engine's single-writer / lock-free-reader discipline:
    ///   mutations (<see cref="TryRegister"/>, <see cref="TryRemove"/>) run only on the single writer
    ///   thread (driven by <c>RegisterPluginTransaction</c> / <c>RemovePluginTransaction</c>), so
    ///   copy-on-write over an immutable snapshot dictionary needs no lock; reads (<see cref="TryGet"/>,
    ///   <see cref="GetAll"/>, <see cref="Count"/>) take the current snapshot with a volatile read and
    ///   never observe a torn state.</para>
    /// </summary>
    public sealed class PluginRegistry
    {
        #region Data

        /// <summary>
        ///   The published snapshot: an immutable-by-convention dictionary REPLACED wholesale on every
        ///   mutation, never modified in place, so readers holding an older snapshot stay consistent.
        /// </summary>
        private Dictionary<String, PluginEntry> _snapshot =
            new Dictionary<String, PluginEntry>(StringComparer.Ordinal);

        private readonly ILogger<PluginRegistry> _logger;

        /// <summary>
        ///   The per-namespace registration ceiling (the stored-query / subgraph-quotas pattern):
        ///   pinned compiled artifacts are process memory (each holds a collectible
        ///   AssemblyLoadContext alive), so the count is bounded. A registration beyond the cap is
        ///   rejected with <see cref="TransactionFailureReason.QuotaExceeded"/>. Defaults to 64;
        ///   configurable via <c>Fallen8:Plugins:MaxCount</c> in the hosted API.
        /// </summary>
        private int _maxCount = DefaultMaxCount;

        /// <summary>The default registration ceiling.</summary>
        public const int DefaultMaxCount = 64;

        /// <summary>The maximum name length accepted by <see cref="IsValidName"/>.</summary>
        public const int MaxNameLength = 128;

        #endregion

        #region constructor

        /// <summary>
        ///   Initializes a new plugin registry.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        public PluginRegistry(ILogger<PluginRegistry> logger)
        {
            _logger = logger;
        }

        #endregion

        #region public members

        /// <summary>
        ///   Gets or sets the registration ceiling. Setting a non-positive value resets to
        ///   <see cref="DefaultMaxCount"/>. Lowering the ceiling below the current count never evicts
        ///   existing entries; it only rejects further registrations.
        /// </summary>
        public int MaxCount
        {
            get { return _maxCount; }
            set { _maxCount = value > 0 ? value : DefaultMaxCount; }
        }

        /// <summary>The number of currently registered plugins.</summary>
        public int Count
        {
            get { return Volatile.Read(ref _snapshot).Count; }
        }

        /// <summary>
        ///   Validates a plugin name: <c>^[A-Za-z0-9_-]{1,128}$</c>, compared ordinally. The
        ///   restriction makes every name a safe URL path segment.
        /// </summary>
        public static bool IsValidName(String name)
        {
            if (String.IsNullOrEmpty(name) || name.Length > MaxNameLength)
            {
                return false;
            }

            foreach (var c in name)
            {
                var valid = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                            (c >= '0' && c <= '9') || c == '_' || c == '-';
                if (!valid)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        ///   Tries to get a plugin entry by name (lock-free snapshot read).
        /// </summary>
        /// <param name="entry">The entry, or null.</param>
        /// <param name="name">The plugin name (ordinal comparison).</param>
        /// <returns><c>true</c> if an entry with that name is registered; otherwise <c>false</c>.</returns>
        public bool TryGet(out PluginEntry entry, String name)
        {
            entry = null;
            if (name == null)
            {
                return false;
            }

            return Volatile.Read(ref _snapshot).TryGetValue(name, out entry);
        }

        /// <summary>
        ///   Returns the names of every registered, currently-<see cref="PluginCompileState.Compiled"/>
        ///   plugin of the given contract (feature plugin-registration): the set an enumeration surface
        ///   (e.g. <c>GET /status</c>'s available-plugin lists, the analytics algorithm list) UNIONs
        ///   with the built-ins so a registered plugin is discoverable, not just invocable by name
        ///   (spec §4.4). Only Compiled entries are returned - a Failed/SourceOnly entry cannot be
        ///   invoked, so advertising it would mislead. Lock-free snapshot read.
        /// </summary>
        public IReadOnlyList<String> NamesForContract(PluginContract contract)
        {
            var entries = EntriesForContract(contract);
            var result = new List<String>(entries.Count);
            foreach (var entry in entries)
            {
                result.Add(entry.Definition.Name);
            }
            return result;
        }

        /// <summary>
        ///   The compiled entries of the given contract - the same lock-free snapshot filter as
        ///   <see cref="NamesForContract"/>, but returning the entries themselves so a caller can
        ///   read each plugin's <c>Description</c> as well as its <c>Name</c> (the
        ///   <c>/analytics/algorithms</c> picker needs both). The one home for the
        ///   "compiled entries of this contract" predicate that <see cref="NamesForContract"/>
        ///   delegates to, so the name list and the description list can never select a different
        ///   set (consolidation-audit CA-9).
        /// </summary>
        public IReadOnlyList<PluginEntry> EntriesForContract(PluginContract contract)
        {
            var snap = Volatile.Read(ref _snapshot);
            var result = new List<PluginEntry>();
            foreach (var kv in snap)
            {
                var entry = kv.Value;
                if (entry.CompileState == PluginCompileState.Compiled && entry.Definition.Contract == contract)
                {
                    result.Add(entry);
                }
            }
            return result;
        }

        /// <summary>
        ///   Returns a point-in-time list of all registered entries (lock-free snapshot read).
        /// </summary>
        public IReadOnlyList<PluginEntry> GetAll()
        {
            var snap = Volatile.Read(ref _snapshot);
            var result = new List<PluginEntry>(snap.Count);
            foreach (var kv in snap)
            {
                result.Add(kv.Value);
            }
            return result;
        }

        /// <summary>
        ///   Activates a FRESH instance of a registered, compiled plugin resolved by name, if one
        ///   exists whose pinned type is assignable to <typeparamref name="T"/> (the requested contract
        ///   interface). Returns false for an unknown name, a non-<see cref="PluginCompileState.Compiled"/>
        ///   entry, a category/contract mismatch, or an activation failure. A fresh instance per call
        ///   (never cached by name) means a delete/re-register is never served a stale instance - the
        ///   resolution seam relied on by the path/subgraph/analytics endpoints and graph-function
        ///   invocation (feature plugin-registration).
        /// </summary>
        public bool TryActivate<T>(out T result, String name) where T : class
        {
            result = null;

            if (!TryGet(out var entry, name))
            {
                return false;
            }

            if (entry.CompileState != PluginCompileState.Compiled || entry.Artifact == null)
            {
                return false;
            }

            if (!typeof(T).IsAssignableFrom(entry.Artifact))
            {
                return false;
            }

            try
            {
                result = Activator.CreateInstance(entry.Artifact) as T;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Activating registered plugin \"{Name}\" failed.", name);
                result = null;
                return false;
            }

            return result != null;
        }

        #endregion

        #region writer-thread mutations

        /// <summary>
        ///   Registers an entry. WRITER THREAD ONLY (driven by <c>RegisterPluginTransaction</c>).
        ///   Re-checks the invariants a controller pre-checked, so TOCTOU races resolve here: an
        ///   invalid name maps to <see cref="TransactionFailureReason.InvalidInput"/>, a duplicate to
        ///   <see cref="TransactionFailureReason.Conflict"/>, and a breach of <see cref="MaxCount"/> to
        ///   <see cref="TransactionFailureReason.QuotaExceeded"/>. <paramref name="enforceQuota"/> is
        ///   false ONLY on write-ahead-log replay, which re-applies registrations that were already
        ///   quota-checked at their original commit (see <c>RegisterPluginTransaction.BypassQuota</c>).
        /// </summary>
        internal bool TryRegister(PluginEntry entry, out TransactionFailureReason reason, bool enforceQuota = true)
        {
            reason = TransactionFailureReason.None;

            if (entry == null || entry.Definition == null || !IsValidName(entry.Definition.Name))
            {
                _logger.LogError("Cannot register plugin: the entry or its name is invalid.");
                reason = TransactionFailureReason.InvalidInput;
                return false;
            }

            var snap = _snapshot;

            if (snap.ContainsKey(entry.Definition.Name))
            {
                _logger.LogWarning("Cannot register plugin \"{Name}\": the name is already in use.", entry.Definition.Name);
                reason = TransactionFailureReason.Conflict;
                return false;
            }

            if (enforceQuota && snap.Count >= _maxCount)
            {
                _logger.LogWarning(
                    "Cannot register plugin \"{Name}\": the maximum number of plugins ({Max}) has been reached.",
                    entry.Definition.Name, _maxCount);
                reason = TransactionFailureReason.QuotaExceeded;
                return false;
            }

            var next = new Dictionary<String, PluginEntry>(snap, StringComparer.Ordinal)
            {
                { entry.Definition.Name, entry }
            };
            Volatile.Write(ref _snapshot, next);

            _logger.LogInformation("Registered plugin \"{Name}\" ({Category}/{Contract}, {State}).",
                entry.Definition.Name, entry.Definition.Category, entry.Definition.Contract, entry.CompileState);
            return true;
        }

        /// <summary>
        ///   Removes an entry by name. WRITER THREAD ONLY (driven by <c>RemovePluginTransaction</c>). A
        ///   missing name maps to <see cref="TransactionFailureReason.NotFound"/>. The removed entry is
        ///   reported so the transaction can restore it on rollback.
        /// </summary>
        internal bool TryRemove(out PluginEntry removed, String name, out TransactionFailureReason reason)
        {
            reason = TransactionFailureReason.None;
            removed = null;

            var snap = _snapshot;
            if (name == null || !snap.TryGetValue(name, out removed))
            {
                reason = TransactionFailureReason.NotFound;
                return false;
            }

            var next = new Dictionary<String, PluginEntry>(snap, StringComparer.Ordinal);
            next.Remove(name);
            Volatile.Write(ref _snapshot, next);

            _logger.LogInformation("Removed plugin \"{Name}\".", name);
            return true;
        }

        /// <summary>
        ///   Replaces the PERSISTED registry content with the entries rehydrated from a snapshot
        ///   manifest (load-path rehydration), keeping every entry that manifest could not have
        ///   contained: an entry that is not <see cref="PluginEntry.IsPersistable"/> was registered by
        ///   host code as a CLR type, so no manifest can describe it and a wholesale replacement would
        ///   silently unregister something the load never claimed to restore. WRITER/LOAD THREAD ONLY.
        ///
        ///   <para>What that keeps working is everything AFTER this point, not the load that is running:
        ///   this load's own index and service rehydration has already happened
        ///   (<c>PersistencyFactory.Load</c> resolves those plugin names before
        ///   <c>Fallen8.RehydratePlugins</c> is reached), so it cannot be the reason. The reason is that
        ///   a host registers its types ONCE, at start, while a load is a data restore that can happen
        ///   at any time and any number of times: afterwards every create-by-name and invoke-by-name
        ///   must still resolve, and so must the NEXT load's index rehydration, which would otherwise
        ///   find the type gone with no way back short of restarting the host.</para>
        ///
        ///   <para>On an ordinal name collision the HOST entry wins and the collision is logged as an
        ///   error: the host's type is what the running process can actually execute, whereas the
        ///   colliding persisted source needs a compiler a compiler-less host does not have (it would
        ///   rehydrate <see cref="PluginCompileState.SourceOnly"/> and be uninvocable). Two definitions
        ///   claiming one name is an operator problem to resolve, hence an error rather than a
        ///   warning.</para>
        ///
        ///   <para>ACCEPTED consequence of host-wins: the discarded definition is not in the registry, so
        ///   the next checkpoint's manifest omits it and stops carrying its SOURCE forward. Accepted
        ///   rather than merged (one name, one entry) because the source is not destroyed - the save
        ///   point that was just loaded still holds it, since a save never overwrites a published save
        ///   point - and the collision is reported as an error, so the operator can rename one of the
        ///   two and register it again.</para>
        /// </summary>
        internal void ReplacePersistedEntries(IEnumerable<PluginEntry> entries)
        {
            var next = new Dictionary<String, PluginEntry>(StringComparer.Ordinal);

            foreach (var kv in _snapshot)
            {
                if (!kv.Value.IsPersistable)
                {
                    next[kv.Key] = kv.Value;
                }
            }

            if (entries != null)
            {
                foreach (var entry in entries)
                {
                    if (entry?.Definition?.Name == null)
                    {
                        continue;
                    }

                    if (next.TryGetValue(entry.Definition.Name, out var kept) && !kept.IsPersistable)
                    {
                        _logger.LogError(
                            "The loaded plugin manifest holds \"{Name}\", which the host already registered as type {Type}; the host registration wins and the persisted definition is discarded.",
                            entry.Definition.Name, kept.Artifact);
                        continue;
                    }

                    next[entry.Definition.Name] = entry;
                }
            }

            Volatile.Write(ref _snapshot, next);
        }

        /// <summary>
        ///   Removes every entry (engine teardown). WRITER/DISPOSE THREAD ONLY.
        /// </summary>
        internal void Clear()
        {
            Volatile.Write(ref _snapshot, new Dictionary<String, PluginEntry>(StringComparer.Ordinal));
        }

        #endregion
    }
}
