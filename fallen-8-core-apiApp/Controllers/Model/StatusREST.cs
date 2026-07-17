// MIT License
//
// StatusREST.cs
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
using System.ComponentModel;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   Represents the current status of the Fallen-8 database
    /// </summary>
    /// <example>
    /// {
    ///   "usedMemory": 1073741824,
    ///   "vertexCount": 10000,
    ///   "edgeCount": 25000,
    ///   "indices": [{ "indexId": "nameIndex", "pluginType": "DictionaryIndex" }],
    ///   "availableIndexPlugins": ["DictionaryIndex", "SpatialIndex"],
    ///   "availablePathPlugins": ["Dijkstra", "AStar"],
    ///   "availableAnalyticsPlugins": ["PAGERANK", "WCC"],
    ///   "availableServicePlugins": ["ImportService", "ExportService"],
    ///   "apiKeyRequired": false,
    ///   "authenticated": false
    /// }
    /// </example>
    public sealed class StatusREST
    {
        /// <summary>
        ///   The amount of memory currently used by the database process in bytes
        /// </summary>
        /// <example>1073741824</example>
        [DefaultValue(1073741824L)] // Using long literal by adding 'L' suffix
        public Int64 UsedMemory
        {
            get; set;
        }

        /// <summary>
        ///   The total number of vertices in the database
        /// </summary>
        /// <example>10000</example>
        [DefaultValue(10000)]
        public Int32 VertexCount
        {
            get; set;
        }

        /// <summary>
        ///   The total number of edges in the database
        /// </summary>
        /// <example>25000</example>
        [DefaultValue(25000)]
        public Int32 EdgeCount
        {
            get; set;
        }

        /// <summary>
        ///   The indices currently registered on this instance (id + plugin type) — the live
        ///   inventory, available without running the budgeted statistics pass
        /// </summary>
        public List<IndexDescriptionREST> Indices
        {
            get; set;
        }

        /// <summary>
        ///   List of available index plugins that can be used with the database
        /// </summary>
        /// <example>["DictionaryIndex", "SpatialIndex", "FullTextIndex"]</example>
        public List<String> AvailableIndexPlugins
        {
            get; set;
        }

        /// <summary>
        ///   List of available path-finding algorithm plugins
        /// </summary>
        /// <example>["Dijkstra", "AStar", "BellmanFord"]</example>
        public List<String> AvailablePathPlugins
        {
            get; set;
        }

        /// <summary>
        ///   List of available graph-analytics algorithm plugins
        /// </summary>
        /// <example>["PAGERANK", "WCC", "LABELPROPAGATION", "DEGREE", "TRIANGLECOUNT"]</example>
        public List<String> AvailableAnalyticsPlugins
        {
            get; set;
        }

        /// <summary>
        ///   List of available service plugins that can be started with the database
        /// </summary>
        /// <example>["ImportService", "ExportService", "AnalyticsService"]</example>
        public List<String> AvailableServicePlugins
        {
            get; set;
        }

        /// <summary>
        ///   True when this server has an API key configured, i.e. every endpoint outside the
        ///   anonymous allowlist answers 401 without a valid credential. /status itself stays
        ///   anonymous, so it doubles as the connection probe: a caller is authorized iff
        ///   <c>!ApiKeyRequired || Authenticated</c>.
        /// </summary>
        /// <example>false</example>
        public Boolean ApiKeyRequired
        {
            get; set;
        }

        /// <summary>
        ///   True when the request that produced this status carried a valid credential
        ///   (see <see cref="ApiKeyRequired"/> for how clients combine the two).
        /// </summary>
        /// <example>false</example>
        public Boolean Authenticated
        {
            get; set;
        }
    }
}
