// MIT License
//
// SubGraphRecipeManifest.cs
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

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.Core.SubGraph
{
    /// <summary>
    /// The single, versioned manifest that persists every subgraph recipe of a save (finding C6).
    /// It replaces the former per-recipe <c>_subgraph_N</c> sidecar files that a directory scan
    /// rehydrated on load: because this one file is written wholesale (atomically) on each save and
    /// read as a whole on load, a later save with fewer recipes can no longer leave stale,
    /// higher-numbered recipe files behind for the loader to pick up. The
    /// <see cref="FormatVersion"/> lets an unknown/foreign manifest be rejected rather than misread.
    /// </summary>
    public sealed class SubGraphRecipeManifest
    {
        /// <summary>The on-disk format version of this manifest.</summary>
        [JsonPropertyName("formatVersion")]
        public int FormatVersion
        {
            get; set;
        }

        /// <summary>The recipes to rebuild on load, in no particular order.</summary>
        [JsonPropertyName("recipes")]
        public List<SubGraphRecipe> Recipes
        {
            get; set;
        }
    }
}
