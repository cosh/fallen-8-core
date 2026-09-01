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
using System.Text.Json;
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

        /// <summary>
        ///   What this run declares itself COMPLETE OVER, or null for the whole identity.
        ///
        ///   <para>Completeness is otherwise the identity, and a source too large for one job cannot then
        ///   be described at all: every job is a complete snapshot that does not mention the other jobs'
        ///   elements, so each withdraws the others'. Naming a scope makes a job complete over the part
        ///   it carried, and reconciliation compares only that part.</para>
        ///
        ///   <para>USE THE SAME SCOPE for every job describing the same part, and a different one for a
        ///   different part. It is a SEPARATE dimension from any identity a provider puts in its claim
        ///   values: two scopes of one source routinely describe the same element, and such an element
        ///   carries both scopes' claims and is deleted only when the last one goes. Folding the two
        ///   together would instead split every shared element in two.</para>
        ///
        ///   <para>Letters, digits, dot, dash and underscore, at most 64 characters. The caller owns its
        ///   stability exactly as it owns the identity's, and for the same reason: nothing inside can
        ///   tell a renamed scope from a new one, and a renamed scope withdraws nothing while the old
        ///   scope's elements stay claimed by a scope no later run mentions.</para>
        /// </summary>
        [JsonPropertyName("scope")]
        public String? Scope { get; set; }

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
        /// <remarks>
        ///   One entry per file SETTING, and each entry carries either one file or an ordered list of them:
        ///   a setting the descriptor declares <c>multiple</c> may be given several. See
        ///   <see cref="JobFileGroup" /> for why both shapes are accepted on the wire.
        /// </remarks>
        [JsonPropertyName("files")]
        public IDictionary<String, JobFileGroup> Files { get; set; }
            = new Dictionary<String, JobFileGroup>(StringComparer.Ordinal);

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
        /// <param name="maxJobFileBytes">The ceiling on the DECODED TOTAL across every file of the job
        /// (<c>Integrations:MaxJobFileBytes</c>). Zero or less means no ceiling.</param>
        /// <param name="maxJobFiles">The ceiling on the NUMBER of files across every file setting
        /// (<c>Integrations:MaxJobFiles</c>). Zero or less means no ceiling.</param>
        public Boolean TryNormalize(out NormalizedJob? normalized, out String? failure,
            Int64 maxFileBytes = 0, Int64 maxJobFileBytes = 0, Int32 maxJobFiles = 0)
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

            var files = new Dictionary<String, JobFileSet>(StringComparer.OrdinalIgnoreCase);

            // The DECODED total across every file of the job, which is a second ceiling and not a restatement
            // of the per-file one: a job may carry a whole vehicle's worth of extracts, and what this process
            // has to hold at once is their sum. Enforced here, among the refusals a caller can act on, rather
            // than discovered as an allocation failure in the middle of a run.
            var totalBytes = 0L;

            // And the COUNT, for the same reason at a different unit: bytes were never the only thing a
            // caller can spend, and an empty file being refused does not make a one-byte file free.
            var fileCount = 0;

            foreach (var pair in Files ?? new Dictionary<String, JobFileGroup>(StringComparer.Ordinal))
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

                if (pair.Value == null || pair.Value.Files.Count == 0)
                {
                    failure = String.Format(
                        "The file supplied for setting '{0}' is null. A file setting the job does not need " +
                        "is left out of 'files' rather than sent empty.", pair.Key);
                    return false;
                }

                var payloads = new List<JobFilePayload>(pair.Value.Files.Count);

                // Names are compared case-INSENSITIVELY because the point of the check is what a READER
                // sees: every diagnostic about a file names it, so two files with one name make each of
                // those messages ambiguous, and 'Body.arxml' beside 'body.arxml' reads as one file
                // mentioned twice. The commonest cause is the same file picked twice by mistake.
                var names = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);

                foreach (var file in pair.Value.Files)
                {
                    if (file == null)
                    {
                        failure = String.Format(
                            "One of the files supplied for setting '{0}' is null. A gap in the list would be " +
                            "a file this run silently did not read.", pair.Key);
                        return false;
                    }

                    if (!JobFiles.TryValidateName(file.Name, pair.Key, out var nameFailure))
                    {
                        failure = nameFailure;
                        return false;
                    }

                    var name = file.Name!.Trim();
                    if (names.TryGetValue(name, out var already))
                    {
                        // BOTH spellings, because they may differ only in case and naming one of them would
                        // read as a complaint about a file the caller cannot find in what they sent.
                        failure = String.Equals(already, name, StringComparison.Ordinal)
                            ? String.Format(
                                "Setting '{0}' was given two files called '{1}'.", pair.Key, name)
                            : String.Format(
                                "Setting '{0}' was given both '{1}' and '{2}', which are one name once case " +
                                "is set aside.", pair.Key, already, name);
                        return false;
                    }

                    names[name] = name;

                    if (!TryDecodeContent(file, pair.Key, maxFileBytes, out var content,
                            out var contentFailure))
                    {
                        failure = contentFailure;
                        return false;
                    }

                    totalBytes += content!.Length;
                    if (maxJobFileBytes > 0 && totalBytes > maxJobFileBytes)
                    {
                        // The MEASURED total as well as the ceiling. Naming only the ceiling left the one
                        // question a caller has to answer - how much to cut - unanswerable from the
                        // refusal, and made the published claim that a refusal "names the size and the
                        // ceiling it broke" true of the per-file case only. The total is "at least",
                        // because the accumulation stops at the first file that crosses the line rather
                        // than decoding the rest to report a number nobody needs.
                        failure = String.Format(
                            "The files this job carries come to at least {0} bytes, more than the {1}-byte " +
                            "total ceiling (Integrations:MaxJobFileBytes). One request carries a whole run, " +
                            "so every file on it is held at once; the ceiling belongs to this runtime's own " +
                            "configuration and not to the instance you submitted through.",
                            totalBytes, maxJobFileBytes);
                        return false;
                    }

                    fileCount++;
                    if (maxJobFiles > 0 && fileCount > maxJobFiles)
                    {
                        // Counted across every file setting, not per setting: the cost this bounds is the
                        // number of payloads this process holds at once, and a provider with two file
                        // settings would otherwise get twice the allowance for the same memory.
                        failure = String.Format(
                            "This job carries more than {0} files (Integrations:MaxJobFiles), counted across " +
                            "every file setting on it. The two byte ceilings do not bound this on their own: " +
                            "a file of one byte is legal, so a set can satisfy both of them and still ask this " +
                            "runtime for an unreasonable number of entries.", maxJobFiles);
                        return false;
                    }

                    payloads.Add(new JobFilePayload(name, content!));
                }

                files[pair.Key] = new JobFileSet(payloads, pair.Value.AsList);
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
        ///   The file's bytes, with the ceiling checked on the bytes the run actually holds and the provider
        ///   parses.
        ///
        ///   <para>The only arm that supplies them is a multipart part; a <c>contentBase64</c> field is
        ///   refused HERE rather than ignored, because ignoring it would leave the caller with "carries no
        ///   bytes", which is true and says nothing about what to do instead. Everything a caller can be told
        ///   about a file it did supply - empty, oversized - is decided once, below.</para>
        /// </summary>
        private static Boolean TryDecodeContent(JobFile file, String settingKey, Int64 maxFileBytes,
            out Byte[]? content, out String? failure)
        {
            content = null;

            Byte[] decoded;
            if (file.ContentBase64 != null)
            {
                // The base64 arm is GONE, and refused by name rather than ignored. Ignoring it would
                // leave the caller with "carries no bytes", which is true and unhelpful.
                //
                // It was dropped because it bounded the whole job ceiling: base64 costs a third, so the
                // largest job this transport could deliver inside the proxy's fixed budget was three
                // quarters of it. Every job now pays that for a shape no client uses. A multipart part
                // carries the bytes as they are, so the ceiling is the budget rather than a fraction of it.
                failure = String.Format(
                    "The file supplied for setting '{0}' carries contentBase64, which this runtime no " +
                    "longer accepts. Send the job as multipart/form-data with the document in a part " +
                    "named 'job' and each file in a part named for its setting: base64 costs a third of " +
                    "the transport budget for no benefit, so the ceiling is higher without it.",
                    settingKey);
                return false;
            }

            if (file.Content != null)
            {

                if (file.Truncated)
                {
                    // The reader stopped at the ceiling, so the honest statement is "more than", not a size.
                    // A multipart part declares no length, so there is no number to report other than the
                    // one it was measured against.
                    failure = String.Format(
                        "The file supplied for setting '{0}' is more than {1} bytes " +
                        "(Integrations:MaxFileBytes); the runtime stopped reading at the ceiling rather than " +
                        "holding a file it was going to refuse. The ceiling belongs to this runtime's own " +
                        "configuration and not to the instance you submitted through.",
                        settingKey, maxFileBytes);
                    return false;
                }

                decoded = file.Content;
            }
            else
            {
                failure = String.Format(
                    "The file supplied for setting '{0}' carries no bytes. A file arrives as a multipart " +
                    "part named for its setting and nowhere else: the runtime opens nothing on disk, so " +
                    "there is no name it could look up instead.", settingKey);
                return false;
            }

            // An EMPTY file is refused rather than read as one. Every shipped file provider treats an
            // unreadable source as a failed run precisely so that "I could not look" never becomes "there
            // is nothing there", and a zero-byte upload - a form submitted before the file was chosen, a
            // truncated copy - is the same statement wearing a different hat: parsed as a complete snapshot
            // it would withdraw every element the identity ever claimed.
            //
            // Checked HERE and nowhere else, for both arms: an empty base64 string decodes to an empty array
            // rather than throwing, and an empty multipart part is an empty array too, so one check on the
            // bytes covers both and there is one message to keep true instead of two to keep in step.
            if (decoded.Length == 0)
            {
                failure = String.Format(
                    "The file supplied for setting '{0}' is empty. An empty file is refused rather than " +
                    "read as an empty source, because a complete snapshot describing nothing withdraws " +
                    "everything this identity ever claimed.", settingKey);
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
    ///   <para>The NAME is what every message about the run calls the file, so a provider's diagnostic can
    ///   still say "devices.csv row 7"; nothing opens it, resolves it or joins it to a path. The CONTENT is
    ///   the bytes as they arrived, never text, so an extract written in UTF-16 decodes exactly as it did
    ///   when the file came off a mount instead of arriving as mojibake.</para>
    /// </summary>
    public sealed class JobFile
    {
        /// <summary>The file's own name, such as <c>devices.csv</c>. Display text, never a path.</summary>
        [JsonPropertyName("name")]
        public String? Name { get; set; }

        /// <summary>
        ///   RETIRED, and kept only so a job that sets it can be refused BY NAME. A file's bytes arrive as a
        ///   multipart part; base64 in the document cost a third of the fixed transport bound, which capped
        ///   the job ceiling for every caller including the ones not using it. Dropping the property instead
        ///   would leave a caller still sending it with "carries no bytes", which is true and unactionable,
        ///   because an unknown JSON member is ignored rather than reported.
        /// </summary>
        [JsonPropertyName("contentBase64")]
        public String? ContentBase64 { get; set; }

        /// <summary>
        ///   The file's bytes as they arrived, set only by the multipart reader
        ///   (<see cref="Hosting.JobRequestReader" />) and the only place a file's content comes from.
        ///
        ///   <para><see cref="JsonIgnoreAttribute" /> is what keeps this off the wire in both directions.
        ///   Bytes have no JSON spelling: a job document names its provider, its settings and its scope, and
        ///   a file's content travels beside it in a part of its own.</para>
        /// </summary>
        [JsonIgnore]
        public Byte[]? Content { get; set; }

        /// <summary>
        ///   Set when the reader stopped at the per-file ceiling instead of reading the whole part, in which
        ///   case <see cref="Content" /> is deliberately EMPTY: the bytes are not kept, because the job is
        ///   going to be refused and holding them would be paying the very cost the ceiling exists to avoid.
        ///
        ///   <para>It exists so the refusal can say "is more than N bytes" rather than a size it never
        ///   measured. A multipart part declares no length, so the only honest thing the reader knows about a
        ///   file it stopped reading is that it was over.</para>
        /// </summary>
        [JsonIgnore]
        public Boolean Truncated { get; set; }
    }

    /// <summary>
    ///   The files one file SETTING was given: one, or an ordered several.
    ///
    ///   <para>Both shapes are read off the wire - a bare object and an array of them - and that is a
    ///   compatibility decision rather than laxity. Every job written before multi-file existed sends the
    ///   object form, every client that only ever needs one file still may, and an array is simply the
    ///   general case: one file is a group of one. Refusing the object form would have broken every
    ///   existing caller to express something the array form does not say any better.</para>
    ///
    ///   <para>ORDER IS PART OF THE MEANING for a provider that composes its files, so this keeps the
    ///   order the job listed rather than a set: the AUTOSAR reader resolves references across an ordered
    ///   union, and which file wins a re-declared path is decided by that order.</para>
    /// </summary>
    [JsonConverter(typeof(JobFileGroupConverter))]
    public sealed class JobFileGroup
    {
        private static readonly JobFile[] None = Array.Empty<JobFile>();

        /// <summary>
        ///   The LIST form, whatever its length. Writing this constructor is itself the request for the
        ///   multiple shape, which is why a group of one built here is still a list: a caller sending
        ///   <c>[one file]</c> to a setting that takes exactly one would otherwise work by accident and
        ///   break the day it sent two. The single-object form is the implicit conversion below.
        /// </summary>
        public JobFileGroup(params JobFile[]? files)
        {
            Files = files ?? None;
            AsList = true;
        }

        private JobFileGroup(IReadOnlyList<JobFile> files, Boolean asList)
        {
            Files = files;
            AsList = asList;
        }

        /// <summary>The files, in the order the job listed them.</summary>
        public IReadOnlyList<JobFile> Files { get; }

        /// <summary>Whether the wire form was an ARRAY rather than a single object.</summary>
        public Boolean AsList { get; }

        /// <summary>
        ///   One file is a group of one, so that every caller written against the single-file shape - and
        ///   every test that builds a job by hand - keeps saying exactly what it said before.
        /// </summary>
        public static implicit operator JobFileGroup(JobFile file)
        {
            return new JobFileGroup(new[] { file }, asList: false);
        }

        /// <summary>The array form, kept distinguishable from a single object of the same content.</summary>
        internal static JobFileGroup FromList(IReadOnlyList<JobFile> files)
        {
            return new JobFileGroup(files, asList: true);
        }
    }

    /// <summary>
    ///   Reads a file setting's value as either one object or an array of them, and remembers which it was.
    /// </summary>
    public sealed class JobFileGroupConverter : JsonConverter<JobFileGroup>
    {
        /// <inheritdoc />
        public override JobFileGroup? Read(ref Utf8JsonReader reader, Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                // Kept as an empty group rather than rejected here: normalisation already has the message
                // for a file setting sent empty, and it names the setting, which this cannot.
                return new JobFileGroup(Array.Empty<JobFile>());
            }

            if (reader.TokenType == JsonTokenType.StartArray)
            {
                var files = new List<JobFile>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    var file = JsonSerializer.Deserialize<JobFile>(ref reader, options);
                    if (file != null)
                    {
                        files.Add(file);
                    }
                }

                return JobFileGroup.FromList(files);
            }

            var single = JsonSerializer.Deserialize<JobFile>(ref reader, options);
            return single == null ? new JobFileGroup(Array.Empty<JobFile>()) : (JobFileGroup)single;
        }

        /// <summary>
        ///   Written back in the shape it arrived in. Nothing in this runtime serialises a job - no route
        ///   reads one back, by design - so this exists for tests and for anything that round-trips a job it
        ///   built, and for those the useful property is that the shape survives.
        /// </summary>
        public override void Write(Utf8JsonWriter writer, JobFileGroup value, JsonSerializerOptions options)
        {
            if (value == null)
            {
                writer.WriteNullValue();
                return;
            }

            if (!value.AsList && value.Files.Count == 1)
            {
                JsonSerializer.Serialize(writer, value.Files[0], options);
                return;
            }

            writer.WriteStartArray();
            foreach (var file in value.Files)
            {
                JsonSerializer.Serialize(writer, file, options);
            }

            writer.WriteEndArray();
        }
    }

    /// <summary>
    ///   The DECODED files one setting was given, in job order, and whether the caller asked for the
    ///   multiple shape.
    ///
    ///   <para>Ordered, because for a provider that composes several files the order decides the outcome:
    ///   the AUTOSAR reader resolves references across the union of its files and gives a re-declared path
    ///   to the first file that declared it, so a different order is a different graph.</para>
    /// </summary>
    public sealed class JobFileSet
    {
        internal JobFileSet(IReadOnlyList<JobFilePayload> files, Boolean asList)
        {
            Files = files;
            AsList = asList;
        }

        /// <summary>The files, in the order the job listed them. Never empty.</summary>
        public IReadOnlyList<JobFilePayload> Files { get; }

        /// <summary>
        ///   Whether the caller used the ARRAY form. Kept past normalisation because the descriptor is what
        ///   decides whether that was allowed, and the descriptor is not known here: a setting that is not
        ///   declared <c>multiple</c> refuses the array shape in <c>JobRunner</c>, which is the first place
        ///   that has both facts.
        /// </summary>
        public Boolean AsList { get; }

        /// <summary>The first file, which is the only one for every single-file setting.</summary>
        public JobFilePayload First => Files[0];
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
            IReadOnlyDictionary<String, JobFileSet> files,
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
        public IReadOnlyDictionary<String, JobFileSet> Files { get; }

        /// <summary>Whether this instance opted into embedding its entity summaries.</summary>
        public Boolean EmbedSummaries { get; }

        /// <summary>The named embedding summaries are written as.</summary>
        public String EmbeddingName { get; }
    }
}
