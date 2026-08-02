// MIT License
//
// NlpStatsREST.cs
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
using System.Threading;
using System.Threading.Tasks;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Ingestion;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   The semantic-layer NLP enrichment state on the discovery surfaces (feature
    ///   semantic-layer, /status and /statistics): the flag plus a cached sidecar probe. The
    ///   Studio's entity controls gate on this; enrichment is additive, so "off" just means no
    ///   entity network is built. Null only when the host wired no NLP options.
    /// </summary>
    public sealed class NlpStatsREST
    {
        public Boolean Enabled
        {
            get; set;
        }

        public Boolean Configured
        {
            get; set;
        }

        /// <summary>Cached, short-TTL probe; only run when the capability is on (like docling).</summary>
        public Boolean Reachable
        {
            get; set;
        }

        public static async Task<NlpStatsREST> From(Fallen8NlpOptions options, INlpClient client,
            CancellationToken cancellationToken)
        {
            if (options == null)
            {
                return null;
            }

            var configured = client != null && client.Configured;
            return new NlpStatsREST
            {
                Enabled = options.Enabled,
                Configured = configured,
                Reachable = options.Enabled && configured && await client.IsReachableAsync(cancellationToken)
            };
        }
    }
}
