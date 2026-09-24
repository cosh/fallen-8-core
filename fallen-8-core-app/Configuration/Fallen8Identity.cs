// MIT License
//
// Fallen8Identity.cs
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

namespace NoSQL.GraphDB.App.Configuration
{
    /// <summary>
    ///   The RESOLVED fleet identity (feature fleet-observability): the effective tenant + instance
    ///   id/name after defaults are applied to <see cref="Fallen8IdentityOptions"/>. Constructed
    ///   ONCE at startup and registered as a singleton, so the auto-generated instance id is stable
    ///   for the process lifetime (it is stamped onto the OTel resource, the
    ///   <c>fallen8.namespace.info</c> gauge, and the request-scoped namespace enrichment).
    ///
    ///   <para>Defaults: an unset tenant id becomes <c>default</c>; an unset instance id becomes a
    ///   fresh <c>f8-</c> GUID (stable for this process, so set <c>Fallen8:Identity:Instance:Id</c>
    ///   for continuity across restarts); an unset name falls back to its id. No clock is read (the
    ///   engine's <c>DateTime.Now</c> ban applies), only <see cref="Guid.NewGuid()"/>.</para>
    /// </summary>
    public sealed class Fallen8Identity
    {
        /// <summary>Resource-attribute key for the tenant id.</summary>
        public const String TenantIdKey = "fallen8.tenant.id";

        /// <summary>Resource-attribute key for the tenant name.</summary>
        public const String TenantNameKey = "fallen8.tenant.name";

        /// <summary>Resource-attribute key for the instance id.</summary>
        public const String InstanceIdKey = "fallen8.instance.id";

        /// <summary>Resource-attribute key for the instance name.</summary>
        public const String InstanceNameKey = "fallen8.instance.name";

        public Fallen8Identity(Fallen8IdentityOptions options)
        {
            var opts = options ?? new Fallen8IdentityOptions();
            TenantId = Coalesce(opts.Tenant?.Id, "default");
            TenantName = Coalesce(opts.Tenant?.Name, TenantId);
            InstanceId = Coalesce(opts.Instance?.Id, "f8-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            InstanceName = Coalesce(opts.Instance?.Name, InstanceId);
        }

        /// <summary>The effective tenant id (never null/empty).</summary>
        public String TenantId { get; }

        /// <summary>The effective tenant name (never null/empty).</summary>
        public String TenantName { get; }

        /// <summary>The effective instance id (never null/empty).</summary>
        public String InstanceId { get; }

        /// <summary>The effective instance name (never null/empty).</summary>
        public String InstanceName { get; }

        /// <summary>The four identity dimensions as OTel resource attributes (attached to every
        /// metric, trace, and log the process emits).</summary>
        public IEnumerable<KeyValuePair<String, Object>> ResourceAttributes()
        {
            yield return new KeyValuePair<String, Object>(TenantIdKey, TenantId);
            yield return new KeyValuePair<String, Object>(TenantNameKey, TenantName);
            yield return new KeyValuePair<String, Object>(InstanceIdKey, InstanceId);
            yield return new KeyValuePair<String, Object>(InstanceNameKey, InstanceName);
        }

        private static String Coalesce(String value, String fallback)
        {
            return String.IsNullOrWhiteSpace(value) ? fallback : value;
        }
    }
}
