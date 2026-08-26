// MIT License
//
// JobReport.cs
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
using System.Text.Json.Serialization;
using NoSQL.GraphDB.Integrations.Contract;

namespace NoSQL.GraphDB.Integrations.Run
{
    /// <summary>
    ///   THE ONLY ACCOUNT of a job, because the runtime keeps none. A provider's diagnostics ride along on its
    ///   snapshot into the same list, so one report covers both what the source could not tell the run and what
    ///   the graph could not be told, and diagnostics are never dropped.
    /// </summary>
    public sealed class JobReport
    {
        /// <summary>Which integration ran.</summary>
        [JsonPropertyName("providerId")]
        public String? ProviderId { get; set; }

        /// <summary>The identity it asserted as.</summary>
        [JsonPropertyName("integrationInstanceId")]
        public String? IntegrationInstanceId { get; set; }

        /// <summary>When it started, UTC.</summary>
        [JsonPropertyName("startedUtc")]
        public DateTimeOffset StartedUtc { get; set; }

        /// <summary>How long it took.</summary>
        [JsonPropertyName("durationMilliseconds")]
        public Int64 DurationMilliseconds { get; set; }

        /// <summary>Elements created.</summary>
        [JsonPropertyName("elementsCreated")]
        public Int32 ElementsCreated { get; set; }

        /// <summary>Elements matched, that is, already claimed by this identity or reclaimed as orphans.</summary>
        [JsonPropertyName("elementsMatched")]
        public Int32 ElementsMatched { get; set; }

        /// <summary>Edges created.</summary>
        [JsonPropertyName("edgesCreated")]
        public Int32 EdgesCreated { get; set; }

        /// <summary>Claims withdrawn, counted only where the claim property was actually still present.</summary>
        [JsonPropertyName("claimsWithdrawn")]
        public Int32 ClaimsWithdrawn { get; set; }

        /// <summary>Elements deleted, which happens only on the LAST claim.</summary>
        [JsonPropertyName("elementsDeleted")]
        public Int32 ElementsDeleted { get; set; }

        /// <summary>Deletions deferred because the target's durability made deleting unsafe.</summary>
        [JsonPropertyName("deletionsDeferred")]
        public Int32 DeletionsDeferred { get; set; }

        /// <summary>
        ///   Whether the run issued ANY mutation call at all, so a re-run over an unchanged source reports
        ///   false. Asserted on the call channel, not on stored values.
        /// </summary>
        [JsonPropertyName("issuedMutations")]
        public Boolean IssuedMutations { get; set; }

        /// <summary>Entity summaries embedded, which is zero unless both halves of the opt-in are set.</summary>
        [JsonPropertyName("summariesEmbedded")]
        public Int32 SummariesEmbedded { get; set; }

        /// <summary>
        ///   Whether the run was STOPPED ON REQUEST at a safe point, rather than finishing or failing.
        ///
        ///   <para>Not a kind of failure, which is why it is a flag of its own and not an
        ///   <see cref="ErrorKind" />: nothing is wrong, and the counts above are what really landed. What
        ///   a reader has to know is the one thing a cancelled run deliberately did not do - it did not
        ///   RECONCILE - because reconciliation withdraws by set difference over what the run claimed, and
        ///   a run that stopped early never claimed the entities it never reached. The next completed run
        ///   of this identity converges the graph, and nothing was withdrawn or deleted in the meantime.</para>
        /// </summary>
        [JsonPropertyName("cancelled")]
        public Boolean Cancelled { get; set; }

        /// <summary>What failed, when something did. A run that failed withdrew nothing.</summary>
        [JsonPropertyName("error")]
        public String? Error { get; set; }

        /// <summary>Which system failed: <c>configuration</c>, <c>credential</c>, <c>source</c> or <c>graph</c>.</summary>
        [JsonPropertyName("errorKind")]
        public String? ErrorKind { get; set; }

        /// <summary>
        ///   A keyed hash of the credentials this run held, under a key random per process: two reports can be
        ///   compared on WHICH credential was used, and neither carries it. Two failures with one identical
        ///   fingerprint say the key you just changed never reached this runtime, which is a different problem
        ///   from a key the source rejects.
        /// </summary>
        [JsonPropertyName("credentialFingerprint")]
        public String? CredentialFingerprint { get; set; }

        /// <summary>Everything a reader needs to know, from both the provider and the runtime.</summary>
        [JsonPropertyName("diagnostics")]
        public IList<DiagnosticDto> Diagnostics { get; set; } = new List<DiagnosticDto>();

        /// <summary>Whether the run failed. A failed run is cheap: nothing withdrawn, nothing deleted.</summary>
        [JsonIgnore]
        public Boolean Failed => ErrorKind != null;
    }

    /// <summary>
    ///   Which system failed. "The job is wrong", "the password is wrong", "the console will not answer" and
    ///   "the graph will not answer" send a reader to four different places, and only a named kind gets them
    ///   there.
    /// </summary>
    public static class JobErrorKinds
    {
        /// <summary>The job cannot be run as written.</summary>
        public const String Configuration = "configuration";

        /// <summary>
        ///   The credential is the thing to go and look at: the runtime could not use the value it was given,
        ///   or the source itself rejected it. Both are one kind because they send a reader to one place, and
        ///   a refused key reported as <see cref="Source"/> would send them to the network instead.
        /// </summary>
        public const String Credential = "credential";

        /// <summary>The source did not answer, or answered unusably.</summary>
        public const String Source = "source";

        /// <summary>The graph did not answer.</summary>
        public const String Graph = "graph";

        /// <summary>
        ///   Another run is already in flight for this identity. A REJECTION kind rather than a fifth run
        ///   failure: nothing was read, nothing written, and the caller has something to fix.
        /// </summary>
        public const String Conflict = "conflict";
    }

    /// <summary>
    ///   "This job could not be run at all." Distinct from a run that ran and failed, which answers 200 with the
    ///   failure on the report: the request did what was asked, so the interesting outcome is the run's.
    /// </summary>
    public sealed class JobRejectedException : Exception
    {
        public JobRejectedException(String kind, String message)
            : base(message)
        {
            Kind = kind;
        }

        /// <summary><see cref="JobErrorKinds.Configuration"/> (a 400) or <see cref="JobErrorKinds.Conflict"/>
        /// (a 409).</summary>
        public String Kind { get; }
    }
}
