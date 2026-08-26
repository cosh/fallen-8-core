// MIT License
//
// RunResumeService.cs
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
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Integrations.Run;

namespace NoSQL.GraphDB.Integrations.Hosting
{
    /// <summary>
    ///   Picks up, on start, the runs a stopped process left in flight.
    ///
    ///   <para>It is the other half of <see cref="RunSpool" /> and does nothing at all when no spool is
    ///   configured, which is the default. What it is NOT is a scheduler: it reads entries that already
    ///   exist, resumes each once, and never runs anything again. There is no interval here and no queue,
    ///   and a run it resumes is subject to the same single-flight gate as any other.</para>
    ///
    ///   <para>An entry with NO snapshot cannot be resumed - the file and the credential that would produce
    ///   one are dropped when a run ends, by design - so it is turned into an honest terminal slot instead
    ///   of being retried. That matters: without it, a restart during a long source read leaves whoever was
    ///   watching polling an identity this process has never heard of, which reads as a run that vanished.</para>
    /// </summary>
    public sealed class RunResumeService : BackgroundService
    {
        private readonly RunSpool _spool;
        private readonly JobRunner _runner;
        private readonly RunTracker _tracker;
        private readonly ILogger<RunResumeService> _logger;

        public RunResumeService(RunSpool spool, JobRunner runner, RunTracker tracker,
            ILogger<RunResumeService> logger)
        {
            _spool = spool ?? throw new ArgumentNullException(nameof(spool));
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
            _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_spool.Enabled)
            {
                return;
            }

            var pending = _spool.Pending();
            if (pending.Count == 0)
            {
                return;
            }

            _logger.LogInformation(
                "{Count} integration run(s) were in flight when this runtime last stopped, and are being " +
                "picked up from {Directory}.", pending.Count, _spool.Directory);

            foreach (var spooled in pending)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    // Shutting down again already. The entries that are left stay where they are, which is
                    // exactly what they are for.
                    return;
                }

                await ResumeOneAsync(spooled).ConfigureAwait(false);
            }
        }

        private async Task ResumeOneAsync(SpooledRun spooled)
        {
            if (!spooled.Resumable)
            {
                // Reported as a FINISHED run rather than dropped in silence, so whoever was watching learns
                // what happened to it. Nothing was written: the entry was created before the provider was
                // invoked, and the snapshot is what marks the point at which writing could begin.
                _logger.LogWarning(
                    "The integration run {RunId} as {InstanceId} was interrupted before its source had been " +
                    "read, so it cannot be resumed: the file and the credential it needed are gone, by " +
                    "design. Nothing was written and nothing was withdrawn. Submit the job again.",
                    spooled.RunId, spooled.InstanceId);

                MaterialiseUnresumable(spooled);
                _spool.Delete(spooled.InstanceId);
                return;
            }

            // Counted and PERSISTED before the attempt, not after it: an attempt that dies without answering
            // is exactly the kind the bound on these exists to stop, so it has to count even when nothing
            // gets to write afterwards. Whether this is the LAST attempt is the runner's decision, made where
            // it already decides the entry's fate - one place rather than two that could disagree.
            spooled.Attempts++;
            _spool.WriteIntent(spooled);

            _logger.LogInformation(
                "Resuming integration run {RunId} as {InstanceId} ({Provider}), started {StartedAt}, " +
                "attempt {Attempt}: {Journal}.",
                spooled.RunId, spooled.InstanceId, spooled.ProviderId, spooled.StartedAt, spooled.Attempts,
                spooled.Progress?.Describe() ?? "no embedding journal, so the plan is recomputed");

            // Under the run's OWN id and original start time, so a client polling this identity sees the run
            // it was already watching continue rather than a new one appear.
            using var handle = _tracker.Begin(spooled.RunId, spooled.ProviderId, spooled.InstanceId,
                spooled.Namespace, spooled.EmbedSummaries, resumed: true, startedUtc: StartedAt(spooled));

            try
            {
                var report = await _runner.ResumeAsync(spooled, handle, handle.CancellationToken)
                    .ConfigureAwait(false);
                _tracker.Finish(spooled.InstanceId, handle.RunId, report);
            }
            catch (RunInterruptedException)
            {
                // Interrupted AGAIN, by this process shutting down while it was catching up. The entry is
                // deliberately still on disk and the next start tries once more.
                _tracker.Abort(spooled.InstanceId, handle.RunId,
                    "interrupted again by shutdown; the run stays resumable");
            }
            catch (Exception failure)
            {
                // A resumed run that fails is a failed run, and its entry is already gone: the runner drops
                // it on every ending. Recorded on the slot, because that is the only place a reader could
                // find out.
                _logger.LogWarning(failure,
                    "The resumed integration run {RunId} as {InstanceId} failed. It withdrew nothing; the " +
                    "next run of this identity converges the graph.", spooled.RunId, spooled.InstanceId);
                _tracker.Abort(spooled.InstanceId, handle.RunId, failure.Message);
            }
        }

        /// <summary>
        ///   Opens and immediately finishes a slot for a run that cannot be resumed, so the identity has
        ///   something honest to answer with instead of a 404 that reads as "it never ran".
        /// </summary>
        private void MaterialiseUnresumable(SpooledRun spooled)
        {
            using var handle = _tracker.Begin(spooled.RunId, spooled.ProviderId, spooled.InstanceId,
                spooled.Namespace, spooled.EmbedSummaries, resumed: true, startedUtc: StartedAt(spooled));
            handle.EnterPhase(RunPhases.Observe);
            _tracker.Abort(spooled.InstanceId, handle.RunId,
                "This run was interrupted while its source was still being read, so it could not be " +
                "resumed: the file and the credential it needed are dropped when a run ends. Nothing was " +
                "written and nothing was withdrawn. Submit the job again.");
        }

        private static DateTimeOffset? StartedAt(SpooledRun spooled)
        {
            return DateTimeOffset.TryParse(spooled.StartedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var started)
                ? started
                : null;
        }
    }
}
