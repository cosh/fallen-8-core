// MIT License
//
// Fallen8.Metrics.cs
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

namespace NoSQL.GraphDB.Core
{
    public sealed partial class Fallen8
    {
        #region metric gauge accessors (feature observability - atomically published values only)

        /// <summary>Transactions waiting for the single writer (0 during teardown races).</summary>
        internal Int32 TransactionQueueDepthForMetrics => _txManager?.QueueDepth ?? 0;

        /// <summary>The metric face of DurabilityDegraded: the D1 sticky fence has tripped or an
        /// anchored log awaits its paired load (D3).</summary>
        internal Boolean WalDegradedForMetrics
        {
            get
            {
                var wal = _wal;
                return (wal != null && wal.HasFailed) || _walAwaitingPairedLoad;
            }
        }

        /// <summary>The current write-ahead log file length (0 when the WAL is off).</summary>
        internal Int64 WalSizeForMetrics => _wal?.CurrentLength ?? 0L;

        /// <summary>Registered index count.</summary>
        internal Int32 IndexCountForMetrics => IndexFactory?.Indices?.Count ?? 0;

        /// <summary>Total keys across all registered indices (aggregate only - per-index detail
        /// is GET /statistics' job, behind auth; no index NAME ever becomes a metric tag).
        /// Enumerates a read-locked SNAPSHOT of the index map - the exporter's collection
        /// thread must never race a concurrent create/delete on the plain dictionary.</summary>
        internal Int64 IndexEntriesForMetrics
        {
            get
            {
                var factory = IndexFactory;
                if (factory == null)
                {
                    return 0L;
                }

                var total = 0L;
                foreach (var index in factory.GetIndicesSnapshot())
                {
                    total += index.CountOfKeys();
                }
                return total;
            }
        }

        #endregion

        /// <summary>
        ///   Total on-disk size of a checkpoint: the snapshot file plus its partitions and index
        ///   sidecars, which all share the snapshot path as their name prefix. Files whose name
        ///   extends the prefix with a '#' are EXCLUDED - that is the version stamp a later save
        ///   to the same base path gets (PersistencyFactory), i.e. a DIFFERENT checkpoint whose
        ///   size must not be summed into this one's. Best-effort (-1 when unmeasurable) - a
        ///   metrics detail must never fault a save/load.
        /// </summary>
        private static long MeasureCheckpointBytes(string checkpointPath)
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(checkpointPath));
                var prefix = System.IO.Path.GetFileName(checkpointPath);
                if (directory == null || string.IsNullOrEmpty(prefix))
                {
                    return -1L;
                }

                var total = 0L;
                foreach (var file in System.IO.Directory.EnumerateFiles(directory, prefix + "*"))
                {
                    var name = System.IO.Path.GetFileName(file);
                    if (name.Length > prefix.Length && name[prefix.Length] == '#')
                    {
                        continue;
                    }
                    total += new System.IO.FileInfo(file).Length;
                }
                return total;
            }
            catch
            {
                return -1L;
            }
        }
    }
}
