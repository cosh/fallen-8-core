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
using NoSQL.GraphDB.Integrations.Graph;

namespace NoSQL.GraphDB.Integrations.Run
{
    /// <summary>
    ///   Everything one run needs, and nothing that outlives it. There is no interval, no floor, no enable
    ///   step, no run history and no instance store: a runtime holding a schedule would own a second copy of a
    ///   decision only whoever wants the data can make, in a place with no way to know what the data is for.
    ///
    ///   <para>A job carrying a credential is a secret in a document. The runtime keeps none of it, but the
    ///   caller is holding one for as long as they keep the body, so a job is not a thing to save.</para>
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
        ///   The credential ITSELF, per credential setting, which is the only way one arrives
        ///   (<see cref="Credentials.CredentialResolver" /> owns why).
        ///
        ///   <para>The cost is real and belongs to the caller, not the runtime: the value travels in this request,
        ///   so that hop wants TLS, and whatever composed the request is holding a secret for as long as it keeps
        ///   the body. A job carrying one is therefore not a job to save.</para>
        ///
        ///   <para>It is its own map because a credential may never arrive as a <c>setting</c>: a setting is
        ///   neither leased nor redacted, so a value there would be logged and reported like any other.</para>
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
        ///   Folds both maps case-insensitively before anything looks in them.
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

            var credentials = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in CredentialValues ?? new Dictionary<String, String>(StringComparer.Ordinal))
            {
                if (String.IsNullOrWhiteSpace(pair.Key))
                {
                    failure = "A supplied credential has no setting key.";
                    return false;
                }

                if (credentials.ContainsKey(pair.Key))
                {
                    failure = String.Format(
                        "Two supplied credentials differ only in case ('{0}').", pair.Key);
                    return false;
                }

                credentials[pair.Key] = pair.Value ?? String.Empty;
            }

            // The instance id is FOLDED TO LOWERCASE, and this is the one normalisation that protects data
            // rather than lookups. Every claim key is composed with the instance id and compared ordinally, so
            // "Office" and "office" are two identities; but the run gate that serialises runs of one identity is
            // case-INSENSITIVE, so the two never even collide there. The result of typing the other case once is
            // a silently forked identity: the new one claims nothing, so it duplicates every element, and the old
            // one is never reconciled again, so everything it claimed is orphaned. Folding here makes the two
            // spellings the same identity everywhere - keys, gate and reconciliation - which is what a reader
            // assumes when they retype a name. Done at the boundary, once, so no later comparison has to
            // remember. (v1: there are no legacy graphs carrying a mixed-case identity to preserve.)
            var instanceId = IntegrationInstanceId?.ToLowerInvariant();

            normalized = new NormalizedJob(ProviderId, instanceId, Namespace, settings, credentials,
                EmbedSummaries, EmbeddingName);
            failure = null;
            return true;
        }
    }

    /// <summary>
    ///   A job whose two maps have been folded, so every later lookup is case-insensitive by construction
    ///   rather than by hope.
    /// </summary>
    public sealed class NormalizedJob
    {
        internal NormalizedJob(String? providerId, String? instanceId, String? namespaceName,
            IReadOnlyDictionary<String, String> settings, IReadOnlyDictionary<String, String> credentials,
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

        /// <summary>The folded credential values, keyed by credential setting.</summary>
        public IReadOnlyDictionary<String, String> Credentials { get; }

        /// <summary>Whether this instance opted into embedding its entity summaries.</summary>
        public Boolean EmbedSummaries { get; }

        /// <summary>The named embedding summaries are written as.</summary>
        public String EmbeddingName { get; }
    }
}
