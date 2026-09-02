// MIT License
//
// Fallen8SettingCatalog.cs
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.App.Namespaces;

namespace NoSQL.GraphDB.App.Configuration
{
    /// <summary>
    ///   The one home that decides which of this instance's configuration keys an operator may write
    ///   (feature writable-instance-config). One entry per bindable configuration leaf across every
    ///   <c>Fallen8:*</c> section, carrying the tier, the domain a written value must fall inside, and
    ///   for a never-writable key the rule that excludes it plus a reason published to operators.
    ///
    ///   <para><b>Completeness is derived, not maintained.</b> <c>SettingCatalogTest</c> reflects over
    ///   every options class the app binds and fails unless each leaf appears here exactly once, so a
    ///   new option property forces a recorded decision instead of quietly missing from the surface.
    ///   The same test rejects a stale entry whose property no longer exists.</para>
    ///
    ///   <para><b>Why the domains here are load-bearing</b> is explained once, on
    ///   <see cref="Fallen8SettingEntry.AllowedValues"/>. Two conventions follow from it. A count or
    ///   size is floored at the smallest value that still functions, which is 1 where zero disables
    ///   the feature it bounds and 0 where zero is itself a meaningful setting. Every key measured in
    ///   seconds is capped at <see cref="MaxSeconds"/>, the point above which the timer primitives
    ///   these values reach (<c>CancelAfter</c>, <c>HttpClient.Timeout</c>, <c>PeriodicTimer</c>,
    ///   <c>Task.Delay</c>) throw rather than wait; one blanket ceiling is easier to review than a
    ///   per-key crash analysis, and it refuses no value an operator could legitimately want.</para>
    ///
    ///   <para>The catalog deliberately describes no key's MEANING; see
    ///   <see cref="Fallen8SettingEntry"/> for why and for where that lives instead.</para>
    /// </summary>
    public static class Fallen8SettingCatalog
    {
        /// <summary>
        ///   The largest value any seconds-valued key accepts: <see cref="Int32.MaxValue"/>
        ///   milliseconds expressed in seconds, which is the tightest of the ceilings the .NET timer
        ///   primitives impose. A value above it is never a configuration, only a mistake that throws.
        /// </summary>
        private const Double MaxSeconds = 2_147_483d;

        /// <summary>
        ///   The ceiling on the change-feed ring buffer, whose backing array is allocated eagerly
        ///   while the engine is constructed at boot. Without it a written value could exhaust memory
        ///   before any route exists to write it back, leaving no recovery path over REST.
        /// </summary>
        private const Double MaxChangeFeedBuffer = 1_000_000d;

        // Reasons shared verbatim by several keys under one rule, held once so the published wording
        // cannot drift apart between the keys it covers.
        private const String FleetIdentityReason =
            "Fleet identity is baked into the telemetry resource attributes at boot, so a write could "
            + "only falsify the reported identity of a process whose signals already went out under the "
            + "real one.";

        private const String EmbeddingStampReason =
            "It is part of the immutable identity stamp written beside every stored embedding, so a "
            + "write mislabels vectors that already exist rather than failing.";

        private const String ModelFileReason =
            "It names the model file that produces vectors, so a written value changes the embedding "
            + "function under an unchanged identity stamp.";

        private const String EmbeddingFunctionReason =
            "The model is the embedding function itself, so a write produces vectors that no "
            + "longer match the ones already stored under the same stamp.";

        /// <summary>
        ///   R8: the credential this server PRESENTS to a model provider. R1 covers the credential the
        ///   server demands and is scoped to <c>Fallen8:Security</c>; nothing covered a secret the
        ///   server hands to a third party until a backend existed that requires one, and the two
        ///   hazards are not the same. A written value redirects someone else's metered spend to a
        ///   destination of the caller's choosing, and a published one hands the key over: an
        ///   always-writable tier would do the first and a value on the read surface the second,
        ///   which is why never-writable (whose entries publish the rule and the reason but NO value)
        ///   is the only tier that holds.
        /// </summary>
        private const String ProviderCredentialReason =
            "It is the credential this server presents to the model provider, so a written value "
            + "redirects metered spend to a destination the caller chose and a published value hands "
            + "the key itself to anyone who can read this response.";

        private const String ProviderEndpointReason =
            "This is a URL the server dials WITH a credential attached, so a written value would aim "
            + "an authenticated request at an address of the caller's choosing.";

        private static String OrphansIndexReason(String search)
        {
            return "Changing it orphans the populated index and makes " + search
                + " search return silently empty results instead of an error.";
        }

        private static readonly IReadOnlyList<Fallen8SettingEntry> _entries =
            new ReadOnlyCollection<Fallen8SettingEntry>(Build());

        // Case-insensitive to match configuration itself, where Fallen8:Plugins:MaxCount and
        // fallen8:plugins:maxcount are the same key: a lookup stricter than the thing it describes
        // would answer "not catalogued" for a key the binder would happily bind. Building it this way
        // also means two entries differing only in case cannot load at all, which is why no test
        // hunts for that duplicate. Declared after _entries, which it reads.
        private static readonly IReadOnlyDictionary<String, Fallen8SettingEntry> _byKey =
            _entries.ToDictionary(entry => entry.Key, entry => entry, StringComparer.OrdinalIgnoreCase);

        /// <summary>Every catalogued configuration leaf, in section order.</summary>
        public static IReadOnlyList<Fallen8SettingEntry> Entries => _entries;

        /// <summary>Looks a catalogued key up by its full colon-delimited configuration key.</summary>
        public static Boolean TryGet(String key, out Fallen8SettingEntry entry)
        {
            if (key == null)
            {
                entry = null;
                return false;
            }

            return _byKey.TryGetValue(key, out entry);
        }

        #region applying a live setting to the running process

        /// <summary>
        ///   The change-feed subscriber limits. Both live keys share one delegate because they live on the
        ///   same shared object, and each is assigned from its own freshly bound value, so applying one
        ///   never invents a value for the other.
        ///
        ///   <para>Named properties are assigned on the object every engine already holds. Re-projecting
        ///   the whole section instead would make the buffer size live for engines built later while
        ///   leaving existing ones alone, which is exactly the per-provider conversion spec 4.8
        ///   forbids.</para>
        /// </summary>
        private static void ApplyChangeFeedLimits(IServiceProvider services)
        {
            var limits = services.GetRequiredService<Fallen8Namespaces>().ChangeFeedLimits;
            if (limits == null)
            {
                return; // the feed is disabled for this process; nothing holds these limits
            }

            var configured = Configured<Fallen8ChangeFeedOptions>(services, Fallen8ChangeFeedOptions.SectionName);
            limits.MaxSubscribers = configured.MaxSubscribers;
            limits.SubscriberQueueSize = configured.SubscriberQueueSize;
        }

        /// <summary>
        ///   Binds a FRESH options instance straight from configuration, which has already reloaded by the
        ///   time an apply delegate runs.
        ///
        ///   <para>Deliberately not <c>IOptionsMonitor.CurrentValue</c>. The monitor invalidates its cache
        ///   from the same configuration reload token these delegates run on, and callback order is
        ///   registration order, so a monitor read here can legitimately hand back the value from BEFORE
        ///   the reload. Binding fresh has no such ordering relationship with anything. It also keeps this
        ///   per key by construction: the caller assigns the ONE property it is responsible for and the
        ///   bound instance is discarded.</para>
        /// </summary>
        private static T Configured<T>(IServiceProvider services, String section) where T : class, new()
        {
            var configured = new T();
            services.GetRequiredService<IConfiguration>().GetSection(section).Bind(configured);
            return configured;
        }

        /// <summary>
        ///   The heartbeat period, assigned on the options instance the change-feed controller reads per
        ///   request. Mutating that instance rather than converting the consumer to a monitor is
        ///   deliberate: the instance is a process singleton that every existing holder already points at,
        ///   whereas a monitor hands out a NEW instance on reload and would leave every consumer that
        ///   captured the value at construction reading the old one.
        /// </summary>
        private static void ApplyChangeFeedKeepAlive(IServiceProvider services)
        {
            var inForce = services.GetRequiredService<IOptions<Fallen8ChangeFeedOptions>>().Value;
            inForce.KeepAliveSeconds =
                Configured<Fallen8ChangeFeedOptions>(services, Fallen8ChangeFeedOptions.SectionName).KeepAliveSeconds;
        }

        private static void ApplyPluginCeiling(IServiceProvider services)
        {
            services.GetRequiredService<Fallen8Namespaces>().ApplyRegistryCeilings(
                Configured<Fallen8PluginOptions>(services, Fallen8PluginOptions.SectionName).MaxCount,
                storedQueryMaxCount: null);
        }

        private static void ApplyStoredQueryCeiling(IServiceProvider services)
        {
            services.GetRequiredService<Fallen8Namespaces>().ApplyRegistryCeilings(
                pluginMaxCount: null,
                Configured<Fallen8StoredQueryOptions>(services, Fallen8StoredQueryOptions.SectionName).MaxCount);
        }

        private static void ApplyNamespaceCeiling(IServiceProvider services)
        {
            services.GetRequiredService<Fallen8Namespaces>().ApplyNamespaceCeiling(
                Configured<Fallen8NamespacesOptions>(services, Fallen8NamespacesOptions.SectionName).MaxNamespaces);
        }

        #endregion

        private static IList<Fallen8SettingEntry> Build()
        {
            var entries = new List<Fallen8SettingEntry>();

            #region Fallen8:Analytics

            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Analytics:DefaultTimeBudgetSeconds",
                Fallen8SettingKind.Int, minimum: 1, maximum: MaxSeconds));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Analytics:MaxTimeBudgetSeconds",
                Fallen8SettingKind.Int, minimum: 1, maximum: MaxSeconds));
            // Stays restart-tier deliberately: the run gate is a SemaphoreSlim sized once at
            // construction, and the 429 text quotes this value, so a live write would make the
            // message lie about the cap actually in force (spec section 8).
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Analytics:MaxConcurrentRuns",
                Fallen8SettingKind.Int, minimum: 1));

            #endregion

            #region Fallen8:BulkIO

            entries.Add(Fallen8SettingEntry.Restart("Fallen8:BulkIO:ImportBatchSize",
                Fallen8SettingKind.Int, minimum: 1));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:BulkIO:MaxLineBytes",
                Fallen8SettingKind.Int, minimum: 1));
            // Null means unlimited and is also how a write clears the override, so zero is the floor
            // rather than one; a negative value would 413 every import and is refused by Kestrel
            // before the body is read.
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:BulkIO:MaxImportRequestBytes",
                Fallen8SettingKind.Int, minimum: 0));

            #endregion

            #region Fallen8:ChangeFeed

            entries.Add(Fallen8SettingEntry.Restart("Fallen8:ChangeFeed:Enabled", Fallen8SettingKind.Bool));
            // Stays restart-tier: the ring array is allocated at engine construction, so an existing
            // dispatcher cannot change size and nothing a client can observe would move.
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:ChangeFeed:BufferSize",
                Fallen8SettingKind.Int, minimum: 1, maximum: MaxChangeFeedBuffer));
            // New work only: the queue depth is fixed when a subscription is created, so an existing
            // subscriber keeps the depth it was given.
            entries.Add(Fallen8SettingEntry.LiveForNewWork("Fallen8:ChangeFeed:SubscriberQueueSize",
                Fallen8SettingKind.Int, ApplyChangeFeedLimits, minimum: 1));
            // New work only: the cap is compared when a caller subscribes and nobody is evicted, so
            // lowering it leaves existing streams connected.
            entries.Add(Fallen8SettingEntry.LiveForNewWork("Fallen8:ChangeFeed:MaxSubscribers",
                Fallen8SettingKind.Int, ApplyChangeFeedLimits, minimum: 1));
            // New work only: the heartbeat period is fixed when a stream opens.
            entries.Add(Fallen8SettingEntry.LiveForNewWork("Fallen8:ChangeFeed:KeepAliveSeconds",
                Fallen8SettingKind.Int, ApplyChangeFeedKeepAlive, minimum: 1, maximum: MaxSeconds));

            #endregion

            #region Fallen8:Chat

            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Chat:Enabled", Fallen8SettingKind.Bool, "R5",
                "Turning the chat gateway on is a capability the operator opted out of, and its state "
                + "is readable anonymously through the status probe."));
            // The accepted set is load-bearing: the backend factory matches ordinally and throws
            // otherwise, and that throw is cached by a Lazy and surfaces as a permanent 503.
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Chat:Backend",
                Fallen8SettingKind.Enum, allowedValues: new[] { "Ollama", "Nahil", "OpenAI", "Anthropic" }));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Chat:TimeoutSeconds",
                Fallen8SettingKind.Int, minimum: 1, maximum: MaxSeconds));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Chat:Stream", Fallen8SettingKind.Bool));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Chat:Ollama:Endpoint", Fallen8SettingKind.String, "R4",
                "This is a URL the server dials, so a written value turns the chat gateway into a "
                + "request forwarder onto the operator's own network."));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Chat:Ollama:Model", Fallen8SettingKind.String));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Chat:Nahil:Endpoint", Fallen8SettingKind.String, "R4",
                ProviderEndpointReason));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Chat:Nahil:ApiKey", Fallen8SettingKind.String, "R8",
                ProviderCredentialReason));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Chat:Nahil:Model", Fallen8SettingKind.String));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Chat:OpenAI:Endpoint", Fallen8SettingKind.String, "R4",
                ProviderEndpointReason));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Chat:OpenAI:ApiKey", Fallen8SettingKind.String, "R8",
                ProviderCredentialReason));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Chat:OpenAI:Model", Fallen8SettingKind.String));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Chat:Anthropic:Endpoint", Fallen8SettingKind.String, "R4",
                ProviderEndpointReason));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Chat:Anthropic:ApiKey", Fallen8SettingKind.String, "R8",
                ProviderCredentialReason));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Chat:Anthropic:Model", Fallen8SettingKind.String));
            // The Messages API requires the field on every request, which is why only this backend
            // carries the knob. The ceiling is the largest output any current Claude model offers.
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Chat:Anthropic:MaxTokens",
                Fallen8SettingKind.Int, minimum: 256, maximum: 128_000));

            #endregion

            #region Fallen8:Durability

            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Durability:StorageDirectory", Fallen8SettingKind.String, "R2",
                "This is not a write location but a delete location: dropping a namespace removes the "
                + "write-ahead logs under the directory derived from it and then the directory itself."));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Durability:CheckpointBaseName", Fallen8SettingKind.String, "R2",
                "It is also the glob that discovers existing checkpoints, so a written value hides "
                + "every checkpoint this instance has already taken."));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Durability:WalPath", Fallen8SettingKind.String, "R2",
                "Moving the write-ahead log orphans the commits it holds that no checkpoint has "
                + "absorbed yet, which loses acknowledged writes. It also binds the DEFAULT namespace "
                + "only: every other namespace keeps its own log under the storage directory."));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Durability:Volatile", Fallen8SettingKind.Bool, "R2",
                "It selects which engine constructor runs at boot, so it decides whether this "
                + "instance persists anything at all."));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Durability:SaveOnShutdown", Fallen8SettingKind.Bool));

            #endregion

            #region Fallen8:Embedding

            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Embedding:Enabled", Fallen8SettingKind.Bool, "R5",
                "Embedding is a capability the operator opted out of, and lifting it re-opens the "
                + "gateway plus the semantic arms of path and subgraph traversal."));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Embedding:Backend", Fallen8SettingKind.String, "R3",
                "It changes the function that produces vectors while the identity stamp stored beside "
                + "them stays the same, so old and new vectors become silently incomparable."));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Embedding:TimeoutSeconds",
                Fallen8SettingKind.Int, minimum: 1, maximum: MaxSeconds));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Embedding:ModelName", Fallen8SettingKind.String, "R3",
                EmbeddingStampReason));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Embedding:ModelVersion", Fallen8SettingKind.String, "R3",
                EmbeddingStampReason));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Embedding:Dimension", Fallen8SettingKind.Int, "R3",
                "Every stored vector and every bound vector index has this width, so a written value "
                + "corrupts the stored corpus instead of reporting an error."));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Embedding:IntendedMetric", Fallen8SettingKind.String, "R3",
                "It is the distance metric stamped into bound vector indices, so a write makes every "
                + "stored index answer with a similarity it was not built for."));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Embedding:MaxBatchSize",
                Fallen8SettingKind.Int, minimum: 1));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Embedding:MaxConcurrentBatches",
                Fallen8SettingKind.Int, minimum: 1));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Embedding:MaxTextLength",
                Fallen8SettingKind.Int, minimum: 1));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Embedding:QueryPrefix", Fallen8SettingKind.String));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Embedding:Onnx:ModelPath", Fallen8SettingKind.String, "R3",
                ModelFileReason));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Embedding:Onnx:VocabPath", Fallen8SettingKind.String, "R3",
                "The vocabulary decides how text becomes tokens, so a written value changes the "
                + "embedding function under an unchanged identity stamp."));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Embedding:Onnx:MaxTokens", Fallen8SettingKind.Int, "R3",
                "It decides where input text is truncated before embedding, so a write changes the "
                + "vector a given document produces while its stamp claims otherwise."));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Embedding:Onnx:Pooling", Fallen8SettingKind.String, "R3",
                "Pooling is the arithmetic that turns token vectors into one vector, so a write "
                + "changes the embedding function under an unchanged identity stamp."));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Embedding:Onnx:Normalize", Fallen8SettingKind.Bool, "R3",
                "Normalisation decides the scale of stored vectors, so mixing both settings in one "
                + "index makes the distances it reports meaningless."));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Embedding:LLamaSharp:ModelPath", Fallen8SettingKind.String, "R3",
                ModelFileReason));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Embedding:Ollama:Endpoint", Fallen8SettingKind.String, "R4",
                "This is a URL the server dials, so a written value makes the embedding gateway "
                + "reach an address of the caller's choosing on the operator's own network."));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Embedding:Ollama:Model", Fallen8SettingKind.String, "R3",
                EmbeddingFunctionReason));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Embedding:Nahil:Endpoint", Fallen8SettingKind.String, "R4",
                ProviderEndpointReason));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Embedding:Nahil:ApiKey", Fallen8SettingKind.String, "R8",
                ProviderCredentialReason));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Embedding:Nahil:Model", Fallen8SettingKind.String, "R3",
                EmbeddingFunctionReason));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Embedding:OpenAI:Endpoint", Fallen8SettingKind.String, "R4",
                ProviderEndpointReason));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Embedding:OpenAI:ApiKey", Fallen8SettingKind.String, "R8",
                ProviderCredentialReason));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Embedding:OpenAI:Model", Fallen8SettingKind.String, "R3",
                EmbeddingFunctionReason));

            #endregion

            #region Fallen8:Identity

            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Identity:Tenant:Id", Fallen8SettingKind.String, "R6",
                FleetIdentityReason));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Identity:Tenant:Name", Fallen8SettingKind.String, "R6",
                FleetIdentityReason));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Identity:Instance:Id", Fallen8SettingKind.String, "R6",
                "The instance id is the stable key a central consumer separates this process by, and "
                + "it is stamped on every signal at boot."));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Identity:Instance:Name", Fallen8SettingKind.String, "R6",
                FleetIdentityReason));

            #endregion

            #region Fallen8:Ingestion

            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Ingestion:Enabled", Fallen8SettingKind.Bool, "R5",
                "Document ingestion is a capability the operator opted out of, and lifting it opens "
                + "an upload surface that reaches the configured sidecars."));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Ingestion:MaxUploadBytes",
                Fallen8SettingKind.Int, minimum: 1, maximum: 536_870_912));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Ingestion:MaxPages",
                Fallen8SettingKind.Int, minimum: 1));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Ingestion:MaxChunksPerDocument",
                Fallen8SettingKind.Int, minimum: 1));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Ingestion:MaxChunksPerNamespace",
                Fallen8SettingKind.Int, minimum: 1));
            // Stays restart-tier deliberately: the real cap is an immutable bounded channel created
            // at startup, and the 503 text quotes this value (spec section 8).
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Ingestion:MaxQueueLength",
                Fallen8SettingKind.Int, minimum: 1));
            // Zero is meaningful here: it means no minimum chunk length.
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Ingestion:ChunkMinChars",
                Fallen8SettingKind.Int, minimum: 0));
            // Nothing clamps this one anywhere: at zero the chunker indexes a paragraph at position
            // -1 and every ingestion fails AFTER the request already answered 202.
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Ingestion:ChunkMaxChars",
                Fallen8SettingKind.Int, minimum: 1));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Ingestion:MaxIdentifiersPerChunk",
                Fallen8SettingKind.Int, minimum: 0));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Ingestion:MaxLinksPerChunk",
                Fallen8SettingKind.Int, minimum: 0));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Ingestion:EmbeddingName", Fallen8SettingKind.String, "R3",
                "It names the embedding every ingested chunk is stored under, so a write leaves the "
                + "existing corpus addressed by a name nothing searches any more."));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Ingestion:EnsureVectorIndex", Fallen8SettingKind.Bool));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Ingestion:VectorIndexId", Fallen8SettingKind.String, "R3",
                OrphansIndexReason("document")));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Ingestion:EnsureFulltextIndex", Fallen8SettingKind.Bool));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Ingestion:FulltextIndexId", Fallen8SettingKind.String, "R3",
                OrphansIndexReason("keyword")));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Ingestion:EnsureEntityIndex", Fallen8SettingKind.Bool));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Ingestion:EntityIndexId", Fallen8SettingKind.String, "R3",
                OrphansIndexReason("entity")));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Ingestion:Docling:Endpoint", Fallen8SettingKind.String, "R4",
                "This is a URL the server dials with uploaded documents, so a written value "
                + "exfiltrates them to an address of the caller's choosing."));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Ingestion:Docling:TimeoutSeconds",
                Fallen8SettingKind.Int, minimum: 1, maximum: MaxSeconds));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Ingestion:Docling:PollIntervalSeconds",
                Fallen8SettingKind.Int, minimum: 1, maximum: MaxSeconds));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Ingestion:Docling:DoOcr", Fallen8SettingKind.Bool));
            // Two values, forwarded verbatim to the sidecar: without the closed set a typo is judged
            // only there, and every binary ingestion fails afterwards.
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Ingestion:Docling:TableMode",
                Fallen8SettingKind.Enum, allowedValues: new[] { "fast", "accurate" }));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Ingestion:Docling:OcrEngine", Fallen8SettingKind.String));

            #endregion

            #region Fallen8:Integrations

            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Integrations:Enabled", Fallen8SettingKind.Bool, "R5",
                "The integrations runtime is a capability the operator opted out of, and lifting it "
                + "opens a proxy that runs jobs against systems on their own network."));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Integrations:Endpoint", Fallen8SettingKind.String, "R4",
                "It is the base address of an authenticated pass-through proxy that forwards status, "
                + "body and content type unchanged, so writable it becomes an arbitrary-URL proxy "
                + "onto the operator's own network."));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Integrations:TimeoutSeconds",
                Fallen8SettingKind.Int, minimum: 1, maximum: MaxSeconds));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Integrations:JobTimeoutSeconds",
                Fallen8SettingKind.Int, minimum: 1, maximum: MaxSeconds));

            #endregion

            #region Fallen8:Metadata

            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Metadata:Directory", Fallen8SettingKind.String, "R2",
                "It locates the namespace inventory, the save-game registry and the overrides file "
                + "itself, so this layer cannot be allowed to move the file that defines it."));

            #endregion

            #region Fallen8:Namespaces

            // New creations only: lowering the ceiling below the namespaces that exist removes none.
            entries.Add(Fallen8SettingEntry.LiveForNewWork("Fallen8:Namespaces:MaxNamespaces",
                Fallen8SettingKind.Int, ApplyNamespaceCeiling, minimum: 1));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Namespaces:LoadOnStartup", Fallen8SettingKind.Bool));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Namespaces:StartupLoadMode",
                Fallen8SettingKind.Enum, allowedValues: new[] { "Catalog", "All", "DefaultOnly" }));

            #endregion

            #region Fallen8:Nlp

            // Not an R5 capability flag: unlike the other Enabled keys this one gates no REST
            // endpoint and grants no caller anything. It only tells the ingestion pipeline whether to
            // call the sidecar at the endpoint the operator already configured.
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Nlp:Enabled", Fallen8SettingKind.Bool));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Nlp:Endpoint", Fallen8SettingKind.String, "R4",
                "This is a URL the server dials with ingested document text, so a written value "
                + "exfiltrates it to an address of the caller's choosing."));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Nlp:TimeoutSeconds",
                Fallen8SettingKind.Int, minimum: 1, maximum: MaxSeconds));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Nlp:MaxCharsPerChunk",
                Fallen8SettingKind.Int, minimum: 1));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Nlp:MaxEntitiesPerChunk",
                Fallen8SettingKind.Int, minimum: 0));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Nlp:MaxKeyTermsPerChunk",
                Fallen8SettingKind.Int, minimum: 0));

            #endregion

            #region Fallen8:Observability

            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Observability:Prometheus:Enabled", Fallen8SettingKind.Bool, "R5",
                "Enabling the exporter alone maps a metrics endpoint that is anonymous by default, so "
                + "this key and the one guarding it may only move together in configuration."));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Observability:Prometheus:RequireApiKey", Fallen8SettingKind.Bool, "R5",
                "Clearing it makes the metrics endpoint anonymous, which is an authenticated caller "
                + "turning authentication off."));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Observability:Otlp:Endpoint", Fallen8SettingKind.String, "R4",
                "It is where this process pushes metrics, traces and logs, so a written value "
                + "redirects the instance's whole telemetry stream to a collector of the caller's "
                + "choosing."));
            // Stays restart-tier deliberately: live sampling needs a custom sampler, and it could
            // only work where an exporter existed at boot (spec section 8).
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Observability:TracingSamplingRatio",
                Fallen8SettingKind.Double, minimum: 0, maximum: 1));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Observability:StatisticsElementBudget",
                Fallen8SettingKind.Int, minimum: 1));
            entries.Add(Fallen8SettingEntry.Restart("Fallen8:Observability:StatisticsTopN",
                Fallen8SettingKind.Int, minimum: 1));

            #endregion

            #region Fallen8:Plugins

            // New registrations only: the registry compares at registration and never evicts, so
            // lowering the ceiling leaves what a namespace already holds in place.
            entries.Add(Fallen8SettingEntry.LiveForNewWork("Fallen8:Plugins:MaxCount",
                Fallen8SettingKind.Int, ApplyPluginCeiling, minimum: 1));

            #endregion

            #region Fallen8:Security - rule R1 covers the whole section, without carve-outs

            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Security:ApiKey", Fallen8SettingKind.String, "R1",
                "Blanking it makes the handler authenticate nobody while the installed fallback policy "
                + "still demands a principal, which is a permanent 401 on every route with no way "
                + "back in over REST."));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Security:ApiKeyHeader", Fallen8SettingKind.String, "R1",
                "Renaming the header every caller authenticates with locks out every existing client "
                + "at once, including the one that would change it back."));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Security:EnableDynamicPluginLoading", Fallen8SettingKind.Bool, "R1",
                "It is the switch on registering full-trust compiled C# into this process, so "
                + "writing it is straightforward privilege escalation."));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Security:AllowedCorsOrigins", Fallen8SettingKind.Array, "R1",
                "It is the browser perimeter, and a written value invites a chosen origin to make "
                + "authenticated requests on a signed-in operator's behalf."));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Security:SensitiveRateLimitPermitPerWindow", Fallen8SettingKind.Int, "R1",
                "It is the only brake on the sensitive endpoints, so raising it removes the sole "
                + "bound on repeated code execution and plugin registration."));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Security:RateLimitWindowSeconds", Fallen8SettingKind.Int, "R1",
                "It is the other half of the only brake on the sensitive endpoints, and widening the "
                + "window has exactly the effect of raising the permit count."));
            // Catalogued rather than hidden. R1 already refuses the write, so an exemption would buy no
            // safety and would only break the derived-completeness gate; publishing it with its reason is
            // also how an operator learns the switch exists.
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Security:EnableConfigurationWrite", Fallen8SettingKind.Bool, "R1",
                "It is the gate on configuration writes themselves, so writing it would let the write "
                + "surface grant itself permission it was never given."));
            entries.Add(Fallen8SettingEntry.NotWritable("Fallen8:Security:BenchmarkMaxIterations", Fallen8SettingKind.Int, "R1",
                "It is the only bound on a benchmark pass, where each iteration saturates every core "
                + "and the loop cannot be interrupted once it has started."));

            #endregion

            #region Fallen8:StoredQueries

            // New registrations only, for the same reason as the plugin ceiling.
            entries.Add(Fallen8SettingEntry.LiveForNewWork("Fallen8:StoredQueries:MaxCount",
                Fallen8SettingKind.Int, ApplyStoredQueryCeiling, minimum: 1));

            #endregion

            return entries;
        }
    }
}
