// MIT License
//
// ConformanceReport.cs
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
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace NoSQL.GraphDB.Integrations.Conformance
{
    /// <summary>
    ///   What the conformance suite checks, with PINNED numeric values because the report is exposed over the
    ///   wire: retiring a member must leave its value as a gap rather than renumber the members after it.
    ///
    ///   <para>The checks are NAMED and STABLE because a negative fixture asserts a SPECIFIC one. A verifier
    ///   answering only "invalid" cannot be tested: the only way to know it looks at the right thing is for a
    ///   deliberately broken provider to fail the check it was broken for, and for the suite to say which. An
    ///   untested verifier certifies rather than verifies, which is worse than no verifier because it is
    ///   trusted.</para>
    /// </summary>
    public enum ConformanceCheck
    {
        /// <summary>The envelope satisfies the contract: schema version, provider id, instance id, completeness.</summary>
        SnapshotValid = 0,

        /// <summary>Every identity claim names a vocabulary type whose value canonicalises and then validates.</summary>
        ClaimsWellFormed = 1,

        /// <summary>No claim declares a strength the vocabulary disagrees with.</summary>
        StrengthDeclarationHonest = 2,

        /// <summary>Two runs over one fixture describe it identically, compared on the serialised snapshot.</summary>
        Deterministic = 3,

        /// <summary>A second run over an unchanged source issues zero write calls to the graph.</summary>
        Idempotent = 4,

        /// <summary>
        ///   A run writes only to what it claims, to what it withdraws its own claim from, and to an unclaimed
        ///   orphan it reclaims. No element another instance claims was written to.
        /// </summary>
        ClaimScoped = 5,

        /// <summary>Nothing in the snapshot offers a score, a threshold or a confidence for identity.</summary>
        NoSimilarityIdentity = 6,

        /// <summary>The run completed against substituted seams ALONE.</summary>
        RunsOffline = 7,

        /// <summary>No credential value reached a log sink, the job report or the graph.</summary>
        NoCredentialLeak = 8,

        /// <summary>Every file read was one the fixture offered, by name.</summary>
        NoPathEscape = 9,

        /// <summary>A provider that cannot observe the whole source did not declare a complete snapshot.</summary>
        CompletenessHonest = 10,

        /// <summary>A run that failed withdrew nothing, and a source that answered unusably failed the run.</summary>
        UnreadableSourceFails = 11,
    }

    /// <summary>One check's verdict, with a detail sentence an author can act on.</summary>
    public sealed class ConformanceFinding
    {
        public ConformanceFinding(ConformanceCheck check, Boolean passed, String detail)
        {
            Check = check;
            Passed = passed;
            Detail = detail;
        }

        /// <summary>Which check this is.</summary>
        [JsonPropertyName("check")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ConformanceCheck Check { get; }

        /// <summary>Whether it passed.</summary>
        [JsonPropertyName("passed")]
        public Boolean Passed { get; }

        /// <summary>What was observed, in a sentence an author can act on.</summary>
        [JsonPropertyName("detail")]
        public String Detail { get; }
    }

    /// <summary>
    ///   The verdict on one candidate. <see cref="Failed"/> is what a negative fixture asserts on, because
    ///   asserting on "does not conform" would pass for the wrong reason.
    /// </summary>
    public sealed class ConformanceReport
    {
        public ConformanceReport(ImmutableArray<ConformanceFinding> findings)
        {
            Findings = findings;
        }

        /// <summary>Every check's verdict, in enum order, always all of them.</summary>
        [JsonPropertyName("findings")]
        public ImmutableArray<ConformanceFinding> Findings { get; }

        /// <summary>Whether every check passed.</summary>
        [JsonPropertyName("conforms")]
        public Boolean Conforms
        {
            get
            {
                foreach (var finding in Findings)
                {
                    if (!finding.Passed)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        /// <summary>The checks that failed, by name.</summary>
        [JsonPropertyName("failed")]
        public IReadOnlyList<ConformanceCheck> Failed
        {
            get
            {
                var failed = new List<ConformanceCheck>();
                foreach (var finding in Findings)
                {
                    if (!finding.Passed)
                    {
                        failed.Add(finding.Check);
                    }
                }

                return failed;
            }
        }

        /// <summary>The detail of one check, for an assertion message.</summary>
        public String DetailOf(ConformanceCheck check)
        {
            foreach (var finding in Findings)
            {
                if (finding.Check == check)
                {
                    return finding.Detail;
                }
            }

            return String.Empty;
        }
    }
}
