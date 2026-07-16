// MIT License
//
// PersistencyFactory.cs
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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NoSQL.GraphDB.Core.Helper;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Serializer;
using NoSQL.GraphDB.Core.Service;
using NoSQL.GraphDB.Core.StoredQueries;
using NoSQL.GraphDB.Core.SubGraph;

namespace NoSQL.GraphDB.Core.Persistency
{
    /// <summary>
    ///   Persistency factory.
    /// </summary>
    internal class PersistencyFactory
    {
        #region Data

        /// <summary>
        /// The logger
        /// </summary>
        private readonly ILogger<PersistencyFactory> _logger;

        #endregion

        public PersistencyFactory(ILogger<PersistencyFactory> logger)
        {
            _logger = logger;
        }

        #region internal methods

        /// <summary>
        ///   Load Fallen-8 from a save point
        /// </summary>
        /// <param name="fallen8">Fallen-8</param>
        /// <param name="pathToSavePoint">The path to the save point.</param>
        /// <param name="currentId">The maximum graph element id</param>
        /// <param name="startServices">Start the services</param>
        internal Boolean Load(Fallen8 fallen8, string pathToSavePoint, ref Int32 currentId, Boolean startServices)
        {
            //if there is no savepoint file... do nothing
            if (!File.Exists(pathToSavePoint))
            {
                _logger.LogError(String.Format("Fallen-8 could not be loaded because the path \"{0}\" does not exist.", pathToSavePoint));

                return false;
            }

            var pathName = Path.GetDirectoryName(pathToSavePoint);
            var fileName = Path.GetFileName(pathToSavePoint);

            _logger.LogInformation(String.Format("Now loading file \"{0}\" from path \"{1}\"", fileName, pathName));

            // 1) Magic + version gate FIRST, reading only the 12-byte preamble: a pre-existing/
            //    unversioned, foreign, or unknown-version file (of ANY size) is cleanly REJECTED here,
            //    before the file is ever read into memory - so a large garbage file cannot drive a
            //    huge allocation (findings C4/C5). A file that carries our magic + a known version is
            //    our own header and is small (id-space size + the sidecar completion manifest), so it
            //    is then read whole to validate the trailing self-CRC and parse the manifest, which
            //    also avoids the per-read FileStream.Position cost the SerializationReader warns about.
            byte[] headerBytes;
            using (var file = new FileStream(pathToSavePoint, FileMode.Open, FileAccess.Read, FileShare.Read,
                       Constants.BufferSize, FileOptions.SequentialScan))
            {
                PersistenceFormat.ReadAndValidatePreamble(file, fileName);

                if (file.Length < PersistenceFormat.PreambleLength + PersistenceFormat.TrailerLength ||
                    file.Length > Int32.MaxValue)
                {
                    throw new InvalidDataException(String.Format(
                        "The save file \"{0}\" has an implausible header length ({1} bytes); it is corrupt.", fileName, file.Length));
                }

                file.Position = 0;
                headerBytes = new byte[file.Length];
                file.ReadExactly(headerBytes, 0, headerBytes.Length);
            }

            // 2) Whole-file integrity: the trailing CRC-32 covers the preamble + header region.
            var contentLength = headerBytes.Length - PersistenceFormat.TrailerLength;
            var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes.AsSpan(contentLength));
            var actualCrc = Crc32.Compute(headerBytes, 0, contentLength);
            if (actualCrc != expectedCrc)
            {
                throw new InvalidDataException(String.Format(
                    "The save file \"{0}\" failed its integrity check (header CRC mismatch); it is corrupt.", fileName));
            }

            // 3) Parse the header region (the slice between the preamble and the trailing CRC).
            Guid currentGuId;
            int idSpaceSize;
            List<SidecarManifestEntry> bunchManifest;
            List<SidecarManifestEntry> indexManifest;
            List<SidecarManifestEntry> serviceManifest;
            using (var region = new MemoryStream(headerBytes, PersistenceFormat.PreambleLength,
                       contentLength - PersistenceFormat.PreambleLength, false))
            {
                var reader = new SerializationReader(region);
                currentGuId = reader.ReadGuid();
                idSpaceSize = reader.ReadInt32();
                bunchManifest = ReadManifestList(reader);
                indexManifest = ReadManifestList(reader);
                serviceManifest = ReadManifestList(reader);
            }

            if (idSpaceSize < 0)
            {
                throw new InvalidDataException(String.Format(
                    "The save file \"{0}\" declares a negative id-space size ({1}); it is corrupt.", fileName, idSpaceSize));
            }

            // 4) Validate the completion manifest before trusting the save (finding C2). Graph-element
            //    bunches are MANDATORY: a missing/truncated/corrupt bunch, or one referencing a sidecar
            //    that a crash mid-save never finished, is fatal. Index and service sidecars are
            //    best-effort (an unreadable one is skipped, matching the existing per-sidecar
            //    resilience for a throwing index/service) so one bad index cannot brick the load.
            foreach (var entry in bunchManifest)
            {
                PersistenceFormat.ValidateSidecar(Path.Combine(pathName, entry.FileName), entry);
            }
            var indexStreams = ValidateOptionalSidecars(pathName, indexManifest, "index");
            var serviceStreams = ValidateOptionalSidecars(pathName, serviceManifest, "service");

            // 5) Rehydrate. The ordering is preserved from before: build the dense, id-ordered element
            //    array, publish it into the segmented master store, THEN rehydrate indices and services
            //    (which resolve element ids through fallen8.TryGetGraphElement against that store).
            fallen8.SetId(currentGuId);
            currentId = idSpaceSize;

            #region graph elements

            // Build the dense, id-ordered element array and publish it into the segmented master
            // store. This is scoped to its own method so the dense source array (idSpaceSize element
            // references) is released as soon as it has been copied into the store (finding P5) -
            // i.e. BEFORE the indices and services rehydrate below - rather than being pinned alive
            // alongside the new store for the whole load.
            LoadAndPublishGraphElements(fallen8, bunchManifest, pathName, idSpaceSize);

            #endregion

            #region indexe

            var indexLogger = ((Fallen8)fallen8).CreateLogger<IndexFactory>();
            var newIndexFactory = new IndexFactory(fallen8, indexLogger);
            LoadIndices(fallen8, newIndexFactory, indexStreams);
            fallen8.IndexFactory = newIndexFactory;

            #endregion

            #region services

            var serviceLogger = ((Fallen8)fallen8).CreateLogger<ServiceFactory>();
            var newServiceFactory = new ServiceFactory(fallen8, serviceLogger);
            fallen8.ServiceFactory = newServiceFactory;
            LoadServices(fallen8, newServiceFactory, serviceStreams, startServices);

            #endregion

            return true;
        }

        /// <summary>
        /// The minimum on-disk size of one <see cref="SidecarManifestEntry"/>: a name length-prefix
        /// (>= 1 byte for the empty string), an <see cref="Int64"/> size (8), and a
        /// <see cref="UInt32"/> CRC (4). Used to bound a declared manifest count against the bytes
        /// actually left in the header before allocating (feature load-path-integrity L3). Must track
        /// the entry framing in <see cref="WriteManifestList"/> / <see cref="SidecarManifestEntry"/>.
        /// </summary>
        private const int MinManifestEntrySize = sizeof(Int64) + sizeof(UInt32) + 1;

        /// <summary>
        /// Reads one length-prefixed list of <see cref="SidecarManifestEntry"/> (file name, byte
        /// size and CRC-32) from the header. The count is bounded against the bytes remaining in the
        /// header region BEFORE the capacity allocation, so a crafted header (a CRC is integrity, not
        /// authenticity) cannot drive a huge preallocation (finding C5 / load-path-integrity L3); each
        /// name is then bounded by the reader's per-prefix guards.
        /// </summary>
        private static List<SidecarManifestEntry> ReadManifestList(SerializationReader reader)
        {
            var count = reader.ReadInt32();
            if (count < 0)
            {
                throw new InvalidDataException(String.Format("A checkpoint manifest declared a negative entry count ({0}); the header is corrupt.", count));
            }

            // Reject a count that cannot possibly be backed by the bytes left in the header, before
            // allocating the list's capacity (matches the EnsureAvailable discipline the rest of the
            // load already follows). BytesRemaining is -1 on a reader with no known end; fall back to
            // the seekable base stream so the header region (always length-known) is still bounded.
            long remaining = reader.BytesRemaining;
            if (remaining < 0 && reader.BaseStream.CanSeek)
            {
                remaining = reader.BaseStream.Length - reader.BaseStream.Position;
            }
            if (remaining >= 0 && count > remaining / MinManifestEntrySize)
            {
                throw new InvalidDataException(String.Format(
                    "A checkpoint manifest declared {0} entries, more than the {1} byte(s) remaining in the header could hold; it is corrupt.",
                    count, remaining));
            }

            var list = new List<SidecarManifestEntry>(count);
            for (var i = 0; i < count; i++)
            {
                var name = reader.ReadString();
                var size = reader.ReadInt64();
                var crc = reader.ReadUInt32();
                list.Add(new SidecarManifestEntry(name, size, crc));
            }

            return list;
        }

        /// <summary>
        /// Writes one length-prefixed list of <see cref="SidecarManifestEntry"/> to the header.
        /// </summary>
        private static void WriteManifestList(SerializationWriter writer, List<SidecarManifestEntry> entries)
        {
            writer.Write(entries.Count);
            foreach (var entry in entries)
            {
                writer.Write(entry.FileName);
                writer.Write(entry.Size);
                writer.Write(entry.Crc);
            }
        }

        /// <summary>
        /// Validates best-effort sidecars (indices, services). Each entry is checked for presence,
        /// size, preamble and CRC; a failing one is logged and SKIPPED (dropped from the returned
        /// load list) rather than aborting the whole load - the same resilience the load path already
        /// gives a throwing index/service. Returns the full paths of the sidecars that passed.
        /// </summary>
        private List<String> ValidateOptionalSidecars(string pathName, List<SidecarManifestEntry> manifest, string kind)
        {
            var valid = new List<String>(manifest.Count);
            foreach (var entry in manifest)
            {
                var fullPath = Path.Combine(pathName, entry.FileName);
                try
                {
                    PersistenceFormat.ValidateSidecar(fullPath, entry);
                    valid.Add(fullPath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, String.Format("The {0} sidecar \"{1}\" failed its integrity check and will be skipped.", kind, entry.FileName));
                }
            }
            return valid;
        }

        /// <summary>
        ///   Save the specified graphElements, indices and pathToSavePoint.
        /// </summary>
        /// <param name='fallen8'> Fallen-8. </param>
        /// <param name='graphElements'> Graph elements. </param>
        /// <param name='savePartitions'> The number of save partitions for the graph elements. </param>
        /// <returns>The path of the savegame</returns>
        internal string Save(IFallen8 fallen8, String path, int savePartitions)
        {
            // A new save never overwrites an existing one: it gets a unique, monotonically increasing,
            // UTC-based version suffix (finding C8 - replacing DateTime.Now, which is local,
            // DST-sensitive and collides within a tick). The loop additionally guards the
            // astronomically unlikely case of the versioned name already existing on disk.
            if (File.Exists(path))
            {
                string candidate;
                do
                {
                    candidate = path + Constants.VersionSeparator + NextVersionStamp();
                }
                while (File.Exists(candidate));
                path = candidate;
            }

            // Ensure the target directory exists before writing the header or the parallel bunch
            // sidecars into it. A save to a path whose directory does not exist (e.g. a fresh
            // "C:/Fallen8/database.f8s") would otherwise throw DirectoryNotFoundException from the
            // first sidecar FileStream and roll the save back; creating it up front makes a save to a
            // new location succeed. (A bare filename has no directory component - nothing to create.)
            var saveDirectory = Path.GetDirectoryName(path);
            if (!String.IsNullOrEmpty(saveDirectory))
            {
                Directory.CreateDirectory(saveDirectory);
            }

            var graphElements = fallen8.GetAllGraphElements();

            // C1: size the loaded id-space to cover the HIGHEST surviving id, not the live COUNT.
            // Removal without a Trim leaves id gaps, so an element can survive with Id >= liveCount;
            // sizing the loaded array by the count then made Load's `graphElements[id] = ...` throw.
            // max(Id)+1 (0 when empty) always covers every surviving id.
            var idSpaceSize = graphElements.Count == 0 ? 0 : graphElements.Max(ge => ge.Id) + 1;

            // Fan the save out across POOLED tasks (finding P6). The previous design used
            // LongRunning tasks, which dedicate a brand-new thread to each - wasteful for what are
            // short, CPU-bound serialization jobs; Task.Run queues onto the thread pool instead.
            // This thread (the single transaction writer, inside the save transaction) blocks on the
            // results below, so the whole save still completes - all bytes durably committed - before
            // the transaction returns. Single-writer and the blocking-but-correct save semantics are
            // therefore preserved (the file writing is NOT moved off the worker; see the P3 deferral
            // note in features/done/persistence-hardening/plan.md).

            #region graph elements

            Task<SidecarManifestEntry>[] graphElementSaver;

            if (graphElements.Count > 0)
            {
                // Right-size the partition count to the work AND the hardware (finding P6):
                // min(cores, ceil(count / targetChunk)), never above the caller's request. This
                // removes both degeneracies of the old fixed count - a tiny graph is no longer split
                // one-file-per-element, and a large graph is no longer pinned to a small fixed count.
                var partitionCount = ComputePartitionCount(graphElements.Count, savePartitions);
                var graphElementPartitions = CreatePartitions(graphElements.Count, partitionCount);
                graphElementSaver = new Task<SidecarManifestEntry>[graphElementPartitions.Count];

                for (var i = 0; i < graphElementPartitions.Count; i++)
                {
                    var partition = graphElementPartitions[i];
                    graphElementSaver[i] = Task.Run(() => SaveBunch(partition, graphElements, path));
                }
            }
            else
            {
                graphElementSaver = Array.Empty<Task<SidecarManifestEntry>>();
            }

            #endregion

            #region indices

            var indexSaver = new Task<SidecarManifestEntry?>[fallen8.IndexFactory.Indices.Count];

            var counter = 0;
            foreach (var aIndex in fallen8.IndexFactory.Indices)
            {
                var indexName = aIndex.Key;
                var index = aIndex.Value;

                indexSaver[counter] = Task.Run(() => SaveIndex(indexName, index, path));
                counter++;
            }

            #endregion

            #region services

            var serviceSaver = new Task<SidecarManifestEntry?>[fallen8.ServiceFactory.Services.Count];

            counter = 0;
            foreach (var aService in fallen8.ServiceFactory.Services)
            {
                var serviceName = aService.Key;
                var service = aService.Value;

                serviceSaver[counter] = Task.Run(() => SaveService(serviceName, service, path));
                counter++;
            }

            #endregion

            // Collect the sidecar results. Reading .Result rethrows a graph-element BUNCH failure here,
            // BEFORE any header is committed, so a failed bunch save never leaves a loadable header:
            // graph-element bunches stay MANDATORY. Index and service sidecars are best-effort -
            // SaveIndex/SaveService catch, log and return null on failure, and the null entries are
            // dropped from the manifest below - so one bad index or service can't block checkpointing.
            var bunchEntries = new List<SidecarManifestEntry>(graphElementSaver.Length);
            foreach (var saver in graphElementSaver)
            {
                bunchEntries.Add(saver.Result);
            }

            // Only reference the indices that persisted successfully: SaveIndex returns null for any
            // index that failed to serialize, so one bad index does not abort the whole checkpoint
            // (nor leave a dangling manifest entry pointing at a broken file).
            var indexEntries = new List<SidecarManifestEntry>();
            foreach (var saver in indexSaver)
            {
                var entry = saver.Result;
                if (entry.HasValue)
                {
                    indexEntries.Add(entry.Value);
                }
            }

            // Only reference the services that persisted successfully: SaveService returns null for any
            // service that failed to serialize, so one bad service does not abort the whole checkpoint
            // (nor leave a dangling manifest entry pointing at a broken file).
            var serviceEntries = new List<SidecarManifestEntry>(serviceSaver.Length);
            foreach (var saver in serviceSaver)
            {
                var entry = saver.Result;
                if (entry.HasValue)
                {
                    serviceEntries.Add(entry.Value);
                }
            }

            // Build the header + completion manifest in memory, protect the whole thing with a
            // trailing CRC-32, and publish it LAST via a temp file + fsync + atomic rename (findings
            // C2/C4). The renamed header is the single commit point: a crash before the rename leaves
            // only throwaway temp files (and possibly orphan sidecars under a fresh, never-referenced
            // version suffix), never a header that Load would accept; and the manifest lets Load
            // verify every mandatory sidecar is present and intact before it trusts the save.
            byte[] headerBytes;
            using (var mem = new MemoryStream())
            {
                PersistenceFormat.WritePreamble(mem);

                var writer = new SerializationWriter(mem, true);
                writer.Write(fallen8.Id);
                writer.Write(idSpaceSize);
                WriteManifestList(writer, bunchEntries);
                WriteManifestList(writer, indexEntries);
                WriteManifestList(writer, serviceEntries);
                writer.UpdateHeader();
                writer.Flush();

                var contentLength = (int)mem.Length;
                var crc = Crc32.Compute(mem.GetBuffer(), 0, contentLength);
                var trailer = new byte[PersistenceFormat.TrailerLength];
                BinaryPrimitives.WriteUInt32LittleEndian(trailer, crc);
                mem.Write(trailer, 0, trailer.Length);

                headerBytes = mem.ToArray();
            }

            var tempMain = TempNameFor(path);
            WriteAllBytesDurably(tempMain, headerBytes);

            // Subgraph recipes are persisted as ONE manifest next to the save point (finding C6),
            // written atomically and read as a whole (no directory scan). D6 (feature
            // crash-durability-hardening): write it BEFORE the commit-point rename and FAIL the save
            // if it cannot be written. Because the header has not been renamed into place yet, failing
            // here commits nothing (Load never reads an uncommitted header's manifest), and - now that
            // wal-subgraph-support logs CreateSubGraph/RemoveSubGraph - it leaves the WAL UNRESET, so
            // its subgraph entries survive for the next replay instead of being stranded by a
            // reset-after-a-swallowed-manifest-failure. A committed header therefore always has its
            // recipe manifest already durable.
            try
            {
                SaveSubGraphRecipes(fallen8, path);
                SaveStoredQueries(fallen8, path);
            }
            catch
            {
                TryDeleteFile(tempMain);
                throw;
            }

            File.Move(tempMain, path, true); // atomic commit point (the recipe + stored-query manifests are already durable)

            return path;
        }

        /// <summary>
        /// Produces a unique, monotonically increasing, UTC-based version stamp (finding C8). The
        /// previous stamp was <c>DateTime.Now.ToBinary()</c>, which is local (DST-sensitive) and
        /// collides for two saves within the same tick. The value is still a single
        /// <see cref="Int64"/> in <c>ToBinary()</c> form so it stays orderable and parseable by the
        /// admin "find latest savegame" logic; the interlocked bump guarantees strict monotonicity
        /// and uniqueness even for rapid back-to-back saves.
        /// </summary>
        private static string NextVersionStamp()
        {
            var stamp = DateTime.UtcNow.ToBinary();
            long previous, next;
            do
            {
                previous = Interlocked.Read(ref _lastVersionStamp);
                next = Math.Max(stamp, previous + 1);
            }
            while (Interlocked.CompareExchange(ref _lastVersionStamp, next, previous) != previous);

            return next.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Monotonic guard for <see cref="NextVersionStamp"/>.</summary>
        private static long _lastVersionStamp;

        /// <summary>
        /// The temporary name a checkpoint file is written under before it is fsync'd and atomically
        /// renamed into place (finding C2). The GUID makes it unique per attempt, so a crashed prior
        /// save's leftover temp can never be confused with this one's.
        /// </summary>
        private static string TempNameFor(string finalPath)
        {
            return finalPath + Constants.TempSaveSuffix + "." + Guid.NewGuid().ToString("N");
        }

        /// <summary>
        /// Writes bytes to a file and fsyncs them to disk before returning, so a subsequent atomic
        /// rename cannot expose a file whose contents are still only in the OS write cache.
        /// </summary>
        private static void WriteAllBytesDurably(string path, byte[] bytes)
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                       Constants.BufferSize, FileOptions.SequentialScan))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
        }

        /// <summary>
        /// Persists every persistable subgraph recipe into ONE versioned manifest next to the save
        /// point (finding C6), written atomically (temp + fsync + rename). This replaces the former
        /// per-recipe <c>_subgraph_N</c> files whose 0-based counter, combined with a directory-scan
        /// load, let a later save with fewer recipes leave stale, higher-numbered files behind for
        /// the loader to rehydrate. Rewriting the single manifest wholesale makes that impossible.
        /// </summary>
        private void SaveSubGraphRecipes(IFallen8 fallen8, string path)
        {
            if (fallen8.SubGraphFactory == null)
            {
                return;
            }

            var recipes = fallen8.SubGraphFactory.GetPersistableRecipes()?.Where(r => r != null).ToList()
                          ?? new List<SubGraphRecipe>();
            var manifestPath = path + Constants.SubGraphManifestString;

            if (recipes.Count == 0)
            {
                // Nothing to persist: make sure no stale manifest lingers at this path.
                TryDeleteFile(manifestPath);
                return;
            }

            var manifest = new SubGraphRecipeManifest
            {
                FormatVersion = PersistenceFormat.FormatVersion,
                Recipes = recipes
            };

            var temp = TempNameFor(manifestPath);
            try
            {
                var json = JsonSerializer.Serialize(manifest, CoreJsonContext.Default.SubGraphRecipeManifest);
                WriteAllBytesDurably(temp, Encoding.UTF8.GetBytes(json));
                File.Move(temp, manifestPath, true);
            }
            catch (Exception ex)
            {
                // D6 (feature crash-durability-hardening): do NOT swallow. The caller runs this BEFORE
                // the header commit-point rename, so throwing fails the whole Save with nothing
                // committed and the WAL left unreset (its CreateSubGraph entries survive). Previously
                // this was caught+logged and the Save "succeeded" with no recipes and a reset WAL,
                // losing subgraphs from both durability paths.
                TryDeleteFile(temp);
                _logger.LogError(ex, String.Format("Could not persist the subgraph recipe manifest \"{0}\": {1}", manifestPath, ex.Message));
                throw new IOException(String.Format("Could not persist the subgraph recipe manifest \"{0}\".", manifestPath), ex);
            }
        }

        /// <summary>
        /// Persists every stored query definition into ONE versioned manifest next to the save point
        /// (feature stored-query-library) - the exact discipline of <see cref="SaveSubGraphRecipes"/>:
        /// written atomically (temp + fsync + rename) BEFORE the header commit-point rename, and a
        /// write failure FAILS the whole save (nothing committed, the WAL left unreset so its
        /// RegisterStoredQuery entries survive for the next replay). Source only, never compiled bytes.
        /// </summary>
        private void SaveStoredQueries(IFallen8 fallen8, string path)
        {
            if (fallen8.StoredQueries == null)
            {
                return;
            }

            var definitions = new List<StoredQueryDefinition>();
            foreach (var entry in fallen8.StoredQueries.GetAll())
            {
                if (entry?.Definition != null)
                {
                    definitions.Add(entry.Definition);
                }
            }

            var manifestPath = path + Constants.StoredQueryManifestString;

            if (definitions.Count == 0)
            {
                // Nothing to persist: make sure no stale manifest lingers at this path.
                TryDeleteFile(manifestPath);
                return;
            }

            var manifest = new StoredQueryManifest
            {
                FormatVersion = PersistenceFormat.FormatVersion,
                Definitions = definitions
            };

            var temp = TempNameFor(manifestPath);
            try
            {
                var json = JsonSerializer.Serialize(manifest, CoreJsonContext.Default.StoredQueryManifest);
                WriteAllBytesDurably(temp, Encoding.UTF8.GetBytes(json));
                File.Move(temp, manifestPath, true);
            }
            catch (Exception ex)
            {
                TryDeleteFile(temp);
                _logger.LogError(ex, String.Format("Could not persist the stored query manifest \"{0}\": {1}", manifestPath, ex.Message));
                throw new IOException(String.Format("Could not persist the stored query manifest \"{0}\".", manifestPath), ex);
            }
        }

        /// <summary>
        /// Reads the single stored-query manifest that sits next to the given save point (feature
        /// stored-query-library). A missing manifest means "no stored queries"; an unknown manifest
        /// version or a read error is logged LOUDLY and treated as no stored queries - the manifest
        /// must never fail the whole load, but a corrupt one is an error, not a silent skip.
        /// </summary>
        internal List<StoredQueryDefinition> LoadStoredQueryDefinitions(string pathToSavePoint)
        {
            var result = new List<StoredQueryDefinition>();
            var manifestPath = pathToSavePoint + Constants.StoredQueryManifestString;

            if (!File.Exists(manifestPath))
            {
                return result;
            }

            try
            {
                var json = File.ReadAllText(manifestPath, Encoding.UTF8);
                var manifest = JsonSerializer.Deserialize(json, CoreJsonContext.Default.StoredQueryManifest);
                if (manifest == null)
                {
                    return result;
                }

                if (manifest.FormatVersion != PersistenceFormat.FormatVersion)
                {
                    _logger.LogError(String.Format("The stored query manifest \"{0}\" has unsupported version {1} (expected {2}); its stored queries will not be rehydrated.",
                        manifestPath, manifest.FormatVersion, PersistenceFormat.FormatVersion));
                    return result;
                }

                if (manifest.Definitions != null)
                {
                    result.AddRange(manifest.Definitions.Where(d => d != null));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, String.Format("Could not read the stored query manifest \"{0}\": {1}", manifestPath, ex.Message));
            }

            return result;
        }

        /// <summary>
        /// Reads the single subgraph recipe manifest that sits next to the given save point (finding
        /// C6). No directory scan is performed, so stale recipe files from an earlier save can never
        /// be rehydrated. A missing manifest means "no subgraphs"; an unknown manifest version or a
        /// read error is logged and treated as no subgraphs (recipes are auxiliary - as when no
        /// recipe compiler is registered - and must never fail the whole load).
        /// </summary>
        internal List<SubGraphRecipe> LoadSubGraphRecipes(string pathToSavePoint)
        {
            var result = new List<SubGraphRecipe>();
            var manifestPath = pathToSavePoint + Constants.SubGraphManifestString;

            if (!File.Exists(manifestPath))
            {
                return result;
            }

            try
            {
                var json = File.ReadAllText(manifestPath, Encoding.UTF8);
                var manifest = JsonSerializer.Deserialize(json, CoreJsonContext.Default.SubGraphRecipeManifest);
                if (manifest == null)
                {
                    return result;
                }

                if (manifest.FormatVersion != PersistenceFormat.FormatVersion)
                {
                    _logger.LogError(String.Format("The subgraph recipe manifest \"{0}\" has unsupported version {1} (expected {2}); its subgraphs will not be rehydrated.",
                        manifestPath, manifest.FormatVersion, PersistenceFormat.FormatVersion));
                    return result;
                }

                if (manifest.Recipes != null)
                {
                    result.AddRange(manifest.Recipes.Where(r => r != null));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, String.Format("Could not read the subgraph recipe manifest \"{0}\": {1}", manifestPath, ex.Message));
            }

            return result;
        }

        /// <summary>
        /// Best-effort delete of a temporary or stale file. A cleanup failure must never mask or
        /// escalate the operation that triggered it.
        /// </summary>
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
                _logger.LogWarning(ex, String.Format("Could not delete the file \"{0}\".", file));
            }
        }

        #endregion

        #region private helper

        /// <summary>
        ///   The serialized edge.
        /// </summary>
        private const byte SerializedEdge = 0;

        /// <summary>
        ///   The serialized vertex.
        /// </summary>
        private const byte SerializedVertex = 1;

        /// <summary>
        ///   The serialized null.
        /// </summary>
        private const byte SerializedNull = 2;

        /// <summary>
        ///   Saves the index.
        /// </summary>
        /// <returns> The filename of the persisted index. </returns>
        /// <param name='indexName'> Index name. </param>
        /// <param name='index'> Index. </param>
        /// <param name='path'> Path. </param>
        private SidecarManifestEntry? SaveIndex(string indexName, IIndex index, string path)
        {
            // C9: an index that declares itself non-persistable (CanPersist == false) is skipped
            // SILENTLY, before any file is created. This replaces the former implicit signal - catching
            // a NotSupportedException thrown from Save - used to tell "not yet persistable" apart from a
            // real failure. The capability is now explicit, so Error-level logging (and partial-file
            // cleanup, which WriteSidecar already handles) is reserved for a GENUINE, unexpected
            // serialization failure of an index that claims it CAN persist. With the R-Tree now fully
            // serializable, every built-in index returns CanPersist == true.
            if (!index.CanPersist)
            {
                _logger.LogInformation(String.Format("Index \"{0}\" ({1}) does not support persistence; it is skipped in this checkpoint.", indexName, index.PluginName));

                return null;
            }

            var indexFileName = path + Constants.IndexSaveString + indexName;

            try
            {
                return WriteSidecar(indexFileName, writer =>
                {
                    writer.Write(indexName);
                    writer.Write(index.PluginName);
                    index.Save(writer);
                });
            }
            catch (Exception ex)
            {
                // Defense in depth: a persistable index that nonetheless fails to serialize for a
                // genuine, unexpected reason must not abort the whole checkpoint. Log it loudly and
                // skip it (the caller drops null entries from the index manifest). WriteSidecar has
                // already removed any partial temp file; the final name is only created on success.
                _logger.LogError(ex, String.Format("Could not persist index \"{0}\"; it will be skipped in this checkpoint.", indexName));

                return null;
            }
        }

        /// <summary>
        /// Writes one checkpoint sidecar atomically (findings C2/C4): magic + version preamble, then
        /// the caller's content, all to a unique temp file that is fsync'd and only then renamed onto
        /// its final name; the final file therefore appears in one atomic step with fully durable
        /// content. Returns the sidecar's manifest entry (final name + byte size + CRC-32). On any
        /// failure the partial temp file is removed and the exception is rethrown - the final name is
        /// never created, so a failed/skipped sidecar leaves nothing behind.
        /// </summary>
        private SidecarManifestEntry WriteSidecar(string finalFileName, Action<SerializationWriter> writeContent)
        {
            // Single-pass save (feature checkpoint-io-efficiency 3.2). Build the sidecar image in a
            // MemoryStream - where SerializationWriter's seek-back header patch (UpdateHeader) is free -
            // CRC it in ONE in-memory pass, and write it to the temp file ONCE. The old path streamed
            // the bytes to disk and then re-opened and re-read the whole file just to CRC it
            // (ComputeFileCrc), moving every sidecar byte through the CPU twice inside the save's
            // writer-hold window. This mirrors the main header commit's shape. The on-disk bytes, the
            // CRC coverage (whole file, preamble included) and the manifest entry are byte-identical.
            byte[] image;
            using (var mem = new MemoryStream())
            {
                PersistenceFormat.WritePreamble(mem);

                var writer = new SerializationWriter(mem);
                writeContent(writer);
                writer.UpdateHeader();
                writer.Flush();

                image = mem.ToArray();
            }

            var crc = Crc32.Compute(image, 0, image.Length);

            var temp = TempNameFor(finalFileName);
            try
            {
                // temp + fsync + atomic rename - the existing durability sequence, unchanged.
                WriteAllBytesDurably(temp, image);
                File.Move(temp, finalFileName, true);
                return new SidecarManifestEntry(Path.GetFileName(finalFileName), image.Length, crc);
            }
            catch
            {
                TryDeleteFile(temp);
                throw;
            }
        }

        /// <summary>
        /// Saves the service
        /// </summary>
        /// <param name="serviceName">Service name.</param>
        /// <param name="service">Service.</param>
        /// <param name="path">Path.</param>
        /// <returns>The filename of the persisted service.</returns>
        private SidecarManifestEntry? SaveService(string serviceName, IService service, string path)
        {
            var serviceFileName = path + Constants.ServiceSaveString + serviceName;

            try
            {
                return WriteSidecar(serviceFileName, writer =>
                {
                    writer.Write(serviceName);
                    writer.Write(service.PluginName);
                    service.Save(writer);
                });
            }
            catch (Exception ex)
            {
                // Symmetric with SaveIndex and with the LOAD side (ValidateOptionalSidecars already
                // treats service sidecars as best-effort): a service whose Save throws must not abort
                // the whole checkpoint. Log it loudly and skip it (the caller drops null entries from
                // the manifest). WriteSidecar has already removed any partial temp file; the final
                // name is only created on success, so nothing partial is left behind.
                _logger.LogError(ex, String.Format("Could not persist service \"{0}\"; it will be skipped in this checkpoint.", serviceName));

                return null;
            }
        }

        /// <summary>
        ///   Loads a graph element bunch.
        /// </summary>
        /// <returns> The edges that point to vertices that are not within this bunch. </returns>
        /// <param name='graphElementBunchPath'> Graph element bunch path. </param>
        /// <param name='graphElementsOfFallen8'> Graph elements of Fallen-8. </param>
        /// <param name="edgeTodoOnVertex"> The edges that have to be added to this vertex </param>
        private List<EdgeSneakPeak> LoadAGraphElementBunch(
            string graphElementBunchPath,
            AGraphElementModel[] graphElementsOfFallen8,
            ConcurrentDictionary<Int32, ConcurrentQueue<EdgeOnVertexToDo>> edgeTodoOnVertex)
        {
            //if there is no savepoint file... do nothing
            if (!File.Exists(graphElementBunchPath))
            {
                return null;
            }

            var result = new List<EdgeSneakPeak>();

            // Right-sized, sequential parse open (feature checkpoint-io-efficiency): 64 KB buffer +
            // SequentialScan, matching the integrity opens, instead of the framework-default 4 KB.
            using (var file = new FileStream(graphElementBunchPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                       Constants.BufferSize, FileOptions.SequentialScan))
            {
                // Reject a foreign/old/wrong-version bunch and advance past the preamble bytes so the
                // reader starts on the payload (finding C4).
                PersistenceFormat.ReadAndValidatePreamble(file, Path.GetFileName(graphElementBunchPath));

                var reader = new SerializationReader(file);
                var minimumId = reader.ReadOptimizedInt32();   // P7: var-int partition boundaries
                var maximumId = reader.ReadOptimizedInt32();
                var countOfElements = maximumId - minimumId;

                for (var i = 0; i < countOfElements; i++)
                {
                    var kind = reader.ReadByte();
                    switch (kind)
                    {
                        case SerializedEdge:
                            //edge
                            LoadEdge(reader, graphElementsOfFallen8, ref result);
                            break;

                        case SerializedVertex:
                            //vertex
                            LoadVertex(reader, graphElementsOfFallen8, edgeTodoOnVertex);
                            break;

                        case SerializedNull:
                            //null --> do nothing
                            break;
                    }
                }
            }

            return result;
        }

        private void LoadIndices(Fallen8 fallen8, IndexFactory indexFactory, List<String> indexStreams)
        {
            //load the indices
            for (var i = 0; i < indexStreams.Count; i++)
            {
                try
                {
                    LoadAnIndex(indexStreams[i], fallen8, indexFactory);
                }
                catch (Exception ex)
                {
                    // Defense in depth: a single index that fails to deserialize must not abort
                    // loading the whole checkpoint (graph elements and every other index).
                    _logger.LogError(ex, String.Format("Could not load index from \"{0}\"; it will be skipped.", indexStreams[i]));
                }
            }
        }

        private void LoadServices(Fallen8 fallen8, ServiceFactory newServiceFactory, List<string> serviceStreams, Boolean startServices)
        {
            //load the services
            for (var i = 0; i < serviceStreams.Count; i++)
            {
                try
                {
                    LoadAService(serviceStreams[i], fallen8, newServiceFactory, startServices);
                }
                catch (Exception ex)
                {
                    // Defense in depth (finding C5), mirroring LoadIndices: one service that fails to
                    // deserialize must not abort loading the rest of the checkpoint.
                    _logger.LogError(ex, String.Format("Could not load service from \"{0}\"; it will be skipped.", serviceStreams[i]));
                }
            }
        }

        private void LoadAService(string serviceLocaion, Fallen8 fallen8, ServiceFactory serviceFactory, Boolean startService)
        {
            //if there is no savepoint file... do nothing
            if (!File.Exists(serviceLocaion))
            {
                return;
            }

            using (var file = new FileStream(serviceLocaion, FileMode.Open, FileAccess.Read, FileShare.Read,
                       Constants.BufferSize, FileOptions.SequentialScan))
            {
                PersistenceFormat.ReadAndValidatePreamble(file, Path.GetFileName(serviceLocaion));

                var reader = new SerializationReader(file);

                var indexName = reader.ReadString();
                var indexPluginName = reader.ReadString();

                serviceFactory.OpenService(indexName, indexPluginName, reader, fallen8, startService);
            }
        }

        private void LoadAnIndex(string indexLocaion, Fallen8 fallen8, IndexFactory indexFactory)
        {
            //if there is no savepoint file... do nothing
            if (!File.Exists(indexLocaion))
            {
                return;
            }

            using (var file = new FileStream(indexLocaion, FileMode.Open, FileAccess.Read, FileShare.Read,
                       Constants.BufferSize, FileOptions.SequentialScan))
            {
                PersistenceFormat.ReadAndValidatePreamble(file, Path.GetFileName(indexLocaion));

                var reader = new SerializationReader(file);

                var indexName = reader.ReadString();
                var indexPluginName = reader.ReadString();

                indexFactory.OpenIndex(indexName, indexPluginName, reader);
            }
        }

        /// <summary>
        ///   Saves the graph element bunch.
        /// </summary>
        /// <returns> The path to the graph element bunch </returns>
        /// <param name='range'> Range. </param>
        /// <param name='graphElements'> Graph elements. </param>
        /// <param name='pathToSavePoint'> Path to save point basis. </param>
        private SidecarManifestEntry SaveBunch(Tuple<Int32, Int32> range, IReadOnlyList<AGraphElementModel> graphElements,
                                        String pathToSavePoint)
        {
            var partitionFileName = pathToSavePoint + Constants.GraphElementsSaveString + range.Item1 + "_to_" + range.Item2;

            return WriteSidecar(partitionFileName, partitionWriter =>
            {
                partitionWriter.WriteVarInt32(range.Item1);   // P7: partition boundaries as var-int
                partitionWriter.WriteVarInt32(range.Item2);

                for (var i = range.Item1; i < range.Item2; i++)
                {
                    AGraphElementModel aGraphElement = graphElements[i];
                    //there can be nulls
                    if (aGraphElement == null || aGraphElement._removed)
                    {
                        partitionWriter.Write(SerializedNull); // 2 for null
                        continue;
                    }

                    //code if it is an vertex or an edge
                    if (aGraphElement is VertexModel)
                    {
                        WriteVertex((VertexModel)aGraphElement, partitionWriter);
                    }
                    else
                    {
                        WriteEdge((EdgeModel)aGraphElement, partitionWriter);
                    }
                }
            });
        }

        /// <summary>
        ///   Builds the dense, id-ordered graph-element array from the bunch sidecars and publishes
        ///   it into the master store. Kept as its own method so the dense source array is out of
        ///   scope - and therefore eligible for collection - the instant it has been copied into the
        ///   segmented store, instead of being held alive alongside the new store while the indices
        ///   and services rehydrate afterwards (finding P5). The publish happens BEFORE indices and
        ///   services load because their deserialization resolves element ids through
        ///   <see cref="Fallen8.TryGetGraphElement" /> against the published store; the load-publish
        ///   ordering (dense array -> PublishLoadedGraphElements -> index/service rehydration) is
        ///   therefore unchanged.
        /// </summary>
        private void LoadAndPublishGraphElements(Fallen8 fallen8, List<SidecarManifestEntry> bunchManifest,
                                                 string pathName, int idSpaceSize)
        {
            var graphElementArray = new AGraphElementModel[idSpaceSize];
            var graphElementStreams = bunchManifest.Select(e => Path.Combine(pathName, e.FileName)).ToList();
            LoadGraphElements(graphElementArray, graphElementStreams);
            fallen8.PublishLoadedGraphElements(graphElementArray);
        }

        /// <summary>
        ///   Loads the graph elements.
        /// </summary>
        /// <param name='graphElements'> Graph elements of Fallen-8. </param>
        /// <param name='graphElementStreams'> Graph element streams. </param>
        private void LoadGraphElements(AGraphElementModel[] graphElements, List<String> graphElementStreams)
        {
            try
            {
                LoadGraphElementsCore(graphElements, graphElementStreams);
            }
            catch (AggregateException aggregate)
            {
                // The parallel bunch load surfaces failures as an AggregateException (finding C5). Flatten
                // it to a single, clear "the savegame is corrupt" error rather than letting the opaque
                // aggregate propagate; the worker's transaction guard (C3/B6) then rolls the load back.
                var flat = aggregate.Flatten();
                var inner = flat.InnerException ?? flat;
                throw new InvalidDataException(
                    "The savegame is corrupt: a graph-element bunch could not be loaded. " + inner.Message, inner);
            }
        }

        private void LoadGraphElementsCore(AGraphElementModel[] graphElements, List<String> graphElementStreams)
        {
            // A ConcurrentQueue per edge id, not a shared List: the parallel bunch load (below) records
            // the two endpoints of a not-yet-materialised cross-bunch edge from two threads at once
            // under the same edge-id key. ConcurrentDictionary.AddOrUpdate runs its update delegate
            // OUTSIDE the bucket lock (and can re-run it on a CAS retry), so mutating a shared List
            // there raced - torn backing array / lost or duplicated fix-up (feature load-path-integrity
            // L1). GetOrAdd publishes exactly one queue per key and Enqueue is itself thread-safe, so
            // each fix-up is recorded exactly once with no torn state.
            var edgeTodo = new ConcurrentDictionary<Int32, ConcurrentQueue<EdgeOnVertexToDo>>();
            var result = new List<EdgeSneakPeak>[graphElementStreams.Count];

            //create the major part of the graph elements
            Parallel.For(0, graphElementStreams.Count, i =>
            {
                result[i] = LoadAGraphElementBunch(graphElementStreams[i], graphElements, edgeTodo);
            });

            //Create the edges
            Parallel.ForEach(result, aEdgeSneakPeakList =>
            {
                // A bunch whose file was absent yields a null list; never dereference it (finding C5).
                if (aEdgeSneakPeakList == null)
                {
                    return;
                }

                foreach (var aSneakPeak in aEdgeSneakPeakList)
                {
                    VertexModel sourceVertex = graphElements[aSneakPeak.SourceVertexId] as VertexModel;
                    VertexModel targetVertex = graphElements[aSneakPeak.TargetVertexId] as VertexModel;
                    if (sourceVertex != null && targetVertex != null)
                    {
                        graphElements[aSneakPeak.Id] =
                            new EdgeModel(
                                aSneakPeak.Id,
                                aSneakPeak.CreationDate,
                                aSneakPeak.ModificationDate,
                                targetVertex,
                                sourceVertex,
                                aSneakPeak.Label,
                                aSneakPeak.EdgePropertyId,
                                aSneakPeak.Properties);
                    }
                    else
                    {
                        throw new Exception(String.Format("Corrupt savegame... could not create the edge {0}", aSneakPeak.Id));
                    }
                }
            });

            // Update the vertices with their deferred edges. Bucket the fix-ups by
            // (vertex, direction, edge-property-id) FIRST, preserving encounter order, then apply one
            // batch append per vertex/direction (feature supernode-adjacency-build Step 1). The old loop
            // called AddIncomingEdge/AddOutEdge once per deferred edge, so a hub whose edges were mostly
            // deferred (the common case in a parallel bunch load) was rebuilt one array at a time -
            // O(d²). Bucketing turns it into O(d): one WithEdgesAppended per key, one publish per
            // vertex/direction. The per-vertex OWN groups were already reconstructed in one shot via the
            // internal ctor (FromListGroups), so this only batches the deferred cross-bunch edges.
            var outByVertex = new Dictionary<VertexModel, Dictionary<String, List<EdgeModel>>>();
            var inByVertex = new Dictionary<VertexModel, Dictionary<String, List<EdgeModel>>>();

            foreach (var aKV in edgeTodo)
            {
                EdgeModel edge = graphElements[aKV.Key] as EdgeModel;
                if (edge == null)
                {
                    _logger.LogError(String.Format("Corrupt savegame... could not get the edge {0}", aKV.Key));
                    continue;
                }

                foreach (var aTodo in aKV.Value)
                {
                    VertexModel interestingVertex = graphElements[aTodo.VertexId] as VertexModel;
                    if (interestingVertex == null)
                    {
                        _logger.LogError(String.Format("Corrupt savegame... could not get the vertex {0}", aTodo.VertexId));
                        continue;
                    }

                    var byVertex = aTodo.IsIncomingEdge ? inByVertex : outByVertex;
                    if (!byVertex.TryGetValue(interestingVertex, out var byKey))
                    {
                        byKey = new Dictionary<String, List<EdgeModel>>();
                        byVertex[interestingVertex] = byKey;
                    }

                    if (!byKey.TryGetValue(aTodo.EdgePropertyId, out var list))
                    {
                        list = new List<EdgeModel>();
                        byKey[aTodo.EdgePropertyId] = list;
                    }

                    list.Add(edge);
                }
            }

            foreach (var vertexGroups in outByVertex)
            {
                vertexGroups.Key.AddOutEdges(vertexGroups.Value);
            }
            foreach (var vertexGroups in inByVertex)
            {
                vertexGroups.Key.AddIncomingEdges(vertexGroups.Value);
            }
        }

        /// <summary>
        ///   Computes how many partitions to split a graph of <paramref name="elementCount"/>
        ///   elements into for the save (finding P6): <c>min(cores, ceil(count / targetChunk))</c>,
        ///   clamped to at least one and never above an explicit positive <paramref name="requestedMax"/>.
        ///   Sizing to the work removes the old fixed-count degeneracies - a tiny graph collapses to a
        ///   single bunch (no one-file-per-element), while a large graph is split only up to the core
        ///   count (each bunch is a CPU-bound serialization job, so more files than cores add I/O and
        ///   manifest overhead without adding parallelism). A positive <paramref name="requestedMax"/>
        ///   (the public <c>SaveTransaction.SavePartitions</c>) still caps the result, so an explicit
        ///   "use N partitions" is honoured and every single-partition caller keeps exactly one bunch
        ///   file. Returns 0 for an empty graph (no bunches are written at all).
        /// </summary>
        private static int ComputePartitionCount(int elementCount, int requestedMax)
        {
            if (elementCount <= 0)
            {
                return 0;
            }

            // ceil(elementCount / SaveTargetPartitionSize), computed overflow-safe (elementCount can
            // be up to Int32.MaxValue, so elementCount + chunk - 1 could overflow).
            var byWork = 1 + (elementCount - 1) / Constants.SaveTargetPartitionSize;
            var count = Math.Min(byWork, Environment.ProcessorCount);

            if (requestedMax > 0)
            {
                count = Math.Min(count, requestedMax);
            }

            return Math.Max(1, count);
        }

        /// <summary>
        ///   Creates the partitions.
        /// </summary>
        /// <returns> The partitions. </returns>
        /// <param name='totalCount'> Total count. </param>
        /// <param name='savePartitions'> Save partitions. </param>
        private List<Tuple<Int32, Int32>> CreatePartitions(int totalCount, int savePartitions)
        {
            var result = new List<Tuple<Int32, Int32>>();

            if (totalCount < savePartitions)
            {
                for (var i = 0; i < totalCount; i++)
                {
                    result.Add(new Tuple<Int32, Int32>(i, i + 1));
                }

                return result;
            }

            int size = totalCount / savePartitions;

            for (var i = 0; i < savePartitions; i++)
            {
                var lowerLimit = 0 + i * size;
                var upperLimit = 0 + (i * size) + size;
                result.Add(new Tuple<Int32, Int32>(Convert.ToInt32(lowerLimit), Convert.ToInt32(upperLimit)));
            }

            //trim the last partition
            var lastPartition = Convert.ToInt32(savePartitions - 1);
            var lastElement = Convert.ToInt32(0 + totalCount);
            result[lastPartition] = new Tuple<Int32, Int32>(result[lastPartition].Item1, lastElement);

            return result;
        }

        /// <summary>
        ///   Writes A graph element.
        /// </summary>
        /// <param name='graphElement'> Graph element. </param>
        /// <param name='writer'> Writer. </param>
        private void WriteAGraphElement(AGraphElementModel graphElement, SerializationWriter writer)
        {
            writer.WriteVarInt32(graphElement.Id);          // P7: var-int over a fixed 4-byte int
            writer.Write(graphElement.CreationDate);         // date stays fixed 4 bytes (often > var-int window)
            writer.Write(graphElement.ModificationDate);
            writer.WriteOptimized(graphElement.Label);       // tokenized

            // N1: emit the raw, key-sorted compact property store directly instead of materialising a
            // throwaway ImmutableDictionary per element via GetAllProperties(). This changes the stored
            // property byte order to ordinal key order; the loader rebuilds and re-sorts the store, so
            // it round-trips regardless of order. String VALUES are tokenized inside WriteObject (P2/M5).
            var store = graphElement.GetPropertyStoreForSerialization();
            var propertyCount = store == null ? 0 : store.Length;
            writer.WriteVarInt32(propertyCount);
            for (var i = 0; i < propertyCount; i++)
            {
                writer.WriteOptimized(store[i].Key);         // tokenized
                writer.WriteObject(store[i].Value);
            }
        }

        /// <summary>
        ///   Loads the vertex.
        /// </summary>
        /// <param name='reader'> Reader. </param>
        /// <param name='graphElements'> Graph elements. </param>
        /// <param name='edgeTodo'> Edge todo. </param>
        private void LoadVertex(SerializationReader reader, AGraphElementModel[] graphElements,
                                       ConcurrentDictionary<Int32, ConcurrentQueue<EdgeOnVertexToDo>> edgeTodo)
        {
            var id = reader.ReadOptimizedInt32();              // P7: var-int id
            var creationDate = reader.ReadUInt32();
            var modificationDate = reader.ReadUInt32();
            var label = reader.ReadOptimizedString();

            #region properties

            var propertyCount = reader.ReadOptimizedInt32Checked("vertex properties");   // P7/C5: guarded var-int count
            Dictionary<String, Object> properties = null;

            if (propertyCount > 0)
            {
                properties = new Dictionary<String, Object>(propertyCount);
                for (var i = 0; i < propertyCount; i++)
                {
                    var propertyIdentifier = reader.ReadOptimizedString();
                    var propertyValue = reader.ReadObject();

                    properties.Add(propertyIdentifier, propertyValue);
                }
            }

            #endregion

            #region edges

            #region outgoing edges

            Dictionary<String, List<EdgeModel>> outEdgeProperties = null;
            var outEdgeCount = reader.ReadOptimizedInt32Checked("vertex outgoing-edge groups");

            if (outEdgeCount > 0)
            {
                outEdgeProperties = new Dictionary<String, List<EdgeModel>>(outEdgeCount);
                for (var i = 0; i < outEdgeCount; i++)
                {
                    var outEdgePropertyId = reader.ReadOptimizedString();
                    var outEdgePropertyCount = reader.ReadOptimizedInt32Checked("vertex outgoing edges");
                    var outEdges = new List<EdgeModel>(outEdgePropertyCount);
                    for (var j = 0; j < outEdgePropertyCount; j++)
                    {
                        var edgeId = reader.ReadOptimizedInt32();


                        EdgeModel edge = graphElements[edgeId] as EdgeModel;
                        if (edge != null)
                        {
                            outEdges.Add(edge);
                        }
                        else
                        {
                            var aEdgeTodo = new EdgeOnVertexToDo
                            {
                                VertexId = id,
                                EdgePropertyId = outEdgePropertyId,
                                IsIncomingEdge = false
                            };

                            edgeTodo.GetOrAdd(edgeId, _ => new ConcurrentQueue<EdgeOnVertexToDo>()).Enqueue(aEdgeTodo);
                        }
                    }
                    outEdgeProperties.Add(outEdgePropertyId, outEdges);
                }
            }

            #endregion

            #region incoming edges

            Dictionary<String, List<EdgeModel>> incEdgeProperties = null;
            var incEdgeCount = reader.ReadOptimizedInt32Checked("vertex incoming-edge groups");

            if (incEdgeCount > 0)
            {
                incEdgeProperties = new Dictionary<String, List<EdgeModel>>(incEdgeCount);
                for (var i = 0; i < incEdgeCount; i++)
                {
                    var incEdgePropertyId = reader.ReadOptimizedString();
                    var incEdgePropertyCount = reader.ReadOptimizedInt32Checked("vertex incoming edges");
                    var incEdges = new List<EdgeModel>(incEdgePropertyCount);
                    for (var j = 0; j < incEdgePropertyCount; j++)
                    {
                        var edgeId = reader.ReadOptimizedInt32();


                        EdgeModel edge = graphElements[edgeId] as EdgeModel;
                        if (edge != null)
                        {
                            incEdges.Add(edge);
                        }
                        else
                        {
                            var aEdgeTodo = new EdgeOnVertexToDo
                            {
                                VertexId = id,
                                EdgePropertyId = incEdgePropertyId,
                                IsIncomingEdge = true
                            };

                            edgeTodo.GetOrAdd(edgeId, _ => new ConcurrentQueue<EdgeOnVertexToDo>()).Enqueue(aEdgeTodo);
                        }
                    }
                    incEdgeProperties.Add(incEdgePropertyId, incEdges);
                }
            }

            #endregion

            #endregion

            graphElements[id] = new VertexModel(id, creationDate, modificationDate, label, properties, outEdgeProperties,
                                                     incEdgeProperties);
        }

        /// <summary>
        ///   Writes the vertex.
        /// </summary>
        /// <param name='vertex'> Vertex. </param>
        /// <param name='writer'> Writer. </param>
        private void WriteVertex(VertexModel vertex, SerializationWriter writer)
        {
            writer.Write(SerializedVertex);
            WriteAGraphElement(vertex, writer);

            #region edges

            // P7: edge-group counts, per-group counts and edge ids as var-int (was fixed 4-byte int);
            // edge-property keys stay tokenized. WriteVarInt32 handles the full Int32 range, so a
            // legitimately huge count/id never faults (unlike the guarded WriteOptimized(int)).
            var outgoingEdges = vertex.GetRawOutEdges();
            if (outgoingEdges == null)
            {
                writer.WriteVarInt32(0);
            }
            else
            {
                writer.WriteVarInt32(outgoingEdges.Count);
                foreach (var aOutEdgeProperty in outgoingEdges)
                {
                    writer.WriteOptimized(aOutEdgeProperty.Key);
                    // Persist the LOGICAL count, not the backing array length (which may carry spare
                    // capacity, feature supernode-adjacency-build) - the on-disk bytes are unchanged.
                    writer.WriteVarInt32(aOutEdgeProperty.Value.Count);
                    foreach (var aOutEdge in aOutEdgeProperty.Value)
                    {
                        writer.WriteVarInt32(aOutEdge.Id);
                    }
                }
            }

            var incomingEdges = vertex.GetRawInEdges();
            if (incomingEdges == null)
            {
                writer.WriteVarInt32(0);
            }
            else
            {
                writer.WriteVarInt32(incomingEdges.Count);
                foreach (var aIncEdgeProperty in incomingEdges)
                {
                    writer.WriteOptimized(aIncEdgeProperty.Key);
                    // Persist the LOGICAL count, not the backing array length (feature
                    // supernode-adjacency-build) - on-disk bytes unchanged.
                    writer.WriteVarInt32(aIncEdgeProperty.Value.Count);
                    foreach (var aIncEdge in aIncEdgeProperty.Value)
                    {
                        writer.WriteVarInt32(aIncEdge.Id);
                    }
                }
            }

            #endregion
        }

        /// <summary>
        ///   Loads the edge.
        /// </summary>
        /// <param name='reader'> Reader. </param>
        /// <param name='graphElements'> Graph elements. </param>
        /// <param name='sneakPeaks'> Sneak peaks. </param>
        private void LoadEdge(SerializationReader reader, AGraphElementModel[] graphElements,
                                     ref List<EdgeSneakPeak> sneakPeaks)
        {
            var id = reader.ReadOptimizedInt32();              // P7: var-int id
            var creationDate = reader.ReadUInt32();
            var modificationDate = reader.ReadUInt32();
            var label = reader.ReadOptimizedString();

            #region properties

            Dictionary<String, Object> properties = null;
            var propertyCount = reader.ReadOptimizedInt32Checked("edge properties");   // P7/C5: guarded var-int count

            if (propertyCount > 0)
            {
                properties = new Dictionary<String, Object>(propertyCount);
                for (var i = 0; i < propertyCount; i++)
                {
                    var propertyIdentifier = reader.ReadOptimizedString();
                    var propertyValue = reader.ReadObject();

                    properties.Add(propertyIdentifier, propertyValue);
                }
            }

            #endregion

            var edgePropertyId = reader.ReadOptimizedString();   // P2/M5: tokenized (was untokenized UTF-32)

            var sourceVertexId = reader.ReadOptimizedInt32();    // P7: var-int ids
            var targetVertexId = reader.ReadOptimizedInt32();

            VertexModel sourceVertex = graphElements[sourceVertexId] as VertexModel;
            VertexModel targetVertex = graphElements[targetVertexId] as VertexModel;

            if (sourceVertex != null && targetVertex != null)
            {
                graphElements[id] = new EdgeModel(id, creationDate, modificationDate, targetVertex, sourceVertex, label, edgePropertyId, properties);
            }
            else
            {
                sneakPeaks.Add(new EdgeSneakPeak
                {
                    CreationDate = creationDate,
                    Id = id,
                    ModificationDate = modificationDate,
                    Properties = properties,
                    SourceVertexId = sourceVertexId,
                    TargetVertexId = targetVertexId,
                    Label = label,
                    EdgePropertyId = edgePropertyId
                });
            }
        }

        /// <summary>
        ///   Writes the edge.
        /// </summary>
        /// <param name='edge'> Edge. </param>
        /// <param name='writer'> Writer. </param>
        private void WriteEdge(EdgeModel edge, SerializationWriter writer)
        {
            writer.Write(SerializedEdge);
            WriteAGraphElement(edge, writer);
            // P2/M5: EdgePropertyId is tokenized (was an untokenized UTF-32 copy per edge), so N edges
            // that share a property id - or share it with the vertices' edge-property keys already
            // tokenized in this bunch - store it once and reference a 1-4 byte token thereafter.
            writer.WriteOptimized(edge.EdgePropertyId);
            writer.WriteVarInt32(edge.SourceVertex.Id);      // P7
            writer.WriteVarInt32(edge.TargetVertex.Id);
        }

        #endregion
    }
}
