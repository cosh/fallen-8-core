// MIT License
//
// Fallen8DurabilityOptions.cs
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

namespace NoSQL.GraphDB.App.Configuration
{
    /// <summary>
    ///   Durability configuration for the hosted API, bound from the <c>Fallen8:Durability</c>
    ///   section (feature hosted-durability-lifecycle). The defaults make DURABLE operation the
    ///   default: on boot the host loads the latest checkpoint and replays the paired write-ahead log,
    ///   and on a clean shutdown it saves so the next boot is up to date. Volatile is an explicit
    ///   opt-out, never the accidental default.
    /// </summary>
    public sealed class Fallen8DurabilityOptions
    {
        /// <summary>The configuration section this binds from.</summary>
        public const String SectionName = "Fallen8:Durability";

        /// <summary>
        ///   Directory that holds the checkpoint files and the write-ahead log. When null/blank it
        ///   defaults to <see cref="AppContext.BaseDirectory"/> - the same directory a default
        ///   <c>PUT /save</c> writes to, so an auto-load discovers a manual save too.
        /// </summary>
        public String StorageDirectory { get; set; }

        /// <summary>
        ///   Base name of the checkpoint file (a versioned suffix is appended by the save). Defaults to
        ///   <c>Temp.f8s</c>, matching the AdminController default save file so discovery finds it.
        /// </summary>
        public String CheckpointBaseName { get; set; } = "Temp.f8s";

        /// <summary>
        ///   Path of the write-ahead log. When null/blank it defaults to
        ///   <c>&lt;StorageDirectory&gt;/fallen8.wal</c>.
        /// </summary>
        public String WalPath { get; set; }

        /// <summary>
        ///   Explicit opt-out: when true the host runs purely in memory (no load on boot, no save on
        ///   shutdown, WAL disabled) and a restart loses data by choice. Defaults to false (durable).
        /// </summary>
        public Boolean Volatile { get; set; } = false;

        /// <summary>
        ///   When true (default), a clean shutdown performs a final save so the next boot is
        ///   up to date and the WAL is reset against a fresh snapshot. When false, shutdown relies on
        ///   the per-commit WAL for durability (committed work still survives; the next boot replays a
        ///   longer log). Ignored in volatile mode.
        /// </summary>
        public Boolean SaveOnShutdown { get; set; } = true;

        /// <summary>
        ///   Resolves <see cref="StorageDirectory"/> to a concrete path, defaulting to
        ///   <see cref="AppContext.BaseDirectory"/>.
        /// </summary>
        public String ResolveStorageDirectory()
        {
            return String.IsNullOrWhiteSpace(StorageDirectory) ? AppContext.BaseDirectory : StorageDirectory;
        }

        /// <summary>
        ///   Resolves <see cref="WalPath"/>, defaulting to <c>&lt;StorageDirectory&gt;/fallen8.wal</c>.
        /// </summary>
        public String ResolveWalPath()
        {
            return String.IsNullOrWhiteSpace(WalPath)
                ? Path.Combine(ResolveStorageDirectory(), "fallen8.wal")
                : WalPath;
        }

        /// <summary>
        ///   Resolves the checkpoint save path (base name inside the storage directory) that a
        ///   shutdown save writes to.
        /// </summary>
        public String ResolveCheckpointPath()
        {
            var baseName = String.IsNullOrWhiteSpace(CheckpointBaseName) ? "Temp.f8s" : CheckpointBaseName;
            return Path.Combine(ResolveStorageDirectory(), baseName);
        }
    }
}
