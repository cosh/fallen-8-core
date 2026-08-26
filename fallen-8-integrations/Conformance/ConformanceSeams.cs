// MIT License
//
// ConformanceSeams.cs
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
using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Integrations.Configuration;
using NoSQL.GraphDB.Integrations.Graph;
using NoSQL.GraphDB.Integrations.Run;

namespace NoSQL.GraphDB.Integrations.Conformance
{
    /// <summary>
    ///   The handler behind the client a candidate is given. It RECORDS EVERY ATTEMPT either way, which is what
    ///   makes the offline check observable rather than trusting.
    ///
    ///   <para>The tempting definition of "runs offline" is wrong in both directions: "attempted no request"
    ///   fails every network provider for doing the only thing it can do, and it PASSES a provider that opened
    ///   its own socket behind the runtime's back, since a refusing handler sees nothing in that case either -
    ///   and a pass there certifies the one provider that has escaped every seam the runtime controls.</para>
    /// </summary>
    public sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpMessageHandler? _sourceDouble;
        private readonly List<Attempt> _attempts = new List<Attempt>();
        private HttpMessageInvoker? _invoker;

        /// <param name="sourceDouble">A stand-in for the provider's own service, or null to refuse everything.</param>
        public RecordingHandler(HttpMessageHandler? sourceDouble)
        {
            _sourceDouble = sourceDouble;
        }

        /// <summary>Every request the candidate tried to send, in order.</summary>
        public ImmutableArray<Attempt> Attempts => _attempts.ToImmutableArray();

        /// <summary>Whether a stand-in was supplied at all.</summary>
        public Boolean HasSourceDouble => _sourceDouble != null;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var method = request.Method.Method;
            var address = request.RequestUri?.ToString() ?? "(no address)";

            if (_sourceDouble == null)
            {
                _attempts.Add(new Attempt(method, address, 0, "refused: no source double was supplied"));
                throw new HttpRequestException(String.Format(
                    "The conformance suite supplied no stand-in for this provider's source, so the request " +
                    "{0} {1} was refused. A run is judged against substituted seams alone.", method, address));
            }

            _invoker ??= new HttpMessageInvoker(_sourceDouble, disposeHandler: false);

            try
            {
                var response = await _invoker.SendAsync(request, cancellationToken).ConfigureAwait(false);
                _attempts.Add(new Attempt(method, address, (Int32)response.StatusCode, null));
                return response;
            }
            catch (Exception ex)
            {
                _attempts.Add(new Attempt(method, address, 0, ex.Message));
                throw;
            }
        }

        protected override void Dispose(Boolean disposing)
        {
            // Deliberately does NOT dispose what it borrowed. The suite hands the SAME recorder to both runs,
            // and the runner rightly disposes the client it was given after each one - which disposes the host
            // guard, which disposes its inner handler. Passing that on would leave the second run unable to
            // send, and the second run is the whole point: determinism and idempotence are statements about a
            // repeat. The source double belongs to the caller, so nothing here owns it either.
            base.Dispose(disposing);
        }

        /// <summary>One request a candidate tried to send, and what came back.</summary>
        public sealed class Attempt
        {
            internal Attempt(String method, String address, Int32 status, String? failure)
            {
                Method = method;
                Address = address;
                Status = status;
                Failure = failure;
            }

            /// <summary>The HTTP method, so a read-only claim can be checked over a whole run.</summary>
            public String Method { get; }

            /// <summary>The address, which the refusal quotes so an author can see what escaped.</summary>
            public String Address { get; }

            /// <summary>The status the stand-in answered, or zero when nothing answered.</summary>
            public Int32 Status { get; }

            /// <summary>Why nothing answered, when nothing did.</summary>
            public String? Failure { get; }

            /// <summary>Whether the source answered usably.</summary>
            public Boolean Answered => Failure == null && Status >= 200 && Status < 300;
        }
    }

    /// <summary>
    ///   The client factory the suite installs: the real host guard over the recording handler, so a candidate is
    ///   judged against the same boundary the live path applies.
    /// </summary>
    internal sealed class RecordingHttpFactory : IProviderHttpFactory
    {
        private readonly RecordingHandler _handler;
        private readonly IntegrationsOptions _options;

        public RecordingHttpFactory(RecordingHandler handler, IntegrationsOptions options)
        {
            _handler = handler;
            _options = options;
        }

        public HttpClient Create(Boolean holdsCredential)
        {
            return ProviderHttpFactory.Wrap(_options.Credentials.AllowedHostSet(), holdsCredential, _handler);
        }
    }

    /// <summary>
    ///   The real per-run file holder, with a note of what was asked for. The twin of
    ///   <see cref="RecordingHandler" /> for the other seam a run can reach: the offline check has to see
    ///   BOTH halves, because a run that produced entities while attempting no request got its data from
    ///   somewhere, and a check that could only see the network half would pass it.
    ///
    ///   <para>It substitutes nothing: the files are the job's own, decoded by the same code the runtime
    ///   uses, so a candidate is judged against the real path rather than a fixture that resembles it. The
    ///   ceiling is off, because a suite that refused a fixture for its size would be judging the fixture.</para>
    /// </summary>
    internal sealed class RecordingFilesFactory : IJobFilesFactory
    {
        private readonly List<JobFiles> _created = new List<JobFiles>();

        /// <inheritdoc />
        public Int64 MaxFileBytes => 0;

        /// <inheritdoc />
        public Int64 MaxJobFileBytes => 0;

        /// <summary>Every setting key any run asked a file for, across every run. Readable after the runs
        /// have ended: ending a run drops the bytes, not the note of what was asked for.</summary>
        public IReadOnlyList<String> Requested
        {
            get
            {
                var all = new List<String>();
                foreach (var files in _created)
                {
                    all.AddRange(files.Requested);
                }

                return all;
            }
        }

        /// <summary>How many files were actually read, across every run - which is not the same as how
        /// many were asked about, and the offline check needs this one.</summary>
        public Int32 Reads
        {
            get
            {
                var total = 0;
                foreach (var files in _created)
                {
                    total += files.Reads;
                }

                return total;
            }
        }

        /// <inheritdoc />
        public JobFiles Create(IReadOnlyDictionary<String, JobFileSet>? filesBySettingKey)
        {
            var files = new JobFiles(filesBySettingKey);
            _created.Add(files);
            return files;
        }
    }

    /// <summary>
    ///   Hands the same target to every run, and does not dispose it, so the suite can look at the graph after
    ///   two runs have finished.
    /// </summary>
    internal sealed class StaticGraphTargetFactory : IGraphTargetFactory
    {
        private readonly IGraphTarget _target;

        public StaticGraphTargetFactory(IGraphTarget target)
        {
            _target = target;
        }

        public IGraphTarget Create(String? namespaceName)
        {
            return new UndisposableTarget(_target);
        }

        /// <summary>
        ///   A pass-through whose <see cref="Dispose"/> does nothing: the runner disposes the target it was given
        ///   after every run, which is right for a live connection and wrong for a graph the suite still has to
        ///   read.
        /// </summary>
        private sealed class UndisposableTarget : IGraphTarget
        {
            private readonly IGraphTarget _inner;

            public UndisposableTarget(IGraphTarget inner)
            {
                _inner = inner;
            }

            public Int32 IssuedMutationCount => _inner.IssuedMutationCount;

            public Task<Boolean> EnsureIndicesAsync(CancellationToken cancellationToken)
            {
                return _inner.EnsureIndicesAsync(cancellationToken);
            }

            public Task<IndexRepairOutcome> RepairIndicesAsync(CancellationToken cancellationToken)
            {
                return _inner.RepairIndicesAsync(cancellationToken);
            }

            public Task<ClaimLookup> ResolveClaimKeysAsync(IReadOnlyCollection<String> claimKeys, String instanceId,
                CancellationToken cancellationToken)
            {
                return _inner.ResolveClaimKeysAsync(claimKeys, instanceId, cancellationToken);
            }

            public Task<IReadOnlyList<Int32>> ElementsClaimedByAsync(String instanceId,
                CancellationToken cancellationToken)
            {
                return _inner.ElementsClaimedByAsync(instanceId, cancellationToken);
            }

            public Task<IReadOnlyDictionary<Int32, ElementState>> ReadElementsAsync(IReadOnlyCollection<Int32> ids,
                CancellationToken cancellationToken)
            {
                return _inner.ReadElementsAsync(ids, cancellationToken);
            }

            public Task<IReadOnlyList<Int32>> CreateVerticesAsync(IReadOnlyList<VertexWrite> vertices,
                CancellationToken cancellationToken)
            {
                return _inner.CreateVerticesAsync(vertices, cancellationToken);
            }

            public Task<IReadOnlyList<Int32>> CreateEdgesAsync(IReadOnlyList<EdgeWrite> edges,
                CancellationToken cancellationToken)
            {
                return _inner.CreateEdgesAsync(edges, cancellationToken);
            }

            public Task ApplyPropertyWritesAsync(IReadOnlyList<PropertyWrite> writes,
                CancellationToken cancellationToken)
            {
                return _inner.ApplyPropertyWritesAsync(writes, cancellationToken);
            }

            public Task RemoveElementsAsync(IReadOnlyCollection<Int32> ids, CancellationToken cancellationToken)
            {
                return _inner.RemoveElementsAsync(ids, cancellationToken);
            }

            public Task<IndexWriteOutcome> IndexClaimsAsync(IReadOnlyList<IndexEntry> entries,
                CancellationToken cancellationToken)
            {
                return _inner.IndexClaimsAsync(entries, cancellationToken);
            }

            public Task<TargetDurability> ReadDurabilityAsync(CancellationToken cancellationToken)
            {
                return _inner.ReadDurabilityAsync(cancellationToken);
            }

            public Task<TargetEmbedding> ReadEmbeddingStateAsync(CancellationToken cancellationToken)
            {
                return _inner.ReadEmbeddingStateAsync(cancellationToken);
            }

            public Task<EmbeddingWriteOutcome> EmbedSummariesAsync(String embeddingName,
                IReadOnlyList<SummaryWrite> summaries, CancellationToken cancellationToken,
                NoSQL.GraphDB.Integrations.Run.IRunProgress? progress = null,
                NoSQL.GraphDB.Integrations.Run.RunAbort abort = default)
            {
                return _inner.EmbedSummariesAsync(embeddingName, summaries, cancellationToken, progress, abort);
            }

            public void Dispose()
            {
            }
        }
    }

    /// <summary>
    ///   A log sink that keeps what it was given, so the suite can look at what reached a sink rather than
    ///   trusting that redaction worked.
    /// </summary>
    public sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<String> _lines = new List<String>();

        /// <summary>Every line this sink received, message and structured values together.</summary>
        public ImmutableArray<String> Lines
        {
            get
            {
                lock (_lines)
                {
                    return _lines.ToImmutableArray();
                }
            }
        }

        public ILogger CreateLogger(String categoryName)
        {
            return new CapturingLogger(this);
        }

        public void Dispose()
        {
        }

        private void Add(String line)
        {
            lock (_lines)
            {
                _lines.Add(line);
            }
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly CapturingLoggerProvider _owner;

            public CapturingLogger(CapturingLoggerProvider owner)
            {
                _owner = owner;
            }

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                _owner.Add(state.ToString() ?? String.Empty);
                return null;
            }

            public Boolean IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, String> formatter)
            {
                _owner.Add(formatter(state, exception));

                // The structured state is captured SEPARATELY, because a sink may serialise it rather than the
                // message: a credential that only ever appears as a structured value would otherwise be invisible
                // to the leak check.
                if (state is IReadOnlyList<KeyValuePair<String, Object?>> pairs)
                {
                    foreach (var pair in pairs)
                    {
                        _owner.Add(pair.Key + "=" + Convert.ToString(pair.Value,
                            System.Globalization.CultureInfo.InvariantCulture));
                    }
                }

                if (exception != null)
                {
                    _owner.Add(exception.ToString());
                }
            }
        }
    }
}
