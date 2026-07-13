// MIT License
//
// WriteAheadLogOptions.cs
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
    /// <summary>
    ///   Opt-in configuration for the write-ahead log (WAL), the durability-between-snapshots
    ///   mechanism of the persistence-hardening theme (spec P4 / plan Phase 5).
    ///
    ///   <para>
    ///   The WAL is <b>disabled by default</b>. A Fallen-8 instance created through the ordinary
    ///   constructors carries NO WAL and behaves exactly as before (no per-commit fsync, no extra
    ///   files). It is enabled only by passing an instance of this class - with a non-empty
    ///   <see cref="Path" /> - to the <see cref="Fallen8(Microsoft.Extensions.Logging.ILoggerFactory, WriteAheadLogOptions)" />
    ///   constructor. This keeps the WAL an explicit, cost-aware choice: with it on, every committed
    ///   data-mutating transaction is appended to the log and fsync'd before its
    ///   <c>WaitUntilFinished()</c> returns, which changes write throughput.
    ///   </para>
    ///
    ///   <para>
    ///   Once enabled, the log at <see cref="Path" /> accumulates every committed data-mutating
    ///   transaction (vertex/edge creation, property add/remove, element removal) plus the id-space
    ///   lifecycle markers (Trim, TabulaRasa) since the most recent full snapshot. A full
    ///   <c>Save</c> writes the hardened snapshot and then resets the log against that snapshot;
    ///   <c>Load</c> replays the log entries recorded after the loaded snapshot, so a crash between
    ///   snapshots recovers the committed transactions.
    ///   </para>
    /// </summary>
    public sealed class WriteAheadLogOptions
    {
        /// <summary>
        ///   The file system path of the write-ahead log. Enabling the WAL requires a non-empty
        ///   path; a null/whitespace path leaves the WAL disabled (as if no options were supplied).
        ///   The path should be stable across restarts of the same logical database so that a
        ///   recovering instance opens the same log.
        /// </summary>
        public String Path
        {
            get;
            set;
        }

        /// <summary>
        ///   Creates WAL options for the given log <paramref name="path" />.
        /// </summary>
        public WriteAheadLogOptions(String path)
        {
            Path = path;
        }
    }
}
