// MIT License
//
// IntegrationsIdentityOptions.cs
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

namespace NoSQL.GraphDB.Integrations.Configuration
{
    /// <summary>
    ///   Tenant/instance identity (feature fleet-observability), bound from
    ///   <c>Integrations:Identity</c> - this runtime's mirror of the apiApp's <c>Fallen8:Identity</c>
    ///   and the MCP server's <c>Mcp:Identity</c>. This runtime feeds exactly one Fallen-8, so it
    ///   declares THAT target's identity and the fleet dashboards resolve its panels under that
    ///   instance rather than as an unrelated service. Ids default to auto-generated values so the
    ///   feature is on with zero config; names default to the id.
    /// </summary>
    public sealed class IntegrationsIdentityOptions
    {
        /// <summary>The configuration section this binds from.</summary>
        public const String SectionName = "Integrations:Identity";

        /// <summary>The tenant this runtime's target belongs to.</summary>
        public IdentityLevel Tenant { get; set; } = new IdentityLevel();

        /// <summary>The target Fallen-8 instance this runtime feeds.</summary>
        public IdentityLevel Instance { get; set; } = new IdentityLevel();

        /// <summary>
        ///   The four identity values as OTel resource attributes, applying the fleet defaults
        ///   (tenant id to <c>default</c>, instance id to a fresh GUID, each name to its id). Call
        ///   ONCE at startup: an unset instance id yields a new GUID per call.
        /// </summary>
        public IReadOnlyList<KeyValuePair<String, Object>> ResourceAttributes()
        {
            var tenantId = String.IsNullOrWhiteSpace(Tenant.Id) ? "default" : Tenant.Id!;
            var tenantName = String.IsNullOrWhiteSpace(Tenant.Name) ? tenantId : Tenant.Name!;
            var instanceId = String.IsNullOrWhiteSpace(Instance.Id)
                ? "f8-integrations-" + Guid.NewGuid().ToString("N").Substring(0, 12)
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

        /// <summary>One id+name level (tenant or instance). Null/blank means "auto-fill".</summary>
        public sealed class IdentityLevel
        {
            /// <summary>The stable machine identifier; auto-fills when unset.</summary>
            public String? Id { get; set; }

            /// <summary>The human-readable display name; defaults to the id when unset.</summary>
            public String? Name { get; set; }
        }
    }
}
