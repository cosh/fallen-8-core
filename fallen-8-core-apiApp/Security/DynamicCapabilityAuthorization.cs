// MIT License
//
// DynamicCapabilityAuthorization.cs
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

using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Namespaces;

namespace NoSQL.GraphDB.App.Security
{
    /// <summary>
    ///   The capability an operator must have enabled for a request to a gated endpoint to proceed
    ///   (feature api-security-boundary). Paired with <c>RequireAuthenticatedUser</c> in the
    ///   policy, so a gated endpoint needs BOTH an authenticated caller (else 401) AND the operator to
    ///   have flipped the capability on (else 403).
    /// </summary>
    public sealed class DynamicCapabilityRequirement : IAuthorizationRequirement
    {
        public enum Capability
        {
            DynamicPluginLoading,

            /// <summary>The embedding provider (feature embedding-provider,
            /// <c>Fallen8:Embedding:Enabled</c>) - default off: no model loads, nothing
            /// downloads, the embedding endpoints answer 403.</summary>
            EmbeddingProvider,

            /// <summary>The chat gateway (feature instance-config, <c>Fallen8:Chat:Enabled</c>) -
            /// default off: no backend client is constructed and <c>POST /chat</c> answers 403.</summary>
            Chat,

            /// <summary>Unstructured ingestion (feature unstructured-ingestion,
            /// <c>Fallen8:Ingestion:Enabled</c>) - default off: the document endpoints answer 403
            /// and no sidecar is contacted.</summary>
            Ingestion
        }

        public DynamicCapabilityRequirement(Capability which)
        {
            Which = which;
        }

        public Capability Which { get; }
    }

    /// <summary>
    ///   Succeeds the <see cref="DynamicCapabilityRequirement"/> only when the corresponding
    ///   <see cref="Fallen8SecurityOptions"/> / <see cref="Fallen8EmbeddingOptions"/> flag is enabled.
    ///   When the flag is off the requirement is left unmet, so an authenticated caller is Forbidden
    ///   (403) - the endpoint's DLL load / embedding-model use is never reached.
    /// </summary>
    public sealed class DynamicCapabilityAuthorizationHandler : AuthorizationHandler<DynamicCapabilityRequirement>
    {
        private readonly Fallen8SecurityOptions _security;
        private readonly Fallen8EmbeddingOptions _embedding;
        private readonly Fallen8ChatOptions _chat;
        private readonly Fallen8IngestionOptions _ingestion;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly Fallen8Namespaces _namespaces;

        public DynamicCapabilityAuthorizationHandler(IOptions<Fallen8SecurityOptions> security,
            IOptions<Fallen8EmbeddingOptions> embedding,
            IOptions<Fallen8ChatOptions> chat,
            IOptions<Fallen8IngestionOptions> ingestion,
            IHttpContextAccessor httpContextAccessor,
            Fallen8Namespaces namespaces)
        {
            _security = security.Value;
            _embedding = embedding.Value;
            _chat = chat.Value;
            _ingestion = ingestion.Value;
            _httpContextAccessor = httpContextAccessor;
            _namespaces = namespaces;
        }

        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, DynamicCapabilityRequirement requirement)
        {
            // Dynamic code execution is UNCONDITIONAL and has no capability here: running
            // agent-emitted C# fragments is Fallen-8's core "queries are C#" model, so the compile
            // endpoints (/path, /subgraph, /delegates/validate, /storedquery) are never gated off -
            // they carry only the standard auth (the fallback policy, required when an API key is
            // configured). Plugin registration and the embedding provider stay operator-controlled.
            bool enabled;
            switch (requirement.Which)
            {
                case DynamicCapabilityRequirement.Capability.DynamicPluginLoading:
                    enabled = ResolvePluginRegistrationEnabled();
                    break;
                case DynamicCapabilityRequirement.Capability.EmbeddingProvider:
                    enabled = _embedding.Enabled;
                    break;
                case DynamicCapabilityRequirement.Capability.Chat:
                    enabled = _chat.Enabled;
                    break;
                case DynamicCapabilityRequirement.Capability.Ingestion:
                    enabled = _ingestion.Enabled;
                    break;
                // Explicit so a capability added later cannot silently inherit the plugin gate.
                default:
                    throw new System.ArgumentOutOfRangeException(nameof(requirement),
                        requirement.Which, "Unhandled dynamic capability");
            }

            if (enabled)
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }

        /// <summary>
        ///   Resolves plugin-registration for the ADDRESSED namespace (feature plugin-registration):
        ///   the namespace's per-namespace override wins, and the global
        ///   <see cref="Fallen8SecurityOptions.EnableDynamicPluginLoading"/> default is the fallback.
        ///   The <c>ns</c> route value is populated by routing before authorization runs (the same
        ///   source <c>AddressedFallen8</c> reads); a bare route or an unknown namespace falls back to
        ///   the default namespace / global default (the validation filter 404s an unknown namespace
        ///   before the action runs regardless).
        /// </summary>
        private bool ResolvePluginRegistrationEnabled()
        {
            var name = _httpContextAccessor.HttpContext?
                .Request.RouteValues[NamespaceRouteConvention.RouteParameterName] as string;

            Namespace ns;
            if (name == null)
            {
                ns = _namespaces.Default;
            }
            else if (!_namespaces.TryGet(name, out ns))
            {
                ns = null;
            }

            return ns?.PluginRegistrationEnabled ?? _security.EnableDynamicPluginLoading;
        }
    }
}
