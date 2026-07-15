// MIT License
//
// SaveGameRegistry.cs
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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Persistency;
using NoSQL.GraphDB.Core.Plugin;

namespace NoSQL.GraphDB.App.Services
{
    /// <summary>
    ///   Persistent registry of save games / checkpoints (feature save-games). Records a metadata
    ///   entry (KPIs + file facts) for every successful save, auto-registers a foreign checkpoint on
    ///   load, and - once present - is the sole authority for what the engine loads on startup.
    ///
    ///   <para>The registry is a single JSON document under <c>&lt;deployment&gt;/metadata/savegames.json</c>,
    ///   written atomically (temp file + rename). A corrupt document is a loud failure, never silently
    ///   overwritten. All mutations are serialized by an instance lock; graph writes are already
    ///   serialized by the single-writer transaction model.</para>
    /// </summary>
    public sealed class SaveGameRegistry
    {
        public const String RegistryFileName = "savegames.json";

        private readonly Fallen8MetadataOptions _options;
        private readonly ILogger<SaveGameRegistry> _logger;

        // Per-file lock, keyed by the absolute registry path, so that multiple registry instances
        // pointing at the SAME file within one process (e.g. overlapping hosts during a rolling
        // restart or in tests) serialize their read/modify/write instead of racing on the file.
        private static readonly ConcurrentDictionary<String, Object> _fileGates =
            new ConcurrentDictionary<String, Object>(StringComparer.OrdinalIgnoreCase);

        private Object Gate => _fileGates.GetOrAdd(NormalizedPath, _ => new Object());

        private String NormalizedPath
        {
            get
            {
                try
                {
                    return Path.GetFullPath(RegistryPath);
                }
                catch
                {
                    return RegistryPath;
                }
            }
        }

        private static readonly JsonSerializerOptions _json = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        public SaveGameRegistry(IOptions<Fallen8MetadataOptions> options, ILogger<SaveGameRegistry> logger)
        {
            _options = options.Value;
            _logger = logger;
        }

        public String RegistryPath => Path.Combine(_options.ResolveDirectory(), RegistryFileName);

        #region persistence

        /// <summary>
        ///   Reads the registry document. A missing file yields an empty document; an unreadable or
        ///   unparsable one throws (loud failure - never silently discarded).
        /// </summary>
        public SaveGameRegistryDocument Load()
        {
            lock (Gate)
            {
                return LoadUnlocked();
            }
        }

        [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
            Justification = "Trimming is disabled for this application; the registry DTOs are simple and also registered in AppJsonContext.")]
        private SaveGameRegistryDocument LoadUnlocked()
        {
            var path = RegistryPath;
            if (!File.Exists(path))
            {
                return new SaveGameRegistryDocument();
            }

            String text;
            try
            {
                text = ReadAllTextShared(path);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "The Fallen-8 save-game registry at \"" + path + "\" could not be read.", ex);
            }

            if (String.IsNullOrWhiteSpace(text))
            {
                return new SaveGameRegistryDocument();
            }

            try
            {
                var doc = JsonSerializer.Deserialize<SaveGameRegistryDocument>(text, _json);
                return doc ?? new SaveGameRegistryDocument();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    "The Fallen-8 save-game registry at \"" + path + "\" is corrupt (invalid JSON); " +
                    "startup/registry access is aborted so a bad registry is never silently overwritten. " +
                    "Fix or remove the file and retry.", ex);
            }
        }

        /// <summary>
        ///   Reads a file opened with <see cref="FileShare.ReadWrite" /> and retries briefly on an
        ///   IOException, so a read that races the atomic replace window (or a lingering handle from
        ///   an overlapping host) succeeds rather than throwing.
        /// </summary>
        private static String ReadAllTextShared(String path)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(fs);
                    return reader.ReadToEnd();
                }
                catch (IOException) when (attempt < 5)
                {
                    Thread.Sleep(25);
                }
            }
        }

        [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
            Justification = "Trimming is disabled for this application; the registry DTOs are simple and also registered in AppJsonContext.")]
        private void Persist(SaveGameRegistryDocument document)
        {
            var directory = _options.ResolveDirectory();
            Directory.CreateDirectory(directory);
            var path = RegistryPath;
            var temp = path + ".tmp";

            var text = JsonSerializer.Serialize(document, _json);
            File.WriteAllText(temp, text);

            // Atomic replace: a crash mid-write leaves the previous registry intact (temp is orphaned).
            if (File.Exists(path))
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }
        }

        #endregion

        #region capture

        /// <summary>Cheap KPIs from the live engine - no graph scan (feature save-games FR-5).</summary>
        public SaveGameKpisREST CaptureKpis(IFallen8 fallen8)
        {
            var kpis = new SaveGameKpisREST
            {
                VertexCount = fallen8.VertexCount,
                EdgeCount = fallen8.EdgeCount,
                UsedMemoryBytes = System.Diagnostics.Process.GetCurrentProcess().VirtualMemorySize64,
            };

            var indexFactory = fallen8.IndexFactory;
            if (indexFactory != null)
            {
                // Read a read-locked snapshot (id -> plugin type); iterating IndexFactory.Indices
                // directly races a concurrent create/delete on a request thread.
                foreach (var kv in indexFactory.GetIndexPluginTypesSnapshot())
                {
                    kpis.Indices.Add(new SaveGameIndexREST
                    {
                        IndexId = kv.Key,
                        PluginType = kv.Value,
                    });
                }
            }

            if (PluginFactory.TryGetAvailablePlugins<IIndex>(out var indexPlugins))
            {
                kpis.AvailableIndexPlugins = indexPlugins.ToList();
            }
            if (PluginFactory.TryGetAvailablePlugins<NoSQL.GraphDB.Core.Algorithms.Path.IShortestPathAlgorithm>(out var pathPlugins))
            {
                kpis.AvailablePathPlugins = pathPlugins.ToList();
            }
            if (PluginFactory.TryGetAvailablePlugins<NoSQL.GraphDB.Core.Service.IService>(out var servicePlugins))
            {
                kpis.AvailableServicePlugins = servicePlugins.ToList();
            }

            if (fallen8.SubGraphFactory != null)
            {
                kpis.SubGraphs = fallen8.SubGraphFactory.GetAllSubGraphNames().ToList();
            }

            return kpis;
        }

        /// <summary>
        ///   Counts the files that make up a save game and their total size (FR-6). The primary
        ///   checkpoint and every sidecar (partition/index/service/subgraph) share the primary file
        ///   name as a prefix (see PersistencyFactory), so a prefix match over the directory captures
        ///   exactly this save game's files.
        /// </summary>
        public (Int32 FileCount, Int64 TotalBytes) MeasureFiles(String location)
        {
            Int32 count = 0;
            Int64 bytes = 0;
            foreach (var file in EnumerateOwnFiles(location))
            {
                count++;
                try
                {
                    bytes += new FileInfo(file).Length;
                }
                catch
                {
                    // A file that vanished between enumeration and stat is simply not counted toward size.
                }
            }
            return (count, bytes);
        }

        /// <summary>
        ///   The files that make up EXACTLY this save game: the primary checkpoint file plus its
        ///   sidecars (named <c>&lt;primary&gt;_graphElements_…</c>, <c>_index_…</c>, <c>_service_…</c>,
        ///   <c>_subgraph…</c> - all suffixes begin with '_'). A later save to the same base path is
        ///   versioned with the '#' separator (<c>&lt;primary&gt;#&lt;stamp&gt;</c>), so a bare-named
        ///   primary is a TEXTUAL prefix of its versioned siblings; matching only the exact name or a
        ///   '_'-suffixed sidecar excludes those siblings, so measuring/deleting one save game never
        ///   touches another's files.
        /// </summary>
        private static IEnumerable<String> EnumerateOwnFiles(String location)
        {
            var directory = Path.GetDirectoryName(location);
            var primary = Path.GetFileName(location);
            if (String.IsNullOrEmpty(directory) || String.IsNullOrEmpty(primary) || !Directory.Exists(directory))
            {
                yield break;
            }

            foreach (var file in Directory.EnumerateFiles(directory, primary + "*"))
            {
                var name = Path.GetFileName(file);
                if (name == primary || name.StartsWith(primary + "_", StringComparison.Ordinal))
                {
                    yield return file;
                }
            }
        }

        #endregion

        #region registration + lifecycle

        /// <summary>
        ///   Registers a just-written checkpoint (trigger "api" or "shutdown"), prepends it (newest
        ///   first) and persists. Returns the created entry.
        /// </summary>
        public SaveGameREST Register(IFallen8 fallen8, String location, String trigger)
        {
            lock (Gate)
            {
                var document = LoadUnlocked();
                var entry = BuildEntry(fallen8, location, trigger, DateTime.UtcNow);
                document.SaveGames.Insert(0, entry);
                Persist(document);
                _logger.LogInformation("Registered save game {Id} ({Trigger}) at \"{Location}\": {Vertices} vertices, {Edges} edges, {Files} files.",
                    entry.Id, trigger, entry.Location, entry.Kpis.VertexCount, entry.Kpis.EdgeCount, entry.FileCount);
                return entry;
            }
        }

        /// <summary>
        ///   Registers a loaded checkpoint as "imported" if its resolved path is not already known
        ///   (FR-7). savedAt is the checkpoint file's last-write time. A no-op when already registered.
        /// </summary>
        public SaveGameREST RegisterImportIfUnknown(IFallen8 fallen8, String location)
        {
            lock (Gate)
            {
                var document = LoadUnlocked();
                var full = SafeFullPath(location);
                if (document.SaveGames.Any(s => SafeFullPath(s.Location) == full))
                {
                    return null;
                }

                var savedAt = File.Exists(location) ? File.GetLastWriteTimeUtc(location) : DateTime.UtcNow;
                var entry = BuildEntry(fallen8, location, "imported", savedAt);
                document.SaveGames.Insert(0, entry);
                Persist(document);
                _logger.LogInformation("Imported previously-unregistered save game {Id} at \"{Location}\".", entry.Id, entry.Location);
                return entry;
            }
        }

        /// <summary>All entries, newest first (by savedAt).</summary>
        public List<SaveGameREST> GetAll()
        {
            lock (Gate)
            {
                return LoadUnlocked().SaveGames
                    .OrderByDescending(s => s.SavedAt, StringComparer.Ordinal)
                    .ToList();
            }
        }

        public SaveGameREST GetById(String id)
        {
            lock (Gate)
            {
                return LoadUnlocked().SaveGames.FirstOrDefault(s => s.Id == id);
            }
        }

        /// <summary>The entry with the newest savedAt, or null when the registry is empty (FR-8).</summary>
        public SaveGameREST Newest()
        {
            lock (Gate)
            {
                return LoadUnlocked().SaveGames
                    .OrderByDescending(s => s.SavedAt, StringComparer.Ordinal)
                    .FirstOrDefault();
            }
        }

        /// <summary>
        ///   Removes an entry; when <paramref name="deleteFiles"/> is true also deletes its checkpoint
        ///   files. Returns false when the id is unknown.
        /// </summary>
        public Boolean Delete(String id, Boolean deleteFiles)
        {
            lock (Gate)
            {
                var document = LoadUnlocked();
                var entry = document.SaveGames.FirstOrDefault(s => s.Id == id);
                if (entry == null)
                {
                    return false;
                }

                document.SaveGames.Remove(entry);
                Persist(document);

                if (deleteFiles)
                {
                    DeleteFiles(entry.Location);
                }
                _logger.LogInformation("Deleted save game {Id} (deleteFiles={DeleteFiles}).", id, deleteFiles);
                return true;
            }
        }

        private void DeleteFiles(String location)
        {
            // Only this save game's own files (never a '#'-versioned sibling's) - see EnumerateOwnFiles.
            foreach (var file in EnumerateOwnFiles(location).ToList())
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not delete save-game file \"{File}\".", file);
                }
            }
        }

        private SaveGameREST BuildEntry(IFallen8 fallen8, String location, String trigger, DateTime savedAtUtc)
        {
            var full = SafeFullPath(location);
            var (fileCount, totalBytes) = MeasureFiles(full);
            return new SaveGameREST
            {
                Id = NewId(savedAtUtc),
                SavedAt = savedAtUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                Trigger = trigger,
                Location = full,
                FileCount = fileCount,
                TotalBytes = totalBytes,
                EngineVersion = typeof(IFallen8).Assembly.GetName().Version?.ToString(),
                Kpis = CaptureKpis(fallen8),
            };
        }

        private static String SafeFullPath(String location)
        {
            try
            {
                return Path.GetFullPath(location);
            }
            catch
            {
                return location;
            }
        }

        private static String NewId(DateTime savedAtUtc)
        {
            return "sg-" + savedAtUtc.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
        }

        #endregion
    }
}
