// MIT License
//
// NamespaceValidationFilter.cs
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
using System.Linq;
using Microsoft.AspNetCore.Mvc.Filters;

namespace NoSQL.GraphDB.App.Namespaces
{
    /// <summary>
    ///   Global resource filter that refuses a <c>/ns/{ns}/…</c> route BEFORE any action runs when
    ///   the named namespace cannot serve it: 404 problem+json when the collection does not hold it
    ///   (feature graph-namespaces, spec §5.1), 503 when it holds it but has no engine for it in this
    ///   process (feature namespace-startup-load, spec §4.7). Both bodies come from
    ///   <see cref="NamespaceProblems"/> and carry the <c>namespace</c> extension member - the stable
    ///   marker F8 Studio keys its "recreate or switch" recover state on, which is exactly why the
    ///   not-loaded case must NOT be a 404. Bare routes carry no <c>ns</c> route value and pass
    ///   through untouched, EXCEPT for a <see cref="NamespaceRequiredAttribute"/> action, which has no
    ///   default-namespace alias and is refused with 400 instead.
    ///
    ///   <para>The namespace MANAGEMENT routes (<c>GET /ns</c>, <c>GET|PUT|PATCH|DELETE /ns/{name}</c>,
    ///   <c>POST /ns/{name}/activate</c>) are never refused here, and that is structural rather than a
    ///   special case: their controller is <see cref="Fallen8LevelAttribute"/>-marked, so
    ///   <see cref="NamespaceRouteConvention"/> gives them no <c>/ns/{ns}</c> twin, and their own route
    ///   parameter is <c>name</c>, not <c>ns</c>. An operator can therefore always list, ACTIVATE,
    ///   re-configure (turn the startup-load policy back on) and drop a not-loaded namespace - without
    ///   that, a wrong exclusion would be unrecoverable over REST, and activation in particular would
    ///   be refused by the very state it exists to leave.
    ///   <see cref="NamespaceResidencyOptionalAttribute"/> covers the one data-plane action that must
    ///   also answer.</para>
    /// </summary>
    public sealed class NamespaceValidationFilter : IResourceFilter
    {
        private readonly Fallen8Namespaces _namespaces;

        public NamespaceValidationFilter(Fallen8Namespaces namespaces)
        {
            _namespaces = namespaces;
        }

        public void OnResourceExecuting(ResourceExecutingContext context)
        {
            if (!context.RouteData.Values.TryGetValue(NamespaceRouteConvention.RouteParameterName, out var value)
                || !(value is String name))
            {
                // A bare URL. For almost every namespace-scoped action that IS the default-namespace
                // alias; an action that refuses to pick a graph for the caller is answered instead.
                // The matched route template - not the request path - is echoed, so the message never
                // reflects unvalidated input back at the caller.
                if (context.ActionDescriptor.EndpointMetadata.OfType<NamespaceRequiredAttribute>().Any())
                {
                    context.Result = NamespaceProblems.NamespaceRequired(
                        context.ActionDescriptor.AttributeRouteInfo?.Template);
                }

                return;
            }

            if (!_namespaces.TryGet(name, out var ns))
            {
                context.Result = NamespaceProblems.NotFound(name);
                return;
            }

            if (!ns.IsLoaded && !context.ActionDescriptor.EndpointMetadata
                .OfType<NamespaceResidencyOptionalAttribute>().Any())
            {
                context.Result = NamespaceProblems.NotLoaded(name);
            }
        }

        public void OnResourceExecuted(ResourceExecutedContext context)
        {
        }
    }

    /// <summary>
    ///   Marks a namespace-scoped action that must still answer for a namespace with no engine
    ///   (feature namespace-startup-load): it reports residency instead of touching one. Only
    ///   <c>GET /status</c> carries it - the anonymous connection/capability probe every client calls
    ///   first, which a 503 would turn into "the server is down" rather than "this one namespace is
    ///   not loaded". An action wearing this attribute MUST branch on
    ///   <see cref="Namespace.IsLoaded"/> itself; the throwing <see cref="Namespace.Engine"/> accessor
    ///   is the backstop if it forgets.
    ///   <para>Deliberately <see cref="AttributeTargets.Method"/> only, and not inherited: a
    ///   controller-level or inherited exemption would waive the 503 for every action beside the one
    ///   that reasoned about it, which is how a data route silently starts answering over a namespace
    ///   it has no engine for. <c>NamespaceResidencyConventionTest</c> pins the set of actions
    ///   carrying it.</para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class NamespaceResidencyOptionalAttribute : Attribute
    {
    }

    /// <summary>
    ///   Marks a namespace-scoped action (or a whole controller) that has NO bare-URL alias to the
    ///   default namespace: reached over its bare route it is refused with
    ///   <see cref="NamespaceProblems.NamespaceRequired"/> rather than served against
    ///   <c>default</c>. The opposite end of the scale from <see cref="Fallen8LevelAttribute"/>,
    ///   which marks an action that concerns the whole collection: this one concerns exactly one
    ///   graph and declines to guess which.
    ///   <para>Only the benchmark controller carries it (feature graph-namespaces): generation
    ///   WRITES a graph and the benchmark MEASURES one, and a caller who left the namespace out of
    ///   the URL almost certainly meant the one they were working in - the bare alias made
    ///   "generate into default" the silent outcome of every such call.</para>
    ///   <para>The bare route is still REGISTERED (the convention keeps twinning, this only refuses
    ///   at request time) because an unrouted path would be answered by the SPA fallback with the app
    ///   shell and HTTP 200 - see <see cref="NamespaceProblems.NamespaceRequired"/>.</para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class NamespaceRequiredAttribute : Attribute
    {
    }
}
