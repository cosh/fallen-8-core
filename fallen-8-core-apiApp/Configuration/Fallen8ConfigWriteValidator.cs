// MIT License
//
// Fallen8ConfigWriteValidator.cs
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
using System.Globalization;
using System.Linq;
using Microsoft.Extensions.Configuration;

namespace NoSQL.GraphDB.App.Configuration
{
    /// <summary>
    ///   Decides whether a batch of configuration writes may be stored (feature
    ///   writable-instance-config). Every key is judged before ANY is stored, following the
    ///   <c>PATCH /ns/{name}</c> precedent, so a batch applies whole or changes nothing.
    ///
    ///   <para>This is the only validation a written value ever passes. Nothing in this application
    ///   implements <c>IValidateOptions</c>, and configuration binding neither rejects an unknown string
    ///   nor an out-of-domain number, so a value that gets past here reaches the running process.</para>
    /// </summary>
    public static class Fallen8ConfigWriteValidator
    {
        /// <summary>Why a batch was refused, and with which status.</summary>
        public sealed class Refusal
        {
            internal Refusal(Boolean isConflict, String detail)
            {
                IsConflict = isConflict;
                Detail = detail;
            }

            /// <summary>
            ///   True when the batch is well formed but the server's own configuration state prevents it
            ///   (a 409), false when the request itself is wrong (a 400).
            /// </summary>
            public Boolean IsConflict { get; }

            /// <summary>The operator-facing explanation, naming every offending key.</summary>
            public String Detail { get; }
        }

        /// <summary>
        ///   Validates a batch. On success <paramref name="refusal" /> is null and every key in
        ///   <paramref name="requested" /> is safe to store.
        /// </summary>
        public static Boolean TryValidate(IReadOnlyDictionary<String, String> requested,
            Fallen8ConfigOverrides overrides, out Refusal refusal)
        {
            if (requested == null || requested.Count == 0)
            {
                refusal = new Refusal(false, "No settings were supplied.");
                return false;
            }

            // Refusals are collected rather than short-circuited: an operator fixing a batch of ten keys
            // should learn about all ten problems from one response, not one per round trip.
            var rejected = new List<String>();
            var conflicts = new List<String>();

            foreach (var pair in requested.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (!Fallen8SettingCatalog.TryGet(pair.Key, out var entry))
                {
                    rejected.Add("'" + pair.Key + "' is not a configuration key this instance binds.");
                    continue;
                }

                if (!entry.IsWritable)
                {
                    rejected.Add("'" + entry.Key + "' can never be written over REST (" + entry.Rule + "): "
                        + entry.Reason);
                    continue;
                }

                // A value that an environment variable or the command line declares could be stored, but
                // it would never take effect. Storing it anyway would be a time bomb that arms the day the
                // operator removes the variable, so the write is refused instead and says what to remove.
                if (overrides != null && overrides.IsAuthorityDeclared(entry.Key))
                {
                    conflicts.Add("'" + entry.Key + "' is declared in the environment or on the command line ("
                        + Fallen8ConfigOverrides.EnvironmentSpelling(entry.Key)
                        + "), which outranks stored configuration. Remove it there to manage this setting here.");
                    continue;
                }

                if (pair.Value == null)
                {
                    continue; // clearing an override is always in domain
                }

                var problem = ValidateValue(entry, pair.Value);
                if (problem != null)
                {
                    rejected.Add(problem);
                }
            }

            if (rejected.Count > 0)
            {
                refusal = new Refusal(false, String.Join(" ", rejected));
                return false;
            }

            if (conflicts.Count > 0)
            {
                refusal = new Refusal(true, String.Join(" ", conflicts));
                return false;
            }

            refusal = null;
            return true;
        }

        /// <summary>
        ///   Whether a value is inside the key's declared domain, and parseable as its kind. The bounds
        ///   here are the ONLY thing standing between an operator and a value that breaks a subsystem at
        ///   the next boot, so the checks are exhaustive rather than indicative.
        /// </summary>
        private static String ValidateValue(Fallen8SettingEntry entry, String value)
        {
            switch (entry.Kind)
            {
                case Fallen8SettingKind.Bool:
                    return Boolean.TryParse(value, out _)
                        ? null
                        : "'" + entry.Key + "' takes true or false, not '" + value + "'.";

                case Fallen8SettingKind.Int:
                    if (!Int64.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integral))
                    {
                        return "'" + entry.Key + "' takes a whole number, not '" + value + "'.";
                    }

                    return OutOfRange(entry, integral);

                case Fallen8SettingKind.Double:
                    if (!Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var real))
                    {
                        return "'" + entry.Key + "' takes a number, not '" + value + "'.";
                    }

                    return OutOfRange(entry, real);

                case Fallen8SettingKind.Enum:
                    // Ordinal, because the consumers that switch on these values match ordinally: accepting
                    // a case variant here would store a value the runtime then refuses.
                    return entry.AllowedValues.Contains(value, StringComparer.Ordinal)
                        ? null
                        : "'" + entry.Key + "' takes one of " + String.Join(", ", entry.AllowedValues)
                            + ", not '" + value + "'.";

                case Fallen8SettingKind.String:
                    return null;

                default:
                    // Array, which no writable entry can carry (the entry factory refuses it).
                    return "'" + entry.Key + "' is not a kind this surface can write.";
            }
        }

        private static String OutOfRange(Fallen8SettingEntry entry, Double value)
        {
            if (entry.Minimum.HasValue && value < entry.Minimum.Value)
            {
                return "'" + entry.Key + "' must be at least "
                    + entry.Minimum.Value.ToString("0.####", CultureInfo.InvariantCulture) + ".";
            }

            if (entry.Maximum.HasValue && value > entry.Maximum.Value)
            {
                return "'" + entry.Key + "' must be at most "
                    + entry.Maximum.Value.ToString("0.####", CultureInfo.InvariantCulture) + ".";
            }

            return null;
        }

        /// <summary>
        ///   Trial-binds a batch against a throwaway configuration root, so a value that binding itself
        ///   would reject is refused before anything is persisted. This catches what the domain checks
        ///   cannot see, for instance a number too large for the property's own type.
        /// </summary>
        public static Boolean TryTrialBind(IReadOnlyDictionary<String, String> requested, out Refusal refusal)
        {
            var supplied = requested
                .Where(pair => pair.Value != null)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            if (supplied.Count == 0)
            {
                refusal = null;
                return true;
            }

            var trial = new ConfigurationBuilder().AddInMemoryCollection(supplied).Build();
            var failures = new List<String>();

            foreach (var section in supplied.Keys
                .Select(SectionOf)
                .Where(section => section != null)
                .Distinct(StringComparer.Ordinal))
            {
                var type = Fallen8OptionsSections.TypeOf(section);
                if (type == null)
                {
                    continue;
                }

                try
                {
                    trial.GetSection(section).Get(type);
                }
                catch (InvalidOperationException exception)
                {
                    failures.Add(exception.Message);
                }
            }

            refusal = failures.Count == 0 ? null : new Refusal(false, String.Join(" ", failures));
            return refusal == null;
        }

        /// <summary>The <c>Fallen8:Section</c> prefix a key belongs to, or null when it has none.</summary>
        private static String SectionOf(String key)
        {
            var parts = key.Split(':');
            return parts.Length >= 3 ? parts[0] + ":" + parts[1] : null;
        }
    }
}
