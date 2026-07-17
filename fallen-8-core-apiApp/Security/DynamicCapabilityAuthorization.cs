// MIT License
//
// DynamicCapabilityAuthorization.cs
//
// Copyright (c) 2025 Henning Rauch
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

using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.App.Configuration;

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
            DynamicCodeExecution,
            DynamicPluginLoading,

            /// <summary>The embedding provider (feature embedding-provider,
            /// <c>Fallen8:Embedding:Enabled</c>) - default off: no model loads, nothing
            /// downloads, the embedding endpoints answer 403.</summary>
            EmbeddingProvider
        }

        public DynamicCapabilityRequirement(Capability which)
        {
            Which = which;
        }

        public Capability Which { get; }
    }

    /// <summary>
    ///   Succeeds the <see cref="DynamicCapabilityRequirement"/> only when the corresponding
    ///   <see cref="Fallen8SecurityOptions"/> flag is enabled. When the flag is off the requirement is
    ///   left unmet, so an authenticated caller is Forbidden (403) - the endpoint's compilation / DLL
    ///   load is never reached.
    /// </summary>
    public sealed class DynamicCapabilityAuthorizationHandler : AuthorizationHandler<DynamicCapabilityRequirement>
    {
        private readonly Fallen8SecurityOptions _security;
        private readonly Fallen8EmbeddingOptions _embedding;

        public DynamicCapabilityAuthorizationHandler(IOptions<Fallen8SecurityOptions> security,
            IOptions<Fallen8EmbeddingOptions> embedding)
        {
            _security = security.Value;
            _embedding = embedding.Value;
        }

        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, DynamicCapabilityRequirement requirement)
        {
            var enabled = requirement.Which switch
            {
                DynamicCapabilityRequirement.Capability.DynamicCodeExecution => _security.EnableDynamicCodeExecution,
                DynamicCapabilityRequirement.Capability.EmbeddingProvider => _embedding.Enabled,
                _ => _security.EnableDynamicPluginLoading
            };

            if (enabled)
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    ///   The single home for the imperative dynamic-code capability check shared by the
    ///   request-shape-aware inline-code endpoints (<c>/path</c>, <c>/subgraph</c>). It evaluates the
    ///   SAME <see cref="DynamicCapabilityRequirement"/> the declarative <c>DynamicCodePolicy</c> uses
    ///   (one source of truth) and returns <see langword="true"/> when the request must be denied
    ///   (the caller answers with <c>Forbid()</c>, the same 403 shape), <see langword="false"/> when
    ///   the capability is enabled. A null authorization service means direct construction (unit
    ///   tests bypass the pipeline exactly as they bypassed the former endpoint-level policy); the
    ///   hosted pipeline always supplies the service. The awaited handler is synchronous (an options
    ///   check), so blocking here cannot deadlock.
    /// </summary>
    public static class DynamicCodeCapabilityGate
    {
        public static bool IsDenied(IAuthorizationService authorizationService, ClaimsPrincipal user)
        {
            if (authorizationService == null)
            {
                return false;
            }

            var authorization = authorizationService.AuthorizeAsync(user, null,
                    new DynamicCapabilityRequirement(DynamicCapabilityRequirement.Capability.DynamicCodeExecution))
                .GetAwaiter().GetResult();

            return !authorization.Succeeded;
        }
    }
}
