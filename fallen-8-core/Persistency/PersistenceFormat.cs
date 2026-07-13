// MIT License
//
// PersistenceFormat.cs
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
using System.IO;

namespace NoSQL.GraphDB.Core.Persistency
{
    /// <summary>
    /// Describes one file that belongs to a checkpoint (a graph-element bunch, an index or a
    /// service sidecar) together with the integrity data recorded for it in the header manifest:
    /// its byte length and CRC-32 (finding C2/C4). On load the referenced file must exist and its
    /// length and CRC must match, or the save is rejected as incomplete / corrupt.
    /// </summary>
    internal readonly struct SidecarManifestEntry
    {
        internal readonly string FileName;
        internal readonly long Size;
        internal readonly uint Crc;

        internal SidecarManifestEntry(string fileName, long size, uint crc)
        {
            FileName = fileName;
            Size = size;
            Crc = crc;
        }
    }

    /// <summary>
    /// The on-disk envelope shared by every binary checkpoint file: an 8-byte magic number followed
    /// by a little-endian <see cref="FormatVersion"/> (findings C4, C2, C5). It makes the format
    /// self-describing and lets load <em>clean-reject</em>: a file that lacks the magic (a
    /// pre-existing/unversioned save, or a foreign file) or that carries an unknown version is
    /// refused loudly, never silently misparsed and never used to drive a huge allocation.
    /// </summary>
    internal static class PersistenceFormat
    {
        /// <summary>
        /// The magic number that prefixes every binary checkpoint file: ASCII <c>"F8SAVE\0\0"</c>.
        /// </summary>
        internal static readonly byte[] Magic =
        {
            (byte)'F', (byte)'8', (byte)'S', (byte)'A', (byte)'V', (byte)'E', 0x00, 0x00
        };

        /// <summary>
        /// The current on-disk format version. v2 is the Stage-A baseline: it adds the magic +
        /// version envelope, per-file CRC integrity, the completion manifest, atomic writes, the
        /// single subgraph-recipe manifest and symmetric OtherType framing, WITHOUT changing the
        /// payload byte encoding (still UTF-32 strings, same property layout). Later stages increment
        /// this behind the same gate when they change the payload encoding.
        /// </summary>
        internal const int FormatVersion = 2;

        /// <summary>Bytes of magic (8) + version (4) written at the start of every binary file.</summary>
        internal const int PreambleLength = 8 + 4;

        /// <summary>Bytes of the trailing CRC-32 that protects a whole main-header file.</summary>
        internal const int TrailerLength = 4;

        /// <summary>Writes the magic + version preamble to the current position of <paramref name="stream"/>.</summary>
        internal static void WritePreamble(Stream stream)
        {
            stream.Write(Magic, 0, Magic.Length);
            Span<byte> version = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(version, FormatVersion);
            stream.Write(version);
        }

        /// <summary>
        /// Reads and validates the magic + version preamble at the current position of
        /// <paramref name="stream"/>. Throws <see cref="InvalidDataException"/> (never a silent
        /// misparse) if the magic is absent or the version is unknown.
        /// </summary>
        internal static void ReadAndValidatePreamble(Stream stream, string fileDescription)
        {
            var magic = new byte[Magic.Length];
            if (!TryReadExactly(stream, magic))
            {
                throw new InvalidDataException(string.Format(
                    "\"{0}\" is too short to be a Fallen-8 save file (missing the format header). Pre-existing/unversioned save files are not loadable by this version.",
                    fileDescription));
            }

            if (!MagicMatches(magic))
            {
                throw new InvalidDataException(string.Format(
                    "\"{0}\" is not a Fallen-8 save file: the format magic is missing. Pre-existing/unversioned or foreign files are rejected rather than misparsed.",
                    fileDescription));
            }

            var versionBytes = new byte[4];
            if (!TryReadExactly(stream, versionBytes))
            {
                throw new InvalidDataException(string.Format(
                    "\"{0}\" is truncated in its format header.", fileDescription));
            }

            var version = BinaryPrimitives.ReadInt32LittleEndian(versionBytes);
            if (version != FormatVersion)
            {
                throw new InvalidDataException(string.Format(
                    "\"{0}\" has save format version {1}, which this build cannot read (expected version {2}).",
                    fileDescription, version, FormatVersion));
            }
        }

        /// <summary>
        /// Validates one sidecar against its manifest entry (finding C2/C4/C5): the file must exist,
        /// its byte length must equal the recorded size (a cheap O(1) truncation check that runs
        /// before any large allocation), it must carry a valid preamble, and its CRC-32 must match.
        /// Throws <see cref="InvalidDataException"/> on any mismatch.
        /// </summary>
        internal static void ValidateSidecar(string fullPath, SidecarManifestEntry entry)
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                throw new InvalidDataException(string.Format(
                    "The checkpoint is incomplete: the manifest references \"{0}\", which is missing.",
                    entry.FileName));
            }

            if (info.Length != entry.Size)
            {
                throw new InvalidDataException(string.Format(
                    "The checkpoint sidecar \"{0}\" is {1} byte(s) but the manifest recorded {2}; it is truncated or corrupt.",
                    entry.FileName, info.Length, entry.Size));
            }

            using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, Helper.Constants.BufferSize, FileOptions.SequentialScan))
            {
                // Reject a foreign / wrong-version sidecar up front (consumes the preamble bytes).
                ReadAndValidatePreamble(stream, entry.FileName);

                // Checksum the WHOLE file, so re-seek to the start.
                stream.Position = 0;
                var crc = Crc32.Compute(stream, out var length);

                if (length != entry.Size || crc != entry.Crc)
                {
                    throw new InvalidDataException(string.Format(
                        "The checkpoint sidecar \"{0}\" failed its integrity check (CRC/size mismatch); it is corrupt.",
                        entry.FileName));
                }
            }
        }

        /// <summary>
        /// Computes the byte length and CRC-32 of a completed file, for recording in the manifest.
        /// </summary>
        internal static uint ComputeFileCrc(string fullPath, out long length)
        {
            using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, Helper.Constants.BufferSize, FileOptions.SequentialScan))
            {
                return Crc32.Compute(stream, out length);
            }
        }

        private static bool MagicMatches(byte[] candidate)
        {
            if (candidate.Length != Magic.Length)
            {
                return false;
            }

            for (var i = 0; i < Magic.Length; i++)
            {
                if (candidate[i] != Magic[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryReadExactly(Stream stream, byte[] buffer)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = stream.Read(buffer, offset, buffer.Length - offset);
                if (read == 0)
                {
                    return false;
                }
                offset += read;
            }
            return true;
        }
    }
}
