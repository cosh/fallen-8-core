// MIT License
//
// PluginCache.cs
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
using NoSQL.GraphDB.Core.Algorithms.Analytics;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.Algorithms.SubGraph;
using System;
using System.Collections.Generic;
using System.Text;

namespace NoSQL.GraphDB.Core.Cache
{
    public class PluginCache
    {
        private readonly static int _slidingExpirationSeconds = 60;

        private readonly MemoryCacheEntryOptions _cacheOptions = new MemoryCacheEntryOptions()
                .SetSlidingExpiration(TimeSpan.FromSeconds(_slidingExpirationSeconds))
                .SetSize(1);


        // One home for the per-algorithm-family cache tuning. Each property still gets its own
        // MemoryCache instance (three distinct caches), matching the previous behavior exactly.
        private static IMemoryCache NewCache() => new MemoryCache(
            new MemoryCacheOptions
            {
                SizeLimit = 1024,
                ExpirationScanFrequency = TimeSpan.FromMinutes(1)
            });

        public IMemoryCache ShortestPath
        {
            get;
        } = NewCache();

        public IMemoryCache SubGraph
        {
            get;
        } = NewCache();

        public IMemoryCache Analytics
        {
            get;
        } = NewCache();

        public void AddShortestPath(IShortestPathAlgorithm algo)
        {
            ShortestPath.Set(algo.PluginName, algo, _cacheOptions);
        }

        public void AddSubGraph(ISubGraphAlgorithm algo)
        {
            SubGraph.Set(algo.PluginName, algo, _cacheOptions);
        }

        public void AddAnalytics(IGraphAnalyticsAlgorithm algo)
        {
            Analytics.Set(algo.PluginName, algo, _cacheOptions);
        }
    }
}
