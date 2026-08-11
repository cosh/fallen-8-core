// MIT License
//
// IntegrationEndpoints.cs
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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NoSQL.GraphDB.Integrations.Contract;
using NoSQL.GraphDB.Integrations.Identity;
using NoSQL.GraphDB.Integrations.Run;
using NoSQL.GraphDB.Integrations.Validation;

namespace NoSQL.GraphDB.Integrations.Hosting
{
    /// <summary>
    ///   The runtime's whole HTTP surface: a health probe and the four routes the apiApp proxies. Nothing here is
    ///   authenticated, and nothing needs to be: the container's port is not published, so the only way in is
    ///   through the apiApp, which is already the authenticated front door. A second auth story on this container
    ///   would be a second thing to get wrong.
    /// </summary>
    public static class IntegrationEndpoints
    {
        /// <summary>Maps every route.</summary>
        public static void Map(IEndpointRouteBuilder app)
        {
            if (app == null)
            {
                throw new ArgumentNullException(nameof(app));
            }

            // The path the shared sidecar client base's cached reachability probe calls, as for the docling and
            // NLP sidecars. It says NOTHING about configuration: a probe disclosing which integrations exist
            // tells an unauthenticated caller which third-party systems this container is given credentials for.
            app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

            app.MapGet("/integration/providers", (ProviderCatalog catalog) => Results.Ok(catalog.Descriptors));

            app.MapGet("/integration/vocabulary",
                (IdentifierVocabulary vocabulary) => Results.Ok(VocabularyDto.From(vocabulary)));

            app.MapPost("/integration/snapshot/validate",
                (SnapshotDocument? document, SnapshotValidator validator) =>
                {
                    if (document == null)
                    {
                        return Problem(StatusCodes.Status400BadRequest, "A snapshot document is required.");
                    }

                    // No descriptor is passed: this route judges a DOCUMENT, and the completeness-honesty
                    // refusal is a statement about a provider, which a pasted document does not have.
                    return Results.Ok(validator.Validate(document));
                });

            app.MapPost("/integration/job", async (IntegrationJob? job, JobRunner runner,
                CancellationToken cancellationToken) =>
            {
                if (job == null)
                {
                    return Problem(StatusCodes.Status400BadRequest, "A job definition is required.");
                }

                try
                {
                    var report = await runner.RunAsync(job, cancellationToken).ConfigureAwait(false);

                    // A job that RAN and failed answers 200 with the failure on the report: the request did what
                    // was asked, so the interesting outcome is the run's.
                    return Results.Ok(report);
                }
                catch (JobRejectedException ex)
                {
                    var status = ex.Kind == JobErrorKinds.Conflict
                        ? StatusCodes.Status409Conflict
                        : StatusCodes.Status400BadRequest;
                    return Problem(status, ex.Message, ex.Kind);
                }
            });
        }

        /// <summary>
        ///   The runtime's own failure shape: problem+json, so the apiApp's proxy can pass the body through
        ///   untouched and the message a configuring operator needs is the runtime's own rather than a
        ///   proxy-shaped one.
        /// </summary>
        private static IResult Problem(Int32 status, String detail, String? errorKind = null)
        {
            return Results.Problem(detail: detail, statusCode: status,
                title: status == StatusCodes.Status409Conflict ? "Conflict" : "Bad Request",
                extensions: errorKind == null
                    ? null
                    : new System.Collections.Generic.Dictionary<String, Object?>(StringComparer.Ordinal)
                    {
                        ["errorKind"] = errorKind,
                    });
        }
    }
}
