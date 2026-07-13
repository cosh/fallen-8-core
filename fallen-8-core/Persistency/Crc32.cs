// MIT License
//
// Crc32.cs
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

using System.IO;

namespace NoSQL.GraphDB.Core.Persistency
{
    /// <summary>
    /// Minimal, dependency-free CRC-32 (IEEE 802.3: reflected input/output, polynomial
    /// <c>0xEDB88320</c>, initial value <c>0xFFFFFFFF</c>, final XOR <c>0xFFFFFFFF</c>). It is used
    /// to detect accidental corruption of a checkpoint - truncation, bit-rot, or a sidecar that does
    /// not belong to this save - when a save is validated on load (finding C4/C2). It is an
    /// integrity check against accidental damage, not a cryptographic signature against tampering.
    /// </summary>
    internal static class Crc32
    {
        /// <summary>The value fed to <see cref="Update"/> for the first byte of a fresh computation.</summary>
        internal const uint Seed = 0xFFFFFFFFu;

        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            const uint polynomial = 0xEDB88320u;
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                var crc = i;
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1u) != 0 ? (crc >> 1) ^ polynomial : crc >> 1;
                }
                table[i] = crc;
            }
            return table;
        }

        /// <summary>Feeds more bytes into a running (not-yet-finalized) CRC state.</summary>
        internal static uint Update(uint runningCrc, byte[] buffer, int offset, int count)
        {
            var crc = runningCrc;
            for (var i = 0; i < count; i++)
            {
                crc = (crc >> 8) ^ Table[(crc ^ buffer[offset + i]) & 0xFFu];
            }
            return crc;
        }

        /// <summary>Turns a running CRC state into the final checksum.</summary>
        internal static uint Finalize(uint runningCrc)
        {
            return runningCrc ^ 0xFFFFFFFFu;
        }

        /// <summary>Computes the CRC-32 of a byte range in one call.</summary>
        internal static uint Compute(byte[] buffer, int offset, int count)
        {
            return Finalize(Update(Seed, buffer, offset, count));
        }

        /// <summary>
        /// Streams the given stream to its end in bounded-size buffers (never allocating an array
        /// sized from untrusted input) and returns its CRC-32 together with the number of bytes read.
        /// </summary>
        internal static uint Compute(Stream stream, out long length)
        {
            var crc = Seed;
            long total = 0;
            var buffer = new byte[Helper.Constants.BufferSize];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                crc = Update(crc, buffer, 0, read);
                total += read;
            }
            length = total;
            return Finalize(crc);
        }
    }
}
