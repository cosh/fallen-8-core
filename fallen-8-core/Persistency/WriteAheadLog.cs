// MIT License
//
// WriteAheadLog.cs
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
    ///   <para><b>Single-writer.</b> Every <see cref="Append" />, <see cref="ResetToSnapshot" /> and
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
    ///   a torn tail.</para>
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
        private bool _valid;

        /// <summary>
        ///   Opens the log at <paramref name="path" />. An existing, well-formed log is adopted (its
        ///   header parsed and its entries left in place for replay); a missing log is created fresh
        ///   with a zero baseline and no snapshot pairing; an unparsable/corrupt existing header is
        ///   logged and reset to a fresh header (its untrusted content is discarded rather than
        ///   misparsed - the clean-reject discipline of the snapshot format).
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

                _logger.LogWarning(
                    "The write-ahead log at \"{Path}\" has an unreadable header and will be reset; any entries it held are discarded.",
                    _path);
            }

            WriteHeader(0, UnanchoredToken, useTempAndRename: false);
            _baselineCurrentId = 0;
            _pairingToken = UnanchoredToken;
            _valid = true;
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
        ///   Appends one framed entry (<c>[Int32 length][payload][UInt32 CRC-32]</c>) and fsyncs it.
        ///   Runs only on the single writer thread, after the transaction has committed.
        /// </summary>
        internal void Append(byte[] payload)
        {
            var frame = new byte[4 + payload.Length + 4];
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0), payload.Length);
            Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
            var crc = Crc32.Compute(payload, 0, payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4 + payload.Length), crc);

            using (var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read,
                       Constants.BufferSize, FileOptions.None))
            {
                fs.Write(frame, 0, frame.Length);
                fs.Flush(true);
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
            var token = NormalizePathToken(snapshotPath);
            WriteHeader(baselineCurrentId, token, useTempAndRename: true);
            _baselineCurrentId = baselineCurrentId;
            _pairingToken = token;
            _valid = true;
        }

        /// <summary>
        ///   Enumerates every COMPLETE entry's payload, in append (commit) order, stopping cleanly at
        ///   the first incomplete or CRC-failing frame. Never throws on a torn/corrupt tail and never
        ///   sizes an allocation from an untrusted length: each frame's declared length is validated
        ///   against the bytes physically remaining in the file before the payload is read.
        /// </summary>
        internal IEnumerable<byte[]> ReadEntries()
        {
            if (!_valid || !File.Exists(_path))
            {
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
                    yield break;
                }

                var lengthBuffer = new byte[4];
                var crcBuffer = new byte[4];

                while (true)
                {
                    var position = fs.Position;

                    // Need at least a 4-byte length + a 4-byte trailing CRC for any complete entry.
                    if (fileLength - position < 8)
                    {
                        yield break;
                    }

                    if (!ReadExactly(fs, lengthBuffer, 4))
                    {
                        yield break;
                    }

                    var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);

                    // A torn/corrupt length: negative, or claiming more than the bytes that remain
                    // (payload + its 4-byte CRC). Stop at the last complete entry - never allocate
                    // from the untrusted prefix.
                    if (payloadLength < 0 || (long)payloadLength + 4 > fileLength - fs.Position)
                    {
                        yield break;
                    }

                    var payload = new byte[payloadLength];
                    if (!ReadExactly(fs, payload, payloadLength))
                    {
                        yield break;
                    }

                    if (!ReadExactly(fs, crcBuffer, 4))
                    {
                        yield break;
                    }

                    var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(crcBuffer);
                    var actualCrc = Crc32.Compute(payload, 0, payloadLength);
                    if (actualCrc != expectedCrc)
                    {
                        // A fully-sized but CRC-failing tail entry: treat as a torn tail and stop.
                        yield break;
                    }

                    yield return payload;
                }
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
                    var temp = _path + Constants.TempSaveSuffix + "." + Guid.NewGuid().ToString("N");
                    try
                    {
                        WriteAllBytesDurably(temp, headerBytes);
                        File.Move(temp, _path, true);
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
                    if (fileLength < PreambleLength + 8 + 4 + 4)
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
            if (fileLength < PreambleLength + 8 + 4 + 4)
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

        private static void WriteAllBytesDurably(string path, byte[] bytes)
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                       Constants.BufferSize, FileOptions.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
        }

        private void TryDeleteFile(string file)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete the temporary write-ahead-log file \"{File}\".", file);
            }
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
