// MIT License
//
// NamespaceNotLoadedException.cs
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
using Microsoft.AspNetCore.Mvc.Filters;

namespace NoSQL.GraphDB.App.Namespaces
{
    /// <summary>
    ///   Thrown when the addressed namespace exists in the catalog but is not loaded in this
    ///   process (feature namespace-startup-load). Two arrival paths, and both must answer the
    ///   same way: the pre-action <see cref="NamespaceValidationFilter"/> refuses before any action
    ///   touches an engine, and this exception covers the off-request path plus any dereference
    ///   site the sweep missed - the exact twin of <see cref="UnknownNamespaceException"/>'s
    ///   arrangement for a namespace that vanished mid-request.
    /// </summary>
    public sealed class NamespaceNotLoadedException : InvalidOperationException
    {
        public NamespaceNotLoadedException(String namespaceName)
            : base("Namespace \"" + namespaceName + "\" exists but is not loaded in this process.")
        {
            NamespaceName = namespaceName;
        }

        public String NamespaceName { get; }
    }

    /// <summary>Maps <see cref="NamespaceNotLoadedException"/> to the 503 problem+json contract.</summary>
    public sealed class NamespaceNotLoadedExceptionFilter : IExceptionFilter
    {
        public void OnException(ExceptionContext context)
        {
            if (context.Exception is NamespaceNotLoadedException notLoaded)
            {
                context.Result = NamespaceProblems.NotLoaded(notLoaded.NamespaceName);
                context.ExceptionHandled = true;
            }
        }
    }
}
