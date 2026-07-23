// MIT License
//
// DurabilityLifecycleService.cs
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
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.App.Namespaces;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.App.Services
{
    /// <summary>
    ///   Owns the load-on-start / save-on-stop durability lifecycle of the hosted API (feature
    ///   hosted-durability-lifecycle, generalized per namespace by feature graph-namespaces): on
    ///   boot every namespace loads its newest registered save game (which replays the paired
    ///   write-ahead log), and on a clean shutdown every namespace is checkpointed into ONE
    ///   Fallen-8-level save-game entry so the next boot is up to date and the WALs are reset. It
    ///   reuses the existing Save/Load transactions on each engine's single writer thread - it
    ///   introduces no new mutation path. In volatile mode it does nothing.
    /// </summary>
    public sealed class DurabilityLifecycleService : IHostedService
    {
        private readonly Fallen8Namespaces _namespaces;
        private readonly Fallen8DurabilityOptions _options;
        private readonly SaveGameRegistry _saveGames;
        private readonly ILogger<DurabilityLifecycleService> _logger;

        // StopAsync must run its save+register at most once: the host can invoke it more than once
        // (double dispose in tests, layered shutdown), and a second pass could snapshot a
        // mid-teardown (possibly empty) graph and register it as the newest save game.
        private int _stopped;

        /// <summary>The readiness flag behind GET /readyz (feature observability); optional so
        /// direct test construction stays unchanged.</summary>
        private readonly StartupState _startupState;

        public DurabilityLifecycleService(Fallen8Namespaces namespaces, IOptions<Fallen8DurabilityOptions> options,
            SaveGameRegistry saveGames, ILogger<DurabilityLifecycleService> logger,
            StartupState startupState = null)
        {
            _namespaces = namespaces;
            _options = options.Value;
            _saveGames = saveGames;
            _logger = logger;
            _startupState = startupState;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (_options.Volatile)
            {
                _logger.LogWarning("Fallen-8 durability is in VOLATILE mode (Fallen8:Durability:Volatile=true): " +
                    "no checkpoint is loaded on start and none is saved on shutdown; a restart loses all data.");

                // Nothing to load: ready immediately (feature observability).
                _startupState?.MarkReady();
                return Task.CompletedTask;
            }

            // Per-namespace registry-driven boot (save-games FR-8 generalized): each namespace
            // loads the newest entry CONTAINING it; a namespace no entry contains starts from its
            // WAL-replayed construction state. The registry - never directory discovery - decides.
            foreach (var ns in _namespaces.Snapshot())
            {
                StartNamespace(ns);
            }

            // Load-at-startup completed for every namespace: the server is ready (feature
            // observability). The throwing failure paths deliberately never mark ready.
            _startupState?.MarkReady();
            return Task.CompletedTask;
        }

        private void StartNamespace(Namespace ns)
        {
            var directory = _namespaces.DirectoryFor(ns);
            var newest = _saveGames.NewestContaining(ns.Name);
            var member = newest == null
                ? null
                : SaveGameRegistry.EffectiveNamespaces(newest).First(m => m.Name == ns.Name);

            if (member == null)
            {
                // Migration hint (FR-11): checkpoint files without a registry entry are not loaded.
                if (CheckpointDiscovery.TryFindLatestCheckpoint(directory, _options.CheckpointBaseName, out var orphan))
                {
                    _logger.LogWarning("Fallen-8 found checkpoint files (e.g. \"{Checkpoint}\") for namespace \"{Namespace}\" " +
                        "but no registered save game contains it, so it starts EMPTY (registry-driven boot). To adopt an " +
                        "existing checkpoint, load it once via PUT /load - it is then registered permanently.",
                        orphan, ns.Name);
                }
                else
                {
                    _logger.LogInformation("No registered save game contains namespace \"{Namespace}\"; it starts with its " +
                        "current in-memory state ({VertexCount} vertices, {EdgeCount} edges) - any unanchored WAL was " +
                        "replayed at construction.", ns.Name, ns.Engine.VertexCount, ns.Engine.EdgeCount);
                }

                return;
            }

            // Crash-window reconciliation (FR-10): a save completes and becomes durable on disk (the WAL
            // is re-anchored to it inside the save transaction) and only THEN is its registry entry
            // written. A crash in that window leaves a complete checkpoint on disk that the registry does
            // not know. If discovery finds a checkpoint strictly newer than the newest REGISTERED member,
            // it is exactly such an orphan - adopt it (load + register) so a crash never silently reverts
            // to an older save.
            var loadTarget = member.Location;
            var adoptOrphan = false;
            if (CheckpointDiscovery.TryFindLatestCheckpoint(directory, _options.CheckpointBaseName, out var diskCheckpoint))
            {
                var diskRegistered = _saveGames.GetAll()
                    .SelectMany(SaveGameRegistry.EffectiveNamespaces)
                    .Any(m => PathsEqual(m.Location, diskCheckpoint));
                var newestFileExists = File.Exists(member.Location);
                var diskNewer = !newestFileExists
                    || File.GetLastWriteTimeUtc(diskCheckpoint) > File.GetLastWriteTimeUtc(member.Location);
                if (!diskRegistered && diskNewer)
                {
                    _logger.LogWarning("A checkpoint on disk (\"{Disk}\") for namespace \"{Namespace}\" is newer than the " +
                        "newest registered save game {Id} (saved {SavedAt}) and is not in the registry; adopting it - it " +
                        "is a durable save whose registration did not complete (crash window).",
                        diskCheckpoint, ns.Name, newest.Id, newest.SavedAt);
                    loadTarget = diskCheckpoint;
                    adoptOrphan = true;
                }
            }

            if (!adoptOrphan)
            {
                _logger.LogInformation("Loading namespace \"{Namespace}\" from save game {Id} at \"{Location}\" (saved {SavedAt}).",
                    ns.Name, newest.Id, loadTarget, newest.SavedAt);
            }

            // A missing primary checkpoint file does NOT roll the load back (the engine's Load treats
            // a non-existent file as a no-op), so it would silently serve an empty graph. Fail startup
            // loudly here instead (FR-9) - the operator restores the files or removes the entry.
            if (!File.Exists(loadTarget))
            {
                throw new InvalidOperationException(
                    "The newest save game containing namespace \"" + ns.Name + "\" (\"" + newest.Id + "\") points at \"" +
                    loadTarget + "\", which does not exist; startup is aborted so a missing save is never masked by an " +
                    "empty graph. Restore its files, or remove the entry (DELETE /savegames/" + newest.Id + ") and restart.");
            }

            var loadInfo = ns.Engine.EnqueueTransaction(new LoadTransaction { Path = loadTarget });
            loadInfo.WaitUntilFinished();

            if (loadInfo.TransactionState == TransactionState.RolledBack)
            {
                throw new InvalidOperationException(
                    "Fallen-8 failed to load namespace \"" + ns.Name + "\" from \"" + loadTarget +
                    "\"; startup is aborted. Restore its files, or remove the entry (DELETE /savegames/" + newest.Id +
                    ") and restart to use the next-newest (or start empty).", loadInfo.Error);
            }

            if (adoptOrphan)
            {
                // Register the adopted orphan now that the graph is loaded (so its KPIs are correct).
                try
                {
                    _saveGames.RegisterImportIfUnknown(ns.Name, ns.Engine, loadTarget);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Adopted the orphan checkpoint \"{Disk}\" for namespace \"{Namespace}\" but could " +
                        "not register it; it will be re-adopted on the next boot.", loadTarget, ns.Name);
                }
            }

            _logger.LogInformation("Namespace \"{Namespace}\" loaded: {VertexCount} vertices, {EdgeCount} edges.",
                ns.Name, ns.Engine.VertexCount, ns.Engine.EdgeCount);
        }

        private static bool PathsEqual(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            // Run the shutdown save + registration at most once (see _stopped).
            if (System.Threading.Interlocked.Exchange(ref _stopped, 1) == 1)
            {
                return Task.CompletedTask;
            }

            if (_options.Volatile || !_options.SaveOnShutdown)
            {
                // Volatile: nothing to persist. SaveOnShutdown=false: the per-commit WAL already made
                // every committed transaction durable; the next boot replays it. Either way, no save.
                return Task.CompletedTask;
            }

            // Save every namespace and register ONE spanning entry - the same shape as PUT /save/all,
            // so the next boot restores the whole namespace set from a single restore point. The
            // whole loop runs under the collection's dispose gate: container disposal of the engines
            // can race this StopAsync during host teardown, and the saves must win that race.
            var ranBeforeDispose = _namespaces.TryRunBeforeDispose(() =>
            {
                var members = new List<(String Name, IFallen8 Engine, String Location)>();
                foreach (var ns in _namespaces.Snapshot())
                {
                    var checkpointPath = ReferenceEquals(ns, _namespaces.Default)
                        ? _options.ResolveCheckpointPath()
                        : Path.Combine(_namespaces.DirectoryFor(ns), _options.CheckpointBaseName);

                    try
                    {
                        _logger.LogInformation("Saving namespace \"{Namespace}\" to \"{CheckpointPath}\" on shutdown.",
                            ns.Name, checkpointPath);

                        var saveTx = new SaveTransaction { Path = checkpointPath };
                        var saveInfo = ns.Engine.EnqueueTransaction(saveTx);
                        saveInfo.WaitUntilFinished();

                        if (saveInfo.TransactionState == TransactionState.RolledBack)
                        {
                            // A failed shutdown save is NOT data loss: the atomic temp+rename means a truncated
                            // save never becomes the loadable checkpoint, and committed work is already durable
                            // in the WAL. Log loudly and keep saving the other namespaces.
                            _logger.LogError(saveInfo.Error, "The shutdown save of namespace \"{Namespace}\" rolled back; its " +
                                "committed transactions remain durable in the write-ahead log and will be replayed on the " +
                                "next boot.", ns.Name);
                        }
                        else
                        {
                            members.Add((ns.Name, ns.Engine, saveTx.ActualPath ?? checkpointPath));
                        }
                    }
                    catch (Exception ex)
                    {
                        // Never let a shutdown-save failure prevent the host from stopping; WAL durability holds.
                        _logger.LogError(ex, "The shutdown save of namespace \"{Namespace}\" threw; its committed transactions " +
                            "remain durable in the write-ahead log and will be replayed on the next boot.", ns.Name);
                    }
                }

                if (members.Count > 0)
                {
                    try
                    {
                        _saveGames.RegisterAll(members, "shutdown");
                        _logger.LogInformation("Fallen-8 shutdown save complete ({Count} namespaces).", members.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "The Fallen-8 shutdown save completed but could not be registered in the save-game registry.");
                    }
                }
            });

            if (!ranBeforeDispose)
            {
                _logger.LogWarning("The Fallen-8 engines were already disposed when the shutdown save ran; no checkpoint was " +
                    "written. Committed transactions remain durable in the write-ahead logs and will be replayed on the next boot.");
            }

            return Task.CompletedTask;
        }
    }
}
