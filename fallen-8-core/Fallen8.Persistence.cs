// MIT License
//
// Fallen8.Persistence.cs
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
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Persistency;
using NoSQL.GraphDB.Core.StoredQueries;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Core
{
    public sealed partial class Fallen8
    {
        /// <summary>
        ///   Opens (or creates) the write-ahead log and, if it holds committed transactions that were
        ///   never captured in a snapshot (an "unanchored" log), replays them onto the empty initial
        ///   graph so a crash before the first Save still recovers those transactions. Runs during
        ///   construction, before the instance is handed out, so the replay is single-threaded.
        /// </summary>
        private void EnableWriteAheadLog(string walPath)
        {
            _wal = new WriteAheadLog(walPath, CreateLogger<WriteAheadLog>());

            if (_wal.IsUnanchored)
            {
                var baseline = (int)_wal.BaselineCurrentId;
                SetSnapshotCountForReplay(baseline);
                _currentId = baseline;
                ReplayWriteAheadLog();
                RecalculateGraphElementCounter();
            }
            else if (_wal.IsAnchored)
            {
                // The adopted log pairs with a snapshot that has NOT been loaded yet. Suspend logging
                // until that snapshot is Loaded (or a Save re-baselines), so a mutation made before the
                // paired Load is not recorded against the empty initial graph / wrong baseline
                // (feature crash-durability-hardening D3). Recommended usage is still: Load, then mutate.
                _walAwaitingPairedLoad = true;
            }
        }

        internal string Save(string path, int savePartitions = 5)
        {
            // Cold-path instrumentation (feature observability): a save is seconds of I/O, so
            // the unconditional timestamp is noise; the span is null when nothing samples.
            using var span = Diagnostics.Fallen8Diagnostics.Source.StartActivity("fallen8.checkpoint.save");
            span?.SetTag("checkpoint.partitions", savePartitions);
            var start = System.Diagnostics.Stopwatch.GetTimestamp();

            string actualPath;
            try
            {
                actualPath = _persistencyFactory.Save(this, path, savePartitions);
            }
            catch
            {
                Metrics?.RecordCheckpointFailure("save");
                span?.SetStatus(System.Diagnostics.ActivityStatusCode.Error);
                throw;
            }

            // WAL compose (spec P4/§5): the snapshot is now DURABLY committed (the factory writes it
            // via temp + fsync + atomic rename). Only now reset the log to build upon this snapshot -
            // recording the current id-space high-water mark as the new baseline and pairing it with
            // the snapshot's actual path, discarding the pre-snapshot entries it superseded. Doing the
            // reset strictly AFTER the snapshot is durable is what makes "snapshot-then-truncate"
            // crash-safe: a crash in between leaves the log still paired with the PREVIOUS snapshot, so
            // loading the NEW snapshot will not replay it (no double-apply) while the new snapshot
            // already contains everything committed up to this save.
            _wal?.ResetToSnapshot(actualPath, _currentId);

            // A successful Save re-baselines the log against this snapshot: it is no longer awaiting a
            // paired load, and its failure fence (if any) was cleared by ResetToSnapshot
            // (feature crash-durability-hardening D1/D3).
            _walAwaitingPairedLoad = false;

            var bytes = MeasureCheckpointBytes(actualPath);
            Metrics?.RecordCheckpointSave(
                System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds, bytes);
            span?.SetTag("checkpoint.bytes", bytes);

            return actualPath;
        }

        /// <summary>
        ///   Publishes the flat graph-element array produced by a load into the master store.
        ///   Invoked by the persistency factory once every element (and its edge fix-up) is
        ///   built, but BEFORE indices and services are rehydrated, because they resolve element
        ///   ids through <see cref="TryGetGraphElement" /> against the published store. The array
        ///   is dense and id-ordered (index == id), matching the on-disk format.
        /// </summary>
        internal void PublishLoadedGraphElements(AGraphElementModel[] graphElements)
        {
            // The loaded array is dense and id-ordered (index == id) and may contain null slots
            // for ids that were null/removed at save time (readers filter them). Copy it into the
            // segmented layout and publish atomically.
            _snapshot = BuildSnapshotFromDenseArray(graphElements, graphElements.Length);
        }

        #region write-ahead log (opt-in durability between snapshots)

        /// <summary>
        ///   Appends a committed transaction to the write-ahead log. Called by the transaction
        ///   manager on the single writer thread AFTER the transaction has reached its committed
        ///   (Finished) terminal state and BEFORE its input is released, so the still-present
        ///   definition can be serialized. A no-op when the WAL is disabled or while replaying (a
        ///   replayed operation must not be re-logged). Only data-mutating transactions and the
        ///   id-space lifecycle transactions (Trim/TabulaRasa) are logged; others are ignored.
        ///
        ///   The append fsyncs before returning, so a committed transaction's log entry is durable
        ///   before its <c>WaitUntilFinished()</c> returns (the task completes only after this call).
        /// </summary>
        /// <summary>
        ///   Appends a committed transaction to the write-ahead log. Returns whether the transaction is
        ///   durable in the log: <c>true</c> when the WAL is disabled (durability is then via the next
        ///   Save) or the entry was appended and fsynced; <c>false</c> when logging is suspended because
        ///   the log is anchored and awaiting its paired snapshot Load (D3) or the failure fence has
        ///   tripped (D1). A first append failure throws (the caller records it); once the fence is
        ///   tripped, subsequent calls return <c>false</c> without throwing.
        /// </summary>
        internal bool LogCommittedTransaction(ATransaction tx)
        {
            var wal = _wal;
            if (wal == null)
            {
                return true;
            }

            if (_walSuspended)
            {
                // Replaying: the operation came from the log and must not be re-appended. Not a new
                // commit, so durability is not degraded.
                return true;
            }

            if (_walAwaitingPairedLoad)
            {
                // D3: an anchored log is waiting for its paired snapshot; a pre-load mutation is applied
                // in memory but must not be logged against the wrong baseline. Report it non-durable.
                return false;
            }

            if (wal.HasFailed)
            {
                // D1: the log is degraded until the next Save; the transaction stays committed but is
                // not durable in the log.
                return false;
            }

            if (!WalTransactionCodec.TryGetEntryType(tx, out var type))
            {
                // Not a loggable transaction (e.g. Save/Load); durability is via the snapshot.
                return true;
            }

            // Buffer the frame for the current commit group (feature write-path-throughput); it becomes
            // durable when the manager calls FlushWal after the batch. Returning true here means
            // "buffered"; the caller ANDs it with FlushWal's result for the final durability.
            wal.AppendBuffered(WalTransactionCodec.SerializeEntry(type, tx));
            return true;
        }

        /// <summary>
        ///   Flushes the current write-ahead-log commit group with a single fsync and returns whether
        ///   the group is durable (feature write-path-throughput). Called by the transaction manager
        ///   once per drained batch, AFTER every transaction body in the batch has buffered its frame
        ///   and BEFORE their completion signals are set - so the durable-before-ack contract holds,
        ///   just amortised. Returns true when the WAL is disabled (durability is via the snapshot).
        /// </summary>
        internal bool FlushWal()
        {
            var wal = _wal;
            if (wal == null)
            {
                return true;
            }

            // Honest flush metrics (feature observability): only a REAL flush attempt - pending
            // frames on a non-tripped fence - is measured. The empty fast path would pollute the
            // duration percentiles with ~0s samples, and the already-degraded path would count
            // ONE real failure as N (every group "fails" until the next Save); the degraded state
            // is what fallen8.wal.degraded and .nondurable report. Timestamp is Enabled-gated.
            var isRealAttempt = wal.PendingFrameCount > 0 && !wal.HasFailed;
            var metrics = Metrics;
            var start = isRealAttempt && metrics != null && metrics.WalFlushDurationEnabled
                ? System.Diagnostics.Stopwatch.GetTimestamp()
                : 0L;

            var durable = wal.FlushGroup();

            if (start != 0L)
            {
                metrics.RecordWalFlushDuration(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds);
            }
            if (isRealAttempt && !durable)
            {
                metrics?.RecordWalFlushFailure();
            }

            return durable;
        }

        /// <summary>
        ///   Appends a payload-less lifecycle marker (used for an automatic Trim, which is not a
        ///   transaction). A no-op when the WAL is disabled, while replaying, while awaiting a paired
        ///   load (D3), or once the failure fence has tripped (D1).
        /// </summary>
        private void LogWriteAheadLogMarker(Persistency.WalEntryType type)
        {
            var wal = _wal;
            if (wal == null || _walSuspended || _walAwaitingPairedLoad || wal.HasFailed)
            {
                return;
            }

            // Buffer the marker in commit order with the surrounding group; it is flushed with them
            // (feature write-path-throughput).
            wal.AppendBuffered(WalTransactionCodec.SerializeEntry(type, null));
        }

        /// <summary>
        ///   Re-executes the write-ahead log's entries, in commit order, against the current graph to
        ///   reconstruct the committed state. Runs with <see cref="_walSuspended" /> set so the
        ///   re-executed operations are not themselves re-logged. Correctness rests on id-determinism
        ///   established by the caller: <see cref="_currentId" /> is restored to the log's baseline and
        ///   the store is padded so <c>id == index</c> holds, so replayed creates re-assign the SAME
        ///   ids and replayed edges/removals/property-changes resolve the SAME elements as before the
        ///   crash. A torn/corrupt tail is handled by <see cref="WriteAheadLog.ReadEntries" /> (it
        ///   stops at the last complete entry), so this loop only ever sees whole, CRC-valid entries.
        /// </summary>
        private int ReplayWriteAheadLog()
        {
            _walSuspended = true;
            try
            {
                var replayed = 0;
                foreach (var payload in _wal.ReadEntries())
                {
                    Persistency.WalEntryType type;
                    ATransaction tx;
                    try
                    {
                        tx = WalTransactionCodec.Deserialize(payload, out type);
                    }
                    catch (Exception ex)
                    {
                        // A CRC-valid entry that nonetheless fails to decode indicates a genuine
                        // format problem; stop replay at the last good entry rather than risk
                        // misapplying it.
                        _logger.LogError(ex, "A write-ahead-log entry could not be decoded; recovery stops at the last good entry ({Count} replayed).", replayed);
                        break;
                    }

                    if (tx != null)
                    {
                        // Fail-stop for CORE DATA entries (feature crash-durability-hardening D4): a
                        // false return or a thrown exception is treated exactly like a decode failure -
                        // stop at the last good entry, because continuing would misapply every later
                        // entry against a diverged id space. Subgraph and stored-query entries
                        // allocate no ids (derived / library state), so a RemoveSubGraph or
                        // RemoveStoredQuery that fails skips-and-continues (like the CreateSubGraph /
                        // RegisterStoredQuery paths below) rather than halting recovery.
                        var isDerivedSubGraphEntry = type == Persistency.WalEntryType.RemoveSubGraph ||
                                                     type == Persistency.WalEntryType.RemoveStoredQuery;
                        bool applied;
                        try
                        {
                            applied = tx.TryExecute(this);
                        }
                        catch (Exception ex)
                        {
                            if (isDerivedSubGraphEntry)
                            {
                                _logger.LogWarning(ex, "Re-executing a logged {Type} entry during recovery threw; skipping it and continuing ({Count} replayed).", type, replayed);
                                replayed++;
                                continue;
                            }

                            _logger.LogError(ex, "Re-executing a logged {Type} transaction during recovery threw; recovery STOPS at the last good entry ({Count} replayed).", type, replayed);
                            break;
                        }

                        if (!applied)
                        {
                            if (isDerivedSubGraphEntry)
                            {
                                _logger.LogWarning("Re-executing a logged {Type} entry during recovery returned false; skipping it and continuing ({Count} replayed).", type, replayed);
                            }
                            else
                            {
                                _logger.LogError("Re-executing a logged {Type} transaction during recovery returned false; recovery STOPS at the last good entry ({Count} replayed).", type, replayed);
                                break;
                            }
                        }
                    }
                    else if (type == Persistency.WalEntryType.Trim)
                    {
                        Trim_internal();
                    }
                    else if (type == Persistency.WalEntryType.TabulaRasa)
                    {
                        TabulaRasa_internal();
                    }
                    else if (type == Persistency.WalEntryType.CreateSubGraph)
                    {
                        ReplaySubGraphCreate(payload);
                    }
                    else if (type == Persistency.WalEntryType.RegisterStoredQuery)
                    {
                        ReplayStoredQueryRegister(payload);
                    }

                    replayed++;
                }

                _logger.LogInformation("Recovered {Count} transaction(s) from the write-ahead log.", replayed);
                return replayed;
            }
            finally
            {
                _walSuspended = false;
            }
        }

        /// <summary>
        ///   Replays one logged <see cref="Persistency.WalEntryType.CreateSubGraph" /> entry:
        ///   recompiles the persisted recipe (via the registered <see cref="SubGraphRecipeCompiler" />)
        ///   and re-executes the equivalent create against the graph as replayed so far. Because
        ///   entries replay in commit order, every vertex/edge the subgraph matched already exists and
        ///   a nested subgraph's source is already registered (resolved by its stable name), so the
        ///   recomputed subgraph matches the identical elements without any id remapping. Any problem -
        ///   an undecodable entry, no compiler registered, a compile failure, or a create that returns
        ///   false - is logged and SKIPPED so recovery continues with later entries (subgraphs are
        ///   rebuildable derived state). Subgraph creation allocates no ids in this graph, so it does
        ///   not perturb the vertex/edge id-determinism the surrounding replay relies on.
        ///
        ///   <para><b>Trust boundary (feature crash-durability-hardening D7).</b> Replaying a recipe
        ///   RECOMPILES the persisted C# fragment via Roslyn and runs it in-process. The WAL/snapshot
        ///   CRC is <em>integrity</em> (against corruption), NOT <em>authenticity</em> (against
        ///   tampering): anyone who can write the save/WAL files gains code execution in the loading
        ///   process at next start. The save/WAL directory is therefore a trust boundary equivalent to
        ///   the application binaries; the mitigation is operational (restrict write access to that
        ///   directory). Recovery reuses the content-keyed compile cache (see
        ///   <c>collectible-codegen-assemblies</c>), so K subgraphs sharing one recipe spec compile
        ///   once and recovery time scales with the number of DISTINCT specs, not the subgraph count.</para>
        /// </summary>
        private void ReplaySubGraphCreate(byte[] payload)
        {
            if (!WalTransactionCodec.TryDecodeSubGraphCreate(payload, out var recipe, out var sourceSubGraphName))
            {
                _logger.LogWarning("A logged CreateSubGraph entry could not be decoded during recovery and was skipped.");
                return;
            }

            var compiler = SubGraphRecipeCompiler;
            if (compiler == null)
            {
                _logger.LogWarning(
                    "The write-ahead log holds a subgraph \"{Name}\" but no recipe compiler is registered; it is skipped on replay. Register IFallen8.SubGraphRecipeCompiler before load to recover logged subgraphs.",
                    recipe.Name);
                return;
            }

            // The create transaction always uses the default subgraph algorithm, so a recipe naming a
            // different algorithm cannot be reproduced faithfully via this path. In the current engine
            // the transaction/REST create is BFS-only, so this never fires; the guard makes the
            // assumption visible if a future multi-algorithm create regresses it.
            if (!string.IsNullOrEmpty(recipe.AlgorithmPluginName) &&
                !string.Equals(recipe.AlgorithmPluginName,
                    Algorithms.SubGraph.BreadthFirstSearchSubgraphAlgorithm.AlgorithmPluginName, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Logged subgraph \"{Name}\" was created with algorithm \"{Algorithm}\", but replay recreates it with the default algorithm.",
                    recipe.Name, recipe.AlgorithmPluginName);
            }

            // Compile + re-execute are guarded: a registered ISubGraphRecipeCompiler is third-party
            // code, and if it throws (violating the Try contract) the throw must NOT abort recovery of
            // later entries. Any failure here - a compile failure, a create returning false, or an
            // unexpected throw from the compiler or the create - is warned and skipped so recovery
            // continues; subgraphs are rebuildable derived state. (The built-in compiler + factory
            // already catch internally, so this guard only matters for a misbehaving custom compiler.)
            try
            {
                if (!compiler.TryCompile(recipe, out var definition, out var error))
                {
                    _logger.LogWarning(
                        "Could not compile the recipe for logged subgraph \"{Name}\" during recovery: {Error}; it is skipped.",
                        recipe.Name, error);
                    return;
                }

                var tx = new CreateSubGraphTransaction
                {
                    Definition = definition,
                    SourceSubGraphName = string.IsNullOrEmpty(sourceSubGraphName) ? null : sourceSubGraphName,
                    SpecificationJson = recipe.SpecificationJson
                };

                if (!tx.TryExecute(this))
                {
                    _logger.LogWarning(
                        "Re-executing a logged CreateSubGraph transaction for subgraph \"{Name}\" during recovery returned false (reason {Reason}); it is skipped.",
                        recipe.Name, tx.FailureReason);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Recovering logged subgraph \"{Name}\" threw during recovery; it is skipped and recovery continues with later entries.",
                    recipe.Name);
            }
        }

        /// <summary>
        ///   Builds the in-memory entry for a persisted stored query definition (feature
        ///   stored-query-library): recompiles the source through the registered
        ///   <see cref="StoredQueryCompiler" /> when one is present. Unlike subgraph recipes, a
        ///   stored query is OPERATOR-REGISTERED state, not derived state - so a failure never
        ///   drops the definition: a compile failure (or a compiler that throws, violating its Try
        ///   contract) keeps the entry as <see cref="StoredQueryCompileState.Failed" /> with its
        ///   diagnostics (visible via list/get, 409 on invoke, recoverable by delete+re-register),
        ///   and a missing compiler keeps it as source-only. Loud, never silent loss.
        /// </summary>
        private StoredQueryEntry BuildRehydratedStoredQueryEntry(StoredQueryDefinition definition)
        {
            var compiler = StoredQueryCompiler;
            if (compiler == null)
            {
                return new StoredQueryEntry(definition, StoredQueryCompileState.SourceOnly, null);
            }

            try
            {
                if (compiler.TryCompile(definition, out var artifact, out var error))
                {
                    return new StoredQueryEntry(definition, StoredQueryCompileState.Compiled, artifact);
                }

                _logger.LogError(
                    "Stored query \"{Name}\" failed to recompile on load and is kept as Failed (delete + re-register to recover): {Error}",
                    definition.Name, error);
                return new StoredQueryEntry(definition, StoredQueryCompileState.Failed, null, error);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Recompiling stored query \"{Name}\" threw; it is kept as Failed (delete + re-register to recover).",
                    definition.Name);
                return new StoredQueryEntry(definition, StoredQueryCompileState.Failed, null, ex.Message);
            }
        }

        /// <summary>
        ///   Replaces the stored query library with the definitions of a loaded snapshot manifest,
        ///   eagerly recompiling each via <see cref="BuildRehydratedStoredQueryEntry" />. Warns once
        ///   when definitions exist but no compiler is registered (embedded engine use: entries load
        ///   as source-only; there is no invocation surface without a hosting layer anyway).
        /// </summary>
        private void RehydrateStoredQueries(List<StoredQueryDefinition> definitions)
        {
            var entries = new List<StoredQueryEntry>(definitions.Count);

            if (definitions.Count > 0 && StoredQueryCompiler == null)
            {
                _logger.LogWarning(
                    "The savegame holds {Count} stored query definition(s) but no stored query compiler is registered; they are loaded as source-only. Register IFallen8.StoredQueryCompiler before load to recompile them.",
                    definitions.Count);
            }

            foreach (var definition in definitions)
            {
                if (definition == null || !StoredQueryLibrary.IsValidName(definition.Name))
                {
                    _logger.LogError("A stored query definition in the manifest has an invalid name and was skipped.");
                    continue;
                }

                entries.Add(BuildRehydratedStoredQueryEntry(definition));
            }

            StoredQueries.ReplaceAll(entries);

            if (entries.Count > 0)
            {
                _logger.LogInformation("Rehydrated {Count} stored query definition(s).", entries.Count);
            }
        }

        /// <summary>
        ///   Replays one logged <see cref="Persistency.WalEntryType.RegisterStoredQuery" /> entry:
        ///   decodes the persisted definition, recompiles it (keep-and-mark-Failed on failure, per
        ///   <see cref="BuildRehydratedStoredQueryEntry" /> - operator state is never silently
        ///   dropped) and re-executes the equivalent registration against the library as replayed so
        ///   far, in commit order. An undecodable entry is warned and skipped so recovery continues;
        ///   registrations allocate no element ids, so skipping one cannot perturb the surrounding
        ///   replay's id-determinism. The D7 trust-boundary note on <see cref="ReplaySubGraphCreate" />
        ///   applies identically: replay RECOMPILES persisted C# via Roslyn in-process, so the
        ///   save/WAL directory is a trust boundary equivalent to the application binaries.
        /// </summary>
        private void ReplayStoredQueryRegister(byte[] payload)
        {
            if (!WalTransactionCodec.TryDecodeStoredQueryRegister(payload, out var definition))
            {
                _logger.LogWarning("A logged RegisterStoredQuery entry could not be decoded during recovery and was skipped.");
                return;
            }

            try
            {
                var tx = new RegisterStoredQueryTransaction
                {
                    Entry = BuildRehydratedStoredQueryEntry(definition),
                    // A replayed registration was already quota-checked at its original commit;
                    // recovery may run before the operator's configured ceiling is applied, so
                    // re-enforcing here could silently drop committed operator state.
                    BypassQuota = true
                };

                if (!tx.TryExecute(this))
                {
                    _logger.LogWarning(
                        "Re-executing a logged RegisterStoredQuery transaction for \"{Name}\" during recovery returned false (reason {Reason}); it is skipped.",
                        definition.Name, tx.FailureReason);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Recovering logged stored query \"{Name}\" threw during recovery; it is skipped and recovery continues with later entries.",
                    definition.Name);
            }
        }

        /// <summary>
        ///   Grows the published snapshot's live slot count to <paramref name="targetCount" /> (padding
        ///   the tail with null slots), used before a WAL replay so that <c>_currentId == Count</c>
        ///   holds. This is required when the snapshot's id-space size is smaller than the log's
        ///   baseline id - which happens when the highest-id element(s) were soft-removed (without a
        ///   Trim) before the snapshot: the snapshot then omits those top ids, but a replayed create
        ///   must still be appended at the SAME index as its original id. A no-op when the target does
        ///   not exceed the current count.
        /// </summary>
        private void SetSnapshotCountForReplay(int targetCount)
        {
            var snap = _snapshot;
            if (targetCount <= snap.Count)
            {
                return;
            }

            var dense = new AGraphElementModel[targetCount];
            var segments = snap.Segments;
            for (int i = 0; i < snap.Count; i++)
            {
                dense[i] = segments[i >> SegmentShift][i & SegmentMask];
            }
            _snapshot = BuildSnapshotFromDenseArray(dense, targetCount);
        }

        #endregion

        internal void Load_internal(String path, Boolean startServices = false)
        {
            if (String.IsNullOrWhiteSpace(path))
            {
                _logger.LogInformation("There is no path given, so nothing will be loaded.");
                return;
            }

            // Cold-path instrumentation (feature observability): duration + bytes + span,
            // failure counter on a rejected load - whether it threw (corrupt/invalid file) or
            // returned false (e.g. a non-existent path). The load itself is unchanged.
            using var span = Diagnostics.Fallen8Diagnostics.Source.StartActivity("fallen8.checkpoint.load");
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            int replayedEntries;
            bool loaded;
            try
            {
                loaded = LoadCore(path, startServices, out replayedEntries);
            }
            catch
            {
                Metrics?.RecordCheckpointFailure("load");
                span?.SetStatus(System.Diagnostics.ActivityStatusCode.Error);
                throw;
            }

            if (!loaded)
            {
                Metrics?.RecordCheckpointFailure("load");
                span?.SetStatus(System.Diagnostics.ActivityStatusCode.Error);
                return;
            }

            var bytes = MeasureCheckpointBytes(path);
            Metrics?.RecordCheckpointLoad(
                System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds, bytes);
            span?.SetTag("checkpoint.bytes", bytes);
            span?.SetTag("checkpoint.wal.replayed", replayedEntries);
        }

        private bool LoadCore(String path, Boolean startServices, out int replayedEntries)
        {
            replayedEntries = 0;

            _logger.LogInformation("Fallen-8 now loads a savegame from path \"{Path}\"", path);

            var oldIndexFactory = IndexFactory;
            var oldServiceFactory = ServiceFactory;
            var oldSubGraphFactory = SubGraphFactory;
            oldServiceFactory.ShutdownAllServices();
            var oldSnapshot = _snapshot;
            var oldCurrentId = _currentId;
            var oldId = Id;

            _snapshot = EmptySnapshot;

            // Load publishes the graph elements into _snapshot itself (via
            // PublishLoadedGraphElements) before it rehydrates indices, because index
            // rehydration resolves element ids through TryGetGraphElement against the
            // published master store. Publication goes through that method rather than a
            // by-ref out-parameter so the volatile snapshot field is written atomically.
            bool success;
            try
            {
                success = _persistencyFactory.Load(this, path, ref _currentId, startServices);
            }
            catch (Exception ex)
            {
                // A rejected load - the file was not a Fallen-8 save (missing magic / unknown
                // version), failed its integrity check, or was truncated/corrupt (findings C2/C4/C5)
                // - must leave the engine exactly as it was, then surface as a rolled-back
                // transaction (the worker maps a throw to RolledBack + Error, which the REST layer
                // maps to 500). Restore the pre-load state and rethrow. The single-writer worker
                // survives this (C3/B6): only the transaction rolls back, the thread keeps running.
                _logger.LogError(ex, "Loading the savegame from \"{Path}\" was rejected; the database is left unchanged.", path);

                _currentId = oldCurrentId;
                _snapshot = oldSnapshot;
                IndexFactory = oldIndexFactory;
                ServiceFactory = oldServiceFactory;
                SubGraphFactory = oldSubGraphFactory;
                // Restore the engine identity too: PersistencyFactory.Load sets it via SetId(...) once
                // it trusts the file, so a load that then failed must not leave the DB carrying the
                // rejected save's Guid. (In practice SetId runs only after clean-reject validation, so
                // this is rarely reachable - but a rolled-back load must leave EVERYTHING unchanged.)
                SetId(oldId);
                ServiceFactory.StartAllServices();
                throw;
            }

            var walHandledIdSpace = false;

            if (success)
            {
                oldIndexFactory.DeleteAllIndices();
                oldSubGraphFactory.DeleteAllSubGraphs();

                // P5: the load has committed and published its own snapshot; the previous graph is no
                // longer reachable for rollback (neither the catch nor the else-restore below can run
                // now), so drop our last reference to it here - BEFORE the closing Trim_internal
                // rebuilds (and transiently doubles) the store - rather than holding the old graph,
                // the new store and the trim's temporaries all at once.
                oldSnapshot = null;

                // Rebuild persisted subgraphs against the freshly loaded graph. Requires a
                // registered recipe compiler; without one, persisted subgraphs are skipped.
                var recipes = _persistencyFactory.LoadSubGraphRecipes(path);
                if (recipes.Count > 0)
                {
                    SubGraphFactory.RehydrateFromRecipes(recipes, SubGraphRecipeCompiler);
                }

                // Rehydrate the stored query library from its manifest (feature
                // stored-query-library): the load REPLACES the library wholesale, exactly like the
                // graph itself, BEFORE any WAL replay applies later Register/Remove entries on top.
                RehydrateStoredQueries(_persistencyFactory.LoadStoredQueryDefinitions(path));

                // WAL (spec P4/§5). When the WAL is enabled it OWNS the loaded snapshot's id-space
                // handling: it deliberately does NOT run the closing compaction, so the in-memory id
                // space stays IDENTICAL to the on-disk snapshot - which is what keeps a future reload
                // + replay id-consistent (the log's baseline id is meaningful only against the exact
                // snapshot id space). At this point _currentId is the snapshot's id-space size.
                if (_wal != null)
                {
                    if (_wal.PairsWith(path))
                    {
                        // The log pairs with THIS snapshot: replay the transactions committed after
                        // it, in commit order. Restore _currentId to the log's baseline (the
                        // snapshot-time high-water mark, which may exceed the snapshot's id-space size
                        // if top ids were soft-removed) and pad the store so id==index holds, so
                        // replayed creates re-assign the SAME ids and edges/removals resolve the SAME
                        // elements. The result equals the exact pre-crash committed state.
                        var baseline = (int)_wal.BaselineCurrentId;
                        SetSnapshotCountForReplay(baseline);
                        _currentId = baseline;
                        replayedEntries = ReplayWriteAheadLog();
                    }
                    else
                    {
                        // The log does not pair with this snapshot (a different/older snapshot, or a
                        // pre-snapshot log). If it STILL HOLDS committed entries, re-anchoring drops
                        // them - work committed since the log's own snapshot that is NOT present in the
                        // snapshot now being loaded. That is legitimate (e.g. loading an older snapshot,
                        // or bootstrapping onto a foreign one), but it must never be silent: warn
                        // loudly so a mispaired reload - a snapshot loaded via a path the log was not
                        // anchored to - surfaces as a signal rather than as silent data loss.
                        if (_wal.HasEntries())
                        {
                            _logger.LogWarning(
                                "The write-ahead log holds committed entries but does not pair with the snapshot being loaded from \"{Path}\"; those entries will be DISCARDED (not replayed). If this snapshot was meant to pair with the log, reload it via the exact path the log was anchored to.",
                                path);
                        }

                        // Discard the stale entries and re-anchor the log to THIS snapshot, baselined at
                        // the snapshot's own id-space size (the current _currentId, before any
                        // compaction), so future commits are logged against the correct baseline and a
                        // later reload of this snapshot stays id-consistent.
                        _wal.ResetToSnapshot(path, _currentId);
                    }

                    _txManager.Trim();
                    RecalculateGraphElementCounter();
                    walHandledIdSpace = true;

                    // The paired snapshot is now loaded (or the log was re-anchored to it): logging is
                    // no longer suspended (feature crash-durability-hardening D3).
                    _walAwaitingPairedLoad = false;
                }
            }
            else
            {
                // A failed load (e.g. a non-existent path returning false) must leave EVERYTHING as it
                // was - symmetric with the catch branch above, which restores _currentId. Restore it
                // here too so a partially-advanced counter cannot survive a failed load.
                _currentId = oldCurrentId;
                _snapshot = oldSnapshot;
                IndexFactory = oldIndexFactory;
                ServiceFactory = oldServiceFactory;
                SubGraphFactory = oldSubGraphFactory;
                ServiceFactory.StartAllServices();

                // D2: with the WAL enabled, the restored snapshot already sits in the exact id space the
                // log baseline was recorded against. Running the closing Trim_internal would reassign
                // ids WITHOUT logging a Trim marker, so a later reload + replay (which does not know
                // about that trim) would resolve the wrong elements. Skip it, symmetric with the
                // success path (feature crash-durability-hardening D2).
                if (_wal != null)
                {
                    walHandledIdSpace = true;
                }
            }

            // WAL-disabled (and failed-load) path: compact as before - behaviour is unchanged. The
            // WAL path above deliberately skips this so the loaded id space matches the on-disk
            // snapshot for replay consistency.
            if (!walHandledIdSpace)
            {
                Trim_internal();
            }

            return success;
        }
    }
}
