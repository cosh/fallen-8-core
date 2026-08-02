// MIT License
//
// IngestionWorker.cs
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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NoSQL.GraphDB.App.Ingestion
{
    /// <summary>
    ///   The single consumer of the global ingestion queue (feature semantic-layer FR-3): drains
    ///   jobs in arrival order, one at a time, on a background thread. On start it sweeps any
    ///   Document left <c>processing</c> by a previous process (FR-5) to <c>failed:interrupted</c>.
    ///   One job's failure never stops the loop; the pipeline records it on that document.
    /// </summary>
    public sealed class IngestionWorker : BackgroundService
    {
        private readonly IngestionJobQueue _queue;
        private readonly DocumentIngestionService _service;
        private readonly ILogger<IngestionWorker> _logger;

        public IngestionWorker(IngestionJobQueue queue, DocumentIngestionService service,
            ILogger<IngestionWorker> logger)
        {
            _queue = queue;
            _service = service;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                _service.SweepInterruptedDocuments();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The ingestion startup sweep failed");
            }

            await foreach (var job in _queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await _service.ProcessJobAsync(job, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // ProcessJobAsync marks the document failed for expected faults; this is the
                    // backstop for the unexpected, so one bad job never kills the consumer.
                    _logger.LogError(ex, "Ingestion job for document {DocumentId} in namespace '{Namespace}' threw",
                        job.DocumentId, job.Namespace ?? "default");
                }
            }
        }
    }
}
