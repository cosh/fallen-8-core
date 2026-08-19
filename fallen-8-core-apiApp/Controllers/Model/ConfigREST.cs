// MIT License
//
// ConfigREST.cs
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
using NoSQL.GraphDB.App.Chat;
using NoSQL.GraphDB.App.Configuration;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>The chat gateway state (feature instance-config), mirroring the embedding block's
    /// shape: it lives on the cheap /status probe (capability discovery) and on GET /config (the
    /// operator view). No endpoint is exposed, matching the embedding block's tag hygiene.</summary>
    public sealed class ChatProviderStatsREST
    {
        /// <summary>Builds the state from the provider; null when the host wired no provider. The
        /// GPU field stays null here (the /status path never probes) - GET /config sets it.</summary>
        public static ChatProviderStatsREST From(Fallen8ChatProvider provider)
        {
            if (provider == null)
            {
                return null;
            }

            return new ChatProviderStatsREST
            {
                Enabled = provider.IsEnabled,
                Backend = provider.Backend,
                Model = provider.Model,
                Loaded = provider.IsLoaded
            };
        }

        /// <summary>Whether the capability flag (Fallen8:Chat:Enabled) is on.</summary>
        [JsonPropertyName("enabled")]
        public Boolean Enabled
        {
            get; set;
        }

        /// <summary>The backend selector (config value), e.g. Ollama.</summary>
        [JsonPropertyName("backend")]
        public String Backend
        {
            get; set;
        }

        /// <summary>The server-owned model.</summary>
        [JsonPropertyName("model")]
        public String Model
        {
            get; set;
        }

        /// <summary>Whether the backend client has been created (a chat call happened).</summary>
        [JsonPropertyName("loaded")]
        public Boolean Loaded
        {
            get; set;
        }

        /// <summary>Whether the model is currently loaded in the backend (Ollama /api/ps): true =
        /// warm, false = not loaded right now (loads on first use), null = undeterminable or not an
        /// Ollama backend. A point-in-time read, only set on GET /config.</summary>
        [JsonPropertyName("resident")]
        public Boolean? Resident
        {
            get; set;
        }

        /// <summary>Best-effort GPU residency of the model: true/false when the backend reports it,
        /// null when undeterminable (or not probed). A point-in-time read, only set on GET /config.</summary>
        [JsonPropertyName("gpu")]
        public Boolean? Gpu
        {
            get; set;
        }
    }

    /// <summary>The observability posture (feature observability), read-only. Endpoints are
    /// operator config (never a secret); this codebase's OTLP options carry only an endpoint.</summary>
    public sealed class ObservabilityConfigREST
    {
        public static ObservabilityConfigREST From(Fallen8ObservabilityOptions options)
        {
            options ??= new Fallen8ObservabilityOptions();
            return new ObservabilityConfigREST
            {
                OtlpEnabled = !String.IsNullOrWhiteSpace(options.Otlp?.Endpoint),
                OtlpEndpoint = String.IsNullOrWhiteSpace(options.Otlp?.Endpoint) ? null : options.Otlp.Endpoint,
                PrometheusEnabled = options.Prometheus?.Enabled ?? false,
                PrometheusRequireApiKey = options.Prometheus?.RequireApiKey ?? false,
                TracingSamplingRatio = options.TracingSamplingRatio,
                StatisticsElementBudget = options.StatisticsElementBudget,
                StatisticsTopN = options.StatisticsTopN
            };
        }

        /// <summary>Whether OTLP push export is on (an endpoint is configured).</summary>
        [JsonPropertyName("otlpEnabled")]
        public Boolean OtlpEnabled
        {
            get; set;
        }

        /// <summary>The OTLP endpoint metrics/traces/logs are pushed to, as configured; null when off.</summary>
        [JsonPropertyName("otlpEndpoint")]
        public String OtlpEndpoint
        {
            get; set;
        }

        /// <summary>Whether the Prometheus scrape endpoint (GET /metrics) is mapped.</summary>
        [JsonPropertyName("prometheusEnabled")]
        public Boolean PrometheusEnabled
        {
            get; set;
        }

        /// <summary>Whether /metrics requires the API key (vs the anonymous default).</summary>
        [JsonPropertyName("prometheusRequireApiKey")]
        public Boolean PrometheusRequireApiKey
        {
            get; set;
        }

        /// <summary>Root trace sampling ratio [0, 1].</summary>
        [JsonPropertyName("tracingSamplingRatio")]
        public Double TracingSamplingRatio
        {
            get; set;
        }

        /// <summary>The GET /statistics element budget before sampling kicks in.</summary>
        [JsonPropertyName("statisticsElementBudget")]
        public Int32 StatisticsElementBudget
        {
            get; set;
        }

        /// <summary>Top-N size for the statistics cardinality lists.</summary>
        [JsonPropertyName("statisticsTopN")]
        public Int32 StatisticsTopN
        {
            get; set;
        }
    }

    /// <summary>The semantic providers (embedding + chat) grouped as one view.</summary>
    public sealed class SemanticConfigREST
    {
        /// <summary>The embedding provider state (may be null when no provider is wired).</summary>
        [JsonPropertyName("embedding")]
        public EmbeddingProviderStatsREST Embedding
        {
            get; set;
        }

        /// <summary>The chat gateway state (may be null when no provider is wired).</summary>
        [JsonPropertyName("chat")]
        public ChatProviderStatsREST Chat
        {
            get; set;
        }
    }

    /// <summary>
    ///   The instance's configuration view (features instance-config and writable-instance-config),
    ///   the single home for the Studio Configuration section: every catalogued setting, the semantic
    ///   providers and the observability posture. Secrets are never emitted (no API key, no
    ///   credentials); only the boolean <see cref="ApiKeyRequired"/> reports the security posture, and
    ///   a never-writable setting publishes its tier and reason but no value, because this route is
    ///   anonymous on an instance with no API key configured.
    /// </summary>
    public sealed class ConfigREST
    {
        /// <summary>
        ///   Every bound configuration key with its tier, the layer its value comes from, and whether a
        ///   written value is waiting for a restart. The live inventory of this instance's
        ///   configuration: what an operator may change, and what they may not, with the reason.
        /// </summary>
        [JsonPropertyName("settings")]
        public List<SettingREST> Settings
        {
            get; set;
        }

        /// <summary>
        ///   The keys whose configured value differs from the value this process started with, so a
        ///   restart would change behaviour (the derivation and its wording rules live on
        ///   <see cref="Fallen8ConfigOverrides"/>), plus any live key whose apply failed.
        /// </summary>
        [JsonPropertyName("pendingRestart")]
        public List<PendingRestartREST> PendingRestart
        {
            get; set;
        }

        /// <summary>
        ///   Whether this instance accepts <c>PATCH /config</c>: an API key is configured AND
        ///   <c>Fallen8:Security:EnableConfigurationWrite</c> is on. Published so a client can render
        ///   the settings read-only instead of offering a Save the server would always refuse; the
        ///   flag's own value stays withheld in <see cref="Settings"/> like every other security key.
        /// </summary>
        [JsonPropertyName("configWriteEnabled")]
        public Boolean ConfigWriteEnabled
        {
            get; set;
        }

        /// <summary>The semantic providers (embedding + chat).</summary>
        [JsonPropertyName("semantic")]
        public SemanticConfigREST Semantic
        {
            get; set;
        }

        /// <summary>The observability posture (OTLP / Prometheus / sampling).</summary>
        [JsonPropertyName("observability")]
        public ObservabilityConfigREST Observability
        {
            get; set;
        }

        /// <summary>Whether an API key is configured (never the key itself).</summary>
        [JsonPropertyName("apiKeyRequired")]
        public Boolean ApiKeyRequired
        {
            get; set;
        }
    }
}
