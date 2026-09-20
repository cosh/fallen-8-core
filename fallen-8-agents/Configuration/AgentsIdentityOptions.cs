// MIT License
//
// AgentsIdentityOptions.cs
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

namespace NoSQL.GraphDB.Agents.Configuration
{
    /// <summary>
    ///   Tenant/instance identity (feature fleet-observability 3.1), bound from
    ///   <c>Agents:Identity</c> and the third mirror of the apiApp's <c>Fallen8:Identity</c>,
    ///   after <c>Mcp:Identity</c> and <c>Integrations:Identity</c>.
    ///
    ///   <para>
    ///     This host runs agents against exactly ONE Fallen-8, so it declares THAT instance's
    ///     identity: set <c>Agents:Identity:Instance:Id</c> to the apiApp instance id it fronts and
    ///     the fleet dashboards resolve the agent panels under the same instance. Ids default to
    ///     auto-generated values so the feature is on with zero config; names default to the id.
    ///   </para>
    ///   <para>
    ///     Deliberately NOT derived from <see cref="Fallen8TargetOptions.BaseUrl" />, although that
    ///     names the instance: a URL is a route to a thing rather than the thing, two hosts can
    ///     reach one instance by different URLs, and one URL can be re-pointed. An operator saying
    ///     which instance this is stays the only reliable answer, which is the rule the other two
    ///     sidecars already follow.
    ///   </para>
    /// </summary>
    public sealed class AgentsIdentityOptions
    {
        /// <summary>The configuration section this binds from.</summary>
        public const String SectionName = "Agents:Identity";

        /// <summary>The tenant this host's target belongs to (<c>Agents:Identity:Tenant</c>).</summary>
        public IdentityLevel Tenant { get; set; } = new IdentityLevel();

        /// <summary>The Fallen-8 instance this host runs against (<c>Agents:Identity:Instance</c>).</summary>
        public IdentityLevel Instance { get; set; } = new IdentityLevel();

        /// <summary>
        ///   The four identity values as OTel resource attributes, applying the 3.1 defaults
        ///   (tenant id to <c>default</c>, instance id to a fresh GUID, each name to its id).
        ///   <para>
        ///     Call this ONCE at startup. An unset instance id mints a new GUID on every call, so a
        ///     second call would describe a second instance that does not exist; the wiring resolves
        ///     it once and reuses the value, as the MCP server's does.
        ///   </para>
        /// </summary>
        public IReadOnlyList<KeyValuePair<String, Object>> ResourceAttributes()
        {
            var tenantId = String.IsNullOrWhiteSpace(Tenant.Id) ? "default" : Tenant.Id!;
            var tenantName = String.IsNullOrWhiteSpace(Tenant.Name) ? tenantId : Tenant.Name!;
            var instanceId = String.IsNullOrWhiteSpace(Instance.Id)
                ? "f8-agents-" + Guid.NewGuid().ToString("N").Substring(0, 12)
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

        /// <summary>One id and name level (tenant or instance). Null or blank means auto-fill.</summary>
        public sealed class IdentityLevel
        {
            /// <summary>The stable machine identifier; auto-fills when unset.</summary>
            public String? Id
            {
                get; set;
            }

            /// <summary>The human-readable display name; defaults to the id when unset.</summary>
            public String? Name
            {
                get; set;
            }
        }
    }
}
