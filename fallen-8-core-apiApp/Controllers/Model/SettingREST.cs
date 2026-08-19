// MIT License
//
// SettingREST.cs
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
using NoSQL.GraphDB.App.Configuration;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   One instance configuration setting as the operator surface sees it (feature
    ///   writable-instance-config): what the key is, whether it may be written, where its current value
    ///   comes from, and whether a written value is waiting for a restart.
    ///
    ///   <para><b>A never-writable key publishes no value.</b> It carries its key, tier, source, the
    ///   rule that excludes it and the reason, with <see cref="ValueWithheld"/> set and
    ///   <see cref="Value"/> absent from the payload entirely. GET /config carries neither
    ///   <c>[Authorize]</c> nor <c>[AllowAnonymous]</c>, and the fallback policy that would demand a
    ///   principal is installed only when an API key is configured, so on a keyless instance this
    ///   response is anonymous: publishing every value would hand sidecar URLs, model file paths and
    ///   durability paths to an unauthenticated caller.</para>
    ///
    ///   <para>Wire values are strings rather than enums deliberately. This application installs no
    ///   string-enum converter, so a .NET enum would serialize as 0, 1, 2 and the published contract
    ///   would carry integers whose meaning lives only in this assembly.</para>
    /// </summary>
    public sealed class SettingREST
    {
        /// <summary>
        ///   Projects one catalogued key, withholding the value unless the key is writable. Pass
        ///   <paramref name="effectiveValues" /> when projecting many keys at once: binding one options
        ///   class per key would otherwise bind every section 94 times per request.
        /// </summary>
        public static SettingREST From(Fallen8SettingEntry entry, Fallen8ConfigOverrides overrides,
            IReadOnlyDictionary<String, String> effectiveValues = null)
        {
            if (entry == null)
            {
                return null;
            }

            var published = new SettingREST
            {
                Key = entry.Key,
                Kind = WireKind(entry.Kind),
                Tier = WireTier(entry.Tier),
                ApplyMode = WireApplyMode(entry.ApplyMode),
                Source = WireSource(overrides?.SourceOf(entry.Key) ?? Fallen8SettingSource.Default),
                Rule = entry.Rule,
                Reason = entry.Reason,
                Minimum = entry.Minimum,
                Maximum = entry.Maximum,
                AllowedValues = entry.AllowedValues.Count == 0 ? null : new List<String>(entry.AllowedValues)
            };

            if (entry.IsWritable)
            {
                published.Value = effectiveValues != null && effectiveValues.TryGetValue(entry.Key, out var value)
                    ? value
                    : overrides?.CurrentValue(entry.Key);
                published.RestartPending = overrides?.IsRestartPending(entry) ?? false;
            }
            else
            {
                published.ValueWithheld = true;
            }

            return published;
        }

        /// <summary>The full configuration key, e.g. <c>Fallen8:Plugins:MaxCount</c>.</summary>
        [JsonPropertyName("key")]
        public String Key
        {
            get; set;
        }

        /// <summary>The value's type: bool, int, double, string, enum or array.</summary>
        [JsonPropertyName("kind")]
        public String Kind
        {
            get; set;
        }

        /// <summary>How the key may be written: live, restart or notWritable.</summary>
        [JsonPropertyName("tier")]
        public String Tier
        {
            get; set;
        }

        /// <summary>When a written value takes effect: live, liveForNewWork, restart or never.</summary>
        [JsonPropertyName("applyMode")]
        public String ApplyMode
        {
            get; set;
        }

        /// <summary>
        ///   The current effective value as configuration text, absent entirely for a never-writable key
        ///   (see <see cref="ValueWithheld"/>) and null when no layer sets it, so the options class's own
        ///   default is in force.
        /// </summary>
        [JsonPropertyName("value")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public String Value
        {
            get; set;
        }

        /// <summary>
        ///   True when the value is deliberately not published because the key is never writable. Sent
        ///   only in that case, so it is never confused with a key whose value is genuinely unset.
        /// </summary>
        [JsonPropertyName("valueWithheld")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public Boolean ValueWithheld
        {
            get; set;
        }

        /// <summary>
        ///   Which layer supplies the current value: default, appSettings, userSecrets, environment,
        ///   commandLine, host or override. Exactly two of those, environment and commandLine, mean a
        ///   stored override can never win, and the editor renders the row read-only for that reason;
        ///   every other source is a layer a write can beat.
        /// </summary>
        [JsonPropertyName("source")]
        public String Source
        {
            get; set;
        }

        /// <summary>
        ///   True when this key's effective value differs from the value this process started with, so a
        ///   restart is needed to apply it. Never true for a never-writable or live key.
        /// </summary>
        [JsonPropertyName("restartPending")]
        public Boolean RestartPending
        {
            get; set;
        }

        /// <summary>The inclusive lower bound for a numeric key, null when unbounded below.</summary>
        [JsonPropertyName("minimum")]
        public Double? Minimum
        {
            get; set;
        }

        /// <summary>The inclusive upper bound for a numeric key, null when unbounded above.</summary>
        [JsonPropertyName("maximum")]
        public Double? Maximum
        {
            get; set;
        }

        /// <summary>The closed set of accepted values for an enum key, null otherwise.</summary>
        [JsonPropertyName("allowedValues")]
        public List<String> AllowedValues
        {
            get; set;
        }

        /// <summary>The rule that excludes a never-writable key (R1 to R6, or 4.3.5), null otherwise.</summary>
        [JsonPropertyName("rule")]
        public String Rule
        {
            get; set;
        }

        /// <summary>Why a never-writable key is excluded, null otherwise.</summary>
        [JsonPropertyName("reason")]
        public String Reason
        {
            get; set;
        }

        private static String WireKind(Fallen8SettingKind kind)
        {
            switch (kind)
            {
                case Fallen8SettingKind.Bool:
                    return "bool";
                case Fallen8SettingKind.Int:
                    return "int";
                case Fallen8SettingKind.Double:
                    return "double";
                case Fallen8SettingKind.String:
                    return "string";
                case Fallen8SettingKind.Enum:
                    return "enum";
                case Fallen8SettingKind.Array:
                    return "array";
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, "unpublished setting kind");
            }
        }

        private static String WireTier(Fallen8SettingTier tier)
        {
            switch (tier)
            {
                case Fallen8SettingTier.NotWritable:
                    return "notWritable";
                case Fallen8SettingTier.Restart:
                    return "restart";
                case Fallen8SettingTier.Live:
                    return "live";
                default:
                    throw new ArgumentOutOfRangeException(nameof(tier), tier, "unpublished setting tier");
            }
        }

        private static String WireApplyMode(Fallen8SettingApplyMode mode)
        {
            switch (mode)
            {
                case Fallen8SettingApplyMode.Never:
                    return "never";
                case Fallen8SettingApplyMode.Restart:
                    return "restart";
                case Fallen8SettingApplyMode.Live:
                    return "live";
                case Fallen8SettingApplyMode.LiveForNewWork:
                    return "liveForNewWork";
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, "unpublished apply mode");
            }
        }

        private static String WireSource(Fallen8SettingSource source)
        {
            switch (source)
            {
                case Fallen8SettingSource.Default:
                    return "default";
                case Fallen8SettingSource.AppSettings:
                    return "appSettings";
                case Fallen8SettingSource.UserSecrets:
                    return "userSecrets";
                case Fallen8SettingSource.Environment:
                    return "environment";
                case Fallen8SettingSource.CommandLine:
                    return "commandLine";
                case Fallen8SettingSource.Host:
                    return "host";
                case Fallen8SettingSource.Override:
                    return "override";
                default:
                    throw new ArgumentOutOfRangeException(nameof(source), source, "unpublished setting source");
            }
        }
    }

    /// <summary>
    ///   One key whose written value is waiting for a restart, disclosed so the operator can see what a
    ///   restart would change rather than only that something would.
    /// </summary>
    public sealed class PendingRestartREST
    {
        /// <summary>Projects a pending key, naming both the running and the waiting value.</summary>
        public static PendingRestartREST From(Fallen8SettingEntry entry, Fallen8ConfigOverrides overrides)
        {
            return new PendingRestartREST
            {
                Key = entry.Key,
                RunningValue = overrides?.BootValue(entry.Key),
                PendingValue = overrides?.CurrentValue(entry.Key)
            };
        }

        /// <summary>The configuration key.</summary>
        [JsonPropertyName("key")]
        public String Key
        {
            get; set;
        }

        /// <summary>The value this process started with and is still using.</summary>
        [JsonPropertyName("runningValue")]
        public String RunningValue
        {
            get; set;
        }

        /// <summary>The value configured now, which the next boot will use.</summary>
        [JsonPropertyName("pendingValue")]
        public String PendingValue
        {
            get; set;
        }
    }
}
