// MIT License
//
// IIntegrationProvider.cs
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

using System.Threading;
using System.Threading.Tasks;

namespace NoSQL.GraphDB.Integrations.Contract
{
    /// <summary>
    ///   THE WHOLE OF WHAT AN INTEGRATION IS: data that is true before any run exists, plus one method
    ///   that observes the source once and describes what it saw.
    ///
    ///   <para>Throwing is a legitimate outcome meaning "I could not reach the source": the job fails,
    ///   the caller is told which system did not answer, and nothing is withdrawn. Returning a snapshot
    ///   that says nothing is a DIFFERENT statement, because a complete snapshot with no entities
    ///   declares the source empty and withdraws everything this identity ever claimed. "I could not
    ///   look" must never become "there is nothing there".</para>
    ///
    ///   <para>A provider never resolves identity, never sees the graph or an element id, never learns
    ///   whether an entity was created or matched, never opens a file, never holds a credential past the
    ///   run, never declares a strength for its own claims, and never declares an interval. Every
    ///   irreversible decision - claim canonicalisation, resolution, reconciliation, index repair,
    ///   deletion safety - is on the runtime's side, so the worst a wrong provider can do is describe
    ///   its source wrongly, which is visible in its snapshot. That boundary is what lets a fourth
    ///   integration be reviewed without re-reviewing identity.</para>
    /// </summary>
    public interface IIntegrationProvider
    {
        /// <summary>What this provider is, as data.</summary>
        ProviderDescriptor Descriptor { get; }

        /// <summary>
        ///   Observes the source once and describes what it saw. Everything a provider is handed is on
        ///   <paramref name="context"/>.
        /// </summary>
        Task<SnapshotDocument> ObserveAsync(ProviderContext context, CancellationToken cancellationToken);
    }

    /// <summary>
    ///   Opt-in for the conformance suite's benefit: the snapshot checks need the document the provider
    ///   returned, and the runtime never needs it. A provider that does not implement this is recorded
    ///   as UNJUDGEABLE AND FAILING rather than passed by default, because a check that cannot fail is
    ///   not a check.
    /// </summary>
    public interface IObservableProvider
    {
        /// <summary>The document the last <c>ObserveAsync</c> returned, or null before the first.</summary>
        SnapshotDocument? LastSnapshot { get; }
    }
}
