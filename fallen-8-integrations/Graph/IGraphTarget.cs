// MIT License
//
// IGraphTarget.cs
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

namespace NoSQL.GraphDB.Integrations.Graph
{
    /// <summary>
    ///   THE GRAPH SIDE, as one seam that names the exact platform surface this feature depends on. Every
    ///   method maps to one REST route at the job's namespace, every mutation is one atomic transaction
    ///   awaited to completion, and it has two implementations: <c>Fallen8RestTarget</c> against a live
    ///   Fallen-8 and <c>InMemoryGraphTarget</c> for the conformance suite, kept honest against each other
    ///   by one shared contract suite so the fake cannot drift stricter or laxer than the platform.
    ///
    ///   <para>Element ids are valid only within ONE call sequence: none is cached across runs or persisted,
    ///   and every durable handle here is a claim key. <c>HEAD /trim</c> renumbers every element id in place,
    ///   unwaited and agent-reachable. The target is created per job and disposed by the runner, so a target
    ///   owning a connection does not leak one per run.</para>
    /// </summary>
    public interface IGraphTarget : IDisposable
    {
        /// <summary>
        ///   How many MUTATION calls this target has issued. The zero-mutation invariant is asserted on the CALL
        ///   CHANNEL rather than on stored values, because the platform already treats an equal-value write as a
        ///   true no-op: a runtime that wrote unconditionally would still leave the graph correct while churning
        ///   the change feed on every run, growing a write-ahead log that nothing here bounds, and making the
        ///   invariant unobservable. An invariant nobody can observe decays without a failing test.
        ///
        ///   <para>A COUNT rather than a flag, because a run must be able to say what IT issued. A live target is
        ///   created per job and would answer either way, but the conformance suite runs a candidate twice
        ///   against ONE graph, and against a flag its idempotence check could never pass: it would read the
        ///   first run's create forever. A check that cannot pass is as useless as one that cannot fail.</para>
        /// </summary>
        Int32 IssuedMutationCount { get; }

        /// <summary>
        ///   Creates the two claim indices if they are absent, and returns TRUE when it had to create one -
        ///   which obliges the caller to repair before trusting a lookup.
        ///
        ///   <para>It runs before EVERY job rather than once at startup, because "it existed when I started"
        ///   is not a fact that stays true: a tabula rasa, loading a save game and a per-index serialization
        ///   failure each drop an index while this runtime is running.</para>
        ///
        ///   <para>An ensure that created silently would turn a dropped index into the worst outcome in the
        ///   feature: a fresh index is empty, empty is indistinguishable from "no element carries this
        ///   claim", so every entity would resolve to nothing, every element would be duplicated, and the
        ///   originals would keep claims no instance knows about, so no withdrawal would ever remove them.
        ///   The runtime cannot tell "empty because new" from "empty because destroyed" and does not need to:
        ///   both demand the same repair, which on a genuinely new graph is a no-op.</para>
        /// </summary>
        Task<Boolean> EnsureIndicesAsync(CancellationToken cancellationToken);

        /// <summary>
        ///   Rebuilds both indices from ELEMENT STATE, add-only.
        ///
        ///   <para>It is posted per index with the reserved PREFIX as the property selector, not an exact
        ///   key: an exact-key backfill restores only the first claim of each element, which leaves it
        ///   findable by one identity and invisible by the rest - a repair that looks successful and then
        ///   duplicates the element on the next resolve.</para>
        /// </summary>
        Task<IndexRepairOutcome> RepairIndicesAsync(CancellationToken cancellationToken);

        /// <summary>
        ///   Looks up claim keys in the identity index, reads every element the index named in ONE batch, and
        ///   narrows to what is in scope for <paramref name="instanceId"/>. Only STRONG keys are ever passed here.
        ///
        ///   <para>The narrowing lives here rather than in the caller because it is a question about element
        ///   STATE that the index cannot answer, and it goes through the one shared
        ///   <see cref="ClaimLookup.Build"/> so the in-memory graph cannot narrow differently from the live one.
        ///   Substituting a target that narrows WRONGLY is how the conformance suite turns its claim-scope check
        ///   red, which is the only red path that check has: the runtime owns every claim write, so no candidate
        ///   provider can produce one.</para>
        /// </summary>
        /// <exception cref="GraphIndexMissingException">The index does not exist. An empty answer would read
        /// as "nothing carries this claim", so this raises instead; the caller ensures, repairs from element
        /// state and retries ONCE, because a second failure is a real fault and must surface rather than loop.</exception>
        Task<ClaimLookup> ResolveClaimKeysAsync(IReadOnlyCollection<String> claimKeys, String instanceId,
            CancellationToken cancellationToken);

        /// <summary>
        ///   Every element id the claim index names for one instance. This single lookup is what makes
        ///   reconciliation a set difference instead of a graph scan.
        ///
        ///   <para>It answers "EVER claimed", not "claims now": the index has no remove path, so an element
        ///   this instance stopped claiming stays in the answer forever. That is why withdrawal is
        ///   effective-only and why the deletion decision is re-read from element state.</para>
        /// </summary>
        /// <exception cref="GraphIndexMissingException">The index does not exist. Reconciliation is then
        /// SKIPPED rather than retried, because an empty answer reads as "this instance claims nothing",
        /// which would withdraw everything the instance ever asserted.</exception>
        Task<IReadOnlyList<Int32>> ElementsClaimedByAsync(String instanceId, CancellationToken cancellationToken);

        /// <summary>
        ///   Reads many elements' current state in one call. An id that resolves to no live element is simply
        ///   absent from the result, because "gone" and "has no properties" are different conclusions and the
        ///   caller acts differently on each.
        /// </summary>
        Task<IReadOnlyDictionary<Int32, ElementState>> ReadElementsAsync(IReadOnlyCollection<Int32> ids,
            CancellationToken cancellationToken);

        /// <summary>Creates vertices in one atomic transaction and returns their ids IN INPUT ORDER.</summary>
        Task<IReadOnlyList<Int32>> CreateVerticesAsync(IReadOnlyList<VertexWrite> vertices,
            CancellationToken cancellationToken);

        /// <summary>Creates edges in one atomic transaction and returns their ids IN INPUT ORDER.</summary>
        Task<IReadOnlyList<Int32>> CreateEdgesAsync(IReadOnlyList<EdgeWrite> edges,
            CancellationToken cancellationToken);

        /// <summary>
        ///   Sets and removes properties across many elements in ONE atomic transaction, so a reconciliation
        ///   spanning several properties cannot be interrupted half-applied, leaving the element in a state
        ///   no source describes.
        /// </summary>
        Task ApplyPropertyWritesAsync(IReadOnlyList<PropertyWrite> writes, CancellationToken cancellationToken);

        /// <summary>Removes elements in one atomic transaction.</summary>
        Task RemoveElementsAsync(IReadOnlyCollection<Int32> ids, CancellationToken cancellationToken);

        /// <summary>
        ///   Adds claim keys to an index.
        ///
        ///   <para>It returns an outcome rather than a bare task because an index write can be DECLINED with
        ///   a plain <c>false</c> and no error: an element findable by none of its claims is duplicated on
        ///   the next resolve, so this is never merely informational. A task-returning seam would discard the
        ///   signal and make the omission untestable, since a fake would then have to invent a harsher
        ///   behaviour than the platform has.</para>
        /// </summary>
        Task<IndexWriteOutcome> IndexClaimsAsync(IReadOnlyList<IndexEntry> entries,
            CancellationToken cancellationToken);

        /// <summary>
        ///   Reads whether it is safe to DELETE right now. Deletion is the one mutation re-running cannot
        ///   undo, and it is driven by a conclusion read out of graph content: on truncated history or a lost
        ///   claim index the claim state the elements were judged by may be incomplete.
        /// </summary>
        Task<TargetDurability> ReadDurabilityAsync(CancellationToken cancellationToken);

        /// <summary>
        ///   What the target can embed, read from the configuration it already publishes. No model, dimension or
        ///   metric is ever hardcoded here: the target owns them, and a runtime that assumed any of the three
        ///   would write vectors a bound index refuses.
        /// </summary>
        Task<TargetEmbedding> ReadEmbeddingStateAsync(CancellationToken cancellationToken);

        /// <summary>
        ///   Embeds entity summaries as a named embedding on the elements they describe.
        ///
        ///   <para>An unavailable embedding surface DEGRADES TO ABSENT rather than to broken: no vector is
        ///   written, the run still succeeds, and the report says so. That is the whole of this feature's
        ///   dependence on the AI capabilities, which is why the degradation matrix over it has two honest cells
        ///   rather than sixteen decorative ones.</para>
        /// </summary>
        /// <remarks>
        ///   <paramref name="progress" /> ticks per CHUNK, which is the finest granularity available: a
        ///   chunk is one call to the target. It matters here more than anywhere else on this interface,
        ///   because this is the only method whose duration is model inference - hours for a real extract -
        ///   and a phase counter that never moves is indistinguishable from a hang.
        /// </remarks>
        Task<EmbeddingWriteOutcome> EmbedSummariesAsync(String embeddingName,
            IReadOnlyList<SummaryWrite> summaries, CancellationToken cancellationToken,
            NoSQL.GraphDB.Integrations.Run.IRunProgress? progress = null);
    }

    /// <summary>
    ///   Raised instead of answering empty when an index a lookup needs does not exist. Carries the index id
    ///   so the caller can say which repair it is about to run.
    /// </summary>
    public sealed class GraphIndexMissingException : Exception
    {
        public GraphIndexMissingException(String indexId)
            : base(String.Format(
                "The index '{0}' does not exist, so a lookup against it cannot answer. An index is dropped " +
                "by a tabula rasa, by loading a save game, and by a per-index serialization failure during a " +
                "checkpoint, so this is a repair rather than an empty result.", indexId))
        {
            IndexId = indexId;
        }

        /// <summary>The index that was missing.</summary>
        public String IndexId { get; }
    }

    /// <summary>
    ///   "The graph did not answer." One of the four failure kinds a report names, because "the mount is
    ///   broken", "the password is wrong", "the console will not answer" and "the graph will not answer" send
    ///   a reader to four different places.
    /// </summary>
    public sealed class GraphTargetException : Exception
    {
        public GraphTargetException(String message)
            : base(message)
        {
        }

        public GraphTargetException(String message, Exception inner)
            : base(message, inner)
        {
        }

        /// <summary>
        ///   How many entity summaries were embedded BEFORE the failure, for the one caller that sends them
        ///   in chunks. Zero everywhere else, which is also the truth everywhere else.
        ///
        ///   <para>It rides on the exception because the count is only known at the throw site and the value
        ///   is a fact about the graph rather than about the failure: the chunks that already landed put real
        ///   vectors on real elements, so a report that said zero would be false about state a bound index
        ///   will happily answer searches over.</para>
        /// </summary>
        public Int32 SummariesWritten { get; init; }
    }
}
