// MIT License
//
// IntegrationJob.cs
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
using NoSQL.GraphDB.Integrations.Credentials;
using NoSQL.GraphDB.Integrations.Graph;

namespace NoSQL.GraphDB.Integrations.Run
{
    /// <summary>
    ///   Everything one run needs, and nothing that outlives it. There is no interval, no floor, no enable
    ///   step, no run history and no instance store: a runtime holding a schedule would own a second copy of a
    ///   decision only whoever wants the data can make, in a place with no way to know what the data is for.
    ///
    ///   <para>A job naming its credentials is safe to keep, to commit next to whatever submits it, and to read
    ///   back as a record of what was asked for. A job CARRYING one is a secret in a document: still held only
    ///   for the run, but no longer a thing to save. Which of the two a caller submits is the caller's call, and
    ///   the difference is documented on <see cref="CredentialSource" />.</para>
    /// </summary>
    public sealed class IntegrationJob
    {
        /// <summary>Which integration to run, from <c>GET /integration/providers</c>.</summary>
        [JsonPropertyName("providerId")]
        public String? ProviderId { get; set; }

        /// <summary>
        ///   The identity this run asserts as. THE CALLER OWNS ITS STABILITY, and nothing can validate that:
        ///   every element a run creates carries a claim keyed on it, instance-scoped identifiers embed it, and
        ///   reconciliation is a set difference against everything it claimed before. A fresh identity per run
        ///   leaves every run's elements claimed by an identity no later run knows about, so the graph
        ///   accumulates orphans nothing will ever withdraw; a reused identity inherits everything the other
        ///   one claimed and, being a complete snapshot that does not mention them, withdraws and deletes them.
        ///   Neither is detectable from inside.
        /// </summary>
        [JsonPropertyName("integrationInstanceId")]
        public String? IntegrationInstanceId { get; set; }

        /// <summary>The namespace to write into, defaulting to the target's configured default.</summary>
        [JsonPropertyName("namespace")]
        public String? Namespace { get; set; }

        /// <summary>The provider's non-credential settings, keyed as its descriptor declares them.</summary>
        [JsonPropertyName("settings")]
        public IDictionary<String, Object?> Settings { get; set; }
            = new Dictionary<String, Object?>(StringComparer.Ordinal);

        /// <summary>
        ///   Which credential each credential setting uses, BY NAME, read from the runtime's credential mount
        ///   when the run starts. The source to prefer for anything that runs unattended: rotating one is
        ///   overwriting a file, and the job itself keeps no secret.
        /// </summary>
        [JsonPropertyName("credentials")]
        public IDictionary<String, String> Credentials { get; set; }
            = new Dictionary<String, String>(StringComparer.Ordinal);

        /// <summary>
        ///   The credential ITSELF, per credential setting, for a caller who has the value in hand and nowhere to
        ///   put it - a person at a form, most of all. It is leased, redacted, fingerprinted and dropped exactly
        ///   as a named one is, and it is written down nowhere: the runtime keeps no job history, and no route
        ///   reads a job back.
        ///
        ///   <para>The cost is real and belongs to the caller, not the runtime: the value travels in this request,
        ///   so that hop wants TLS, and whatever composed the request is holding a secret for as long as it keeps
        ///   the body. A setting is still never a place for one - a setting is neither leased nor redacted - which
        ///   is why this is its own map rather than a value in <c>settings</c>.</para>
        /// </summary>
        [JsonPropertyName("credentialValues")]
        public IDictionary<String, String> CredentialValues { get; set; }
            = new Dictionary<String, String>(StringComparer.Ordinal);

        /// <summary>
        ///   The INSTANCE half of the embedding opt-in, default off. A provider declaring an entity summary
        ///   template is the other half, and neither alone embeds anything: embedding every client on a busy
        ///   network by default is cost and noise in equal measure.
        /// </summary>
        [JsonPropertyName("embedSummaries")]
        public Boolean EmbedSummaries { get; set; }

        /// <summary>
        ///   Which named embedding the summaries are written as. The graph's own convention is <c>default</c>,
        ///   which is the name a vector index is usually bound to; only elements this integration claims are ever
        ///   written to, so nothing another feature embedded is touched.
        /// </summary>
        [JsonPropertyName("embeddingName")]
        public String EmbeddingName { get; set; } = "default";

        /// <summary>
        ///   Folds every map case-insensitively before anything looks in them, and collapses the two credential
        ///   maps into ONE source per setting.
        ///
        ///   <para>A job arrives as JSON and deserialising into a dictionary yields an ORDINAL comparer
        ///   whatever the initialiser says, so <c>Password</c> would slip past a lookup for <c>password</c> and
        ///   defeat the credential-in-a-setting guard with the shift key. Folding also turns two keys differing
        ///   only in case into a REJECTION here instead of a duplicate-key throw further in.</para>
        /// </summary>
        public Boolean TryNormalize(out NormalizedJob? normalized, out String? failure)
        {
            normalized = null;

            var settings = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in Settings ?? new Dictionary<String, Object?>(StringComparer.Ordinal))
            {
                if (String.IsNullOrWhiteSpace(pair.Key))
                {
                    failure = "A setting has no key.";
                    return false;
                }

                if (settings.ContainsKey(pair.Key))
                {
                    failure = String.Format(
                        "Two settings differ only in case ('{0}'), which cannot be told apart once folded.",
                        pair.Key);
                    return false;
                }

                // A setting's value is text by the time a provider sees it, so a number or a boolean in the
                // JSON is rendered exactly as the graph would render it rather than through ToString().
                if (pair.Value == null)
                {
                    continue;
                }

                var rendered = WireValues.TryRender(pair.Value, out _, out var text);
                if (rendered == WireValues.Outcome.Absent)
                {
                    // A setting the caller sent as null is a setting the caller did not send.
                    continue;
                }

                if (rendered != WireValues.Outcome.Rendered || text == null)
                {
                    failure = String.Format(
                        "Setting '{0}' is not a value a setting can carry; settings are scalars.", pair.Key);
                    return false;
                }

                settings[pair.Key] = text;
            }

            var credentials = new Dictionary<String, CredentialSource>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in Credentials ?? new Dictionary<String, String>(StringComparer.Ordinal))
            {
                if (String.IsNullOrWhiteSpace(pair.Key))
                {
                    failure = "A credential mapping has no setting key.";
                    return false;
                }

                if (credentials.ContainsKey(pair.Key))
                {
                    failure = String.Format(
                        "Two credential mappings differ only in case ('{0}').", pair.Key);
                    return false;
                }

                credentials[pair.Key] = CredentialSource.Named(pair.Value);
            }

            foreach (var pair in CredentialValues ?? new Dictionary<String, String>(StringComparer.Ordinal))
            {
                if (String.IsNullOrWhiteSpace(pair.Key))
                {
                    failure = "A supplied credential has no setting key.";
                    return false;
                }

                // Two sources for one setting is a REJECTION rather than a precedence rule: a caller who filled a
                // form and also kept a stale name would otherwise be authenticating with whichever one this loop
                // happened to visit second, and no report could tell them which.
                if (credentials.TryGetValue(pair.Key, out var existing))
                {
                    failure = existing.IsInline
                        ? String.Format("Two supplied credentials differ only in case ('{0}').", pair.Key)
                        : String.Format(
                            "Credential setting '{0}' has both a credential NAME and a credential VALUE. Supply " +
                            "one source: the name of a credential in the runtime's mount, or the value itself.",
                            pair.Key);
                    return false;
                }

                credentials[pair.Key] = CredentialSource.Inline(pair.Value ?? String.Empty);
            }

            normalized = new NormalizedJob(ProviderId, IntegrationInstanceId, Namespace, settings, credentials,
                EmbedSummaries, EmbeddingName);
            failure = null;
            return true;
        }
    }

    /// <summary>
    ///   A job whose maps have been folded, so every later lookup is case-insensitive by construction rather
    ///   than by hope, and whose credentials are one source per setting rather than two maps to reconcile.
    /// </summary>
    public sealed class NormalizedJob
    {
        internal NormalizedJob(String? providerId, String? instanceId, String? namespaceName,
            IReadOnlyDictionary<String, String> settings,
            IReadOnlyDictionary<String, CredentialSource> credentials,
            Boolean embedSummaries, String embeddingName)
        {
            ProviderId = providerId;
            InstanceId = instanceId;
            Namespace = namespaceName;
            Settings = settings;
            Credentials = credentials;
            EmbedSummaries = embedSummaries;
            EmbeddingName = embeddingName;
        }

        /// <summary>Which integration to run.</summary>
        public String? ProviderId { get; }

        /// <summary>The identity this run asserts as.</summary>
        public String? InstanceId { get; }

        /// <summary>The namespace to write into, or null for the target's default.</summary>
        public String? Namespace { get; }

        /// <summary>The folded settings.</summary>
        public IReadOnlyDictionary<String, String> Settings { get; }

        /// <summary>Where each credential setting's value comes from, keyed by credential setting.</summary>
        public IReadOnlyDictionary<String, CredentialSource> Credentials { get; }

        /// <summary>Whether this instance opted into embedding its entity summaries.</summary>
        public Boolean EmbedSummaries { get; }

        /// <summary>The named embedding summaries are written as.</summary>
        public String EmbeddingName { get; }
    }
}
