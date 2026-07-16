// MIT License
//
// ProblemResults.cs
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
using Microsoft.AspNetCore.Mvc;

namespace NoSQL.GraphDB.App.Helper
{
    /// <summary>
    ///   The one way controllers build an explicit RFC 7807 response (feature code-quality:
    ///   consolidates the seven hand-rolled ObjectResult + ProblemDetails +
    ///   application/problem+json blocks the sprint left behind). The emitted bytes are
    ///   identical to the previous inline blocks - the endpoint tests asserting status,
    ///   content type and detail fragments pin that.
    /// </summary>
    internal static class ProblemResults
    {
        /// <summary>Builds a problem+json <see cref="ObjectResult"/> with the given status,
        /// title and detail; <paramref name="extend"/> can add extension members.</summary>
        internal static ObjectResult Create(Int32 status, String title, String detail,
            Action<ProblemDetails> extend = null)
        {
            var problem = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = detail
            };
            extend?.Invoke(problem);

            return new ObjectResult(problem)
            {
                StatusCode = status,
                ContentTypes = { "application/problem+json" }
            };
        }
    }
}
