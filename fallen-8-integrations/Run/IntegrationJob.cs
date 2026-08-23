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
        ///   The FILE ITSELF, per file setting, which is the only way one arrives. The runtime opens
        ///   nothing on disk: it holds a file for the run that needs it and drops it when the run ends,
        ///   exactly as it holds a credential, so there is no mount to prepare, nothing staged to clean up
        ///   and no name for a caller to point somewhere it should not go.
        ///
        ///   <para>It is its own map for the same reason a credential is: a file's content may never
        ///   arrive as a <c>setting</c>, because a setting is a scalar a form types and this is a
        ///   document. What DOES land in <c>settings</c> is the file's name, put there by the runtime, so
        ///   a provider reads it for its messages exactly as it always has.</para>
        /// </summary>
        [JsonPropertyName("files")]
        public IDictionary<String, JobFile> Files { get; set; }
            = new Dictionary<String, JobFile>(StringComparer.Ordinal);

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
        ///   Folds all three maps case-insensitively before anything looks in them, and decodes the files
        ///   the job carried.
        ///
        ///   <para>A job arrives as JSON and deserialising into a dictionary yields an ORDINAL comparer
        ///   whatever the initialiser says, so <c>Password</c> would slip past a lookup for <c>password</c> and
        ///   defeat the credential-in-a-setting guard with the shift key. Folding also turns two keys differing
        ///   only in case into a REJECTION here instead of a duplicate-key throw further in.</para>
        ///
        ///   <para>A file is decoded HERE, before the run starts, so an unusable payload is a refusal the
        ///   caller can act on rather than a failure in the middle of a source read - by which point the
        ///   run has begun making withdrawal-relevant decisions. It is the same reason the credential
        ///   lease is resolved eagerly.</para>
        /// </summary>
        /// <param name="normalized">The folded job.</param>
        /// <param name="failure">Why the job cannot be run as written.</param>
        /// <param name="maxFileBytes">The per-file ceiling on DECODED bytes
        /// (<c>Integrations:MaxFileBytes</c>). Zero or less means no ceiling, which is what a caller
        /// constructing a job by hand in a test gets.</param>
        public Boolean TryNormalize(out NormalizedJob? normalized, out String? failure,
            Int64 maxFileBytes = 0)
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

            var files = new Dictionary<String, JobFilePayload>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in Files ?? new Dictionary<String, JobFile>(StringComparer.Ordinal))
            {
                if (String.IsNullOrWhiteSpace(pair.Key))
                {
                    failure = "A supplied file has no setting key.";
                    return false;
                }

                if (files.ContainsKey(pair.Key))
                {
                    failure = String.Format(
                        "Two supplied files differ only in case ('{0}').", pair.Key);
                    return false;
                }

                if (pair.Value == null)
                {
                    failure = String.Format(
                        "The file supplied for setting '{0}' is null. A file setting the job does not need " +
                        "is left out of 'files' rather than sent empty.", pair.Key);
                    return false;
                }

                if (!JobFiles.TryValidateName(pair.Value.Name, pair.Key, out var nameFailure))
                {
                    failure = nameFailure;
                    return false;
                }

                if (!TryDecodeContent(pair.Value.ContentBase64, pair.Key, maxFileBytes, out var content,
                        out var contentFailure))
                {
                    failure = contentFailure;
                    return false;
                }

                files[pair.Key] = new JobFilePayload(pair.Value.Name!.Trim(), content!);
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

            normalized = new NormalizedJob(ProviderId, instanceId, Namespace, settings, credentials, files,
                EmbedSummaries, EmbeddingName);
            failure = null;
            return true;
        }

        /// <summary>
        ///   Base64 to bytes, with the ceiling checked on the DECODED length. The check is on the decoded
        ///   length because that is what the run holds and what the provider parses; refusing on the
        ///   encoded length would state a limit a third smaller than the one configured.
        /// </summary>
        private static Boolean TryDecodeContent(String? contentBase64, String settingKey, Int64 maxFileBytes,
            out Byte[]? content, out String? failure)
        {
            content = null;

            if (contentBase64 == null)
            {
                failure = String.Format(
                    "The file supplied for setting '{0}' carries no contentBase64. A file's bytes arrive " +
                    "there and nowhere else: the runtime opens nothing on disk, so there is no name it " +
                    "could look up instead.", settingKey);
                return false;
            }

            // An EMPTY file is refused rather than read as one. Every shipped file provider treats an
            // unreadable source as a failed run precisely so that "I could not look" never becomes "there
            // is nothing there", and a zero-byte upload - a form submitted before the file was chosen, a
            // truncated copy - is the same statement wearing a different hat: parsed as a complete
            // snapshot it would withdraw every element the identity ever claimed.
            if (contentBase64.Length == 0)
            {
                failure = String.Format(
                    "The file supplied for setting '{0}' is empty. An empty file is refused rather than " +
                    "read as an empty source, because a complete snapshot describing nothing withdraws " +
                    "everything this identity ever claimed.", settingKey);
                return false;
            }

            Byte[] decoded;
            try
            {
                decoded = Convert.FromBase64String(contentBase64);
            }
            catch (FormatException)
            {
                failure = String.Format(
                    "The contentBase64 supplied for setting '{0}' is not valid base64. The file travels as " +
                    "bytes rather than as text so that an extract written in UTF-16 arrives intact.",
                    settingKey);
                return false;
            }

            if (decoded.Length == 0)
            {
                failure = String.Format(
                    "The file supplied for setting '{0}' decodes to nothing. An empty file is refused " +
                    "rather than read as an empty source.", settingKey);
                return false;
            }

            if (maxFileBytes > 0 && decoded.Length > maxFileBytes)
            {
                failure = String.Format(
                    "The file supplied for setting '{0}' is {1} bytes, over the {2}-byte ceiling " +
                    "(Integrations:MaxFileBytes). Both numbers are named because the ceiling belongs to " +
                    "this runtime's own configuration and not to the instance you submitted through.",
                    settingKey, decoded.Length, maxFileBytes);
                return false;
            }

            content = decoded;
            failure = null;
            return true;
        }
    }

    /// <summary>
    ///   One file on a job: what it is called, and what is in it.
    ///
    ///   <para>Two fields rather than one string because both are load-bearing and neither substitutes for
    ///   the other. The NAME is what every message about the run calls the file, so a provider's
    ///   diagnostic can still say "devices.csv row 7"; nothing opens it, resolves it or joins it to a
    ///   path. The CONTENT is bytes, base64, so a vendor tool's UTF-16 extract decodes exactly as it did
    ///   when the file came off a mount instead of arriving as mojibake.</para>
    /// </summary>
    public sealed class JobFile
    {
        /// <summary>The file's own name, such as <c>devices.csv</c>. Display text, never a path.</summary>
        [JsonPropertyName("name")]
        public String? Name { get; set; }

        /// <summary>The file's bytes, base64. <c>base64 -w0 devices.csv</c> produces exactly this.</summary>
        [JsonPropertyName("contentBase64")]
        public String? ContentBase64 { get; set; }
    }

    /// <summary>
    ///   A job whose three maps have been folded, so every later lookup is case-insensitive by construction
    ///   rather than by hope, and whose files are decoded bytes rather than text a caller may have
    ///   mis-encoded.
    /// </summary>
    public sealed class NormalizedJob
    {
        internal NormalizedJob(String? providerId, String? instanceId, String? namespaceName,
            IReadOnlyDictionary<String, String> settings, IReadOnlyDictionary<String, String> credentials,
            IReadOnlyDictionary<String, JobFilePayload> files,
            Boolean embedSummaries, String embeddingName)
        {
            Files = files;
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

        /// <summary>The decoded files, keyed by the file setting each was supplied for.</summary>
        public IReadOnlyDictionary<String, JobFilePayload> Files { get; }

        /// <summary>Whether this instance opted into embedding its entity summaries.</summary>
        public Boolean EmbedSummaries { get; }

        /// <summary>The named embedding summaries are written as.</summary>
        public String EmbeddingName { get; }
    }
}
