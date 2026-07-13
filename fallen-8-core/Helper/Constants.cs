// MIT License
//
// Constants.cs
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

namespace NoSQL.GraphDB.Core.Helper
{
    /// <summary>
    ///   Constants.
    /// </summary>
    public static class Constants
    {
        /// <summary>
        ///   The size of the file buffer when reading or writing Fallen-8 from a file stream.
        /// </summary>
        /// <remarks>
        ///   64 KB (finding P1). The previous value was 100 MB, allocated per <c>FileStream</c> and
        ///   multiplied across every parallel checkpoint writer (header + one per bunch, index and
        ///   service), which put roughly 0.7-0.9 GB on the Large Object Heap during a single save.
        ///   64 KB sits below the ~85 KB LOH threshold, so each stream buffer is a normal Gen0/Gen1
        ///   allocation rather than LOH, while staying large enough for efficient sequential I/O.
        /// </remarks>
        public const int BufferSize = 65536;

        /// <summary>
        /// The version separator in save files
        /// </summary>
        public const char VersionSeparator = '#';

        /// <summary>
        /// Graph element files contain this string
        /// </summary>
        public const string GraphElementsSaveString = "_graphElements_";

        /// <summary>
        /// Index files contain this string
        /// </summary>
        public const string IndexSaveString = "_index_";

        /// <summary>
        /// Service files contain this string
        /// </summary>
        public const string ServiceSaveString = "_service_";

        /// <summary>
        /// Subgraph recipe files contain this string
        /// </summary>
        public const string SubGraphSaveString = "_subgraph_";

        /// <summary>
        /// The single subgraph-recipe manifest sidecar contains this string. It replaces the former
        /// per-recipe <c>_subgraph_N</c> files (finding C6): recipes are tied to THIS save through
        /// one manifest that is rewritten wholesale on every save, so a directory scan can no longer
        /// rehydrate stale, higher-numbered recipe files left over from an earlier, larger save.
        /// </summary>
        public const string SubGraphManifestString = "_subgraphs";

        /// <summary>
        /// Suffix appended to a checkpoint file while it is being written to a temporary name, before
        /// it is fsync'd and atomically renamed into place (finding C2). A crash mid-write leaves only
        /// these throwaway files, never a half-written file under its final name.
        /// </summary>
        public const string TempSaveSuffix = ".f8tmp";

        /// <summary>
        ///   Target number of graph elements per save partition (finding P6). The save fans the
        ///   master store into parallel "bunch" sidecar files; the partition count is
        ///   <c>min(cores, ceil(elementCount / SaveTargetPartitionSize))</c>, further capped by any
        ///   explicit caller request. This value is the minimum useful chunk: a graph smaller than it
        ///   is written as ONE bunch (removing the old degenerate one-file-per-element for tiny
        ///   graphs), while a larger graph is split only up to the core count - each bunch is a
        ///   CPU-bound serialization job, so more files than cores would add I/O and manifest
        ///   overhead without adding parallelism. The value is a tunable trade-off, not a hard limit
        ///   (correctness is independent of it: every element is written exactly once and the load
        ///   reconstructs by id regardless of how the ids were partitioned).
        /// </summary>
        public const int SaveTargetPartitionSize = 16384;
    }
}
