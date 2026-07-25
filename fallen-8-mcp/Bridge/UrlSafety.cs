// MIT License
//
// UrlSafety.cs
//
// Copyright (c) 2026 Henning Rauch
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

namespace NoSQL.GraphDB.Mcp.Bridge
{
    /// <summary>
    ///   URL-construction integrity is the security boundary (spec §3.9): tier gating is
    ///   enforced purely by <em>which routes the bridge builds</em>, and the downstream key is
    ///   full-authority, so a caller-supplied string must never be able to inject an extra path
    ///   segment, a query, or a fragment into a downstream URL. Every caller value that becomes
    ///   part of a URL passes through here first. Fallen-8 namespace names are deliberately
    ///   permissive (spaces, punctuation incl. <c>?</c>/<c>#</c>/<c>%</c>, Unicode), so encoding
    ///   is mandatory, not cosmetic.
    /// </summary>
    public static class UrlSafety
    {
        /// <summary>
        ///   Percent-encodes a single value for safe use as ONE URL path segment. The result
        ///   contains no '/', '?', '#', or '%'-decodable traversal that could change the route.
        /// </summary>
        public static String EncodeSegment(String value)
        {
            ArgumentNullException.ThrowIfNull(value);
            // Uri.EscapeDataString encodes '/', '?', '#', '%', spaces and reserved chars, so the
            // value can only ever be a single, inert segment.
            return Uri.EscapeDataString(value);
        }

        /// <summary>
        ///   Validates a namespace against Fallen-8's own name rule BEFORE it is used, and
        ///   returns the encoded segment. Rejects the cases Fallen-8 rejects (empty/whitespace,
        ///   too long, "."/"..", '/'/'\'/control chars) so the bridge fails fast with a clear
        ///   tool error rather than issuing a request that Fallen-8 will 400/404 — and so a
        ///   traversal-shaped name never reaches the wire even encoded.
        /// </summary>
        public static Boolean TryEncodeNamespace(String? name, out String encoded, out String error)
        {
            encoded = String.Empty;
            error = String.Empty;

            if (String.IsNullOrEmpty(name))
            {
                error = "A namespace name is required.";
                return false;
            }

            if (name.Length > 63)
            {
                error = "A namespace name may be at most 63 characters.";
                return false;
            }

            if (name != name.Trim())
            {
                error = "A namespace name may not have leading or trailing whitespace.";
                return false;
            }

            if (name == "." || name == "..")
            {
                error = "A namespace name may not be \".\" or \"..\".";
                return false;
            }

            foreach (var ch in name)
            {
                if (ch == '/' || ch == '\\' || Char.IsControl(ch))
                {
                    error = "A namespace name may not contain '/', '\\', or control characters.";
                    return false;
                }
            }

            encoded = EncodeSegment(name);
            return true;
        }

        /// <summary>The reserved namespace that bare (un-prefixed) routes alias.</summary>
        public const String DefaultNamespace = "default";

        /// <summary>
        ///   Whether a namespace addresses the reserved default (null/empty/"default") — in which
        ///   case the bridge uses the bare route rather than a <c>/ns/{ns}</c> twin.
        /// </summary>
        public static Boolean IsDefault(String? name)
        {
            return String.IsNullOrEmpty(name) ||
                   String.Equals(name, DefaultNamespace, StringComparison.Ordinal);
        }
    }
}
