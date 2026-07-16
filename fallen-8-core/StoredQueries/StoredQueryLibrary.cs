// MIT License
//
// StoredQueryLibrary.cs
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
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Core.StoredQueries
{
    /// <summary>
    ///   The registry of named, pre-compiled stored queries (feature stored-query-library).
    ///
    ///   <para>Concurrency model - the engine's single-writer / lock-free-reader discipline:
    ///   mutations (<see cref="TryRegister"/>, <see cref="TryRemove"/>) run only on the single
    ///   writer thread (they are driven by <c>RegisterStoredQueryTransaction</c> /
    ///   <c>RemoveStoredQueryTransaction</c>), so copy-on-write over an immutable snapshot
    ///   dictionary needs no lock; reads (<see cref="TryGet"/>, <see cref="GetAll"/>,
    ///   <see cref="Count"/>) take the current snapshot with a volatile read and never observe a
    ///   torn state.</para>
    /// </summary>
    public sealed class StoredQueryLibrary
    {
        #region Data

        /// <summary>
        ///   The published snapshot: an immutable-by-convention dictionary REPLACED wholesale on
        ///   every mutation, never modified in place, so readers holding an older snapshot stay
        ///   consistent.
        /// </summary>
        private Dictionary<String, StoredQueryEntry> _snapshot =
            new Dictionary<String, StoredQueryEntry>(StringComparer.Ordinal);

        private readonly ILogger<StoredQueryLibrary> _logger;

        /// <summary>
        ///   The library-wide registration ceiling (the subgraph-quotas pattern): pinned compiled
        ///   artifacts are process memory (each holds a collectible AssemblyLoadContext alive), so
        ///   the count is bounded. A registration beyond the cap is rejected with
        ///   <see cref="TransactionFailureReason.QuotaExceeded"/>. Defaults to 256; configurable
        ///   via <c>Fallen8:StoredQueries:MaxCount</c> in the hosted API.
        /// </summary>
        private int _maxCount = DefaultMaxCount;

        /// <summary>The default registration ceiling.</summary>
        public const int DefaultMaxCount = 256;

        /// <summary>The maximum name length accepted by <see cref="IsValidName"/>.</summary>
        public const int MaxNameLength = 128;

        #endregion

        #region constructor

        /// <summary>
        ///   Initializes a new stored query library.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        public StoredQueryLibrary(ILogger<StoredQueryLibrary> logger)
        {
            _logger = logger;
        }

        #endregion

        #region public members

        /// <summary>
        ///   Gets or sets the registration ceiling. Setting a non-positive value resets to
        ///   <see cref="DefaultMaxCount"/>. Lowering the ceiling below the current count never
        ///   evicts existing entries; it only rejects further registrations.
        /// </summary>
        public int MaxCount
        {
            get { return _maxCount; }
            set { _maxCount = value > 0 ? value : DefaultMaxCount; }
        }

        /// <summary>The number of currently registered stored queries.</summary>
        public int Count
        {
            get { return Volatile.Read(ref _snapshot).Count; }
        }

        /// <summary>
        ///   Validates a stored query name: <c>^[A-Za-z0-9_-]{1,128}$</c>, compared ordinally.
        ///   The restriction makes every name a safe URL path segment.
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
        ///   Tries to get a stored query entry by name (lock-free snapshot read).
        /// </summary>
        /// <param name="entry">The entry, or null.</param>
        /// <param name="name">The stored query name (ordinal comparison).</param>
        /// <returns><c>true</c> if an entry with that name is registered; otherwise <c>false</c>.</returns>
        public bool TryGet(out StoredQueryEntry entry, String name)
        {
            entry = null;
            if (name == null)
            {
                return false;
            }

            return Volatile.Read(ref _snapshot).TryGetValue(name, out entry);
        }

        /// <summary>
        ///   Returns a point-in-time list of all registered entries (lock-free snapshot read).
        /// </summary>
        public IReadOnlyList<StoredQueryEntry> GetAll()
        {
            var snap = Volatile.Read(ref _snapshot);
            var result = new List<StoredQueryEntry>(snap.Count);
            foreach (var kv in snap)
            {
                result.Add(kv.Value);
            }
            return result;
        }

        #endregion

        #region writer-thread mutations

        /// <summary>
        ///   Registers an entry. WRITER THREAD ONLY (driven by
        ///   <c>RegisterStoredQueryTransaction</c>). Re-checks the invariants a controller
        ///   pre-checked, so TOCTOU races resolve here: an invalid name maps to
        ///   <see cref="TransactionFailureReason.InvalidInput"/>, a duplicate to
        ///   <see cref="TransactionFailureReason.Conflict"/>, and a breach of
        ///   <see cref="MaxCount"/> to <see cref="TransactionFailureReason.QuotaExceeded"/>.
        ///   <paramref name="enforceQuota"/> is false ONLY on write-ahead-log replay, which
        ///   re-applies registrations that were already quota-checked at their original commit
        ///   (see <c>RegisterStoredQueryTransaction.BypassQuota</c>).
        /// </summary>
        internal bool TryRegister(StoredQueryEntry entry, out TransactionFailureReason reason, bool enforceQuota = true)
        {
            reason = TransactionFailureReason.None;

            if (entry == null || entry.Definition == null || !IsValidName(entry.Definition.Name))
            {
                _logger.LogError("Cannot register stored query: the entry or its name is invalid.");
                reason = TransactionFailureReason.InvalidInput;
                return false;
            }

            var snap = _snapshot;

            if (snap.ContainsKey(entry.Definition.Name))
            {
                _logger.LogWarning("Cannot register stored query \"{Name}\": the name is already in use.", entry.Definition.Name);
                reason = TransactionFailureReason.Conflict;
                return false;
            }

            if (enforceQuota && snap.Count >= _maxCount)
            {
                _logger.LogWarning(
                    "Cannot register stored query \"{Name}\": the maximum number of stored queries ({Max}) has been reached.",
                    entry.Definition.Name, _maxCount);
                reason = TransactionFailureReason.QuotaExceeded;
                return false;
            }

            var next = new Dictionary<String, StoredQueryEntry>(snap, StringComparer.Ordinal)
            {
                { entry.Definition.Name, entry }
            };
            Volatile.Write(ref _snapshot, next);

            _logger.LogInformation("Registered stored query \"{Name}\" ({Kind}, {State}).",
                entry.Definition.Name, entry.Definition.Kind, entry.CompileState);
            return true;
        }

        /// <summary>
        ///   Removes an entry by name. WRITER THREAD ONLY (driven by
        ///   <c>RemoveStoredQueryTransaction</c>). A missing name maps to
        ///   <see cref="TransactionFailureReason.NotFound"/>. The removed entry is reported so the
        ///   transaction can restore it on rollback.
        /// </summary>
        internal bool TryRemove(out StoredQueryEntry removed, String name, out TransactionFailureReason reason)
        {
            reason = TransactionFailureReason.None;
            removed = null;

            var snap = _snapshot;
            if (name == null || !snap.TryGetValue(name, out removed))
            {
                reason = TransactionFailureReason.NotFound;
                return false;
            }

            var next = new Dictionary<String, StoredQueryEntry>(snap, StringComparer.Ordinal);
            next.Remove(name);
            Volatile.Write(ref _snapshot, next);

            _logger.LogInformation("Removed stored query \"{Name}\".", name);
            return true;
        }

        /// <summary>
        ///   Replaces the whole library content (load-path rehydration). WRITER/LOAD THREAD ONLY.
        /// </summary>
        internal void ReplaceAll(IEnumerable<StoredQueryEntry> entries)
        {
            var next = new Dictionary<String, StoredQueryEntry>(StringComparer.Ordinal);
            if (entries != null)
            {
                foreach (var entry in entries)
                {
                    if (entry?.Definition?.Name == null)
                    {
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
            Volatile.Write(ref _snapshot, new Dictionary<String, StoredQueryEntry>(StringComparer.Ordinal));
        }

        #endregion
    }
}
