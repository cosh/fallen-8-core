// MIT License
//
// GeneratedCodeCache.cs
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

using Microsoft.Extensions.Caching.Memory;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core.Algorithms.Path;
using System;

namespace NoSQL.GraphDB.Core.App.Controllers.Cache
{
    public class GeneratedCodeCache
    {
        private readonly static int _slidingExpirationSeconds = 60;

        private readonly static MemoryCacheEntryOptions _cacheOptions = new MemoryCacheEntryOptions()
                .SetSlidingExpiration(TimeSpan.FromSeconds(_slidingExpirationSeconds))
                .SetSize(1);

        /// <summary>
        ///   The compiled path-traverser cache. It is PROCESS-WIDE (static) so it is shared across
        ///   every request and every <c>GraphController</c> instance (finding P1). Controllers are
        ///   instantiated per request, so the previous per-instance cache never saw a second hit and
        ///   every <c>/path</c> call recompiled its traverser with Roslyn.
        ///
        ///   The key is the compiled artifact's TRUE dependency — the <c>(Filter, Cost)</c> pair
        ///   (feature codegen-cache-keying). <c>CreateSource</c> reads only <see cref="PathSpecification.Filter"/>
        ///   and <see cref="PathSpecification.Cost"/>; the numeric bounds (<c>MaxDepth</c>/<c>MaxResults</c>/
        ///   <c>MaxPathWeight</c>) and <c>PathAlgorithmName</c> are applied DOWNSTREAM at traversal time
        ///   and are never baked into the traverser. Keying on the whole spec (as before) therefore
        ///   compiled a fresh, byte-identical assembly - into its own collectible AssemblyLoadContext -
        ///   for two requests that differ only in a numeric bound. Both filter/cost model classes
        ///   implement value equality, so the tuple hashes/compares structurally and handles null
        ///   components (a null filter/cost stays distinct from an all-defaults object, preserving
        ///   match-everything semantics).
        /// </summary>
        private readonly static IMemoryCache _traverser = new MemoryCache(
            new MemoryCacheOptions
            {
                SizeLimit = 1024,
                ExpirationScanFrequency = TimeSpan.FromMinutes(1)
            });

        /// <summary>The cache key: the compiled traverser depends only on the filter + cost fragments.</summary>
        private static (PathFilterSpecification, PathCostSpecification) KeyFor(PathSpecification definition)
        {
            return (definition.Filter, definition.Cost);
        }

        /// <summary>
        ///   Looks up the compiled traverser for a spec's <c>(Filter, Cost)</c>. Two requests differing
        ///   only in a numeric bound / algorithm name resolve to the SAME cached traverser.
        /// </summary>
        public bool TryGetTraverser(PathSpecification definition, out IPathTraverser traverser)
        {
            if (_traverser.TryGetValue(KeyFor(definition), out var cached) && cached is IPathTraverser found)
            {
                traverser = found;
                return true;
            }

            traverser = null;
            return false;
        }

        public void AddTraverser(PathSpecification definition, IPathTraverser generatedCode)
        {
            _traverser.Set(KeyFor(definition), generatedCode, _cacheOptions);
        }
    }
}
