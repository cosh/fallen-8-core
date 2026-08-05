// MIT License
//
// ProblemDetailsContentTypeFilter.cs
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
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace NoSQL.GraphDB.App.Helper
{
    /// <summary>
    ///   Keeps the RFC 7807 error envelope (feature api-error-envelope) true on the wire.
    ///
    ///   <para><see cref="ProblemResults" /> builds every explicit error with
    ///   <c>ContentTypes = { "application/problem+json" }</c>, but an action carrying
    ///   <c>[Produces("application/json")]</c> declares the content type of its SUCCESS shape and
    ///   <see cref="ProducesAttribute" /> is a result filter that REPLACES the result's content
    ///   types wholesale. The error body therefore went out as <c>application/json</c> on every
    ///   action that declared one, so a client could not switch on the media type to tell an error
    ///   from a payload. This filter runs after <see cref="ProducesAttribute" /> (a higher
    ///   <see cref="Order" />; result filters execute ascending) and restores the problem media
    ///   type whenever the value being written IS a <see cref="ProblemDetails" />.</para>
    ///
    ///   <para>Registered once, globally, in <c>Program.cs</c>: the alternative was adding a second
    ///   content type to ~75 <c>[Produces]</c> declarations, which would also have advertised
    ///   <c>application/problem+json</c> as a possible SUCCESS content type in the OpenAPI
    ///   document, where it is never correct.</para>
    /// </summary>
    public sealed class ProblemDetailsContentTypeFilter : IResultFilter, IOrderedFilter
    {
        /// <summary>
        ///   The order that puts this filter after <see cref="ProducesAttribute" /> (default 0).
        ///   It must be passed to <c>options.Filters.Add(type, order)</c> at registration: a filter
        ///   added by type is described by a <c>TypeFilterAttribute</c> whose own order is what MVC
        ///   sorts on, so <see cref="Order" /> alone would not be honoured.
        /// </summary>
        public const Int32 FilterOrder = 1000;

        /// <inheritdoc />
        public Int32 Order => FilterOrder;

        /// <inheritdoc />
        public void OnResultExecuting(ResultExecutingContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (context.Result is ObjectResult result && result.Value is ProblemDetails)
            {
                result.ContentTypes.Clear();
                result.ContentTypes.Add("application/problem+json");
            }
        }

        /// <inheritdoc />
        public void OnResultExecuted(ResultExecutedContext context)
        {
            // Nothing to do after the result has been written.
        }
    }
}
