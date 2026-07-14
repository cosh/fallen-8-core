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
        private readonly ILogger<DurabilityLifecycleService> _logger;

        public DurabilityLifecycleService(IFallen8 fallen8, IOptions<Fallen8DurabilityOptions> options,
            ILogger<DurabilityLifecycleService> logger)
        {
            _fallen8 = fallen8;
            _options = options.Value;
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

            if (CheckpointDiscovery.TryFindLatestCheckpoint(storageDir, _options.CheckpointBaseName, out var checkpointPath))
            {
                _logger.LogInformation("Loading the latest Fallen-8 checkpoint from \"{CheckpointPath}\".", checkpointPath);

                var loadInfo = _fallen8.EnqueueTransaction(new LoadTransaction { Path = checkpointPath });
                loadInfo.WaitUntilFinished();

                if (loadInfo.TransactionState == TransactionState.RolledBack)
                {
                    // Fail startup loudly rather than silently serving an empty graph over a corrupt or
                    // unloadable checkpoint (the atomic/clean-reject envelope already rejected it).
                    throw new InvalidOperationException(
                        "Fallen-8 failed to load the checkpoint at \"" + checkpointPath + "\"; startup is aborted so a corrupt " +
                        "or unloadable save is never masked by an empty graph.", loadInfo.Error);
                }

                _logger.LogInformation("Fallen-8 checkpoint loaded: {VertexCount} vertices, {EdgeCount} edges.",
                    _fallen8.VertexCount, _fallen8.EdgeCount);
            }
            else
            {
                // No checkpoint. Any UNANCHORED write-ahead log was already replayed during construction
                // (Fallen8.EnableWriteAheadLog), so a crash-before-first-save is still recovered; a truly
                // empty storage directory just starts empty.
                _logger.LogInformation("No Fallen-8 checkpoint found in \"{StorageDirectory}\"; starting with the " +
                    "current in-memory state ({VertexCount} vertices, {EdgeCount} edges) - any unanchored WAL was replayed at construction.",
                    storageDir, _fallen8.VertexCount, _fallen8.EdgeCount);
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
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

                var saveInfo = _fallen8.EnqueueTransaction(new SaveTransaction { Path = checkpointPath });
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
