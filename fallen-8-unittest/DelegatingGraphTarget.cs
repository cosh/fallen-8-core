// MIT License
//
// DelegatingGraphTarget.cs
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
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NoSQL.GraphDB.Integrations.Graph;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Pass-through to a real graph, so a fixture overrides ONE seam method and nothing else. Written
    ///   once rather than per fixture, because a hand-rolled second copy is where a fixture quietly stops
    ///   behaving like the graph.
    ///
    ///   <para>It lives in its own file rather than inside one test class because more than one of them
    ///   needs it: the write-path fixtures intercept a single call to make a failure reachable, and the
    ///   resume fixtures intercept one to stop a run at a chosen boundary. Two copies of this would drift,
    ///   and a target that drifts is a fixture that passes for the wrong reason.</para>
    /// </summary>
    internal abstract class DelegatingGraphTarget : IGraphTarget
    {
        private readonly IGraphTarget _inner;

        protected DelegatingGraphTarget(IGraphTarget inner)
        {
            _inner = inner;
        }

        public Int32 IssuedMutationCount => _inner.IssuedMutationCount;

        public virtual Task<Boolean> EnsureIndicesAsync(CancellationToken cancellationToken)
        {
            return _inner.EnsureIndicesAsync(cancellationToken);
        }

        public virtual Task<IndexRepairOutcome> RepairIndicesAsync(CancellationToken cancellationToken)
        {
            return _inner.RepairIndicesAsync(cancellationToken);
        }

        public virtual Task<ClaimLookup> ResolveClaimKeysAsync(IReadOnlyCollection<String> claimKeys,
            String instanceId, CancellationToken cancellationToken)
        {
            return _inner.ResolveClaimKeysAsync(claimKeys, instanceId, cancellationToken);
        }

        public virtual Task<IReadOnlyList<Int32>> ElementsClaimedByAsync(String instanceId,
            CancellationToken cancellationToken)
        {
            return _inner.ElementsClaimedByAsync(instanceId, cancellationToken);
        }

        public virtual Task<IReadOnlyDictionary<Int32, ElementState>> ReadElementsAsync(
            IReadOnlyCollection<Int32> ids, CancellationToken cancellationToken)
        {
            return _inner.ReadElementsAsync(ids, cancellationToken);
        }

        public virtual Task<IReadOnlyList<Int32>> CreateVerticesAsync(IReadOnlyList<VertexWrite> vertices,
            CancellationToken cancellationToken)
        {
            return _inner.CreateVerticesAsync(vertices, cancellationToken);
        }

        public virtual Task<IReadOnlyList<Int32>> CreateEdgesAsync(IReadOnlyList<EdgeWrite> edges,
            CancellationToken cancellationToken)
        {
            return _inner.CreateEdgesAsync(edges, cancellationToken);
        }

        public virtual Task ApplyPropertyWritesAsync(IReadOnlyList<PropertyWrite> writes,
            CancellationToken cancellationToken)
        {
            return _inner.ApplyPropertyWritesAsync(writes, cancellationToken);
        }

        public virtual Task RemoveElementsAsync(IReadOnlyCollection<Int32> ids,
            CancellationToken cancellationToken)
        {
            return _inner.RemoveElementsAsync(ids, cancellationToken);
        }

        public virtual Task<IndexWriteOutcome> IndexClaimsAsync(IReadOnlyList<IndexEntry> entries,
            CancellationToken cancellationToken)
        {
            return _inner.IndexClaimsAsync(entries, cancellationToken);
        }

        public virtual Task<TargetDurability> ReadDurabilityAsync(CancellationToken cancellationToken)
        {
            return _inner.ReadDurabilityAsync(cancellationToken);
        }

        public virtual Task<TargetEmbedding> ReadEmbeddingStateAsync(CancellationToken cancellationToken)
        {
            return _inner.ReadEmbeddingStateAsync(cancellationToken);
        }

        public virtual Task<EmbeddingWriteOutcome> EmbedSummariesAsync(String embeddingName,
            IReadOnlyList<SummaryWrite> summaries, CancellationToken cancellationToken,
            NoSQL.GraphDB.Integrations.Run.IRunProgress progress = null,
            NoSQL.GraphDB.Integrations.Run.RunAbort abort = default)
        {
            return _inner.EmbedSummariesAsync(embeddingName, summaries, cancellationToken, progress, abort);
        }

        public void Dispose()
        {
        }
    }
}
