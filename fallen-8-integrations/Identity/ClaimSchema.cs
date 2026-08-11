// MIT License
//
// ClaimSchema.cs
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
using System.Globalization;

namespace NoSQL.GraphDB.Integrations.Identity
{
    /// <summary>
    ///   THE ONLY PLACE that composes the two reserved property prefixes, and the one home of the instance
    ///   id's shape RULE - though not of its enforcement, which is deliberately at the edges where a caller can
    ///   still be told: the job route refuses a bad id before a provider runs, and the validator refuses one in
    ///   a snapshot envelope. Composing is too late to reject, so <see cref="ClaimProperty"/> only refuses an
    ///   empty id.
    ///
    ///   <para><c>$identity:&lt;ordinal&gt;</c> holds one canonical claim key and uses DENSE ordinals from
    ///   zero, because the property surface accepts scalars and no array, and a structured value does not
    ///   survive a reload with its type, so a set is not expressible. The ordinal is an encoding detail
    ///   nothing reads and nothing may depend on.</para>
    ///
    ///   <para><c>$claim:&lt;instanceId&gt;</c> keys the property by the CLAIMANT and its value carries no
    ///   timestamp. There is no compare-and-set anywhere in the REST contract, so a read-modify-write over
    ///   a shared property silently loses a concurrent writer; with the claimant in the key, two
    ///   integrations asserting one device never touch the same property and withdrawal is an idempotent,
    ///   replay-safe remove. A last-confirmed stamp would rewrite every claim property on every run and
    ///   make the zero-mutation invariant false by construction, while repeating the id as the VALUE means
    ///   one index lookup answers "every element this instance claims".</para>
    ///
    ///   <para>The <c>$</c> sigil follows the engine's convention for reserved keys
    ///   (<c>$embedding:</c>), and the validator rejects any provider-supplied key beginning with it: a
    ///   provider writing one would be forging a claim or a claim set.</para>
    /// </summary>
    public static class ClaimSchema
    {
        /// <summary>The sigil every reserved key begins with.</summary>
        public const String ReservedSigil = "$";

        /// <summary>The prefix of the canonical-claim-key properties.</summary>
        public const String IdentityPrefix = "$identity:";

        /// <summary>The prefix of the claimant properties.</summary>
        public const String ClaimPrefix = "$claim:";

        /// <summary>The index projecting <see cref="IdentityPrefix"/>: claim key to element ids.</summary>
        public const String IdentityIndexId = "f8i-identity";

        /// <summary>The index projecting <see cref="ClaimPrefix"/>: instance id to element ids.</summary>
        public const String ClaimsIndexId = "f8i-claims";

        /// <summary>
        ///   The longest an integration instance id may be. The value is substituted into a property key
        ///   and a claim key, so it is bounded as well as shape-checked.
        /// </summary>
        public const Int32 MaxInstanceIdLength = 64;

        /// <summary>The property holding the claim key at <paramref name="ordinal"/>.</summary>
        public static String IdentityProperty(Int32 ordinal)
        {
            if (ordinal < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, "Identity ordinals start at zero.");
            }

            return IdentityPrefix + ordinal.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>The property by which <paramref name="instanceId"/> asserts an element.</summary>
        public static String ClaimProperty(String instanceId)
        {
            if (String.IsNullOrEmpty(instanceId))
            {
                throw new ArgumentException("An instance id is required.", nameof(instanceId));
            }

            return ClaimPrefix + instanceId;
        }

        /// <summary>Whether a key is one this runtime reserves for itself.</summary>
        public static Boolean IsReserved(String? key)
        {
            return key != null && key.StartsWith(ReservedSigil, StringComparison.Ordinal);
        }

        /// <summary>Whether a key holds a canonical claim key.</summary>
        public static Boolean IsIdentityProperty(String? key)
        {
            return key != null && key.StartsWith(IdentityPrefix, StringComparison.Ordinal);
        }

        /// <summary>Whether a key asserts a claimant.</summary>
        public static Boolean IsClaimProperty(String? key)
        {
            return key != null && key.StartsWith(ClaimPrefix, StringComparison.Ordinal);
        }

        /// <summary>The claimant a <c>$claim:</c> key names, or null for any other key.</summary>
        public static String? ClaimantOf(String? key)
        {
            return IsClaimProperty(key) ? key!.Substring(ClaimPrefix.Length) : null;
        }

        /// <summary>
        ///   Validates an integration instance id as an ALLOW-LIST: letters, digits, dot, dash, underscore,
        ///   at most <see cref="MaxInstanceIdLength"/> characters. A colon, at sign, pipe or dollar would
        ///   let two identities compose one identical key - derived edge keys join their parts with a pipe
        ///   and claim keys use a colon and an at sign - and one run would then resolve into and reconcile
        ///   away another integration's elements.
        ///
        ///   <para>Note what this CANNOT check: whether the id is the one this integration has always used.
        ///   That stability is the caller's, and neither a fresh id per run (which leaves every run's
        ///   elements claimed by an identity no later run knows about, so the graph accumulates orphans
        ///   nothing will ever withdraw) nor a reused one (which inherits everything the other identity
        ///   claimed and, being a complete snapshot that does not mention them, withdraws and deletes them)
        ///   is detectable from inside.</para>
        /// </summary>
        public static Boolean IsValidInstanceId(String? instanceId)
        {
            if (String.IsNullOrEmpty(instanceId) || instanceId!.Length > MaxInstanceIdLength)
            {
                return false;
            }

            foreach (var character in instanceId)
            {
                var allowed = (character >= 'a' && character <= 'z') ||
                              (character >= 'A' && character <= 'Z') ||
                              (character >= '0' && character <= '9') ||
                              character == '.' || character == '-' || character == '_';
                if (!allowed)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
