// MIT License
//
// Fallen8SettingEntry.cs
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
using System.Collections.ObjectModel;
using System.Linq;

namespace NoSQL.GraphDB.App.Configuration
{
    /// <summary>
    ///   The type of a catalogued configuration value, which decides how a written value is parsed
    ///   and validated and which editor control renders it.
    /// </summary>
    public enum Fallen8SettingKind
    {
        /// <summary>A <see cref="Boolean"/> leaf.</summary>
        Bool,

        /// <summary>An integral leaf (<see cref="Int32"/> or <see cref="Int64"/>).</summary>
        Int,

        /// <summary>A <see cref="Double"/> leaf.</summary>
        Double,

        /// <summary>A free-form string leaf.</summary>
        String,

        /// <summary>A string leaf whose accepted values are a closed set.</summary>
        Enum,

        /// <summary>A collection leaf. Never writable, see <see cref="Fallen8SettingEntry"/>.</summary>
        Array
    }

    /// <summary>
    ///   When a written value takes effect. This is the ONE field the catalog stores about
    ///   writability; <see cref="Fallen8SettingEntry.Tier"/> is derived from it, so an entry can
    ///   never claim a tier its apply semantics contradict.
    /// </summary>
    public enum Fallen8SettingApplyMode
    {
        /// <summary>Never: the key is not writable over REST at all.</summary>
        Never,

        /// <summary>Persisted now, applies at the next boot.</summary>
        Restart,

        /// <summary>Takes effect for everything, immediately.</summary>
        Live,

        /// <summary>
        ///   Takes effect for NEW work only, immediately. Reserved for caps that are consulted when
        ///   work starts and never re-checked afterwards, so lowering one leaves existing holders
        ///   untouched. Reporting those as plainly applied would be the silently-did-not-apply
        ///   defect this catalog exists to prevent.
        /// </summary>
        LiveForNewWork
    }

    /// <summary>
    ///   How a catalogued setting may be written. Derived from
    ///   <see cref="Fallen8SettingEntry.ApplyMode"/> rather than stored, and the three values the
    ///   read surface publishes.
    /// </summary>
    public enum Fallen8SettingTier
    {
        /// <summary>Not writable over REST; the catalog carries the reason.</summary>
        NotWritable,

        /// <summary>Writable; takes effect at the next boot.</summary>
        Restart,

        /// <summary>Writable; takes effect in this process.</summary>
        Live
    }

    /// <summary>
    ///   One catalogued configuration leaf key (feature writable-instance-config). Instances are
    ///   created through <see cref="NotWritable"/>, <see cref="Restart"/>, <see cref="Live"/> and
    ///   <see cref="LiveForNewWork"/>, which is what makes the catalog's invariants structural:
    ///   a never-writable entry cannot be given an apply delegate, a writable entry cannot be given
    ///   an exclusion reason, and no entry can declare a tier its apply mode contradicts.
    ///
    ///   <para><b>The catalog deliberately carries no description of what a key MEANS.</b> That
    ///   lives on the options property's XML documentation, which the OpenAPI pipeline already
    ///   publishes, and a second copy here would be the multi-home duplication this repository
    ///   forbids. <see cref="Reason"/> is not an exception: an exclusion rationale is a property of
    ///   this catalog's decision, not of the key, and it has no other home.</para>
    /// </summary>
    public sealed class Fallen8SettingEntry
    {
        private Fallen8SettingEntry(String key, Fallen8SettingKind kind, Fallen8SettingApplyMode applyMode,
            String rule, String reason, Double? minimum, Double? maximum, IReadOnlyList<String> allowedValues,
            Action<IServiceProvider> applyNow)
        {
            if (String.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("a catalogued entry needs a key", nameof(key));
            }

            Key = key;
            Kind = kind;
            ApplyMode = applyMode;
            Rule = rule;
            Reason = reason;
            Minimum = minimum;
            Maximum = maximum;
            // Copied and wrapped, so a caller that downcasts cannot rewrite the accepted values of a
            // key after the catalog has published them.
            AllowedValues = new ReadOnlyCollection<String>((allowedValues ?? Array.Empty<String>()).ToArray());
            ApplyNow = applyNow;
        }

        /// <summary>The full colon-delimited configuration key, e.g. <c>Fallen8:Plugins:MaxCount</c>.</summary>
        public String Key { get; }

        /// <summary>The value's type.</summary>
        public Fallen8SettingKind Kind { get; }

        /// <summary>When a written value takes effect.</summary>
        public Fallen8SettingApplyMode ApplyMode { get; }

        /// <summary>
        ///   How the key may be written, derived from <see cref="ApplyMode"/>.
        /// </summary>
        public Fallen8SettingTier Tier
        {
            get
            {
                // Every mode is listed and the default throws: a mode added later must state its tier
                // here rather than inherit one. Defaulting would fail OPEN, quietly promoting a new
                // mode into the writable-and-live tier.
                switch (ApplyMode)
                {
                    case Fallen8SettingApplyMode.Never:
                        return Fallen8SettingTier.NotWritable;
                    case Fallen8SettingApplyMode.Restart:
                        return Fallen8SettingTier.Restart;
                    case Fallen8SettingApplyMode.Live:
                    case Fallen8SettingApplyMode.LiveForNewWork:
                        return Fallen8SettingTier.Live;
                    default:
                        throw new InvalidOperationException("unclassified apply mode: " + ApplyMode);
                }
            }
        }

        /// <summary>Whether a write may target this key at all.</summary>
        public Boolean IsWritable => Tier != Fallen8SettingTier.NotWritable;

        /// <summary>
        ///   The never-writable rule this key falls under (<c>R1</c> to <c>R7</c>, or <c>4.3.5</c> for
        ///   a collection leaf), or <c>null</c> for a writable key.
        /// </summary>
        public String Rule { get; }

        /// <summary>
        ///   Why the key is not writable, published to operators. <c>null</c> for a writable key.
        /// </summary>
        public String Reason { get; }

        /// <summary>
        ///   Inclusive lower bound for a numeric key, or <c>null</c> when the key is unbounded below.
        ///   Both bounds are <see cref="Double"/> so one pair covers integral and floating keys; every
        ///   bound in the catalog is far below the 2^53 exact-integer limit.
        /// </summary>
        public Double? Minimum { get; }

        /// <summary>Inclusive upper bound, or <c>null</c> when the key is unbounded above.</summary>
        public Double? Maximum { get; }

        /// <summary>
        ///   The closed set of accepted values for an <see cref="Fallen8SettingKind.Enum"/> key, empty
        ///   otherwise.
        ///
        ///   <para>This is the one home for why the catalog's domain data is load-bearing rather than
        ///   decorative. Nothing in this app implements <c>IValidateOptions</c>, and binding neither
        ///   rejects an unknown string nor an out-of-domain number, so for a writable key the accepted
        ///   values here (and <see cref="Minimum"/> / <see cref="Maximum"/>) are the ONLY gate between
        ///   an operator and a value that breaks a subsystem. For a key whose consumer exact-matches
        ///   and then throws, this set is what answers 400 instead of latching a permanent 503.</para>
        /// </summary>
        public IReadOnlyList<String> AllowedValues { get; }

        /// <summary>
        ///   Pushes a written value into the running process, for a live key only. Reads the
        ///   freshly bound options off the service provider (the write path reloads configuration
        ///   before calling this), so no typed value plumbing reaches the catalog. <c>null</c> for
        ///   every non-live key, and non-null for every live one, both enforced by the governance
        ///   test.
        /// </summary>
        public Action<IServiceProvider> ApplyNow { get; }

        /// <summary>
        ///   A key that must never be written over REST, with the rule that excludes it and a
        ///   one-sentence reason naming the concrete hazard.
        /// </summary>
        public static Fallen8SettingEntry NotWritable(String key, Fallen8SettingKind kind, String rule, String reason)
        {
            if (String.IsNullOrWhiteSpace(rule))
            {
                throw new ArgumentException("a never-writable key must name the rule that excludes it", nameof(rule));
            }

            if (String.IsNullOrWhiteSpace(reason))
            {
                throw new ArgumentException("a never-writable key must carry a reason", nameof(reason));
            }

            return new Fallen8SettingEntry(key, kind, Fallen8SettingApplyMode.Never, rule, reason,
                null, null, null, null);
        }

        /// <summary>A writable key that takes effect at the next boot.</summary>
        public static Fallen8SettingEntry Restart(String key, Fallen8SettingKind kind,
            Double? minimum = null, Double? maximum = null, IReadOnlyList<String> allowedValues = null)
        {
            return Writable(key, kind, Fallen8SettingApplyMode.Restart, minimum, maximum, allowedValues, null);
        }

        /// <summary>A writable key that takes effect immediately, for everything.</summary>
        public static Fallen8SettingEntry Live(String key, Fallen8SettingKind kind, Action<IServiceProvider> applyNow,
            Double? minimum = null, Double? maximum = null, IReadOnlyList<String> allowedValues = null)
        {
            return Writable(key, kind, Fallen8SettingApplyMode.Live, minimum, maximum, allowedValues,
                applyNow ?? throw new ArgumentNullException(nameof(applyNow)));
        }

        /// <summary>A writable key that takes effect immediately, for new work only.</summary>
        public static Fallen8SettingEntry LiveForNewWork(String key, Fallen8SettingKind kind,
            Action<IServiceProvider> applyNow, Double? minimum = null, Double? maximum = null,
            IReadOnlyList<String> allowedValues = null)
        {
            return Writable(key, kind, Fallen8SettingApplyMode.LiveForNewWork, minimum, maximum, allowedValues,
                applyNow ?? throw new ArgumentNullException(nameof(applyNow)));
        }

        private static Fallen8SettingEntry Writable(String key, Fallen8SettingKind kind,
            Fallen8SettingApplyMode applyMode, Double? minimum, Double? maximum,
            IReadOnlyList<String> allowedValues, Action<IServiceProvider> applyNow)
        {
            if (kind == Fallen8SettingKind.Array)
            {
                // Configuration providers merge arrays index-wise, so an override could overwrite
                // index 0 but never shrink or clear a longer environment-provided list (spec 4.3.5).
                throw new ArgumentException("a collection leaf is never writable: " + key, nameof(kind));
            }

            if ((minimum.HasValue || maximum.HasValue)
                && kind != Fallen8SettingKind.Int && kind != Fallen8SettingKind.Double)
            {
                throw new ArgumentException("bounds only apply to a numeric key: " + key, nameof(kind));
            }

            if (minimum.HasValue && maximum.HasValue && minimum.Value > maximum.Value)
            {
                throw new ArgumentException("the minimum exceeds the maximum: " + key, nameof(minimum));
            }

            var values = allowedValues ?? (IReadOnlyList<String>)Array.Empty<String>();
            if (kind == Fallen8SettingKind.Enum && values.Count == 0)
            {
                throw new ArgumentException("an enum key must carry its accepted values: " + key, nameof(allowedValues));
            }

            if (kind != Fallen8SettingKind.Enum && values.Count > 0)
            {
                throw new ArgumentException("only an enum key carries accepted values: " + key, nameof(allowedValues));
            }

            if (values.Distinct(StringComparer.Ordinal).Count() != values.Count)
            {
                throw new ArgumentException("the accepted values repeat: " + key, nameof(allowedValues));
            }

            return new Fallen8SettingEntry(key, kind, applyMode, null, null, minimum, maximum, values, applyNow);
        }
    }
}
