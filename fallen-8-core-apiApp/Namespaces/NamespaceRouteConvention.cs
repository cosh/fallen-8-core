// MIT License
//
// NamespaceRouteConvention.cs
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
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace NoSQL.GraphDB.App.Namespaces
{
    /// <summary>
    ///   Gives every namespace-scoped action a REAL route twin under <c>/ns/{ns}/…</c> (feature
    ///   graph-namespaces, spec §5.1): the action keeps its bare absolute route (which aliases the
    ///   default namespace) and gains a second selector whose template carries the namespace name.
    ///   Both are ordinary attribute routes — visible to routing, ApiExplorer, and OpenAPI — so no
    ///   path rewriting happens anywhere. Actions or controllers marked
    ///   <see cref="Fallen8LevelAttribute"/> are skipped.
    /// </summary>
    public sealed class NamespaceRouteConvention : IApplicationModelConvention
    {
        /// <summary>The route parameter the addressed-engine resolution reads.</summary>
        public const String RouteParameterName = "ns";

        private const String Prefix = "/ns/{" + RouteParameterName + "}";

        public void Apply(ApplicationModel application)
        {
            foreach (var controller in application.Controllers)
            {
                if (controller.Attributes.OfType<Fallen8LevelAttribute>().Any())
                {
                    continue;
                }

                foreach (var action in controller.Actions)
                {
                    if (action.Attributes.OfType<Fallen8LevelAttribute>().Any())
                    {
                        continue;
                    }

                    var twins = new List<SelectorModel>();
                    foreach (var selector in action.Selectors)
                    {
                        var template = selector.AttributeRouteModel?.Template;

                        // Only absolute action routes participate (every data action in this app
                        // uses one; a leading '/' is what overrides the controller-level route).
                        if (template == null || !template.StartsWith("/", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        var twin = new SelectorModel(selector);
                        twin.AttributeRouteModel = new AttributeRouteModel(selector.AttributeRouteModel)
                        {
                            Template = Prefix + template
                        };
                        twins.Add(twin);
                    }

                    foreach (var twin in twins)
                    {
                        action.Selectors.Add(twin);
                    }
                }
            }
        }
    }
}
