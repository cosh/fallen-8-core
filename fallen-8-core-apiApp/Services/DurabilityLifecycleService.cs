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
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Helper;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.App.Services
{
    /// <summary>
    ///   Owns the load-on-start / save-on-stop durability lifecycle of the hosted API (feature
    ///   hosted-durability-lifecycle): on boot it discovers and loads the latest checkpoint (which
    ///   replays the paired write-ahead log), and on a clean shutdown it saves so the next boot is up
    ///   to date and the WAL is reset. It reuses the existing Save/Load transactions on the single
    ///   writer thread - it introduces no new mutation path. In volatile mode it does nothing.
    /// </summary>
    public sealed class DurabilityLifecycleService : IHostedService
    {
        private readonly IFallen8 _fallen8;
        private readonly Fallen8DurabilityOptions _options;
        private readonly SaveGameRegistry _saveGames;
        private readonly ILogger<DurabilityLifecycleService> _logger;

        // StopAsync must run its save+register at most once: the host can invoke it more than once
        // (double dispose in tests, layered shutdown), and a second pass could snapshot a
        // mid-teardown (possibly empty) graph and register it as the newest save game.
        private int _stopped;

        public DurabilityLifecycleService(IFallen8 fallen8, IOptions<Fallen8DurabilityOptions> options,
            SaveGameRegistry saveGames, ILogger<DurabilityLifecycleService> logger)
        {
            _fallen8 = fallen8;
            _options = options.Value;
            _saveGames = saveGames;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (_options.Volatile)
            {
                _logger.LogWarning("Fallen-8 durability is in VOLATILE mode (Fallen8:Durability:Volatile=true): " +
                    "no checkpoint is loaded on start and none is saved on shutdown; a restart loses all data.");
                return Task.CompletedTask;
            }

            var storageDir = _options.ResolveStorageDirectory();

            // Registry-driven boot (feature save-games FR-8): the save-game registry - NOT directory
            // discovery - decides what loads. An empty registry starts empty even if checkpoint files
            // sit in the storage directory; otherwise the newest registered save game loads.
            var newest = _saveGames.Newest();

            if (newest == null)
            {
                // Migration hint (FR-11): pre-registry deployments have checkpoint files but no metadata.
                if (CheckpointDiscovery.TryFindLatestCheckpoint(storageDir, _options.CheckpointBaseName, out var orphan))
                {
                    _logger.LogWarning("Fallen-8 found checkpoint files (e.g. \"{Checkpoint}\") but the save-game registry " +
                        "is empty, so startup begins EMPTY (registry-driven boot). To adopt an existing checkpoint, load it " +
                        "once via PUT /load (or the Save games screen) - it is then registered permanently.", orphan);
                }
                else
                {
                    _logger.LogInformation("No registered save games; starting with the current in-memory state " +
                        "({VertexCount} vertices, {EdgeCount} edges) - any unanchored WAL was replayed at construction.",
                        _fallen8.VertexCount, _fallen8.EdgeCount);
                }

                return Task.CompletedTask;
            }

            // Crash-window reconciliation (FR-10): a save completes and becomes durable on disk (the WAL
            // is re-anchored to it inside the save transaction) and only THEN is its registry entry
            // written. A crash in that window leaves a complete checkpoint on disk that the registry does
            // not know. If discovery finds a checkpoint strictly newer than the newest REGISTERED entry,
            // it is exactly such an orphan - adopt it (load + register) so a crash never silently reverts
            // to an older save. The margin absorbs the normal case (the file is written a moment before
            // its savedAt is stamped, so a registered checkpoint is never mistaken for an orphan).
            var loadTarget = newest.Location;
            var adoptOrphan = false;
            if (CheckpointDiscovery.TryFindLatestCheckpoint(storageDir, _options.CheckpointBaseName, out var diskCheckpoint))
            {
                var diskRegistered = _saveGames.GetAll().Exists(e => PathsEqual(e.Location, diskCheckpoint));
                var newestFileExists = File.Exists(newest.Location);
                // The discovered checkpoint is newer when the newest registered entry's own file is gone,
                // or when the on-disk file's mtime is strictly newer (comparing actual file times avoids
                // any skew between a file's write time and its savedAt stamp).
                var diskNewer = !newestFileExists
                    || File.GetLastWriteTimeUtc(diskCheckpoint) > File.GetLastWriteTimeUtc(newest.Location);
                if (!diskRegistered && diskNewer)
                {
                    _logger.LogWarning("A checkpoint on disk (\"{Disk}\") is newer than the newest registered save game " +
                        "{Id} (saved {SavedAt}) and is not in the registry; adopting it - it is a durable save whose " +
                        "registration did not complete (crash window). It will be registered after loading.",
                        diskCheckpoint, newest.Id, newest.SavedAt);
                    loadTarget = diskCheckpoint;
                    adoptOrphan = true;
                }
            }

            if (!adoptOrphan)
            {
                _logger.LogInformation("Loading the newest registered save game {Id} from \"{Location}\" (saved {SavedAt}).",
                    newest.Id, newest.Location, newest.SavedAt);
            }

            // A missing primary checkpoint file does NOT roll the load back (the engine's Load treats
            // a non-existent file as a no-op), so it would silently serve an empty graph. Fail startup
            // loudly here instead (FR-9) - the operator restores the files or removes the entry.
            if (!File.Exists(loadTarget))
            {
                throw new InvalidOperationException(
                    "The newest registered save game \"" + newest.Id + "\" points at \"" + loadTarget +
                    "\", which does not exist; startup is aborted so a missing save is never masked by an empty " +
                    "graph. Restore its files, or remove the entry (DELETE /savegames/" + newest.Id + ") and restart.");
            }

            var loadInfo = _fallen8.EnqueueTransaction(new LoadTransaction { Path = loadTarget });
            loadInfo.WaitUntilFinished();

            if (loadInfo.TransactionState == TransactionState.RolledBack)
            {
                // Fail startup loudly (FR-9): a missing/corrupt newest save game is never masked by an
                // empty graph, and we never silently fall back to an older entry (that would resurrect
                // stale data). The operator restores the files or removes the entry (DELETE /savegames/{id}).
                throw new InvalidOperationException(
                    "Fallen-8 failed to load the save game at \"" + loadTarget +
                    "\"; startup is aborted. Restore its files, or remove the entry (DELETE /savegames/" + newest.Id +
                    ") and restart to use the next-newest (or start empty).", loadInfo.Error);
            }

            if (adoptOrphan)
            {
                // Register the adopted orphan now that the graph is loaded (so its KPIs are correct).
                try
                {
                    _saveGames.RegisterImportIfUnknown(_fallen8, loadTarget);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Adopted the orphan checkpoint \"{Disk}\" but could not register it; it will be " +
                        "re-adopted on the next boot.", loadTarget);
                }
            }

            _logger.LogInformation("Fallen-8 save game loaded: {VertexCount} vertices, {EdgeCount} edges.",
                _fallen8.VertexCount, _fallen8.EdgeCount);

            return Task.CompletedTask;
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

            var checkpointPath = _options.ResolveCheckpointPath();

            try
            {
                _logger.LogInformation("Saving the Fallen-8 checkpoint to \"{CheckpointPath}\" on shutdown.", checkpointPath);

                var saveTx = new SaveTransaction { Path = checkpointPath };
                var saveInfo = _fallen8.EnqueueTransaction(saveTx);
                saveInfo.WaitUntilFinished();

                if (saveInfo.TransactionState == TransactionState.RolledBack)
                {
                    // A failed shutdown save is NOT data loss: the atomic temp+rename means a truncated
                    // save never becomes the loadable checkpoint, and committed work is already durable
                    // in the WAL. Log loudly and let shutdown proceed.
                    _logger.LogError(saveInfo.Error, "The Fallen-8 shutdown save rolled back; committed transactions remain " +
                        "durable in the write-ahead log and will be replayed on the next boot.");
                }
                else
                {
                    // Register the shutdown checkpoint so the next boot loads it (feature save-games FR-4).
                    var actualPath = saveTx.ActualPath ?? checkpointPath;
                    try
                    {
                        _saveGames.Register(_fallen8, actualPath, "shutdown");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "The Fallen-8 shutdown save completed but could not be registered in the save-game registry.");
                    }
                    _logger.LogInformation("Fallen-8 shutdown save complete.");
                }
            }
            catch (Exception ex)
            {
                // Never let a shutdown-save failure prevent the host from stopping; WAL durability holds.
                _logger.LogError(ex, "The Fallen-8 shutdown save threw; committed transactions remain durable in the " +
                    "write-ahead log and will be replayed on the next boot.");
            }

            return Task.CompletedTask;
        }
    }
}
