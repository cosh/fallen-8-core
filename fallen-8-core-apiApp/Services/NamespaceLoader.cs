// MIT License
//
// NamespaceLoader.cs
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
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
    ///   THE one home for restoring a single namespace from the save-game registry, shared by the
    ///   two callers that need it: the boot (<see cref="DurabilityLifecycleService"/>) and runtime
    ///   activation (<c>POST /ns/{name}/activate</c>, feature namespace-startup-load §4.8).
    ///
    ///   <para>Only the ANSWER differs between them, which is why the answer is the caller's and not
    ///   this class's; the two differences are both in <see cref="NamespaceRestoreOutcome"/>:</para>
    ///   <para><b>Failed:</b> at boot an unrestorable checkpoint aborts the whole process (save-games
    ///   FR-9 - a missing save must never be masked by an empty graph on a machine that then starts
    ///   serving), while an activation fails only THAT request and leaves the server up. Before this
    ///   split the load logic lived inside the hosted service and its only possible answer was the
    ///   abort.</para>
    ///   <para><b>UnregisteredCheckpoints</b> (save-games FR-11: checkpoint files on disk that no
    ///   registry entry contains): a boot proceeds, because it has already constructed and published
    ///   that engine and starting from the replayed write-ahead log is exactly the state from which
    ///   the operator adopts those files with one checkpoint load. An ACTIVATION refuses, because it
    ///   still holds the choice and publishing here is the destructive one: the namespace would
    ///   become resident and EMPTY beside real checkpoint files, which retires the data-loss guard
    ///   protecting it (spec §5 - a not-loaded namespace is never a member of a save), and the next
    ///   clean shutdown would then register that empty graph as the namespace's newest checkpoint
    ///   and reset its write-ahead log to a bare header. That is the unrecoverable loss this feature
    ///   exists to close, reached through activation instead of through the shutdown path.</para>
    /// </summary>
    public sealed class NamespaceLoader
    {
        private readonly Fallen8Namespaces _namespaces;
        private readonly Fallen8DurabilityOptions _options;
        private readonly SaveGameRegistry _saveGames;
        private readonly ILogger<NamespaceLoader> _logger;

        public NamespaceLoader(Fallen8Namespaces namespaces, IOptions<Fallen8DurabilityOptions> options,
            SaveGameRegistry saveGames, ILogger<NamespaceLoader> logger)
        {
            _namespaces = namespaces;
            _options = options.Value;
            _saveGames = saveGames;
            _logger = logger;
        }

        /// <summary>
        ///   Activates a namespace: constructs its engine behind the collection's per-namespace load
        ///   gate and restores its newest registered save game into it. The gate, the construction and
        ///   the publication live on <see cref="Fallen8Namespaces.ActivateAsync"/> (it owns the collection
        ///   and the engines); what to restore lives here.
        /// </summary>
        public Task<NamespaceActivation> ActivateAsync(String name)
        {
            return _namespaces.ActivateAsync(name, RestoreNewestRegistered);
        }

        /// <summary>
        ///   The activation routine for a caller that already knows WHICH checkpoint it wants: the
        ///   save-game restore, which is about to replace the graph with this entry's content anyway
        ///   (feature namespace-startup-load, spec decision 8.3). Restoring the namespace's own newest
        ///   save game first would be a full load whose result that restore discards - and a rotted
        ///   newest checkpoint would block the very rollback the operator came to perform.
        ///   <para>Here rather than at the call site so that "enqueue a load, wait, read the rollback"
        ///   has one home per this class's contract.</para>
        /// </summary>
        public NamespaceLoadRoutine RestoreFrom(String location)
        {
            return async (ns, engine) =>
            {
                var info = engine.EnqueueTransaction(new LoadTransaction { Path = location, StartServices = true });
                await info.Completion.ConfigureAwait(false);

                if (info.TransactionState == TransactionState.RolledBack)
                {
                    _logger.LogError(info.Error, "Loading \"{Location}\" into namespace \"{Namespace}\" rolled back.",
                        location, ns.Name);
                    return (NamespaceRestoreOutcome.Failed, "the load of \"" + location + "\" rolled back: " +
                        (info.Error?.Message ?? "no detail given"));
                }

                return (NamespaceRestoreOutcome.Ready, "Restored from \"" + location + "\".");
            };
        }

        /// <summary>
        ///   Restores the newest REGISTERED save game containing <paramref name="ns"/> into
        ///   <paramref name="engine"/>, adopting a crash-window orphan checkpoint when disk is ahead
        ///   of the registry. Matched by the namespace's IMMUTABLE id, so a rename keeps the boot
        ///   chain and a recreated namesake never loads its predecessor's checkpoints.
        ///   <para>The engine is passed in rather than read off <paramref name="ns"/>: an activation
        ///   deliberately does not publish it until this returns true.</para>
        /// </summary>
        /// <param name="ns">The namespace being restored (its id keys the registry lookup).</param>
        /// <param name="engine">The engine to restore into.</param>
        /// <returns>
        ///   How it went (this class's summary says what each caller does with that), what was
        ///   restored or why it was not, in the operator's words, and the underlying exception for a
        ///   caller that wraps or logs it (null unless the restore itself threw).
        /// </returns>
        public async Task<(NamespaceRestoreOutcome Outcome, String Detail, Exception Error)> LoadNewestRegisteredAsync(
            Namespace ns, Fallen8 engine)
        {
            if (_options.Volatile)
            {
                // Volatile mode keeps nothing on disk, so there is nothing to restore. Unreachable
                // from the boot (StartAsync returns before this) and from activation (a volatile
                // Fallen-8 has no catalog, hence no not-loaded namespace) - stated rather than
                // assumed, so a future caller cannot get a registry lookup it has no files for.
                return (NamespaceRestoreOutcome.Ready, "Fallen-8 is in volatile mode; nothing was restored from disk.", null);
            }

            var directory = _namespaces.DirectoryFor(ns);
            var newest = _saveGames.NewestContaining(ns.Id);
            var member = newest == null
                ? null
                : SaveGameRegistry.EffectiveNamespaces(newest).First(m => SaveGameRegistry.EffectiveId(m) == ns.Id);

            if (member == null)
            {
                // THE ORPHAN BRANCH (save-games FR-11): checkpoint files with no registry entry are
                // never restored, and here they are also the reason an activation must not publish
                // an engine - the class summary above is the one home for that argument. A boot and
                // an activation get the SAME outcome and choose their own answer.
                if (CheckpointDiscovery.TryFindLatestCheckpoint(directory, _options.CheckpointBaseName, out var orphan))
                {
                    var url = Namespace.UrlSegment(ns.Name);

                    // One line for two callers, so the cure it names has to be reachable from BOTH.
                    // The boot's is one step (its engine is published, so the namespace can take a
                    // checkpoint load); an activation's is not, because it refuses and the namespace
                    // stays not loaded - which is exactly what the residency guard then refuses
                    // PUT /load with. The reachable sequence for that side lives in the 409 detail
                    // below and is pointed at rather than repeated.
                    _logger.LogWarning("Fallen-8 found checkpoint files (e.g. \"{Checkpoint}\") for namespace \"{Namespace}\" " +
                        "that no registered save game contains, so they are NOT restored (registry-driven boot). At BOOT " +
                        "the namespace still comes up, on its write-ahead log alone (which replays nothing at all when " +
                        "that log is anchored to one of these very files), and stays resident - so those files can be " +
                        "adopted by loading that checkpoint once (PUT /ns/{UrlSegment}/load), which registers them " +
                        "permanently. A runtime ACTIVATION instead refuses rather than publishing an empty graph beside " +
                        "them, and leaves the namespace not loaded - which makes that same PUT /load unreachable, so the " +
                        "sequence that does work is the one its 409 response names.",
                        orphan, ns.Name, url);
                    return (NamespaceRestoreOutcome.UnregisteredCheckpoints,
                        "Namespace \"" + ns.Name + "\" has checkpoint files on disk that no registered save game " +
                        "contains (newest: \"" + orphan + "\"), so it was NOT loaded and nothing on disk was touched. " +
                        "Loading it as an empty graph beside those files is how they get lost: it would then be " +
                        "resident, so it would join the next save, and a clean shutdown would register that empty " +
                        "graph as its newest checkpoint and reset its write-ahead log to a bare header. Adopt the " +
                        "files first, which registers them permanently: set its startup-load policy (PATCH /ns/" + url +
                        " with \"loadOnStartup\": \"enabled\"), restart, then load that checkpoint once (PUT /ns/" + url +
                        "/load with \"saveGameLocation\": \"" + orphan + "\"). Activation restores a registered save " +
                        "game from then on.", null);
                }
                _logger.LogInformation("No registered save game contains namespace \"{Namespace}\"; it starts with its " +
                    "current in-memory state ({VertexCount} vertices, {EdgeCount} edges) - any unanchored WAL was " +
                    "replayed at construction.", ns.Name, engine.VertexCount, engine.EdgeCount);
                return (NamespaceRestoreOutcome.Ready, "No registered save game contains this namespace; its " +
                    "write-ahead log was replayed and nothing else was restored.", null);
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
            // a non-existent file as a no-op), so it would silently serve an empty graph. Refuse here
            // instead - the operator restores the files or removes the entry. At boot the caller turns
            // this into the FR-9 abort.
            if (!File.Exists(loadTarget))
            {
                return (NamespaceRestoreOutcome.Failed, "The newest save game containing namespace \"" + ns.Name + "\" (\"" + newest.Id +
                    "\") points at \"" + loadTarget + "\", which does not exist, so nothing was restored (a missing " +
                    "save must never be masked by an empty graph). Restore its files, or remove the entry " +
                    "(DELETE /savegames/" + newest.Id + ").", null);
            }

            var loadInfo = engine.EnqueueTransaction(new LoadTransaction { Path = loadTarget });
            await loadInfo.Completion.ConfigureAwait(false);

            if (loadInfo.TransactionState == TransactionState.RolledBack)
            {
                return (NamespaceRestoreOutcome.Failed, "Fallen-8 failed to load namespace \"" + ns.Name + "\" from \"" + loadTarget +
                    "\"; nothing was restored. Restore its files, or remove the entry (DELETE /savegames/" +
                    newest.Id + ") to use the next-newest (or start empty).", loadInfo.Error);
            }

            if (adoptOrphan)
            {
                // Register the adopted orphan now that the graph is loaded (so its KPIs are correct).
                try
                {
                    _saveGames.RegisterImportIfUnknown(ns.Name, ns.Id, engine, loadTarget);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Adopted the orphan checkpoint \"{Disk}\" for namespace \"{Namespace}\" but could " +
                        "not register it; it will be re-adopted on the next boot.", loadTarget, ns.Name);
                }
            }

            _logger.LogInformation("Namespace \"{Namespace}\" loaded: {VertexCount} vertices, {EdgeCount} edges.",
                ns.Name, engine.VertexCount, engine.EdgeCount);
            return (NamespaceRestoreOutcome.Ready, "Restored from save game \"" + newest.Id + "\" at \"" + loadTarget + "\"" +
                (adoptOrphan ? " (an unregistered newer checkpoint on disk, adopted)" : String.Empty) +
                "; any post-checkpoint write-ahead-log entries were replayed on top.", null);
        }

        /// <summary>
        ///   The same load in the <see cref="NamespaceLoadRoutine"/> shape, with the underlying
        ///   exception logged here because the activation response carries prose, not a stack.
        ///   The refusal case logs nothing extra: the load itself already logged the situation, at
        ///   warning level, and it is not an error on this server's part.
        /// </summary>
        private async Task<(NamespaceRestoreOutcome Outcome, String Detail)> RestoreNewestRegistered(
            Namespace ns, Fallen8 engine)
        {
            var (outcome, detail, error) = await LoadNewestRegisteredAsync(ns, engine).ConfigureAwait(false);
            if (outcome == NamespaceRestoreOutcome.Failed)
            {
                _logger.LogError(error, "Activating namespace \"{Namespace}\" failed: {Detail}", ns.Name, detail);
            }

            return (outcome, detail);
        }

        private static Boolean PathsEqual(String a, String b)
        {
            try
            {
                return String.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return String.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
