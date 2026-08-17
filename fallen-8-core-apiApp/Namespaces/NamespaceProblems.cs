// MIT License
//
// NamespaceProblems.cs
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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NoSQL.GraphDB.App.Helper;

namespace NoSQL.GraphDB.App.Namespaces
{
    /// <summary>
    ///   The single home for the "namespace not found" 404 problem+json body: the title, the
    ///   detail wording, and the Studio-facing <c>namespace</c> extension member the client keys
    ///   its recover-state on. The three sites that emit it - the pre-action
    ///   <see cref="NamespaceValidationFilter"/>, its drop/rename-race twin
    ///   <c>UnknownNamespaceExceptionFilter</c>, and the <c>NamespacesController</c> management
    ///   lookups - all build it here, so the data-plane 404, the race 404, and the management 404
    ///   can never diverge (the "indistinguishable from arriving a moment later" guarantee).
    /// </summary>
    internal static class NamespaceProblems
    {
        internal static ObjectResult NotFound(String name) =>
            ProblemResults.Create(StatusCodes.Status404NotFound, "Namespace not found",
                "No namespace named \"" + name + "\" exists on this Fallen-8.",
                p => p.Extensions["namespace"] = name);

        /// <summary>
        ///   The refusal for a <see cref="NamespaceRequiredAttribute"/> action reached over its BARE
        ///   URL (feature graph-namespaces): it has no default-namespace alias, so the request names
        ///   no graph and is answered rather than guessed at.
        ///   <para>400 and not 404, and the bare route stays registered rather than being removed:
        ///   this app serves F8 Studio from the same origin behind
        ///   <c>MapFallbackToFile("index.html")</c>, so an UNROUTED bare path would answer with the
        ///   app shell and HTTP 200 - a silent success that looks like the generation worked.</para>
        ///   <para>Deliberately carries NO <c>namespace</c> extension member: there is no namespace to
        ///   report, and that member is what a client keys its "namespace is gone" recover state on.</para>
        /// </summary>
        /// <param name="bareRoute">The matched route template, with or without its leading slash
        /// (MVC reports the absolute template "/generate" as "generate").</param>
        internal static ObjectResult NamespaceRequired(String bareRoute)
        {
            var route = "/" + (bareRoute ?? String.Empty).TrimStart('/');

            return ProblemResults.Create(StatusCodes.Status400BadRequest, "Namespace required",
                "The route \"" + route + "\" names no namespace, and this operation has no " +
                "default-namespace alias: it acts on ONE graph, so it never picks one for you. " +
                "Address a namespace explicitly - /ns/{namespace}" + route + " - and see GET /ns for " +
                "the namespaces this Fallen-8 holds.");
        }

        /// <summary>
        ///   The "exists but is not loaded" refusal (feature namespace-startup-load).
        ///   <para>503 and NOT 404, deliberately: the Studio client turns any 404 problem+json
        ///   carrying a string <c>namespace</c> extension into its recover state, whose primary
        ///   action recreates the namespace EMPTY. Answering 404 here would tell an operator their
        ///   populated graph is gone and then offer them the one button that makes that true. 503
        ///   also carries the honest semantics: the namespace is temporarily unavailable in this
        ///   process, and the request is retryable once it is loaded.</para>
        ///   <para><c>namespaceState</c> is what a client branches on, so it never has to parse the
        ///   detail sentence.</para>
        ///   <para>The detail names both ways out, in the order an operator wants them: activate it
        ///   now (this process), and set the policy so the next boot loads it too. Naming only the
        ///   policy would tell an operator mid-incident to restart when they do not have to; naming
        ///   only activation would have them fix it once per boot forever.</para>
        /// </summary>
        /// <param name="name">The addressed namespace.</param>
        internal static ObjectResult NotLoaded(String name)
        {
            // The URLs carry the ENCODED name while the quoted one stays human-readable: names are
            // permissive (a space, "#", "?", "%" or non-ASCII is valid) and the two instructions
            // below would otherwise be wrong for exactly the names an operator needs help copying.
            // Encoded once, here, per Namespace.UrlSegment's contract.
            var url = Namespace.UrlSegment(name);
            return ProblemResults.Create(StatusCodes.Status503ServiceUnavailable, "Namespace not loaded",
                "The namespace \"" + name + "\" exists on this Fallen-8 but is not loaded in this " +
                "process, so it cannot serve requests. Its data on disk is untouched. Load it now " +
                "with POST /ns/" + url + "/activate; to have every boot load it, also set its " +
                "startup-load policy (PATCH /ns/" + url + " with \"loadOnStartup\": \"enabled\").",
                p =>
                {
                    p.Extensions["namespace"] = name;
                    p.Extensions["namespaceState"] = Namespace.WireName(NamespaceState.NotLoaded);
                });
        }
    }
}
