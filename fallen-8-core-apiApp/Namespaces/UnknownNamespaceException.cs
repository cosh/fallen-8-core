// MIT License
//
// UnknownNamespaceException.cs
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
    ///   Thrown when the addressed namespace vanished BETWEEN the validation filter's 404 check
    ///   and the engine resolution — i.e. a drop/rename raced an in-flight request. The exception
    ///   filter below turns it into the same 404 problem+json the validation filter answers, so
    ///   the race is indistinguishable from arriving a moment later.
    /// </summary>
    public sealed class UnknownNamespaceException : InvalidOperationException
    {
        public UnknownNamespaceException(String namespaceName)
            : base("Unknown namespace \"" + namespaceName + "\".")
        {
            NamespaceName = namespaceName;
        }

        public String NamespaceName { get; }
    }

    /// <summary>Maps <see cref="UnknownNamespaceException"/> to the 404 problem+json contract.</summary>
    public sealed class UnknownNamespaceExceptionFilter : IExceptionFilter
    {
        public void OnException(ExceptionContext context)
        {
            if (context.Exception is UnknownNamespaceException unknown)
            {
                context.Result = ProblemResults.Create(StatusCodes.Status404NotFound,
                    "Namespace not found",
                    "No namespace named \"" + unknown.NamespaceName + "\" exists on this Fallen-8.",
                    p => p.Extensions["namespace"] = unknown.NamespaceName);
                context.ExceptionHandled = true;
            }
        }
    }
}
