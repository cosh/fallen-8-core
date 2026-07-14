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

using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using NoSQL.GraphDB.App.Configuration;

namespace NoSQL.GraphDB.App.Security
{
    /// <summary>
    ///   The capability an operator must have enabled for a request to a gated endpoint to proceed
    ///   (feature api-security-boundary). Paired with <see cref="RequireAuthenticatedUser"/> in the
    ///   policy, so a gated endpoint needs BOTH an authenticated caller (else 401) AND the operator to
    ///   have flipped the capability on (else 403).
    /// </summary>
    public sealed class DynamicCapabilityRequirement : IAuthorizationRequirement
    {
        public enum Capability
        {
            DynamicCodeExecution,
            DynamicPluginLoading
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

        public DynamicCapabilityAuthorizationHandler(IOptions<Fallen8SecurityOptions> security)
        {
            _security = security.Value;
        }

        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, DynamicCapabilityRequirement requirement)
        {
            var enabled = requirement.Which == DynamicCapabilityRequirement.Capability.DynamicCodeExecution
                ? _security.EnableDynamicCodeExecution
                : _security.EnableDynamicPluginLoading;

            if (enabled)
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }
    }
}
