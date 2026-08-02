// MIT License
//
// IdentifierExtractor.cs
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

namespace NoSQL.GraphDB.App.Ingestion
{
    /// <summary>
    ///   Generic identifier-token extraction (spec unstructured-ingestion FR-6): underscore
    ///   identifiers (any case mix), CamelCase words, and hex ids. These tokens are the
    ///   exact-match currency of structural linking (FR-13) and exact lexical lookup; the
    ///   patterns are deliberately domain-agnostic.
    /// </summary>
    public static class IdentifierExtractor
    {
        /// <summary>Underscore identifiers: <c>RETRY_BUDGET_MS</c>, <c>Tls_Frontend_V2</c>.</summary>
        private static readonly Regex UnderscoreIdentifier =
            new Regex(@"\b[A-Z][A-Za-z0-9]*(?:_[A-Za-z0-9]+)+\b", RegexOptions.Compiled);

        /// <summary>CamelCase with at least two humps: <c>CheckoutService</c>.</summary>
        private static readonly Regex CamelCaseIdentifier =
            new Regex(@"\b[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]+)+\b", RegexOptions.Compiled);

        /// <summary>Hex ids: <c>0x1A2B</c>.</summary>
        private static readonly Regex HexIdentifier =
            new Regex(@"\b0x[0-9A-Fa-f]{2,}\b", RegexOptions.Compiled);

        private const Int32 MinUnderscoreLength = 4;
        private const Int32 MinCamelCaseLength = 6;

        /// <summary>
        ///   Extracts tokens: deduplicated (ordinal), capped by FIRST OCCURRENCE, returned
        ///   sorted (ordinal) - deterministic for identical input.
        /// </summary>
        public static List<String> Extract(String text, Int32 cap)
        {
            if (String.IsNullOrEmpty(text) || cap <= 0)
            {
                return new List<String>();
            }

            var candidates = new List<KeyValuePair<Int32, String>>();
            Collect(candidates, UnderscoreIdentifier, text, MinUnderscoreLength);
            Collect(candidates, CamelCaseIdentifier, text, MinCamelCaseLength);
            Collect(candidates, HexIdentifier, text, 0);

            // First occurrence wins the cap; equal positions (never for distinct matches of one
            // regex, possible across regexes) break by ordinal value.
            candidates.Sort((a, b) => a.Key != b.Key
                ? a.Key.CompareTo(b.Key)
                : String.CompareOrdinal(a.Value, b.Value));

            var seen = new HashSet<String>(StringComparer.Ordinal);
            var kept = new List<String>();
            foreach (var candidate in candidates)
            {
                if (seen.Add(candidate.Value))
                {
                    kept.Add(candidate.Value);
                    if (kept.Count >= cap)
                    {
                        break;
                    }
                }
            }

            kept.Sort(StringComparer.Ordinal);
            return kept;
        }

        private static void Collect(List<KeyValuePair<Int32, String>> candidates, Regex pattern,
            String text, Int32 minLength)
        {
            foreach (Match match in pattern.Matches(text))
            {
                if (match.Length >= minLength)
                {
                    candidates.Add(new KeyValuePair<Int32, String>(match.Index, match.Value));
                }
            }
        }
    }
}
