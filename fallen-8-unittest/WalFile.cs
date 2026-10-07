// MIT License
//
// WalFile.cs
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
using System.IO;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Raw damage to a write-ahead log file, the way a crash or the disk leaves it, for the
    ///   recovery tests. Read <see cref="Length" /> after each commit to learn that commit's entry
    ///   boundaries without knowing anything about the frame format.
    /// </summary>
    internal static class WalFile
    {
        internal static long Length(String path)
        {
            return new FileInfo(path).Length;
        }

        /// <summary>Cuts the file to <paramref name="length" /> bytes, as a crash mid-append leaves it.</summary>
        internal static void CutTo(String path, long length)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Write);
            fs.SetLength(length);
        }

        /// <summary>Inverts one byte; the entry or header holding it no longer passes its CRC.</summary>
        internal static void FlipByteAt(String path, long offset)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
            fs.Seek(offset, SeekOrigin.Begin);
            var original = fs.ReadByte();
            fs.Seek(offset, SeekOrigin.Begin);
            fs.WriteByte((byte)(original ^ 0xFF));
        }

        internal static void Append(String path, byte[] bytes)
        {
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write);
            fs.Write(bytes, 0, bytes.Length);
        }
    }
}
