// MIT License
//
// IngestionJobQueue.cs
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
using System.Threading;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.App.Configuration;

namespace NoSQL.GraphDB.App.Ingestion
{
    /// <summary>One queued ingestion job (feature semantic-layer). It carries the NAMESPACE NAME
    /// (null = the default namespace) so the worker re-resolves the concrete engine off the
    /// request thread, where the addressing AsyncLocal does not flow. Link index ids are the
    /// validated allowlist (re-resolved against the engine at processing time).</summary>
    public sealed class IngestionJob
    {
        public String Namespace
        {
            get; set;
        }

        public Int32 DocumentId
        {
            get; set;
        }

        public IngestionRequest Request
        {
            get; set;
        }

        public List<String> LinkIndexIds
        {
            get; set;
        }

        public Int32 LinkCap
        {
            get; set;
        }
    }

    /// <summary>
    ///   THE single global ingestion queue (feature semantic-layer FR-3): one bounded channel
    ///   shared across all namespaces, drained by ONE consumer in arrival order (FIFO). Enqueue
    ///   beyond the bound fails fast (the controller answers 503) rather than blocking the
    ///   request thread.
    /// </summary>
    public sealed class IngestionJobQueue
    {
        private readonly Channel<IngestionJob> _channel;

        public IngestionJobQueue(IOptions<Fallen8IngestionOptions> options)
        {
            var capacity = Math.Max(1, options.Value?.MaxQueueLength ?? 256);
            _channel = Channel.CreateBounded<IngestionJob>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                // TryWrite returns false when full (we surface 503); no writer ever blocks.
                FullMode = BoundedChannelFullMode.Wait
            });
        }

        /// <summary>Enqueues a job; false when the queue is at capacity.</summary>
        public Boolean TryEnqueue(IngestionJob job) => _channel.Writer.TryWrite(job);

        /// <summary>The consumer's ordered stream of jobs.</summary>
        public IAsyncEnumerable<IngestionJob> ReadAllAsync(CancellationToken cancellationToken)
            => _channel.Reader.ReadAllAsync(cancellationToken);
    }
}
