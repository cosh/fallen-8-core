// MIT License
//
// GroundingCheck.cs
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
using System.Text.RegularExpressions;

namespace NoSQL.GraphDB.Agents.Runtime
{
    /// <summary>
    ///   Counts the citations in an agent's final text against the tool calls its trace records.
    ///
    ///   <para>
    ///     <b>It is a count and deliberately not a judgement.</b> No judge model, no claim schema,
    ///     no attempt to decide whether a figure is right. It exists so a reviewer can see at a
    ///     glance whether an answer points back at work that actually happened, which is the
    ///     cheapest useful signal against the failure this whole feature guards: a fabricated result
    ///     that looks like an answer.
    ///   </para>
    ///   <para>
    ///     <b>What the numbers do and do not mean.</b> A high <c>valid</c> count is not a correct
    ///     answer: an agent can cite a real call and still misread it. A <c>dangling</c> count is
    ///     not proof of a lie either: the model may have named a tool it holds but did not call on
    ///     this run, or one that does not exist, and a count cannot tell either apart from a
    ///     fabrication. It is no longer excused by the trace BOUND, though, and this paragraph used
    ///     to say it was: the tool-name set is kept OUTSIDE the bound precisely so a citation to a
    ///     real early call cannot dangle once its step has been dropped. So on a long run a dangling
    ///     count means the answer points at work this trace has no record of, which is worth a
    ///     reviewer's attention rather than a bookkeeping artefact to discount.
    ///     What the pair IS good for is the shape a fabricating run has: every figure asserted, no
    ///     citations at all, and a trace with no tool calls in it. That was measured on the shipped
    ///     agent model, so this is aimed at something real.
    ///   </para>
    ///   <para>
    ///     <b>Tool NAMES rather than tool-call ids</b>, which is a correction from a measurement: no
    ///     backend shows a tool-call id to a model, so asking it to cite one was asking it to invent
    ///     one, in the prompt whose job is to stop invention.
    ///   </para>
    /// </summary>
    public static class GroundingCheck
    {
        /// <summary>
        ///   The marker the role prompts ask for: <c>[t:count_vertices]</c>, and
        ///   <c>[t:count_vertices#2]</c> for a second call to the same tool.
        ///
        ///   <para>
        ///     Deliberately forgiving of the occurrence suffix and of surrounding whitespace,
        ///     because a small model reproduces a format approximately and the alternative is
        ///     counting a real citation as absent. It is NOT forgiving about the name itself: that
        ///     is the part being checked.
        ///   </para>
        /// </summary>
        private static readonly Regex Marker = new Regex(
            @"\[t:\s*(?<tool>[A-Za-z0-9_.\-]+)\s*(?:#\s*\d+\s*)?\]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        ///   Counts citations in <paramref name="text" /> against <paramref name="toolsCalled" />.
        ///
        ///   <para>
        ///     Counts OCCURRENCES, not distinct names. An answer carrying ten figures should carry
        ///     ten citations, and collapsing them to one would make an answer that cited its first
        ///     figure and asserted the other nine look fully grounded.
        ///   </para>
        /// </summary>
        public static CitationCounts Count(String? text, IReadOnlyCollection<String>? toolsCalled)
        {
            var counts = new CitationCounts();
            if (String.IsNullOrEmpty(text))
            {
                return counts;
            }

            var known = new HashSet<String>(toolsCalled ?? Array.Empty<String>(),
                StringComparer.OrdinalIgnoreCase);

            foreach (Match match in Marker.Matches(text))
            {
                var tool = match.Groups["tool"].Value;
                if (known.Contains(tool))
                {
                    counts.Valid++;
                }
                else
                {
                    counts.Dangling++;
                }
            }

            return counts;
        }
    }
}
