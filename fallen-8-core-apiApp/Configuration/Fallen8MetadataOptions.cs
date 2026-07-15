// MIT License
//
// Fallen8MetadataOptions.cs
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

namespace NoSQL.GraphDB.App.Configuration
{
    /// <summary>
    ///   Configuration for the save-game metadata registry (feature save-games), bound from
    ///   the <c>Fallen8:Metadata</c> section. The registry is a persistent historical record of
    ///   checkpoints and, once present, is the sole authority for what loads on startup.
    /// </summary>
    public sealed class Fallen8MetadataOptions
    {
        /// <summary>The configuration section this binds from.</summary>
        public const String SectionName = "Fallen8:Metadata";

        /// <summary>
        ///   Directory holding <c>savegames.json</c>. Defaults to a <c>metadata</c> subdirectory of
        ///   <see cref="AppContext.BaseDirectory"/> (the deployment directory).
        /// </summary>
        public String Directory
        {
            get; set;
        }

        /// <summary>Resolves <see cref="Directory"/>, defaulting to <c>&lt;base&gt;/metadata</c>.</summary>
        public String ResolveDirectory()
        {
            return String.IsNullOrWhiteSpace(Directory)
                ? System.IO.Path.Combine(AppContext.BaseDirectory, "metadata")
                : Directory;
        }
    }
}
