// MIT License
//
// ProblemAssert.cs
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
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The one shared assertion for the uniform RFC 7807 error envelope (feature
    ///   api-error-envelope): every error the REST surface returns is
    ///   <c>application/problem+json</c> with a matching status. <see cref="AssertProblem"/>
    ///   asserts that against the unit-level result an action returns, keeping the controller-test
    ///   churn DRY.
    /// </summary>
    internal static class ProblemAssert
    {
        /// <summary>
        ///   Asserts a unit-level action result is a problem+json <see cref="ObjectResult"/> with
        ///   the expected status and (optionally) a <c>detail</c> containing
        ///   <paramref name="detailContains"/>. Accepts a bare <see cref="IActionResult"/> or an
        ///   <see cref="ActionResult{TValue}"/> (unwrapped via <see cref="IConvertToActionResult"/>).
        ///   Returns the <see cref="ProblemDetails"/> for any further assertions.
        /// </summary>
        internal static ProblemDetails AssertProblem(Object result, Int32 expectedStatus, String detailContains = null)
        {
            var actual = result is IConvertToActionResult convertible ? convertible.Convert() : result;
            var objectResult = actual as ObjectResult;
            Assert.IsNotNull(objectResult,
                String.Format("Expected an ObjectResult carrying a ProblemDetails, got '{0}'.",
                    result?.GetType().Name ?? "null"));
            Assert.AreEqual(expectedStatus, objectResult.StatusCode ?? -1, "problem status code");
            Assert.IsTrue(objectResult.ContentTypes.Contains("application/problem+json"),
                "the error must be served as application/problem+json");

            var problem = objectResult.Value as ProblemDetails;
            Assert.IsNotNull(problem,
                String.Format("Expected a ProblemDetails body, got '{0}'.",
                    objectResult.Value?.GetType().Name ?? "null"));
            Assert.AreEqual(expectedStatus, problem.Status ?? -1, "problem.Status");

            if (detailContains != null)
            {
                StringAssert.Contains(problem.Detail ?? String.Empty, detailContains,
                    String.Format("problem.Detail should contain '{0}'", detailContains));
            }

            return problem;
        }
    }
}
