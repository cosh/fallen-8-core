// MIT License
//
// StreamedBodyAttribute.cs
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
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace NoSQL.GraphDB.App.Integrations
{
    /// <summary>
    ///   Keeps MVC's hands off the request body so the action can STREAM it (feature
    ///   integration-file-transport).
    ///
    ///   <para>The problem this solves is not obvious and is invisible in the controller. MVC builds its
    ///   value providers before an action runs, and <c>FormValueProviderFactory</c> calls
    ///   <c>ReadFormAsync</c> on ANY request with a form content type - it does not care that no parameter
    ///   of the action is bound from the form. For <c>multipart/form-data</c> that reader spools every part
    ///   over 64 KiB to a temp file and leaves <c>Request.Body</c> at its end, so an action that then
    ///   forwards the body sends nothing at all. Measured, not theorised: without this the proxy answered
    ///   503 "Sent 0 request content bytes, but Content-Length promised 288."</para>
    ///
    ///   <para>Two things are wrong with that and both matter. The body is gone, and a caller's extract has
    ///   been written into the container's filesystem by a hop whose entire contract is not to look at it.
    ///   The standing rule is that the integrations path touches no disk, enforced as a convention gate in
    ///   <c>CodeQualityTest</c>; that gate can only ban the API by NAME, and this call is one MVC makes on
    ///   the action's behalf. So the gate and this attribute are two halves of one guarantee.</para>
    ///
    ///   <para>Query and route binding are untouched, which is why the action keeps its
    ///   <c>[FromQuery]</c> parameters.</para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
    public sealed class StreamedBodyAttribute : Attribute, IResourceFilter
    {
        /// <inheritdoc />
        public void OnResourceExecuting(ResourceExecutingContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            for (var i = context.ValueProviderFactories.Count - 1; i >= 0; i--)
            {
                var factory = context.ValueProviderFactories[i];
                if (factory is FormValueProviderFactory ||
                    factory is FormFileValueProviderFactory ||
                    factory is JQueryFormValueProviderFactory)
                {
                    context.ValueProviderFactories.RemoveAt(i);
                }
            }
        }

        /// <inheritdoc />
        public void OnResourceExecuted(ResourceExecutedContext context)
        {
        }
    }
}
