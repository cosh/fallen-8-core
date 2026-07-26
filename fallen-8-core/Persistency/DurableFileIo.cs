// MIT License
//
// DurableFileIo.cs
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
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Core.Helper;

namespace NoSQL.GraphDB.Core.Persistency
{
    /// <summary>
    ///   The one home for the durable-file primitives shared by the checkpoint writer
    ///   (<see cref="PersistencyFactory"/>) and the write-ahead log (<see cref="WriteAheadLog"/>):
    ///   the temp-name + fsync-before-rename write and the best-effort cleanup delete. Both used to
    ///   carry near-identical private copies that differed only incidentally (the <see cref="FileOptions"/>
    ///   flag and a log-message wording); a single source keeps the two atomic-write commit points from
    ///   drifting apart. The write path is: write bytes to a unique temp name, fsync, then the caller
    ///   atomically renames it into place (findings C2, load-path-integrity).
    /// </summary>
    internal static class DurableFileIo
    {
        /// <summary>
        ///   The temporary name a file is written under before it is fsync'd and atomically renamed
        ///   into place. The GUID makes it unique per attempt, so a crashed prior write's leftover temp
        ///   can never be confused with this one's.
        /// </summary>
        internal static string TempNameFor(string finalPath)
        {
            return finalPath + Constants.TempSaveSuffix + "." + Guid.NewGuid().ToString("N");
        }

        /// <summary>
        ///   Writes bytes to a file and fsyncs them to disk before returning, so a subsequent atomic
        ///   rename cannot expose a file whose contents are still only in the OS write cache. The
        ///   caller supplies the <paramref name="options"/> (checkpoint sidecars use
        ///   <see cref="FileOptions.SequentialScan"/>; the WAL header uses <see cref="FileOptions.None"/>).
        /// </summary>
        internal static void WriteAllBytesDurably(string path, byte[] bytes, FileOptions options)
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                       Constants.BufferSize, options))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
        }

        /// <summary>
        ///   Best-effort delete of a temporary or stale file. A cleanup failure must never mask or
        ///   escalate the operation that triggered it, so it is logged and swallowed.
        /// </summary>
        internal static void TryDeleteFile(string file, ILogger logger)
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
                logger?.LogWarning(ex, "Could not delete the temporary or stale file \"{File}\".", file);
            }
        }
    }
}
