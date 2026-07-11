// MIT License
//
// XmlDocumentationOperationTransformer.cs
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
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi.Models;

namespace NoSQL.GraphDB.App.Helper
{
    /// <summary>
    ///   Surfaces the C# XML documentation (<c>&lt;summary&gt;</c> / <c>&lt;remarks&gt;</c>)
    ///   of controller actions into the generated OpenAPI document as operation summaries
    ///   and descriptions.
    /// </summary>
    /// <remarks>
    ///   The 9.x <c>Microsoft.AspNetCore.OpenApi</c> package does not read XML doc comments
    ///   on its own, so this operation transformer bridges the gap without taking a
    ///   dependency on a newer package. The generated documentation file
    ///   (<c>&lt;assembly&gt;.xml</c>, emitted because <c>GenerateDocumentationFile</c> is
    ///   enabled) is parsed once and cached. Actions are matched by their declaring type
    ///   and method name, which is unambiguous because no controller action is overloaded.
    /// </remarks>
    public sealed class XmlDocumentationOperationTransformer : IOpenApiOperationTransformer
    {
        private static readonly Lazy<IReadOnlyDictionary<String, (String Summary, String Remarks)>> _members =
            new Lazy<IReadOnlyDictionary<String, (String, String)>>(LoadXmlMembers, LazyThreadSafetyMode.ExecutionAndPublication);

        public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
        {
            if (context.Description.ActionDescriptor is ControllerActionDescriptor action)
            {
                var key = action.MethodInfo.DeclaringType?.FullName + "." + action.MethodInfo.Name;

                if (_members.Value.TryGetValue(key, out var doc))
                {
                    if (!String.IsNullOrWhiteSpace(doc.Summary))
                    {
                        operation.Summary = doc.Summary;
                    }

                    if (!String.IsNullOrWhiteSpace(doc.Remarks))
                    {
                        operation.Description = doc.Remarks;
                    }
                }
            }

            return Task.CompletedTask;
        }

        private static IReadOnlyDictionary<String, (String Summary, String Remarks)> LoadXmlMembers()
        {
            var result = new Dictionary<String, (String, String)>(StringComparer.Ordinal);

            try
            {
                var xmlPath = Path.Combine(AppContext.BaseDirectory,
                    System.Reflection.Assembly.GetExecutingAssembly().GetName().Name + ".xml");

                if (!File.Exists(xmlPath))
                {
                    return result;
                }

                var document = XDocument.Load(xmlPath);

                foreach (var member in document.Descendants("member"))
                {
                    var name = member.Attribute("name")?.Value;

                    // Only method members: "M:Namespace.Type.Method(params)".
                    if (String.IsNullOrEmpty(name) || !name.StartsWith("M:", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // Strip the "M:" prefix and the parameter list to get "Namespace.Type.Method".
                    var signature = name.Substring(2);
                    var parenIndex = signature.IndexOf('(');
                    if (parenIndex >= 0)
                    {
                        signature = signature.Substring(0, parenIndex);
                    }

                    var summary = Normalize(member.Element("summary")?.Value);
                    var remarks = Normalize(member.Element("remarks")?.Value);

                    // First declaration wins; unambiguous because actions are not overloaded.
                    if (!result.ContainsKey(signature))
                    {
                        result[signature] = (summary, remarks);
                    }
                }
            }
            catch
            {
                // Documentation is best-effort; never fail OpenAPI generation over it.
            }

            return result;
        }

        private static String Normalize(String value)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            // XML doc text is indented and line-wrapped; collapse runs of whitespace.
            return Regex.Replace(value, @"\s+", " ").Trim();
        }
    }
}
