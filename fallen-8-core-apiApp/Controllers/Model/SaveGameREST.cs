// MIT License
//
// SaveGameREST.cs
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
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   Cheap KPIs captured at save/import time - values the engine already has, no graph scan.
    /// </summary>
    public sealed class SaveGameKpisREST
    {
        [JsonPropertyName("vertexCount")]
        public Int32 VertexCount
        {
            get; set;
        }

        [JsonPropertyName("edgeCount")]
        public Int32 EdgeCount
        {
            get; set;
        }

        [JsonPropertyName("usedMemoryBytes")]
        public Int64 UsedMemoryBytes
        {
            get; set;
        }

        [JsonPropertyName("indices")]
        public List<IndexDescriptionREST> Indices
        {
            get; set;
        } = new List<IndexDescriptionREST>();

        [JsonPropertyName("availableIndexPlugins")]
        public List<String> AvailableIndexPlugins
        {
            get; set;
        } = new List<String>();

        [JsonPropertyName("availablePathPlugins")]
        public List<String> AvailablePathPlugins
        {
            get; set;
        } = new List<String>();

        [JsonPropertyName("availableServicePlugins")]
        public List<String> AvailableServicePlugins
        {
            get; set;
        } = new List<String>();

        [JsonPropertyName("subGraphs")]
        public List<String> SubGraphs
        {
            get; set;
        } = new List<String>();
    }

    /// <summary>
    ///   A registered save game (checkpoint) with its metadata (feature save-games).
    /// </summary>
    public sealed class SaveGameREST
    {
        /// <summary>Stable id; sortable timestamp prefix + random suffix. The REST identifier.</summary>
        [JsonPropertyName("id")]
        public String Id
        {
            get; set;
        }

        /// <summary>ISO-8601 UTC instant the save game was created (or the file's write time for imports).</summary>
        [JsonPropertyName("savedAt")]
        public String SavedAt
        {
            get; set;
        }

        /// <summary>How the entry was created: "api", "shutdown" or "imported".</summary>
        [JsonPropertyName("trigger")]
        public String Trigger
        {
            get; set;
        }

        /// <summary>Absolute path of the primary checkpoint file.</summary>
        [JsonPropertyName("location")]
        public String Location
        {
            get; set;
        }

        /// <summary>Number of files belonging to this save game (checkpoint + partitions + sidecars).</summary>
        [JsonPropertyName("fileCount")]
        public Int32 FileCount
        {
            get; set;
        }

        /// <summary>Total size of those files in bytes at registration time.</summary>
        [JsonPropertyName("totalBytes")]
        public Int64 TotalBytes
        {
            get; set;
        }

        /// <summary>The engine/assembly version that produced the entry.</summary>
        [JsonPropertyName("engineVersion")]
        public String EngineVersion
        {
            get; set;
        }

        [JsonPropertyName("kpis")]
        public SaveGameKpisREST Kpis
        {
            get; set;
        } = new SaveGameKpisREST();

        /// <summary>
        ///   The namespaces this save game contains (feature graph-namespaces, registry schema v2).
        ///   Null on pre-namespace (v1) entries, which are read forever as "a default-only save"
        ///   whose checkpoint is <see cref="Location"/>. On single-namespace entries the top-level
        ///   location/kpis mirror the one member (v1-shaped); on multi-namespace entries the
        ///   top-level location is null and fileCount/totalBytes are sums across the members.
        /// </summary>
        [JsonPropertyName("namespaces")]
        public List<SaveGameNamespaceREST> Namespaces
        {
            get; set;
        }
    }

    /// <summary>
    ///   One namespace inside a save game: its own checkpoint location, file facts, and KPIs
    ///   (feature graph-namespaces, registry schema v2).
    /// </summary>
    public sealed class SaveGameNamespaceREST
    {
        /// <summary>The namespace name at save time (restores under this name).</summary>
        [JsonPropertyName("name")]
        public String Name
        {
            get; set;
        }

        /// <summary>Absolute path of this namespace's primary checkpoint file.</summary>
        [JsonPropertyName("location")]
        public String Location
        {
            get; set;
        }

        /// <summary>Number of files belonging to this namespace's checkpoint.</summary>
        [JsonPropertyName("fileCount")]
        public Int32 FileCount
        {
            get; set;
        }

        /// <summary>Total size of those files in bytes at registration time.</summary>
        [JsonPropertyName("totalBytes")]
        public Int64 TotalBytes
        {
            get; set;
        }

        [JsonPropertyName("kpis")]
        public SaveGameKpisREST Kpis
        {
            get; set;
        } = new SaveGameKpisREST();
    }

    /// <summary>
    ///   The persisted registry document: a schema version plus the save games, newest first.
    ///   Schema v2 (feature graph-namespaces) adds the per-entry namespaces manifest; v1 documents
    ///   and entries are read forever (default-only interpretation, see SaveGameREST.Namespaces).
    /// </summary>
    public sealed class SaveGameRegistryDocument
    {
        /// <summary>The schema version this build writes.</summary>
        public const Int32 CurrentSchemaVersion = 2;

        [JsonPropertyName("schemaVersion")]
        public Int32 SchemaVersion
        {
            get; set;
        } = CurrentSchemaVersion;

        [JsonPropertyName("saveGames")]
        public List<SaveGameREST> SaveGames
        {
            get; set;
        } = new List<SaveGameREST>();
    }
}
