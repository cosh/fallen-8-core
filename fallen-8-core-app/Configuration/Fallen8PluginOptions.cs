// MIT License
//
// Fallen8PluginOptions.cs
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
using NoSQL.GraphDB.Core.Plugins;

namespace NoSQL.GraphDB.App.Configuration
{
    /// <summary>
    ///   Plugin registry configuration, bound from the <c>Fallen8:Plugins</c> section (feature
    ///   plugin-registration).
    /// </summary>
    public sealed class Fallen8PluginOptions
    {
        /// <summary>The configuration section this binds from.</summary>
        public const String SectionName = "Fallen8:Plugins";

        /// <summary>
        ///   The per-namespace registration ceiling. Every registered plugin pins its compiled type
        ///   (a collectible AssemblyLoadContext) in process memory for its registered lifetime, so the
        ///   count is bounded. Default 64.
        /// </summary>
        public Int32 MaxCount { get; set; } = PluginRegistry.DefaultMaxCount;
    }
}
