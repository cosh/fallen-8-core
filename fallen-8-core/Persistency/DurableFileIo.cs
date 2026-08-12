// MIT License
//
// DurableFileIo.cs
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
using System.Text;
using System.Threading;
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
    ///
    ///   <para>PUBLIC because it is the one home for the WHOLE SOLUTION, not just the engine
    ///   (platform-integrity-audit W1). The host's two pointer files - the save-game registry and the
    ///   namespace catalog - are what make every fsync'd byte in here reachable, and they were written
    ///   with a plain write-then-rename: rename atomicity is not content durability, so a power loss
    ///   could publish a zero-length pointer to a complete checkpoint. They now go through
    ///   <see cref="ReplaceAllTextDurably" /> rather than carrying a third private copy of this
    ///   discipline. The engine declares no <c>InternalsVisibleTo</c> by decision, so public is the
    ///   available way to share it.</para>
    /// </summary>
    public static class DurableFileIo
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

        /// <summary>
        ///   Replaces a small text file's contents durably and atomically: write UTF-8 bytes to a
        ///   unique temp name, <b>fsync</b>, then atomically rename over the target. This is the whole
        ///   discipline in one call for callers whose payload is a single small document, so they cannot
        ///   accidentally get the atomic half without the durable half - which is exactly what happened
        ///   to the two host pointer files before platform-integrity-audit W1.
        ///
        ///   <para>Why both halves matter: <see cref="File.Replace(String,String,String)" /> is atomic
        ///   with respect to READERS (a reader sees either the old or the new file, never a mix), but it
        ///   says nothing about whether the new file's BYTES have reached the platter. Without the fsync,
        ///   a power loss can publish a rename whose content is still only in the OS write cache, i.e. a
        ///   zero-length or partial file. Callers that must survive a power cut need this, not
        ///   <c>File.WriteAllText</c> plus a rename.</para>
        ///
        ///   <para>The temp file is removed on a failed attempt (best effort) so a throwing write does
        ///   not litter the directory. NOT the same thing as crash-durability-hardening D5 (write-through
        ///   rename / directory fsync), which is a separate, still-deferred concern about the RENAME's own
        ///   durability; this is the ordinary content fsync the engine already performs everywhere.</para>
        /// </summary>
        /// <param name="path">The final path to replace.</param>
        /// <param name="text">The full contents to write.</param>
        /// <param name="logger">Optional logger for the best-effort temp cleanup.</param>
        public static void ReplaceAllTextDurably(String path, String text, ILogger logger = null)
        {
            var temp = TempNameFor(path);
            try
            {
                WriteAllBytesDurably(temp, new UTF8Encoding(false).GetBytes(text ?? String.Empty), FileOptions.None);
                PublishWithRetry(temp, path, logger);
            }
            catch
            {
                TryDeleteFile(temp, logger);
                throw;
            }
        }

        /// <summary>
        ///   Publishes a finished temp file over its target atomically, retrying a few times with a short
        ///   backoff before giving up.
        ///
        ///   <para>THE reason for the retry, which is not durability but Windows: another process holding the
        ///   destination for a moment - a virus scanner or the search indexer opening a file it just saw
        ///   appear - makes the rename fail with <see cref="UnauthorizedAccessException" /> or a sharing
        ///   <see cref="IOException" />. That surfaced as a save-game transaction rolling back at random (it
        ///   flaked a full-suite run), and the operation is safe to repeat: the temp file is complete and
        ///   fsynced, the target is either the old file or the new one, and a caller who never sees the
        ///   exception is not deceived about anything. A few tens of milliseconds of backoff is worth not
        ///   failing a checkpoint the engine has otherwise finished writing.</para>
        ///
        ///   <para>Anything other than those two exception types is a real error and is not retried: a
        ///   missing temp file or a bad path will fail identically the second time.</para>
        /// </summary>
        internal static void PublishWithRetry(String temp, String path, ILogger logger = null)
        {
            const Int32 attempts = 5;
            var delayMilliseconds = 5;

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Replace(temp, path, null);
                    }
                    else
                    {
                        File.Move(temp, path);
                    }

                    return;
                }
                catch (Exception ex) when (attempt < attempts &&
                                           (ex is UnauthorizedAccessException || ex is IOException))
                {
                    logger?.LogDebug(ex,
                        "Publishing \"{Path}\" was refused on attempt {Attempt} of {Attempts}; retrying in {Delay} ms.",
                        path, attempt, attempts, delayMilliseconds);
                    Thread.Sleep(delayMilliseconds);
                    delayMilliseconds *= 2;
                }
            }
        }
    }
}
