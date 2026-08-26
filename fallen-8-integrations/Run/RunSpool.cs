// MIT License
//
// RunSpool.cs
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
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Integrations.Contract;

namespace NoSQL.GraphDB.Integrations.Run
{
    /// <summary>
    ///   THE ONE THING THIS RUNTIME REMEMBERS ACROSS A RESTART: a run that is still IN FLIGHT.
    ///
    ///   <para>It exists because of an asymmetry that made an interruption unrecoverable rather than merely
    ///   annoying. A run's graph writes are idempotent by re-resolution: run it again and every element it
    ///   created is matched instead, so nothing is duplicated and nothing is written twice. The EMBEDDING
    ///   phase is not, and cannot be, because the applier embeds only the entities whose data CHANGED this
    ///   run - which is what keeps a re-run over an unchanged source from being a write. So a run
    ///   interrupted after twenty of twelve thousand summaries, re-run, finds every element already present
    ///   and unchanged, embeds NOTHING, and the only cure is clearing the namespace and importing from
    ///   scratch. For a real extract that is hours, lost to any <c>docker compose restart</c>.</para>
    ///
    ///   <para>WHAT IT MAY HOLD, and the list is the whole security argument. The job's ENVELOPE (which
    ///   provider, which identity, which namespace, the embedding opt-in), the SNAPSHOT once the provider
    ///   produced one and the validator accepted it, and the embedding journal. Never a credential. Never a
    ///   file's bytes. A credential is needed only while the source is being read, and a file only to
    ///   produce the snapshot; past that point neither can affect the run, so neither is written down. The
    ///   snapshot is the one thing here that IS caller data, and it was always destined for the graph.</para>
    ///
    ///   <para>WHAT IT IS NOT is a run history. An entry exists only while its run does: it is deleted on
    ///   success, on failure and on cancellation alike, so a healthy runtime's spool is empty. That is what
    ///   keeps "this runtime keeps no schedule, no history and no credential" true rather than nearly true.</para>
    ///
    ///   <para>OFF BY DEFAULT. With no <c>Integrations:SpoolDirectory</c> configured nothing is written to
    ///   disk at all and the behaviour is exactly what it was before this existed, which is what a bare
    ///   <c>dotnet run</c> gets. The compose environment points it at a volume.</para>
    /// </summary>
    public sealed class RunSpool
    {
        /// <summary>
        ///   The format version of an entry. A newer runtime that cannot read an older entry REFUSES it
        ///   rather than guessing: a half-understood snapshot resumed is a wrong graph, where an honest
        ///   refusal costs one re-run.
        /// </summary>
        public const Int32 Version = 1;

        /// <summary>
        ///   How many times a spooled run may be picked up before it is given up on. Three, because the
        ///   failure this exists for is a graph that has not finished starting, which one more attempt
        ///   fixes; a graph that is gone for good would otherwise have this entry retried on every start
        ///   for ever.
        /// </summary>
        public const Int32 MaxAttempts = 3;

        /// <summary>
        ///   The prefix on every file this spool writes. It is not decoration: an integration instance id
        ///   may be any of letters, digits, dot, dash and underscore, so an identity called <c>con</c>,
        ///   <c>nul</c> or <c>aux</c> would otherwise compose a Windows reserved device name and every write
        ///   for it would fail in a way nothing here could explain.
        /// </summary>
        private const String Prefix = "run-";

        private const String JobSuffix = ".job.json";
        private const String ProgressSuffix = ".progress.json";

        private static readonly JsonSerializerOptions Format = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly String? _directory;
        private readonly ILogger _logger;

        public RunSpool(String? directory, ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _directory = String.IsNullOrWhiteSpace(directory) ? null : directory!.Trim();
        }

        /// <summary>Whether anything is written down at all. False is the default and writes nothing.</summary>
        public Boolean Enabled => _directory != null;

        /// <summary>Where entries live, or null when the spool is off.</summary>
        public String? Directory => _directory;

        /// <summary>
        ///   Records that a run has been accepted, BEFORE its provider is invoked.
        ///
        ///   <para>An entry with no snapshot cannot be resumed - the credential and the files it would need
        ///   died with the process, by design - so this exists to make the interruption VISIBLE rather than
        ///   silent. Without it, a restart during a long source read leaves a caller polling an identity the
        ///   new process has never heard of, which reads as "the run vanished".</para>
        /// </summary>
        public void WriteIntent(SpooledRun run)
        {
            if (!Enabled || run == null)
            {
                return;
            }

            Write(JobPath(run.InstanceId), run);
        }

        /// <summary>
        ///   Adds the accepted snapshot to an entry, which is the moment the run becomes resumable: from
        ///   here on, everything the run still has to do is a function of this document and the graph.
        ///
        ///   <para>It hands back the document AS A RESUMED RUN WILL SEE IT, and the run then uses that
        ///   rather than the one the provider returned. That is not tidiness, it is a bug fix with a test:
        ///   a provider's property value is a CLR object in process and a <c>JsonElement</c> after a
        ///   round trip, and the two do not render identically - JSON cannot tell an <c>Int32</c> from an
        ///   <c>Int64</c>, so it must pick one. So the first attempt would store <c>System.Int32</c>, the
        ///   resumed attempt would compare <c>System.Int64</c> against it, find every property different,
        ///   and REWRITE THE WHOLE IMPORT. Every restart, silently, churning the change feed and growing
        ///   the write-ahead log, with nothing on the report to say why.</para>
        ///
        ///   <para>Making the writing run read its own entry back closes that by construction rather than
        ///   by keeping two renderings in step: whatever a resumed run would see is what this run saw.
        ///   Null means the spool is off or could not be written, in which case there is nothing to resume
        ///   and nothing to keep in step with.</para>
        /// </summary>
        public SnapshotDocument? WriteSnapshot(SpooledRun run, SnapshotDocument snapshot)
        {
            if (!Enabled || run == null || snapshot == null)
            {
                return null;
            }

            run.Snapshot = snapshot;
            var written = Write(JobPath(run.InstanceId), run);
            if (written == null)
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<SpooledRun>(written, Format)?.Snapshot;
            }
            catch (JsonException failure)
            {
                // The entry this process just wrote cannot be read by this process. That is a defect rather
                // than a condition, but failing the run for it would be the wrong trade: the graph write is
                // the point, so the run carries on with the document it has and simply is not resumable.
                _logger.LogWarning(failure,
                    "The spooled snapshot for identity {InstanceId} could not be read back, so this run is " +
                    "not resumable. The run itself is unaffected.", run.InstanceId);
                return null;
            }
        }

        /// <summary>
        ///   Records the embedding journal, or advances its cursor.
        ///
        ///   <para>Its own file, and that is a cost decision rather than tidiness: the cursor moves once per
        ///   chunk - hundreds of times for a real extract - and the job entry beside it carries the whole
        ///   snapshot, which can be tens of megabytes. Rewriting that per chunk would spend more on
        ///   bookkeeping than on the work.</para>
        /// </summary>
        public void WriteProgress(String instanceId, SpooledProgress progress)
        {
            if (!Enabled || progress == null)
            {
                return;
            }

            Write(ProgressPath(instanceId), progress);
        }

        /// <summary>
        ///   Drops an entry. Called on EVERY terminal outcome - success, failure and cancellation - because
        ///   an entry that outlived its run would be resumed by the next restart, re-running a job whose
        ///   answer somebody already has.
        /// </summary>
        public void Delete(String instanceId)
        {
            if (!Enabled)
            {
                return;
            }

            Remove(JobPath(instanceId));
            Remove(ProgressPath(instanceId));
        }

        /// <summary>
        ///   Every entry on disk, oldest first, with whatever progress each had.
        ///
        ///   <para>An unreadable entry is REPORTED AND DROPPED rather than skipped quietly or retried: it
        ///   describes a run somebody started, so silence about it is the failure mode this whole type
        ///   exists to remove, and guessing at a half-written snapshot is the one thing worse than losing
        ///   it. Half-written is a real state: the process may have died during the write.</para>
        /// </summary>
        public IReadOnlyList<SpooledRun> Pending()
        {
            var pending = new List<SpooledRun>();
            if (!Enabled || !System.IO.Directory.Exists(_directory))
            {
                return pending;
            }

            foreach (var path in System.IO.Directory.GetFiles(_directory!, Prefix + "*" + JobSuffix))
            {
                SpooledRun? run = null;
                try
                {
                    run = JsonSerializer.Deserialize<SpooledRun>(File.ReadAllText(path), Format);
                }
                catch (Exception failure) when (failure is JsonException || failure is IOException)
                {
                    _logger.LogWarning(failure,
                        "A spooled integration run at {Path} could not be read, so it is dropped rather " +
                        "than resumed. A run resumed from a half-written entry would write a graph nobody " +
                        "described; whatever it was has to be submitted again.", path);
                }

                if (run == null || String.IsNullOrWhiteSpace(run.InstanceId))
                {
                    Remove(path);
                    continue;
                }

                if (run.Version != Version)
                {
                    _logger.LogWarning(
                        "A spooled integration run for identity {InstanceId} is format version {Found}, " +
                        "which this runtime does not read (it writes {Expected}). It is dropped rather than " +
                        "guessed at; submit the job again.", run.InstanceId, run.Version, Version);
                    Delete(run.InstanceId);
                    continue;
                }

                run.Progress = ReadProgress(run.InstanceId);
                pending.Add(run);
            }

            // OLDEST FIRST, so that a restart which found several runs resumes them in the order they were
            // started. It is the only order that makes sense of two runs whose namespaces overlap.
            pending.Sort((left, right) => String.CompareOrdinal(left.StartedAt, right.StartedAt));
            return pending;
        }

        private SpooledProgress? ReadProgress(String instanceId)
        {
            var path = ProgressPath(instanceId);
            if (!File.Exists(path))
            {
                // No journal is a legitimate state: the run died before it reached the writes. Everything it
                // was going to do is recomputed from the snapshot, which is exactly what a fresh run does.
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<SpooledProgress>(File.ReadAllText(path), Format);
            }
            catch (Exception failure) when (failure is JsonException || failure is IOException)
            {
                // A lost journal costs re-embedding, never a wrong graph, so this is the one thing here that
                // may be dropped quietly-ish: the run still resumes, it just re-embeds what it already did,
                // and every embedding is an idempotent overwrite of the same vector.
                _logger.LogWarning(failure,
                    "The embedding journal for identity {InstanceId} could not be read. The run still " +
                    "resumes; it re-embeds what it had already embedded, which overwrites the same vectors.",
                    instanceId);
                return null;
            }
        }

        /// <summary>
        ///   Writes atomically: a temporary file, then a rename over the target.
        ///
        ///   <para>The whole point of this type is surviving a process that stopped without warning, so a
        ///   half-written entry has to be impossible rather than unlikely. A rename is the only operation
        ///   both filesystems this ships on treat as atomic.</para>
        /// </summary>
        /// <returns>What was written, so a caller can read its own entry back without parsing the file
        /// again; null when nothing was written.</returns>
        private String? Write(String path, Object entry)
        {
            try
            {
                System.IO.Directory.CreateDirectory(_directory!);
                var json = JsonSerializer.Serialize(entry, entry.GetType(), Format);
                var temporary = path + ".tmp";
                File.WriteAllText(temporary, json);
                File.Move(temporary, path, overwrite: true);
                return json;
            }
            catch (Exception failure) when (failure is IOException
                                            || failure is UnauthorizedAccessException
                                            || failure is NotSupportedException)
            {
                // NEVER fails the run. The spool is an aid to recovery, not a precondition for the import:
                // a read-only mount or a full volume must cost resumability and nothing else, because the
                // graph write is the point of the run and it is already under way.
                _logger.LogWarning(failure,
                    "A spooled integration run could not be written to {Path}, so this run will not be " +
                    "resumable if the process stops. The run itself is unaffected.", path);
                return null;
            }
        }

        private void Remove(String path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception failure) when (failure is IOException || failure is UnauthorizedAccessException)
            {
                _logger.LogWarning(failure,
                    "A spooled integration run at {Path} could not be deleted. It describes a run that has " +
                    "ended, so a restart would try to resume work that is already done; remove it by hand.",
                    path);
            }
        }

        private String JobPath(String instanceId)
        {
            return Path.Combine(_directory!, Prefix + instanceId + JobSuffix);
        }

        private String ProgressPath(String instanceId)
        {
            return Path.Combine(_directory!, Prefix + instanceId + ProgressSuffix);
        }
    }

    /// <summary>
    ///   One in-flight run as the spool holds it. Deliberately NOT an <c>IntegrationJob</c>: that type
    ///   carries credential values and file bytes, and the difference between the two shapes is the whole of
    ///   what may be written down.
    /// </summary>
    public sealed class SpooledRun
    {
        [JsonPropertyName("version")]
        public Int32 Version { get; set; } = RunSpool.Version;

        /// <summary>The run's own id, kept so a resumed run is the SAME run to every reader of it.</summary>
        [JsonPropertyName("runId")]
        public String RunId { get; set; } = String.Empty;

        [JsonPropertyName("providerId")]
        public String ProviderId { get; set; } = String.Empty;

        [JsonPropertyName("instanceId")]
        public String InstanceId { get; set; } = String.Empty;

        [JsonPropertyName("namespace")]
        public String? Namespace { get; set; }

        [JsonPropertyName("embedSummaries")]
        public Boolean EmbedSummaries { get; set; }

        [JsonPropertyName("embeddingName")]
        public String EmbeddingName { get; set; } = "default";

        /// <summary>
        ///   When the run first started, ISO-8601. A resumed run keeps it, because the elapsed time a reader
        ///   wants is how long the import has been going, outage included: that is what actually elapsed.
        /// </summary>
        [JsonPropertyName("startedAt")]
        public String StartedAt { get; set; } = String.Empty;

        /// <summary>
        ///   How many times this entry has been PICKED UP, counted before each attempt.
        ///
        ///   <para>It exists for one failure that would otherwise be self-defeating. This container restarts
        ///   alongside the graph it writes into, and it may well come up first: the resumed run then fails
        ///   because the graph did not answer, and an entry deleted on that failure loses exactly the hours
        ///   of work the spool exists to keep. So a resumed run that failed on the GRAPH keeps its entry and
        ///   is tried again on the next start, bounded by this count - because a graph that is gone for good
        ///   must not make the entry immortal.</para>
        /// </summary>
        [JsonPropertyName("attempts")]
        public Int32 Attempts { get; set; }

        /// <summary>
        ///   The document the provider produced, present once the validator accepted its envelope. Null
        ///   means the run never got that far, and cannot be resumed: the file and the credential it would
        ///   need to produce one are gone.
        /// </summary>
        [JsonPropertyName("snapshot")]
        public SnapshotDocument? Snapshot { get; set; }

        /// <summary>The journal, read from its own file. Never serialized as part of this entry.</summary>
        [JsonIgnore]
        public SpooledProgress? Progress { get; set; }

        /// <summary>Whether this entry describes a run that can be picked up where it stopped.</summary>
        [JsonIgnore]
        public Boolean Resumable => Snapshot != null;
    }

    /// <summary>
    ///   THE EMBEDDING JOURNAL: which entities need a summary, and how many of them already have one.
    ///
    ///   <para>It is written AHEAD of the writes that make it true, and that ordering is the correctness
    ///   argument. The applier can only know which entities changed by comparing the snapshot against the
    ///   graph BEFORE it writes; once it has written, a re-run finds them equal and concludes nothing needs
    ///   embedding. So the list is recorded first, while the answer is still knowable, and the invariant
    ///   becomes: if any element of this run was written, its journal exists.</para>
    ///
    ///   <para>The consequence is AT-LEAST-ONCE. A chunk that landed just as the process died leaves the
    ///   cursor behind it, so a resumed run embeds those summaries again. That is an idempotent overwrite
    ///   of the same vector on the same element. The alternative - advancing the cursor first - would be
    ///   at-most-once, and a skipped summary is the unrecoverable loss this whole feature exists to
    ///   prevent.</para>
    /// </summary>
    public sealed class SpooledProgress
    {
        /// <summary>
        ///   The entity positions needing a summary, ASCENDING. Sorted rather than in discovery order
        ///   because the cursor below has to mean the same thing in a different process, and a hash set's
        ///   iteration order does not survive one.
        /// </summary>
        [JsonPropertyName("embedEntities")]
        public Int32[] EmbedEntities { get; set; } = Array.Empty<Int32>();

        /// <summary>How many of them have been embedded, counted from the start of the list.</summary>
        [JsonPropertyName("embedded")]
        public Int32 Embedded { get; set; }

        /// <summary>The positions still to embed, in journal order.</summary>
        public IReadOnlyList<Int32> Remaining()
        {
            if (Embedded >= EmbedEntities.Length)
            {
                return Array.Empty<Int32>();
            }

            var remaining = new Int32[EmbedEntities.Length - Math.Max(0, Embedded)];
            Array.Copy(EmbedEntities, Math.Max(0, Embedded), remaining, 0, remaining.Length);
            return remaining;
        }

        /// <summary>A one-line account for the log, so a resumed run says what it is skipping.</summary>
        public String Describe()
        {
            return String.Format(CultureInfo.InvariantCulture, "{0} of {1} summaries already embedded",
                Math.Min(Embedded, EmbedEntities.Length), EmbedEntities.Length);
        }
    }
}
