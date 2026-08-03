// MIT License
//
// ProblemResults.cs
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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.App.Helper
{
    /// <summary>
    ///   The one way controllers build an explicit RFC 7807 response, so every error the REST
    ///   surface returns is a uniform <c>application/problem+json</c> body (feature
    ///   api-error-envelope). The <see cref="BadRequest"/>/<see cref="NotFound"/>/
    ///   <see cref="Conflict"/>/<see cref="StatusCode"/> wrappers are the one-for-one
    ///   replacements for the framework's plain-string <c>BadRequest("...")</c> etc.; they keep
    ///   the human message as the problem <c>detail</c> and give each status a short, stable
    ///   <c>title</c> (the HTTP reason phrase). <see cref="Create"/> stays for the callers that
    ///   need a bespoke title or extension members (the embedding provider faults).
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

        /// <summary>400 Bad Request with the message carried as the problem <c>detail</c>.</summary>
        internal static ObjectResult BadRequest(String detail) =>
            Create(StatusCodes.Status400BadRequest, TitleFor(StatusCodes.Status400BadRequest), detail);

        /// <summary>404 Not Found with the message carried as the problem <c>detail</c>.</summary>
        internal static ObjectResult NotFound(String detail) =>
            Create(StatusCodes.Status404NotFound, TitleFor(StatusCodes.Status404NotFound), detail);

        /// <summary>409 Conflict with the message carried as the problem <c>detail</c>.</summary>
        internal static ObjectResult Conflict(String detail) =>
            Create(StatusCodes.Status409Conflict, TitleFor(StatusCodes.Status409Conflict), detail);

        /// <summary>500 Internal Server Error with the message carried as the problem <c>detail</c>.</summary>
        internal static ObjectResult InternalServerError(String detail) =>
            Create(StatusCodes.Status500InternalServerError, TitleFor(StatusCodes.Status500InternalServerError), detail);

        /// <summary>An explicit-status problem+json result (the general form behind
        /// GraphController's <c>RolledBackResult</c>, which maps a structured failure reason to
        /// 400/404/409/500); the title is derived from the status.</summary>
        internal static ObjectResult StatusCode(Int32 status, String detail) =>
            Create(status, TitleFor(status), detail);

        /// <summary>The HTTP status a rolled-back transaction's <see cref="TransactionFailureReason"/>
        /// maps to: <c>InvalidInput</c> → 400, <c>NotFound</c> → 404, <c>QuotaExceeded</c>/<c>Conflict</c>
        /// → 409, and everything else (<c>None</c>, <c>InternalError</c>) → 500. The SINGLE home for
        /// that mapping: every controller that waits on a write and reports its rollback selects the
        /// status here and keeps its own tailored detail/title, so the same engine failure can never
        /// surface as a different status across endpoints. (BulkController keeps a documented
        /// per-row NotFound → 400 override for batch import semantics.)</summary>
        internal static Int32 StatusForFailureReason(TransactionFailureReason reason)
        {
            switch (reason)
            {
                case TransactionFailureReason.InvalidInput:
                    return StatusCodes.Status400BadRequest;
                case TransactionFailureReason.NotFound:
                    return StatusCodes.Status404NotFound;
                case TransactionFailureReason.QuotaExceeded:
                case TransactionFailureReason.Conflict:
                    return StatusCodes.Status409Conflict;
                default:
                    return StatusCodes.Status500InternalServerError;
            }
        }

        /// <summary>The short, stable problem <c>title</c> for a status: its HTTP reason phrase
        /// (e.g. 400 → "Bad Request"), falling back to a generic label for unknown codes.</summary>
        private static String TitleFor(Int32 status)
        {
            var phrase = ReasonPhrases.GetReasonPhrase(status);
            return String.IsNullOrEmpty(phrase) ? "Error" : phrase;
        }
    }
}
