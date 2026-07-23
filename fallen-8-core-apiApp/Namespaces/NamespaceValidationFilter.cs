// MIT License
//
// NamespaceValidationFilter.cs
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

using System;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using NoSQL.GraphDB.App.Helper;

namespace NoSQL.GraphDB.App.Namespaces
{
    /// <summary>
    ///   Global resource filter that answers 404 problem+json BEFORE any action runs when a
    ///   <c>/ns/{ns}/…</c> route names a namespace the collection does not hold (feature
    ///   graph-namespaces, spec §5.1). The body carries the <c>namespace</c> extension member —
    ///   the stable marker F8 Studio keys its "recreate or switch" recover state on. Bare routes
    ///   carry no <c>ns</c> route value and pass through untouched.
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
            if (context.RouteData.Values.TryGetValue(NamespaceRouteConvention.RouteParameterName, out var value)
                && value is String name
                && !_namespaces.TryGet(name, out _))
            {
                context.Result = ProblemResults.Create(StatusCodes.Status404NotFound,
                    "Namespace not found",
                    "No namespace named \"" + name + "\" exists on this Fallen-8.",
                    p => p.Extensions["namespace"] = name);
            }
        }

        public void OnResourceExecuted(ResourceExecutedContext context)
        {
        }
    }
}
