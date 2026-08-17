// MIT License
//
// DurabilityLifecycleService.cs
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
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.App.Configuration;
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
        private readonly NamespaceLoader _loader;
        private readonly ILogger<DurabilityLifecycleService> _logger;

        // StopAsync must run its save+register at most once: the host can invoke it more than once
        // (double dispose in tests, layered shutdown), and a second pass could snapshot a
        // mid-teardown (possibly empty) graph and register it as the newest save game.
        private int _stopped;

        /// <summary>The readiness flag behind GET /readyz (feature observability); optional so
        /// direct test construction stays unchanged.</summary>
        private readonly StartupState _startupState;

        public DurabilityLifecycleService(Fallen8Namespaces namespaces, IOptions<Fallen8DurabilityOptions> options,
            SaveGameRegistry saveGames, NamespaceLoader loader, ILogger<DurabilityLifecycleService> logger,
            StartupState startupState = null)
        {
            _namespaces = namespaces;
            _options = options.Value;
            _saveGames = saveGames;
            _loader = loader;
            _logger = logger;
            _startupState = startupState;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_options.Volatile)
            {
                _logger.LogWarning("Fallen-8 durability is in VOLATILE mode (Fallen8:Durability:Volatile=true): " +
                    "no checkpoint is loaded on start and none is saved on shutdown; a restart loses all data.");

                // Nothing to load: ready immediately (feature observability).
                _startupState?.MarkReady();
                return;
            }

            // Per-namespace registry-driven boot (save-games FR-8 generalized): each namespace
            // loads the newest entry CONTAINING it; a namespace no entry contains starts from its
            // WAL-replayed construction state. The registry - never directory discovery - decides.
            foreach (var ns in _namespaces.Snapshot())
            {
                // A namespace the collection deliberately did not load has no engine to restore into
                // (feature namespace-startup-load); the collection has already logged why. This skip
                // is also what scopes save-games FR-9's loud whole-process abort below to the
                // SELECTED namespaces: a rotted or missing checkpoint under a namespace nobody asked
                // for must not keep the whole server down.
                if (!ns.IsLoaded)
                {
                    continue;
                }

                await StartNamespaceAsync(ns).ConfigureAwait(false);
            }

            // Load-at-startup completed for every namespace: the server is ready (feature
            // observability). The throwing failure paths deliberately never mark ready.
            _startupState?.MarkReady();
        }

        /// <summary>
        ///   Restores one namespace at boot through the shared <see cref="NamespaceLoader"/>, and
        ///   applies THE BOOT'S failure contract to the result: an unrestorable checkpoint aborts the
        ///   whole process (save-games FR-9), because a machine that comes up serving an empty graph
        ///   where a save was expected is worse than one that does not come up at all.
        ///   <para>That contract is now the only thing this method adds. The load itself moved to the
        ///   loader so runtime activation can share it (feature namespace-startup-load §4.8), where
        ///   the identical failure must fail ONE request and leave the server standing - the reason
        ///   the routine reports rather than throws.</para>
        /// </summary>
        private async Task StartNamespaceAsync(Namespace ns)
        {
            var (outcome, detail, error) = await _loader.LoadNewestRegisteredAsync(ns, ns.Engine).ConfigureAwait(false);

            // Only a FAILED restore aborts. Unregistered checkpoint files (save-games FR-11) do not:
            // this engine is already constructed and published, and the loader's contract says why
            // that makes the boot the caller that proceeds while an activation refuses.
            if (outcome == NamespaceRestoreOutcome.Failed)
            {
                throw new InvalidOperationException(detail + " Startup is aborted; restart once its files or its " +
                    "registry entry are in order.", error);
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
                var members = new List<(String Name, String Id, IFallen8 Engine, String Location)>();
                foreach (var ns in _namespaces.Snapshot())
                {
                    // THE DATA-LOSS GUARD (feature namespace-startup-load §5). A namespace with no
                    // resident engine is never a member of a save. Saving one would be catastrophic
                    // rather than merely useless: Fallen8.Save resets the write-ahead log to a bare
                    // header, so every post-checkpoint delta the log still held is gone with no
                    // other artifact carrying it, and the empty-but-complete checkpoint it writes
                    // gets registered as the NEWEST entry for that id, so the next boot loads the
                    // empty one. Both halves are silent today. Nothing is written and nothing is
                    // registered instead, so the data on disk stays exactly as the last process
                    // that actually held this namespace left it.
                    if (!ns.IsLoaded)
                    {
                        _logger.LogInformation("Skipping the shutdown save of namespace \"{Namespace}\": it is not loaded " +
                            "in this process, so its checkpoint and write-ahead log are left untouched.", ns.Name);
                        continue;
                    }

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
                            members.Add((ns.Name, ns.Id, ns.Engine, saveTx.ActualPath ?? checkpointPath));
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
