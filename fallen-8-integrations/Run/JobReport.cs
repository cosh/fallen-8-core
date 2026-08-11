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

        /// <summary>What failed, when something did. A run that failed withdrew nothing.</summary>
        [JsonPropertyName("error")]
        public String? Error { get; set; }

        /// <summary>Which system failed: <c>configuration</c>, <c>credential</c>, <c>source</c> or <c>graph</c>.</summary>
        [JsonPropertyName("errorKind")]
        public String? ErrorKind { get; set; }

        /// <summary>
        ///   A keyed hash of the credentials this run held, under a key random per process. A credential file
        ///   replaced by MOVING a new file over it gives the file a new inode and a bind-mounted container keeps
        ///   reading the old one, so the job succeeds with the credential the operator believes they revoked; a
        ///   fingerprint that does not change after a rotation is how that is seen.
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
    ///   Which system failed. "The mount is broken", "the password is wrong", "the console will not answer" and
    ///   "the graph will not answer" send a reader to four different places, and only a named kind gets them
    ///   there.
    /// </summary>
    public static class JobErrorKinds
    {
        /// <summary>The job cannot be run as written.</summary>
        public const String Configuration = "configuration";

        /// <summary>A named credential could not be read.</summary>
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
