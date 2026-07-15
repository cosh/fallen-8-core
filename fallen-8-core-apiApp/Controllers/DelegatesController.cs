// MIT License
//
// DelegatesController.cs
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
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NoSQL.GraphDB.App.Configuration;
using NoSQL.GraphDB.App.Controllers.Model;
using NoSQL.GraphDB.Core.App.Helper;

namespace NoSQL.GraphDB.App.Controllers
{
    /// <summary>
    ///   Compile-checks delegate fragments for the delegate editor (feature web-ui, gap G-2)
    /// </summary>
    [ApiController]
    [Route("api/v{version:apiVersion}/[controller]")]
    [ApiVersion("0.1")]
    public class DelegatesController : ControllerBase
    {
        /// <summary>
        /// Validates a single delegate fragment without executing it
        /// </summary>
        /// <param name="specification">The delegate kind and the fragment to compile-check</param>
        /// <returns>Whether the fragment compiles, with diagnostics in fragment coordinates</returns>
        /// <remarks>
        /// The fragment is wrapped and compiled exactly as the path (POST /path/{from}/to/{to})
        /// and subgraph (PUT /subgraph) endpoints would wrap it, but nothing is emitted, loaded,
        /// or executed - validation is side-effect free.
        ///
        /// Diagnostic positions are already mapped back to the submitted fragment (1-based; line 1
        /// is the fragment's first line), so an editor renders markers without further mapping.
        /// A null/empty fragment is valid by definition (it means "match everything" / "no custom
        /// cost"). Warnings are reported but do not make the fragment invalid.
        ///
        /// The endpoint sits behind the same dynamic-code authorization gate as the query
        /// endpoints: compilation is the expensive half of that surface, and validation is only
        /// useful where fragment submission is possible at all.
        ///
        /// Sample request:
        ///
        ///     POST /delegates/validate
        ///     {
        ///        "delegateKind": "VertexFilter",
        ///        "fragment": "return (v) => v.TryGetProperty(out int age, \"age\") &amp;&amp; age > 30;"
        ///     }
        ///
        /// </remarks>
        /// <response code="200">The validation result (also for invalid fragments - inspect "valid")</response>
        /// <response code="400">Unknown delegateKind or malformed request</response>
        /// <response code="401">Authentication required but missing/invalid</response>
        /// <response code="403">Dynamic code execution is disabled on this server (Fallen8:Security:EnableDynamicCodeExecution)</response>
        /// <response code="429">Rate limit for sensitive endpoints exceeded</response>
        [HttpPost("/delegates/validate")]
        [Authorize(Policy = Fallen8SecurityOptions.DynamicCodePolicy)]
        [EnableRateLimiting(Fallen8SecurityOptions.SensitiveRateLimitPolicy)]
        [RequestSizeLimit(1_048_576)]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(DelegateValidationREST), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status403Forbidden)]
        [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
        public ActionResult<DelegateValidationREST> ValidateDelegate([FromBody] ValidateDelegateSpecification specification)
        {
            // A JSON `null` body binds to null (NRT is off, so MVC adds no implicit check);
            // return 400 rather than dereferencing into a 500 (matches AddProperty).
            if (specification == null)
            {
                return BadRequest("A validation specification body is required.");
            }

            if (!DelegateValidationHelper.TryValidate(specification.DelegateKind, specification.Fragment, out var result))
            {
                return BadRequest(String.Format("Unknown delegateKind '{0}'. Expected one of: {1}.",
                    specification.DelegateKind, DelegateValidationHelper.KnownKindsList));
            }

            return result;
        }
    }
}
