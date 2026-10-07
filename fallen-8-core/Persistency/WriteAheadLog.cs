// MIT License
//
// WriteAheadLog.cs
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
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Core.Helper;

namespace NoSQL.GraphDB.Core.Persistency
{
    /// <summary>
    ///   The append-only write-ahead log that provides durability between full snapshots
    ///   (persistence-hardening spec P4 / plan Phase 5). It is enabled explicitly (opt-in); with it
    ///   off, none of this runs.
    ///
    ///   <para><b>File layout.</b> A self-describing envelope consistent with the Stage-A snapshot
    ///   format: an 8-byte magic + little-endian version, then a CRC-protected header recording the
    ///   <em>baseline</em> id-space size (<see cref="BaselineCurrentId" /> - the writer's
    ///   <c>_currentId</c> as of the snapshot this log builds upon) and a <em>pairing token</em>
    ///   (the <em>canonicalized</em> path of that snapshot - see <see cref="NormalizePathToken" /> -
    ///   or empty for a log that predates any snapshot). After the
    ///   header come the entries, each framed as <c>[Int32 length][payload][UInt32 CRC-32]</c>.</para>
    ///
    ///   <para><b>Single-writer.</b> Every <see cref="AppendBuffered" />, <see cref="ResetToSnapshot" /> and
    ///   the fresh-header write happen only on the Fallen-8 single transaction-writer thread, after a
    ///   transaction has reached its committed terminal state. The log holds no persistent file
    ///   handle: each append opens the file in append mode, writes one framed entry, fsyncs
    ///   (<c>Flush(true)</c>) and closes - so a committed transaction's entry is durable before the
    ///   append returns (hence before <c>WaitUntilFinished</c> returns for it), and no lock is held
    ///   between commits.</para>
    ///
    ///   <para><b>Corrupt/torn tail.</b> A crash mid-append leaves an incomplete trailing entry.
    ///   <see cref="ReadEntries" /> reads entries only while a full, CRC-valid frame remains, sizing
    ///   every read against the bytes physically left in the file (never against an untrusted length
    ///   prefix), and stops cleanly at the last complete entry - it never throws or over-allocates on
    ///   a torn tail. What recovery then does with the bytes it stopped at, before the next append,
    ///   is <see cref="SealAfterReplay" />'s job.</para>
    /// </summary>
    internal sealed class WriteAheadLog : IDisposable
    {
        /// <summary>Magic prefixing the WAL file: ASCII <c>"F8WAL"</c> + three NUL bytes.</summary>
        private static readonly byte[] Magic =
        {
            (byte)'F', (byte)'8', (byte)'W', (byte)'A', (byte)'L', 0x00, 0x00, 0x00
        };

        /// <summary>On-disk WAL format version.</summary>
        private const int FormatVersion = 1;

        /// <summary>Magic (8) + version (4).</summary>
        private const int PreambleLength = 8 + 4;

        /// <summary>The shortest valid header: preamble, baseline (8), token length (4), an empty
        /// token and the header CRC (4). The header of a fresh, unanchored log is exactly this long.</summary>
        private const int MinimumHeaderLength = PreambleLength + 8 + 4 + 4;

        /// <summary>The shortest complete frame: the length (4), a payload of at least one byte and the
        /// CRC (4).</summary>
        private const int MinimumEntryLength = 4 + 1 + 4;

        /// <summary>Above this many bytes after the point a scan stopped, <see cref="RemainderHoldsAnEntry" />
        /// does not search and answers that entries may follow.</summary>
        private const int ProbeLimitBytes = 64 * 1024 * 1024;

        /// <summary>How many payload bytes <see cref="RemainderHoldsAnEntry" /> may hash before it stops
        /// searching and answers that entries may follow.</summary>
        private const long ProbeHashBudgetBytes = 1024L * 1024 * 1024;

        /// <summary>The pairing token of a log that does not yet build upon any snapshot.</summary>
        private const string UnanchoredToken = "";

        /// <summary>
        ///   How pairing tokens (snapshot paths) are compared. A pairing token is a file-system path,
        ///   so it is matched the way the host file system resolves paths: case-insensitively on
        ///   Windows and macOS (whose default volumes are case-insensitive), case-sensitively
        ///   elsewhere. Together with <see cref="NormalizePathToken" /> this makes the SAME snapshot
        ///   pair with its log across a Windows case variant, relative-vs-absolute, <c>"./"</c>
        ///   segments and trailing separators - so a non-verbatim reload of the same snapshot replays
        ///   its log rather than silently discarding committed entries.
        /// </summary>
        private static readonly StringComparison PathComparison =
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        private readonly string _path;
        private readonly ILogger _logger;
        private readonly Encoding _enc = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private long _baselineCurrentId;
        private string _pairingToken;

        /// <summary>Whether the header was read, created or rewritten: false only for an existing log
        /// whose header cannot be read (<see cref="HeaderUnreadable" />).</summary>
        private bool _valid;

        /// <summary>
        ///   Sticky failure fence (feature crash-durability-hardening D1). Once a <see cref="FlushGroup" />
        ///   fails, a recovery leaves bytes its replay could not pass (<see cref="SealAfterReplay" />), or
        ///   the header cannot be read (<see cref="HeaderUnreadable" />), it is set and every subsequent
        ///   <see cref="AppendBuffered" /> is a no-op (it does not touch the file), so a torn frame is
        ///   never followed by more frames that a later replay would silently drop. Cleared only by
        ///   <see cref="ResetToSnapshot" /> (a successful Save re-writes the log fresh against the new
        ///   snapshot - the sanctioned recovery from a degraded log).
        /// </summary>
        private bool _failed;

        /// <summary>
        ///   Frames buffered for the current commit group (feature write-path-throughput): each
        ///   <see cref="AppendBuffered" /> adds one framed entry here without touching disk, and
        ///   <see cref="FlushGroup" /> writes them ALL and fsyncs ONCE. Amortising the fsync across a
        ///   drained batch is the throughput win; a lone commit (a group of one) still fsyncs
        ///   immediately, so its latency is unchanged. Only ever touched on the single writer thread.
        /// </summary>
        private readonly List<byte[]> _pendingFrames = new List<byte[]>();

        /// <summary>
        ///   Opens the log at <paramref name="path" />. An existing, well-formed log is adopted (its
        ///   header parsed and its entries left in place for replay); a missing log is created fresh
        ///   with a zero baseline and no snapshot pairing. An existing header that cannot be read is
        ///   never parsed as anything: if the file is too short to hold an entry
        ///   (<see cref="CannotHoldAnEntry" />), it is reset; otherwise it may hold acknowledged commits,
        ///   so it is kept as it is and the log is fenced (feature wal-torn-tail, see
        ///   <see cref="HeaderUnreadable" />).
        /// </summary>
        internal WriteAheadLog(string path, ILogger logger)
        {
            _path = path;
            _logger = logger;

            if (File.Exists(path))
            {
                if (TryReadHeader())
                {
                    _valid = true;
                    _logger.LogInformation(
                        "Write-ahead log opened at \"{Path}\" (baseline id {Baseline}, pairing token \"{Token}\").",
                        _path, _baselineCurrentId, _pairingToken);
                    return;
                }

                if (!CannotHoldAnEntry())
                {
                    _failed = true;
                    _logger.LogError(
                        "Write-ahead log \"{Path}\": the header cannot be read, so the file is kept as it is and nothing in it is replayed; the log is degraded and new commits are not durable until the next successful Save.",
                        _path);
                    return;
                }

                _logger.LogWarning(
                    "Write-ahead log \"{Path}\": the header cannot be read and the file is too short to hold an entry, so it is reset.",
                    _path);
            }

            WriteHeader(0, UnanchoredToken, useTempAndRename: false);
            _baselineCurrentId = 0;
            _pairingToken = UnanchoredToken;
            _valid = true;
        }

        /// <summary>
        ///   Whether the file is no longer than the shortest header, so resetting it destroys no entry.
        ///   An interrupted write of a fresh log's header leaves such a file: that header is exactly
        ///   that long and written in place, while an anchored one goes through a temp file and a
        ///   rename and is never torn.
        /// </summary>
        private bool CannotHoldAnEntry()
        {
            try
            {
                return new FileInfo(_path).Length <= MinimumHeaderLength;
            }
            catch (Exception ex)
            {
                // Keeping the file and fencing the log is the answer that destroys nothing.
                _logger.LogWarning(ex, "Reading the length of the write-ahead log at \"{Path}\" failed.", _path);
                return false;
            }
        }

        /// <summary>
        ///   Whether the existing log's header could not be read when it was opened. Such a log is kept
        ///   unchanged and fenced: it is neither unanchored nor anchored, nothing in it is replayed, and
        ///   the engine reports the recovery as one that lost history. A Save, or a Load that re-anchors
        ///   the log, rewrites it.
        /// </summary>
        internal bool HeaderUnreadable
        {
            get { return !_valid; }
        }

        /// <summary>The writer <c>_currentId</c> as of the snapshot this log builds upon.</summary>
        internal long BaselineCurrentId
        {
            get { return _baselineCurrentId; }
        }

        /// <summary>
        ///   Whether the log pairs with the snapshot at <paramref name="snapshotPath" />. Both the
        ///   stored token and the compared path are canonicalized (<see cref="NormalizePathToken" />)
        ///   and matched with <see cref="PathComparison" /> so that any file-system-equivalent form of
        ///   the same snapshot path pairs - a raw ordinal match would fail on a Windows case variant, a
        ///   relative-vs-absolute form, a <c>"./"</c> segment or a trailing separator, and the
        ///   non-pairing branch would then DISCARD the log's committed-since-snapshot entries.
        /// </summary>
        internal bool PairsWith(string snapshotPath)
        {
            return _valid
                   && !string.IsNullOrEmpty(_pairingToken)
                   && string.Equals(
                          NormalizePathToken(_pairingToken),
                          NormalizePathToken(snapshotPath),
                          PathComparison);
        }

        /// <summary>
        ///   Canonicalizes a snapshot path into the form stored and compared as the pairing token: an
        ///   absolute, normalized path via <see cref="Path.GetFullPath(string)" /> (which collapses
        ///   <c>"./"</c> and redundant separators and resolves a relative path against the current
        ///   directory). A null/empty path is the unanchored token; a path that cannot be canonicalized
        ///   falls back to its raw form, so pairing degrades to the previous exact-match behaviour
        ///   rather than throwing. Idempotent: normalizing an already-normalized token is a no-op.
        /// </summary>
        private static string NormalizePathToken(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return UnanchoredToken;
            }

            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }

        /// <summary>
        ///   Whether the log currently holds at least one complete, replayable entry (a full,
        ///   CRC-valid frame past the header). Cheap: it stops at the first such entry rather than
        ///   scanning the whole file. Used to decide whether discarding a non-pairing log would drop
        ///   committed work (and therefore must be signalled loudly).
        /// </summary>
        internal bool HasEntries()
        {
            foreach (var _ in ReadEntries())
            {
                return true;
            }

            return false;
        }

        /// <summary>Whether the log has entries but does not yet build upon any snapshot.</summary>
        internal bool IsUnanchored
        {
            get { return _valid && string.IsNullOrEmpty(_pairingToken); }
        }

        /// <summary>
        ///   Whether the log builds upon (is paired with) a specific snapshot - i.e. it carries a
        ///   non-empty pairing token and is waiting for that snapshot to be loaded before its entries
        ///   replay. A fresh empty log and an unanchored log both have no token and are NOT anchored.
        /// </summary>
        internal bool IsAnchored
        {
            get { return _valid && !string.IsNullOrEmpty(_pairingToken); }
        }

        /// <summary>
        ///   Whether the sticky failure fence has tripped (see <see cref="_failed" />). While
        ///   set, the log is degraded: no further entries are written, and durability is restored only
        ///   by a successful Save (<see cref="ResetToSnapshot" />). Feature crash-durability-hardening D1.
        /// </summary>
        internal bool HasFailed
        {
            get { return _failed; }
        }

        /// <summary>The number of frames buffered for the current commit group (feature
        /// observability): lets the flush metrics distinguish a REAL flush attempt from the
        /// empty/degraded fast paths, so failure counts and duration percentiles stay honest.</summary>
        internal int PendingFrameCount
        {
            get { return _pendingFrames.Count; }
        }

        /// <summary>
        ///   The current on-disk length of the log file, for the <c>fallen8.wal.size</c> gauge
        ///   (feature observability). Best-effort: 0 when the file is missing or unreadable -
        ///   a gauge callback must never throw into the exporter's collection thread.
        /// </summary>
        internal long CurrentLength
        {
            get
            {
                try
                {
                    var info = new FileInfo(_path);
                    return info.Exists ? info.Length : 0L;
                }
                catch
                {
                    return 0L;
                }
            }
        }

        /// <summary>
        ///   Buffers one framed entry (<c>[Int32 length][payload][UInt32 CRC-32]</c>) for the current
        ///   commit group WITHOUT touching disk (feature write-path-throughput). Runs only on the single
        ///   writer thread, after the transaction has committed. The bytes reach disk - and become
        ///   durable - only when <see cref="FlushGroup" /> is called. A no-op once the failure fence has
        ///   tripped (feature crash-durability-hardening D1): the transaction is then non-durable, which
        ///   the caller records.
        /// </summary>
        internal void AppendBuffered(byte[] payload)
        {
            if (_failed)
            {
                return;
            }

            var frame = new byte[4 + payload.Length + 4];
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0), payload.Length);
            Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
            var crc = Crc32.Compute(payload, 0, payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4 + payload.Length), crc);

            _pendingFrames.Add(frame);
        }

        /// <summary>
        ///   Writes every frame buffered since the last flush and fsyncs them in ONE
        ///   <c>Flush(true)</c> (feature write-path-throughput group commit), then clears the buffer.
        ///   Returns whether the group is durable: <c>true</c> when the write+fsync succeeded (or there
        ///   was nothing to flush and the fence is clear), <c>false</c> when the fence had already
        ///   tripped or this flush failed. On failure it best-effort truncates the partially written
        ///   group back to the pre-flush length, trips the sticky fence, logs one Error, and returns
        ///   <c>false</c> (it does NOT throw - the caller marks the whole group non-durable and the
        ///   worker survives; feature crash-durability-hardening D1). The single open+write+fsync+close
        ///   per group (rather than per commit) is exactly what amortises the fsync.
        /// </summary>
        internal bool FlushGroup()
        {
            if (_pendingFrames.Count == 0)
            {
                return !_failed;
            }

            if (_failed)
            {
                _pendingFrames.Clear();
                return false;
            }

            try
            {
                using (var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read,
                           Constants.BufferSize, FileOptions.None))
                {
                    // In append mode the stream is positioned at end before the write, so Length is the
                    // pre-flush length - captured so a partially written group can be truncated back.
                    var preLength = fs.Length;
                    try
                    {
                        for (var i = 0; i < _pendingFrames.Count; i++)
                        {
                            fs.Write(_pendingFrames[i], 0, _pendingFrames[i].Length);
                        }
                        fs.Flush(true);
                    }
                    catch
                    {
                        // Best-effort: drop the partially written group so the tail is not torn.
                        try
                        {
                            if (fs.Length > preLength)
                            {
                                fs.SetLength(preLength);
                                fs.Flush(true);
                            }
                        }
                        catch
                        {
                            // Contained: the log is already being marked degraded below.
                        }
                        throw;
                    }
                }

                _pendingFrames.Clear();
                return true;
            }
            catch (Exception ex)
            {
                _failed = true;
                _pendingFrames.Clear();
                _logger.LogError(ex,
                    "Flushing the write-ahead-log commit group at \"{Path}\" failed; the log is now DEGRADED - the transactions in this group are not durable in the log until the next successful Save.",
                    _path);
                return false;
            }
        }

        /// <summary>
        ///   Resets the log to build upon a freshly written snapshot: it is rewritten (atomically, via
        ///   a temp file + fsync + rename) to just a header recording the new
        ///   <paramref name="baselineCurrentId" /> and a pairing token of <paramref name="snapshotPath" />,
        ///   discarding the now-superseded pre-snapshot entries. MUST be called only AFTER the snapshot
        ///   is durably committed, so that a crash between "snapshot durable" and "log reset" still
        ///   leaves a log whose (old) pairing token does not match the new snapshot - it is then simply
        ///   not replayed onto the new snapshot (no double-apply), while the new snapshot already
        ///   contains every transaction committed up to the save.
        /// </summary>
        internal void ResetToSnapshot(string snapshotPath, long baselineCurrentId)
        {
            // Store the CANONICAL path (not the raw save/load path as-passed) so the on-disk pairing
            // token is stable across file-system-equivalent forms of the same snapshot path.
            // Any frames buffered but not yet flushed are superseded by the snapshot being anchored to
            // (Save is a group boundary, so in practice the buffer is already empty; clear defensively).
            _pendingFrames.Clear();

            var token = NormalizePathToken(snapshotPath);
            WriteHeader(baselineCurrentId, token, useTempAndRename: true);
            _baselineCurrentId = baselineCurrentId;
            _pairingToken = token;
            _valid = true;

            // A successful Save re-wrote the log fresh against the new snapshot: clear the failure
            // fence (the new snapshot is the durable baseline) - feature crash-durability-hardening D1.
            _failed = false;
        }

        /// <summary>How a scan of the log ended (feature wal-torn-tail).</summary>
        internal enum ScanEnd
        {
            /// <summary>The reader stopped consuming before the scan reached its own end: a replay that
            /// broke off at an entry it read but could not apply, or one that threw.</summary>
            Abandoned,

            /// <summary>Every byte after the header belongs to a complete, CRC-valid entry.</summary>
            Clean,

            /// <summary>The scan stopped at bytes it could not read as an entry, and no complete entry
            /// follows them: what an interrupted write leaves, never acknowledged.</summary>
            TornTail,

            /// <summary>The scan stopped at bytes it could not read as an entry and a complete entry
            /// follows them, or the header no longer parses: acknowledged entries may sit beyond the
            /// point the scan reached.</summary>
            Unreadable,
        }

        /// <summary>Where and why a scan of the log stopped, filled in by
        /// <see cref="ReadEntries(Scan)" /> and read by <see cref="SealAfterReplay" />.</summary>
        internal sealed class Scan
        {
            internal ScanEnd End { get; set; } = ScanEnd.Abandoned;

            /// <summary>The start of the entry the scan ended on (the torn or unreadable one, or the
            /// one the reader broke off after), or the end of the file after a clean scan: the first
            /// byte a replay that stopped there did not apply.</summary>
            internal long StopOffset { get; set; }
        }

        /// <summary>
        ///   Enumerates every COMPLETE entry's payload, in append (commit) order, stopping cleanly at
        ///   the first incomplete or CRC-failing frame. Never throws on a torn/corrupt tail and never
        ///   sizes an allocation from an untrusted length: each frame's declared length is validated
        ///   against the bytes physically remaining in the file before the payload is read. When
        ///   <paramref name="scan" /> is given, it records where and why the scan stopped.
        /// </summary>
        internal IEnumerable<byte[]> ReadEntries(Scan scan = null)
        {
            if (!_valid || !File.Exists(_path))
            {
                Stop(scan, ScanEnd.Clean, 0);
                yield break;
            }

            using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read,
                       Constants.BufferSize, FileOptions.SequentialScan))
            {
                var fileLength = fs.Length;

                // Re-validate + skip the header. A header that no longer parses (e.g. externally
                // truncated to below the header) yields no entries rather than misparsing.
                if (!TrySkipHeader(fs, fileLength))
                {
                    Stop(scan, ScanEnd.Unreadable, 0);
                    yield break;
                }

                var lengthBuffer = new byte[4];
                var crcBuffer = new byte[4];

                while (true)
                {
                    // Set before every read, so a fault while reading, or a reader that breaks off
                    // after this entry, leaves it pointing at the first entry not applied.
                    var position = fs.Position;
                    if (scan != null)
                    {
                        scan.StopOffset = position;
                    }

                    if (position == fileLength)
                    {
                        Stop(scan, ScanEnd.Clean, position);
                        yield break;
                    }

                    var payload = TryReadEntry(fs, fileLength, lengthBuffer, crcBuffer);
                    if (payload == null)
                    {
                        // Which kind of stop this is matters only to a recovery (SealAfterReplay).
                        if (scan != null)
                        {
                            var entriesFollow = RemainderHoldsAnEntry(fs, position + 1, fileLength);
                            Stop(scan, entriesFollow ? ScanEnd.Unreadable : ScanEnd.TornTail, position);
                        }

                        yield break;
                    }

                    yield return payload;
                }
            }
        }

        private static void Stop(Scan scan, ScanEnd end, long offset)
        {
            if (scan != null)
            {
                scan.End = end;
                scan.StopOffset = offset;
            }
        }

        /// <summary>
        ///   Reads the entry at the stream's position, or returns null when the bytes there are not one:
        ///   fewer than a complete entry needs, a length that is not a positive count fitting in what
        ///   remains (no writer produces an empty or a negative one), or a CRC that does not match.
        ///   Never allocates from an unchecked length.
        /// </summary>
        private static byte[] TryReadEntry(FileStream fs, long fileLength, byte[] lengthBuffer, byte[] crcBuffer)
        {
            if (fileLength - fs.Position < MinimumEntryLength || !ReadExactly(fs, lengthBuffer, 4))
            {
                return null;
            }

            var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
            if (payloadLength <= 0 || (long)payloadLength + 4 > fileLength - fs.Position)
            {
                return null;
            }

            var payload = new byte[payloadLength];
            if (!ReadExactly(fs, payload, payloadLength) || !ReadExactly(fs, crcBuffer, 4))
            {
                return null;
            }

            var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(crcBuffer);
            return Crc32.Compute(payload, 0, payloadLength) == expectedCrc ? payload : null;
        }

        /// <summary>
        ///   Whether the bytes from <paramref name="from" /> to the end of the file contain at least one
        ///   complete entry anywhere: a positive length that fits, a payload of the shape every entry has
        ///   (<see cref="WalTransactionCodec.HasEntryShape" />), and a matching CRC-32. The search is
        ///   bounded (<see cref="ProbeLimitBytes" />, <see cref="ProbeHashBudgetBytes" />), and past a
        ///   bound it answers <c>true</c>, because keeping the bytes destroys nothing and cutting could.
        ///   See <see cref="SealAfterReplay" /> for what the answer decides.
        /// </summary>
        private static bool RemainderHoldsAnEntry(FileStream fs, long from, long fileLength)
        {
            var length = fileLength - from;
            if (length < MinimumEntryLength)
            {
                return false;
            }

            if (length > ProbeLimitBytes)
            {
                return true;
            }

            var bytes = new byte[length];
            fs.Seek(from, SeekOrigin.Begin);
            if (!ReadExactly(fs, bytes, (int)length))
            {
                return true;
            }

            long hashed = 0;
            for (var p = 0; p + MinimumEntryLength <= length; p++)
            {
                var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p));
                if (payloadLength <= 0
                    || (long)p + 4 + payloadLength + 4 > length
                    || !WalTransactionCodec.HasEntryShape(bytes.AsSpan(p + 4, payloadLength)))
                {
                    continue;
                }

                hashed += payloadLength;
                if (hashed > ProbeHashBudgetBytes)
                {
                    return true;
                }

                var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p + 4 + payloadLength));
                if (Crc32.Compute(bytes, p + 4, payloadLength) == storedCrc)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        ///   Makes the log safe to append to after a replay, and reports whether entries that may have
        ///   been acknowledged were left unreplayed (feature wal-torn-tail). Appends go to the end of the
        ///   FILE, while a replay stops at the first entry it cannot read or apply, so anything appended
        ///   behind that point would be acknowledged as durable and never replayed. Therefore:
        ///   <list type="bullet">
        ///     <item>A torn tail (<see cref="ScanEnd.TornTail" />) is cut back to the last complete
        ///     entry. No complete entry follows the stop, so the bytes are what an interrupted write
        ///     left and were never acknowledged: a partial entry, a full-length entry with a bad CRC
        ///     (the sectors of one write can reach the disk out of order on a power loss), or zeros (a
        ///     file system can persist a new size before the data). If the cut fails, the fence is
        ///     tripped instead.</item>
        ///     <item>Anything else (an entry the replay could not apply, a replay that threw, or
        ///     unreadable bytes with a complete entry after them) is left in place and the fence is
        ///     tripped: later commits report non-durable until a Save re-baselines the log. Cutting
        ///     could destroy acknowledged entries; appending behind them would repeat the defect.</item>
        ///   </list>
        ///   The two cannot always be told apart: a crash inside one commit group of several entries can
        ///   leave an unreadable entry with a complete one of the same unacknowledged group after it. That
        ///   is read as the second case, a false alarm that costs a degraded log until the next Save and
        ///   destroys nothing.
        /// </summary>
        /// <param name="scan">The scan the replay read its entries from, after the replay ended.</param>
        /// <returns>Whether entries that may have been acknowledged were left unreplayed.</returns>
        internal bool SealAfterReplay(Scan scan)
        {
            switch (scan.End)
            {
                case ScanEnd.Clean:
                    return false;

                case ScanEnd.TornTail:
                    CutTornTail(scan.StopOffset);
                    return false;

                default:
                    // Abandoned (the replay broke off or threw) or Unreadable.
                    _failed = true;
                    _logger.LogError(
                        "Write-ahead log \"{Path}\": recovery stopped at offset {Offset}, before the end of the log; the entries from there on are kept but not replayed, and the log is degraded: new commits are not durable until the next successful Save.",
                        _path, scan.StopOffset);
                    return true;
            }
        }

        private void CutTornTail(long lastCompleteEnd)
        {
            try
            {
                long before;
                using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Write, FileShare.Read,
                           Constants.BufferSize, FileOptions.None))
                {
                    before = fs.Length;
                    fs.SetLength(lastCompleteEnd);
                    fs.Flush(true);
                }

                _logger.LogWarning(
                    "Write-ahead log \"{Path}\": cut {Bytes} bytes left by an interrupted write after the last complete entry.",
                    _path, before - lastCompleteEnd);
            }
            catch (Exception ex)
            {
                _failed = true;
                _logger.LogError(ex,
                    "Write-ahead log \"{Path}\": the bytes left by an interrupted write could not be cut; the log is degraded and new commits are not durable until the next successful Save.",
                    _path);
            }
        }

        public void Dispose()
        {
            // The log holds no persistent handle (each append opens/fsyncs/closes), so there is
            // nothing to release. Provided for symmetry with the owning engine's lifecycle.
        }

        #region header

        private void WriteHeader(long baselineCurrentId, string token, bool useTempAndRename)
        {
            var tokenBytes = _enc.GetBytes(token ?? UnanchoredToken);

            using (var mem = new MemoryStream(PreambleLength + 8 + 4 + tokenBytes.Length + 4))
            {
                mem.Write(Magic, 0, Magic.Length);

                Span<byte> scratch = stackalloc byte[8];
                BinaryPrimitives.WriteInt32LittleEndian(scratch, FormatVersion);
                mem.Write(scratch.Slice(0, 4));

                // Header body: baseline (8) + tokenLength (4) + token bytes, then a CRC over the body.
                var bodyStart = (int)mem.Position;
                BinaryPrimitives.WriteInt64LittleEndian(scratch, baselineCurrentId);
                mem.Write(scratch.Slice(0, 8));
                BinaryPrimitives.WriteInt32LittleEndian(scratch, tokenBytes.Length);
                mem.Write(scratch.Slice(0, 4));
                mem.Write(tokenBytes, 0, tokenBytes.Length);

                var buffer = mem.GetBuffer();
                var bodyLength = (int)mem.Position - bodyStart;
                var crc = Crc32.Compute(buffer, bodyStart, bodyLength);
                BinaryPrimitives.WriteUInt32LittleEndian(scratch, crc);
                mem.Write(scratch.Slice(0, 4));

                var headerBytes = mem.ToArray();

                if (useTempAndRename)
                {
                    var temp = DurableFileIo.TempNameFor(_path);
                    try
                    {
                        WriteAllBytesDurably(temp, headerBytes);

                        // Through the shared retry: this exact rename lost a race with a Windows file handle
                        // and rolled a Save transaction back (see DurableFileIo.PublishWithRetry).
                        DurableFileIo.PublishWithRetry(temp, _path, _logger);
                    }
                    catch
                    {
                        TryDeleteFile(temp);
                        throw;
                    }
                }
                else
                {
                    WriteAllBytesDurably(_path, headerBytes);
                }
            }
        }

        /// <summary>
        ///   Reads and validates the header into <see cref="_baselineCurrentId" /> /
        ///   <see cref="_pairingToken" />. Returns false (never throws) if the file is too short, the
        ///   magic/version is wrong, or the header CRC fails.
        /// </summary>
        private bool TryReadHeader()
        {
            try
            {
                using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read,
                           Constants.BufferSize, FileOptions.SequentialScan))
                {
                    var fileLength = fs.Length;
                    if (fileLength < MinimumHeaderLength)
                    {
                        return false;
                    }

                    var preamble = new byte[PreambleLength];
                    if (!ReadExactly(fs, preamble, PreambleLength) || !MagicMatches(preamble))
                    {
                        return false;
                    }
                    if (BinaryPrimitives.ReadInt32LittleEndian(preamble.AsSpan(8)) != FormatVersion)
                    {
                        return false;
                    }

                    var fixedBody = new byte[12]; // baseline (8) + tokenLength (4)
                    if (!ReadExactly(fs, fixedBody, 12))
                    {
                        return false;
                    }
                    var baseline = BinaryPrimitives.ReadInt64LittleEndian(fixedBody.AsSpan(0));
                    var tokenLength = BinaryPrimitives.ReadInt32LittleEndian(fixedBody.AsSpan(8));

                    // Validate the token length against the bytes remaining (+ the 4-byte CRC) before
                    // allocating, so a corrupt length cannot drive a huge allocation.
                    if (tokenLength < 0 || (long)tokenLength + 4 > fileLength - fs.Position)
                    {
                        return false;
                    }

                    var tokenBytes = new byte[tokenLength];
                    if (!ReadExactly(fs, tokenBytes, tokenLength))
                    {
                        return false;
                    }

                    var crcBytes = new byte[4];
                    if (!ReadExactly(fs, crcBytes, 4))
                    {
                        return false;
                    }

                    // The header CRC covers the body: baseline + tokenLength + token bytes.
                    var body = new byte[12 + tokenLength];
                    Buffer.BlockCopy(fixedBody, 0, body, 0, 12);
                    Buffer.BlockCopy(tokenBytes, 0, body, 12, tokenLength);
                    var expected = BinaryPrimitives.ReadUInt32LittleEndian(crcBytes);
                    if (Crc32.Compute(body, 0, body.Length) != expected)
                    {
                        return false;
                    }

                    _baselineCurrentId = baseline;
                    _pairingToken = _enc.GetString(tokenBytes);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reading the write-ahead-log header at \"{Path}\" failed.", _path);
                return false;
            }
        }

        /// <summary>
        ///   Positions <paramref name="fs" /> just past a valid header, or returns false if the header
        ///   no longer parses. Mirrors <see cref="TryReadHeader" /> but only advances the position.
        /// </summary>
        private bool TrySkipHeader(FileStream fs, long fileLength)
        {
            if (fileLength < MinimumHeaderLength)
            {
                return false;
            }

            var preamble = new byte[PreambleLength];
            if (!ReadExactly(fs, preamble, PreambleLength) || !MagicMatches(preamble))
            {
                return false;
            }
            if (BinaryPrimitives.ReadInt32LittleEndian(preamble.AsSpan(8)) != FormatVersion)
            {
                return false;
            }

            var fixedBody = new byte[12];
            if (!ReadExactly(fs, fixedBody, 12))
            {
                return false;
            }
            var tokenLength = BinaryPrimitives.ReadInt32LittleEndian(fixedBody.AsSpan(8));
            if (tokenLength < 0 || (long)tokenLength + 4 > fileLength - fs.Position)
            {
                return false;
            }

            // Skip the token bytes + the header CRC.
            fs.Seek(tokenLength + 4, SeekOrigin.Current);
            return true;
        }

        private static bool MagicMatches(byte[] candidate)
        {
            for (var i = 0; i < Magic.Length; i++)
            {
                if (candidate[i] != Magic[i])
                {
                    return false;
                }
            }
            return true;
        }

        #endregion

        #region io helpers

        // The durable-write/atomic-rename/delete primitives live once in DurableFileIo (shared with the
        // checkpoint writer) so the two atomic-write commit points cannot drift. The WAL header is a
        // small single write, so it passes FileOptions.None.
        private static void WriteAllBytesDurably(string path, byte[] bytes)
        {
            DurableFileIo.WriteAllBytesDurably(path, bytes, FileOptions.None);
        }

        private void TryDeleteFile(string file)
        {
            DurableFileIo.TryDeleteFile(file, _logger);
        }

        private static bool ReadExactly(Stream stream, byte[] buffer, int count)
        {
            var offset = 0;
            while (offset < count)
            {
                var read = stream.Read(buffer, offset, count - offset);
                if (read == 0)
                {
                    return false;
                }
                offset += read;
            }
            return true;
        }

        #endregion
    }
}
