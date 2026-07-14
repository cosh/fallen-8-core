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

using System;
using System.IO;

namespace NoSQL.GraphDB.Core.Persistency
{
    /// <summary>
    /// CRC-32 (IEEE 802.3 / ISO-HDLC: reflected input/output, polynomial <c>0xEDB88320</c>, initial
    /// value <c>0xFFFFFFFF</c>, final XOR <c>0xFFFFFFFF</c>). It detects accidental corruption of a
    /// checkpoint - truncation, bit-rot, or a sidecar that does not belong to this save - when a save
    /// is validated on load (finding C4/C2). An integrity check against accidental damage, not a
    /// cryptographic signature against tampering.
    ///
    /// <para>
    /// Backed by the hardware-accelerated <see cref="System.IO.Hashing.Crc32"/> (feature
    /// checkpoint-io-efficiency), which computes CRC-32/ISO-HDLC with EXACTLY these parameters, so the
    /// emitted <c>uint</c> is byte-for-byte identical to the former hand-rolled table loop - every
    /// existing checkpoint keeps validating and there is no <c>formatVersion</c> bump. This is a pure
    /// throughput swap: the accelerated implementation runs roughly an order of magnitude faster than
    /// the scalar table loop, on the critical path of both save (the per-sidecar CRC) and load (the
    /// validate pass).
    /// </para>
    /// </summary>
    internal static class Crc32
    {
        /// <summary>Computes the CRC-32 of a byte range in one call.</summary>
        internal static uint Compute(byte[] buffer, int offset, int count)
        {
            return System.IO.Hashing.Crc32.HashToUInt32(new ReadOnlySpan<byte>(buffer, offset, count));
        }

        /// <summary>
        /// Streams the given stream to its end in bounded-size buffers (never allocating an array
        /// sized from untrusted input) and returns its CRC-32 together with the number of bytes read.
        /// </summary>
        internal static uint Compute(Stream stream, out long length)
        {
            var hasher = new System.IO.Hashing.Crc32();
            long total = 0;
            var buffer = new byte[Helper.Constants.BufferSize];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hasher.Append(new ReadOnlySpan<byte>(buffer, 0, read));
                total += read;
            }
            length = total;
            return hasher.GetCurrentHashAsUInt32();
        }
    }
}
