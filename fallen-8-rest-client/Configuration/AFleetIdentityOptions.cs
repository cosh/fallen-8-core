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
    ///   auto-fill, which <see cref="AFleetIdentityOptions.Resolve" /> applies.
    /// </summary>
    public sealed class IdentityLevel
    {
        /// <summary>The stable machine identifier; auto-fills when unset.</summary>
        public String? Id { get; set; }

        /// <summary>The human-readable display name; defaults to the id when unset.</summary>
        public String? Name { get; set; }
    }

    /// <summary>
    ///   A sidecar's identity with every default already applied: four values that are never null or
    ///   empty, and the resource attributes built from them.
    ///
    ///   <para>
    ///     It exists because reading one of those values back out of the attribute LIST was the third
    ///     copy of the same line in three deployables
    ///     (<c>attributes.First(kv =&gt; kv.Key == "fallen8.instance.id")</c>), and because the value
    ///     that line recovers is needed on its own: OTel's <c>service.instance.id</c> must be THIS
    ///     id rather than the SDK's random per-process GUID, or the promoted label churns on every
    ///     restart. The apiApp's own <c>Fallen8Identity</c> has had the same shape from the start;
    ///     this is that shape for the deployables beside it.
    ///   </para>
    /// </summary>
    public sealed class FleetIdentity
    {
        /// <summary>Resource-attribute key for the tenant id.</summary>
        public const String TenantIdKey = "fallen8.tenant.id";

        /// <summary>Resource-attribute key for the tenant name.</summary>
        public const String TenantNameKey = "fallen8.tenant.name";

        /// <summary>Resource-attribute key for the instance id.</summary>
        public const String InstanceIdKey = "fallen8.instance.id";

        /// <summary>Resource-attribute key for the instance name.</summary>
        public const String InstanceNameKey = "fallen8.instance.name";

        internal FleetIdentity(String tenantId, String tenantName, String instanceId, String instanceName)
        {
            TenantId = tenantId;
            TenantName = tenantName;
            InstanceId = instanceId;
            InstanceName = instanceName;
        }

        /// <summary>The effective tenant id, never null or empty.</summary>
        public String TenantId { get; }

        /// <summary>The effective tenant name, never null or empty.</summary>
        public String TenantName { get; }

        /// <summary>The effective instance id, never null or empty. Also what a consumer passes as
        /// OTel's <c>service.instance.id</c>.</summary>
        public String InstanceId { get; }

        /// <summary>The effective instance name, never null or empty.</summary>
        public String InstanceName { get; }

        /// <summary>The four values as OTel resource attributes, attached to every metric, trace and
        /// log the process emits.</summary>
        public IReadOnlyList<KeyValuePair<String, Object>> Attributes()
        {
            return new[]
            {
                new KeyValuePair<String, Object>(TenantIdKey, TenantId),
                new KeyValuePair<String, Object>(TenantNameKey, TenantName),
                new KeyValuePair<String, Object>(InstanceIdKey, InstanceId),
                new KeyValuePair<String, Object>(InstanceNameKey, InstanceName),
            };
        }
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
        ///   Applies the fleet defaults (tenant id to <c>default</c>, instance id to a prefixed
        ///   fresh GUID, each name to its id) and returns the result.
        ///   <para>
        ///     <b>Call this ONCE at startup.</b> An unset instance id mints a new GUID on every
        ///     call, so a second call would describe a second instance that does not exist. No
        ///     clock is read, only <see cref="Guid.NewGuid()" />.
        ///   </para>
        /// </summary>
        public FleetIdentity Resolve()
        {
            var tenantId = String.IsNullOrWhiteSpace(Tenant?.Id) ? "default" : Tenant!.Id!;
            var tenantName = String.IsNullOrWhiteSpace(Tenant?.Name) ? tenantId : Tenant!.Name!;
            var instanceId = String.IsNullOrWhiteSpace(Instance?.Id)
                ? _instanceIdPrefix + Guid.NewGuid().ToString("N").Substring(0, 12)
                : Instance!.Id!;
            var instanceName = String.IsNullOrWhiteSpace(Instance?.Name) ? instanceId : Instance!.Name!;

            return new FleetIdentity(tenantId, tenantName, instanceId, instanceName);
        }
    }
}
