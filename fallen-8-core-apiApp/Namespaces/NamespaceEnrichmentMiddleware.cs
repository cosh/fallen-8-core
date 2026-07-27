// MIT License
//
// NamespaceEnrichmentMiddleware.cs
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
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace NoSQL.GraphDB.App.Namespaces
{
    /// <summary>
    ///   Stamps the addressed namespace's id + name onto host-originated signals (feature
    ///   fleet-observability, spec §3.5): (a) the built-in HTTP server metric via
    ///   <see cref="IHttpMetricsTagsFeature"/>, (b) the ambient request <see cref="Activity"/>,
    ///   and (c) an <see cref="ILogger"/> scope so every log line in the request carries them.
    ///
    ///   <para>The id is the collection-assigned <see cref="Namespace.Id"/> (never the user name),
    ///   matching the engine meter's <c>fallen8.scope.id</c> tag so the Collector's
    ///   <c>fallen8_namespace_info</c> join lines up; the human name rides only on these
    ///   host-originated signals (identity dimensions are the narrowed tag-hygiene exception,
    ///   §3.3). Registered only when an exporter is enabled - zero cost otherwise.</para>
    /// </summary>
    public sealed class NamespaceEnrichmentMiddleware
    {
        /// <summary>The namespace-id tag key; identical to the engine meter's scope tag (the join key).</summary>
        public const String ScopeIdTag = "fallen8.scope.id";

        /// <summary>The human namespace-name tag key (host-originated signals only).</summary>
        public const String NamespaceNameTag = "fallen8.namespace.name";

        private readonly RequestDelegate _next;
        private readonly Fallen8Namespaces _namespaces;
        private readonly ILogger<NamespaceEnrichmentMiddleware> _logger;

        public NamespaceEnrichmentMiddleware(RequestDelegate next, Fallen8Namespaces namespaces,
            ILogger<NamespaceEnrichmentMiddleware> logger)
        {
            _next = next;
            _namespaces = namespaces;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            String id = null;
            String name = null;

            // Same resolution as AddressedFallen8 / NamespaceValidationFilter: the /ns/{ns} route
            // value, or the bare-URL alias to the reserved default namespace.
            var routeName = context.Request.RouteValues[NamespaceRouteConvention.RouteParameterName] as String;
            if (routeName == null)
            {
                id = _namespaces.Default.Id;
                name = _namespaces.Default.Name;
            }
            else if (_namespaces.TryGet(routeName, out var ns))
            {
                id = ns.Id;
                name = ns.Name;
            }
            // else: an unknown namespace - NamespaceValidationFilter answers 404 downstream; tag nothing.

            if (id == null)
            {
                await _next(context);
                return;
            }

            // (a) the built-in HTTP server metric (http.server.request.duration). The feature is
            //     present whenever the Hosting meter has listeners; recorded at request completion,
            //     so adding here (before the action) is in time.
            var metricsTags = context.Features.Get<IHttpMetricsTagsFeature>();
            if (metricsTags != null)
            {
                metricsTags.Tags.Add(new KeyValuePair<String, Object>(ScopeIdTag, id));
                metricsTags.Tags.Add(new KeyValuePair<String, Object>(NamespaceNameTag, name));
            }

            // (b) the ambient request Activity (the Microsoft.AspNetCore span, an exported source).
            //     The controller spans (fallen8.path.search / subgraph.run / analytics.run) are its
            //     children and correlate by trace context.
            var activity = Activity.Current;
            activity?.SetTag(ScopeIdTag, id);
            activity?.SetTag(NamespaceNameTag, name);

            // (c) an ILogger scope: with IncludeScopes on the OTel logger, every log line the request
            //     emits carries the namespace id + name via the shared external scope provider.
            using (_logger.BeginScope(new Dictionary<String, Object>
            {
                [ScopeIdTag] = id,
                [NamespaceNameTag] = name,
            }))
            {
                await _next(context);
            }
        }
    }
}
