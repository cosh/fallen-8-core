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

            // Every write is by this build, so every write is the current schema (v1 entries inside
            // the document stay readable regardless).
            document.SchemaVersion = SaveGameRegistryDocument.CurrentSchemaVersion;
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
                    kpis.Indices.Add(new IndexDescriptionREST
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
        ///   Registers a just-written checkpoint of the DEFAULT namespace (trigger "api" or
        ///   "shutdown"). Kept as the un-namespaced convenience over
        ///   <see cref="Register(String, String, IFallen8, String, String)"/>.
        /// </summary>
        public SaveGameREST Register(IFallen8 fallen8, String location, String trigger)
        {
            return Register(Namespaces.Fallen8Namespaces.DefaultName, Namespaces.Fallen8Namespaces.DefaultId,
                fallen8, location, trigger);
        }

        /// <summary>
        ///   Registers a just-written checkpoint of ONE namespace (feature graph-namespaces),
        ///   prepends it (newest first) and persists. Returns the created entry, whose top-level
        ///   location/kpis mirror the single member (v1-shaped). <paramref name="namespaceId"/> is
        ///   the immutable id the boot chain matches on.
        /// </summary>
        public SaveGameREST Register(String namespaceName, String namespaceId, IFallen8 fallen8, String location, String trigger)
        {
            lock (Gate)
            {
                var document = LoadUnlocked();
                var entry = BuildEntry(namespaceName, namespaceId, fallen8, location, trigger, DateTime.UtcNow);
                document.SaveGames.Insert(0, entry);
                Persist(document);
                _logger.LogInformation("Registered save game {Id} ({Trigger}, namespace \"{Namespace}\") at \"{Location}\": {Vertices} vertices, {Edges} edges, {Files} files.",
                    entry.Id, trigger, namespaceName, entry.Location, entry.Kpis.VertexCount, entry.Kpis.EdgeCount, entry.FileCount);
                return entry;
            }
        }

        /// <summary>
        ///   Registers ONE entry spanning several just-written per-namespace checkpoints — the
        ///   Fallen-8-level restore point behind PUT /save/all and the shutdown auto-save (feature
        ///   graph-namespaces). Top-level file facts are sums; the top-level location/kpis mirror
        ///   the single member when there is exactly one.
        /// </summary>
        public SaveGameREST RegisterAll(IReadOnlyList<(String Name, String Id, IFallen8 Engine, String Location)> members, String trigger)
        {
            lock (Gate)
            {
                var document = LoadUnlocked();
                var savedAt = DateTime.UtcNow;

                var entry = new SaveGameREST
                {
                    Id = NewId(savedAt),
                    SavedAt = savedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    Trigger = trigger,
                    EngineVersion = typeof(IFallen8).Assembly.GetName().Version?.ToString(),
                    Namespaces = new List<SaveGameNamespaceREST>(members.Count),
                };
                foreach (var member in members)
                {
                    entry.Namespaces.Add(BuildMember(member.Name, member.Id, member.Engine, member.Location));
                }

                entry.FileCount = entry.Namespaces.Sum(m => m.FileCount);
                entry.TotalBytes = entry.Namespaces.Sum(m => m.TotalBytes);
                if (entry.Namespaces.Count == 1)
                {
                    entry.Location = entry.Namespaces[0].Location;
                    entry.Kpis = entry.Namespaces[0].Kpis;
                }
                else
                {
                    // No single checkpoint to point at; the honest aggregate is the counts.
                    entry.Location = null;
                    entry.Kpis = new SaveGameKpisREST
                    {
                        VertexCount = entry.Namespaces.Sum(m => m.Kpis.VertexCount),
                        EdgeCount = entry.Namespaces.Sum(m => m.Kpis.EdgeCount),
                        UsedMemoryBytes = System.Diagnostics.Process.GetCurrentProcess().VirtualMemorySize64,
                    };
                }

                document.SaveGames.Insert(0, entry);
                Persist(document);
                _logger.LogInformation("Registered save game {Id} ({Trigger}) spanning {Count} namespaces: {Names}.",
                    entry.Id, trigger, entry.Namespaces.Count, String.Join(", ", entry.Namespaces.Select(m => m.Name)));
                return entry;
            }
        }

        /// <summary>Default-namespace convenience over <see cref="RegisterImportIfUnknown(String, String, IFallen8, String)"/>.</summary>
        public SaveGameREST RegisterImportIfUnknown(IFallen8 fallen8, String location)
        {
            return RegisterImportIfUnknown(Namespaces.Fallen8Namespaces.DefaultName, Namespaces.Fallen8Namespaces.DefaultId,
                fallen8, location);
        }

        /// <summary>
        ///   Registers a loaded checkpoint as "imported" if no entry already pairs this namespace
        ///   id with this resolved path (FR-7, per namespace: restoring a checkpoint into a
        ///   RECREATED namespace must re-register under the new id, or the next boot finds no
        ///   entry containing it). savedAt is the checkpoint file's last-write time.
        /// </summary>
        public SaveGameREST RegisterImportIfUnknown(String namespaceName, String namespaceId, IFallen8 fallen8, String location)
        {
            lock (Gate)
            {
                var document = LoadUnlocked();
                var full = SafeFullPath(location);
                if (document.SaveGames.Any(s => EffectiveNamespaces(s)
                        .Any(m => SafeFullPath(m.Location) == full && EffectiveId(m) == namespaceId)))
                {
                    return null;
                }

                var savedAt = File.Exists(location) ? File.GetLastWriteTimeUtc(location) : DateTime.UtcNow;
                var entry = BuildEntry(namespaceName, namespaceId, fallen8, location, "imported", savedAt);
                document.SaveGames.Insert(0, entry);
                Persist(document);
                _logger.LogInformation("Imported previously-unregistered save game {Id} at \"{Location}\".", entry.Id, entry.Location);
                return entry;
            }
        }

        /// <summary>
        ///   The namespaces a save game effectively contains, normalizing pre-namespace entries: a
        ///   v1 entry (no manifest) is a default-only save whose checkpoint is the entry location
        ///   (feature graph-namespaces).
        /// </summary>
        public static List<SaveGameNamespaceREST> EffectiveNamespaces(SaveGameREST entry)
        {
            if (entry.Namespaces != null && entry.Namespaces.Count > 0)
            {
                return entry.Namespaces;
            }

            return new List<SaveGameNamespaceREST>
            {
                new SaveGameNamespaceREST
                {
                    Name = Namespaces.Fallen8Namespaces.DefaultName,
                    Id = Namespaces.Fallen8Namespaces.DefaultId,
                    Location = entry.Location,
                    FileCount = entry.FileCount,
                    TotalBytes = entry.TotalBytes,
                    Kpis = entry.Kpis,
                }
            };
        }

        /// <summary>A member's id for matching: entries written before ids carry none and mean the
        /// default namespace (whose id is stable).</summary>
        public static String EffectiveId(SaveGameNamespaceREST member)
        {
            return member.Id ?? Namespaces.Fallen8Namespaces.DefaultId;
        }

        /// <summary>
        ///   The newest entry containing the namespace with this IMMUTABLE id, or null — the
        ///   per-namespace boot authority (feature graph-namespaces). Keyed by id, never by the
        ///   mutable name: a renamed namespace keeps its boot chain, and a recreated namesake
        ///   (fresh id) never resurrects the dropped one's checkpoints.
        /// </summary>
        public SaveGameREST NewestContaining(String namespaceId)
        {
            lock (Gate)
            {
                return LoadUnlocked().SaveGames
                    .OrderByDescending(s => s.SavedAt, StringComparer.Ordinal)
                    .FirstOrDefault(s => EffectiveNamespaces(s).Any(m => EffectiveId(m) == namespaceId));
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
                    // Every member's checkpoint files (a v1 entry normalizes to one default member).
                    foreach (var member in EffectiveNamespaces(entry))
                    {
                        DeleteFiles(member.Location);
                    }
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

        private SaveGameREST BuildEntry(String namespaceName, String namespaceId, IFallen8 fallen8, String location, String trigger, DateTime savedAtUtc)
        {
            var member = BuildMember(namespaceName, namespaceId, fallen8, location);
            return new SaveGameREST
            {
                Id = NewId(savedAtUtc),
                SavedAt = savedAtUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                Trigger = trigger,
                // Single-namespace entry: the top level mirrors the one member (v1-shaped).
                Location = member.Location,
                FileCount = member.FileCount,
                TotalBytes = member.TotalBytes,
                EngineVersion = typeof(IFallen8).Assembly.GetName().Version?.ToString(),
                Kpis = member.Kpis,
                Namespaces = new List<SaveGameNamespaceREST> { member },
            };
        }

        private SaveGameNamespaceREST BuildMember(String namespaceName, String namespaceId, IFallen8 fallen8, String location)
        {
            var full = SafeFullPath(location);
            var (fileCount, totalBytes) = MeasureFiles(full);
            return new SaveGameNamespaceREST
            {
                Name = namespaceName,
                Id = namespaceId,
                Location = full,
                FileCount = fileCount,
                TotalBytes = totalBytes,
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
