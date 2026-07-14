// MIT License
//
// CheckpointDiscovery.cs
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
using System.Globalization;
using System.IO;
using System.Linq;
using NoSQL.GraphDB.Core.Helper;

namespace NoSQL.GraphDB.App.Helper
{
    /// <summary>
    ///   Discovers the newest Fallen-8 checkpoint header in a storage directory (feature
    ///   hosted-durability-lifecycle). This is the logic formerly in the dead
    ///   <c>AdminController.FindLatestFallen8</c>, relocated into a reusable, unit-testable helper that
    ///   follows the repo's <c>Try*</c> convention. A save writes a versioned header
    ///   (<c>&lt;base&gt;&lt;VersionSeparator&gt;&lt;stamp&gt;</c>) plus sidecars; only the main header
    ///   carries a parseable Int64 <see cref="DateTime.ToBinary"/> stamp, so the sidecar/temp files the
    ///   glob catches are excluded and the newest header is chosen by that stamp. Falls back to the
    ///   un-versioned base file if no versioned header is present.
    /// </summary>
    public static class CheckpointDiscovery
    {
        /// <summary>
        ///   Tries to find the newest checkpoint header for <paramref name="checkpointBaseName"/> in
        ///   <paramref name="storageDirectory"/>. Returns false (with <paramref name="path"/> null) when
        ///   the directory does not exist or holds no loadable header.
        /// </summary>
        public static Boolean TryFindLatestCheckpoint(String storageDirectory, String checkpointBaseName, out String path)
        {
            path = null;

            if (String.IsNullOrWhiteSpace(storageDirectory) || !Directory.Exists(storageDirectory))
            {
                return false;
            }

            var baseName = String.IsNullOrWhiteSpace(checkpointBaseName) ? "Temp.f8s" : checkpointBaseName;

            var versions = Directory.EnumerateFiles(storageDirectory,
                                               baseName + Constants.VersionSeparator + "*")
                                               .ToList();

            if (versions.Count > 0)
            {
                var fileToPathMapper = versions
                    .Select(p => p.Split(Path.DirectorySeparatorChar))
                    .Where(_ => !_.Last().Contains(Constants.GraphElementsSaveString))
                    .Where(_ => !_.Last().Contains(Constants.IndexSaveString))
                    .Where(_ => !_.Last().Contains(Constants.ServiceSaveString))
                    .Where(_ => !_.Last().Contains(Constants.SubGraphSaveString))
                    .Where(_ => !_.Last().Contains(Constants.SubGraphManifestString))
                    .Where(_ => !_.Last().Contains(Constants.TempSaveSuffix))
                    .ToDictionary(key => key.Last(), value => value.Aggregate((a, b) => a + Path.DirectorySeparatorChar + b));

                // Only a main header carries a parseable Int64 version stamp after the separator; any
                // other file the glob caught is skipped rather than crashing the scan. The stamp is a
                // UTC-based, monotonic Int64 in ToBinary() form (finding C8), so ordering by
                // DateTime.FromBinary picks the newest.
                var latestByStamp = fileToPathMapper
                    .Select(entry =>
                    {
                        var parts = entry.Key.Split(Constants.VersionSeparator);
                        long stamp = 0;
                        var parsed = parts.Length > 1 &&
                            long.TryParse(parts[parts.Length - 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out stamp);
                        return new { entry.Value, Stamp = parsed ? (long?)stamp : null };
                    })
                    .Where(_ => _.Stamp.HasValue)
                    .OrderByDescending(_ => DateTime.FromBinary(_.Stamp.Value))
                    .FirstOrDefault();

                if (latestByStamp != null)
                {
                    path = latestByStamp.Value;
                    return true;
                }
            }

            var lookupPath = Path.Combine(storageDirectory, baseName);
            if (File.Exists(lookupPath))
            {
                path = lookupPath;
                return true;
            }

            return false;
        }
    }
}
