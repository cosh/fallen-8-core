// MIT License
//
// McpIdentityOptions.cs
//
// Copyright (c) 2026 Henning Rauch
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

namespace NoSQL.GraphDB.Mcp.Configuration
{
    /// <summary>
    ///   Tenant/instance identity (feature fleet-observability §3.1), bound from
    ///   <c>Mcp:Identity</c> - the MCP mirror of the apiApp's <c>Fallen8:Identity</c>. The MCP
    ///   fronts exactly one target Fallen-8, so it declares THAT target's identity: set
    ///   <c>Mcp:Identity:Instance:Id</c> to the apiApp instance id it bridges so the fleet
    ///   dashboards resolve the MCP panels under the same instance. Ids default to auto-generated
    ///   values so the feature is on with zero config; names default to the id.
    /// </summary>
    public sealed class McpIdentityOptions
    {
        /// <summary>The configuration section this binds from.</summary>
        public const String SectionName = "Mcp:Identity";

        /// <summary>The tenant this MCP's target belongs to (<c>Mcp:Identity:Tenant</c>).</summary>
        public IdentityLevel Tenant { get; set; } = new IdentityLevel();

        /// <summary>The target Fallen-8 instance this MCP fronts (<c>Mcp:Identity:Instance</c>).</summary>
        public IdentityLevel Instance { get; set; } = new IdentityLevel();

        /// <summary>
        ///   The four identity values as OTel resource attributes, applying the §3.1 defaults
        ///   (tenant id -> "default", instance id -> a fresh GUID, each name -> its id). Call
        ///   ONCE at startup: an unset instance id yields a new GUID per call.
        /// </summary>
        public IReadOnlyList<KeyValuePair<String, Object>> ResourceAttributes()
        {
            var tenantId = String.IsNullOrWhiteSpace(Tenant.Id) ? "default" : Tenant.Id!;
            var tenantName = String.IsNullOrWhiteSpace(Tenant.Name) ? tenantId : Tenant.Name!;
            var instanceId = String.IsNullOrWhiteSpace(Instance.Id) ? "f8-mcp-" + Guid.NewGuid().ToString("N").Substring(0, 12) : Instance.Id!;
            var instanceName = String.IsNullOrWhiteSpace(Instance.Name) ? instanceId : Instance.Name!;

            return new[]
            {
                new KeyValuePair<String, Object>("fallen8.tenant.id", tenantId),
                new KeyValuePair<String, Object>("fallen8.tenant.name", tenantName),
                new KeyValuePair<String, Object>("fallen8.instance.id", instanceId),
                new KeyValuePair<String, Object>("fallen8.instance.name", instanceName),
            };
        }

        /// <summary>One id+name level (tenant or instance). Null/blank means "auto-fill" (§3.1).</summary>
        public sealed class IdentityLevel
        {
            /// <summary>The stable machine identifier; auto-fills when unset.</summary>
            public String? Id { get; set; }

            /// <summary>The human-readable display name; defaults to the id when unset.</summary>
            public String? Name { get; set; }
        }
    }
}
