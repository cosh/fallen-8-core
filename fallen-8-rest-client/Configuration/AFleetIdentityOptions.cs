// MIT License
//
// AFleetIdentityOptions.cs
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

namespace NoSQL.GraphDB.Rest.Configuration
{
    /// <summary>
    ///   One id and name level of the fleet identity (a tenant or an instance). Null or blank means
    ///   auto-fill, which <see cref="AFleetIdentityOptions.ResourceAttributes" /> applies.
    /// </summary>
    public sealed class IdentityLevel
    {
        /// <summary>The stable machine identifier; auto-fills when unset.</summary>
        public String? Id { get; set; }

        /// <summary>The human-readable display name; defaults to the id when unset.</summary>
        public String? Name { get; set; }
    }

    /// <summary>
    ///   Tenant and instance identity for a deployable that sits beside exactly one Fallen-8
    ///   (feature fleet-observability 3.1), and the mirror of the apiApp's <c>Fallen8:Identity</c>.
    ///
    ///   <para>
    ///     Each sidecar fronts ONE instance, so it declares THAT instance's identity: point
    ///     <c>Instance:Id</c> at the apiApp instance id it serves and the fleet dashboards resolve
    ///     its panels under the same instance rather than as an unrelated service. Ids default to
    ///     auto-generated values so the feature is on with zero configuration; names default to the
    ///     id. The section a deployable binds this from is its own, because that is what an
    ///     operator writes: <c>Mcp:Identity</c>, <c>Integrations:Identity</c>,
    ///     <c>Agents:Identity</c>.
    ///   </para>
    ///   <para>
    ///     Deliberately NOT derived from the target's base URL, although that also names the
    ///     instance: a URL is a route to a thing rather than the thing, two deployables can reach
    ///     one instance by different URLs, and one URL can be re-pointed. An operator saying which
    ///     instance this is stays the only reliable answer.
    ///   </para>
    /// </summary>
    public abstract class AFleetIdentityOptions
    {
        private readonly String _instanceIdPrefix;

        /// <summary>
        ///   Sets the prefix an auto-generated instance id carries.
        /// </summary>
        /// <param name="instanceIdPrefix">
        ///   What an unset <see cref="Instance" /> id is prefixed with, for example
        ///   <c>f8-mcp-</c>. It is the one thing that differs between the deployables' identity
        ///   blocks, and it exists so an auto-filled id says which deployable minted it.
        /// </param>
        protected AFleetIdentityOptions(String instanceIdPrefix)
        {
            _instanceIdPrefix = instanceIdPrefix ?? String.Empty;
        }

        /// <summary>The tenant this deployable's target belongs to.</summary>
        public IdentityLevel Tenant { get; set; } = new IdentityLevel();

        /// <summary>The Fallen-8 instance this deployable sits beside.</summary>
        public IdentityLevel Instance { get; set; } = new IdentityLevel();

        /// <summary>
        ///   The four identity values as OTel resource attributes, applying the fleet defaults
        ///   (tenant id to <c>default</c>, instance id to a prefixed fresh GUID, each name to its
        ///   id).
        ///   <para>
        ///     <b>Call this ONCE at startup.</b> An unset instance id mints a new GUID on every
        ///     call, so a second call would describe a second instance that does not exist. Every
        ///     consumer's wiring resolves it once and reuses the value, including for
        ///     <c>service.instance.id</c>, so the promoted label does not churn across restarts.
        ///   </para>
        /// </summary>
        public IReadOnlyList<KeyValuePair<String, Object>> ResourceAttributes()
        {
            var tenantId = String.IsNullOrWhiteSpace(Tenant.Id) ? "default" : Tenant.Id!;
            var tenantName = String.IsNullOrWhiteSpace(Tenant.Name) ? tenantId : Tenant.Name!;
            var instanceId = String.IsNullOrWhiteSpace(Instance.Id)
                ? _instanceIdPrefix + Guid.NewGuid().ToString("N").Substring(0, 12)
                : Instance.Id!;
            var instanceName = String.IsNullOrWhiteSpace(Instance.Name) ? instanceId : Instance.Name!;

            return new[]
            {
                new KeyValuePair<String, Object>("fallen8.tenant.id", tenantId),
                new KeyValuePair<String, Object>("fallen8.tenant.name", tenantName),
                new KeyValuePair<String, Object>("fallen8.instance.id", instanceId),
                new KeyValuePair<String, Object>("fallen8.instance.name", instanceName),
            };
        }
    }
}
