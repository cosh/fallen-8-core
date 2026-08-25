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

            // ACCEPTED, not awaited. The synchronous shape this route used to have could not survive a real
            // source: the report is the only copy this runtime produces, the proxy in front holds a
            // connection for a bounded time, and the run is deliberately built to outlive its caller. So a
            // long run used to be unobservable while it happened AND unknowable after it ended.
            //
            // What is still synchronous is every judgement that can REJECT the job - its shape, the
            // provider, the identity, the files, and the run gate. An accepted job is one that really
            // started; a rejected one never ran, exactly as before.
            app.MapPost("/integration/job", async (IntegrationJob? job, JobRunner runner, RunTracker tracker,
                Boolean? wait, CancellationToken cancellationToken) =>
            {
                if (job == null)
                {
                    return Problem(StatusCodes.Status400BadRequest, "A job definition is required.");
                }

                // The old contract, on request. Kept for scripts and small sources, and NOT the default,
                // because the proxy timeout still applies to it.
                if (wait == true)
                {
                    try
                    {
                        // A job that RAN and failed answers 200 with the failure on the report: the request did
                        // what was asked, so the interesting outcome is the run's.
                        return Results.Ok(await runner.RunAsync(job, cancellationToken).ConfigureAwait(false));
                    }
                    catch (JobRejectedException ex)
                    {
                        return Reject(ex);
                    }
                }

                var instanceId = job.IntegrationInstanceId;
                if (String.IsNullOrWhiteSpace(instanceId))
                {
                    // No identity means nothing to track a run under and nothing to reconcile it against, so
                    // the run itself refuses it. Its message is the one worth returning.
                    try
                    {
                        return Results.Ok(await runner.RunAsync(job, cancellationToken).ConfigureAwait(false));
                    }
                    catch (JobRejectedException ex)
                    {
                        return Reject(ex);
                    }
                }

                var handle = tracker.Begin(Guid.NewGuid().ToString("N"), job.ProviderId ?? String.Empty,
                    instanceId!, job.Namespace);
                var run = ExecuteAsync(runner, tracker, job, instanceId!, handle);

                // Deterministic, not timed: either the run enters its first phase, or it ends without ever
                // entering one - which is precisely what "it was rejected" means.
                if (await Task.WhenAny(run, handle.Started).ConfigureAwait(false) == run)
                {
                    try
                    {
                        await run.ConfigureAwait(false);
                    }
                    catch (JobRejectedException ex)
                    {
                        return Reject(ex);
                    }
                }

                tracker.Attach(instanceId!, run);
                return Results.Accepted((String?)null, new RunAcceptedDto
                {
                    RunId = handle.RunId,
                    ProviderId = job.ProviderId ?? String.Empty,
                    IntegrationInstanceId = instanceId!,
                    Progress = "/integration/run/" + instanceId,
                });
            });

            // What is happening now, and what happened last, per identity. Deliberately not a run log: one
            // slot per identity, in memory, superseded by that identity's next run (see RunTracker).
            app.MapGet("/integration/run", (RunTracker tracker) => Results.Ok(tracker.All()));

            app.MapGet("/integration/run/{instanceId}", (String instanceId, RunTracker tracker) =>
                tracker.TryGet(instanceId, out var state)
                    ? Results.Ok(state)
                    : Problem(StatusCodes.Status404NotFound,
                        String.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "No run is tracked for identity '{0}'. Either it has never run in this process, or " +
                            "a restart or 32 other identities have since displaced it.", instanceId)));
        }

        /// <summary>
        ///   Runs the job and records its outcome on the tracker.
        ///
        ///   <para>The token is deliberately NOT the request's: the caller walking away must not stop a run,
        ///   which is the same principle the apply phase already held for half the run and this now holds for
        ///   all of it.</para>
        ///
        ///   <para>A failure is RETHROWN only while the run has not started, because that is the case the
        ///   route is still waiting to map into a 400 or a 409. Once it has started, nobody is waiting on this
        ///   task, so a rethrow would be an unobserved exception; the failure is recorded on the slot instead,
        ///   which is the only place a reader could ever find it.</para>
        /// </summary>
        private static async Task ExecuteAsync(JobRunner runner, RunTracker tracker, IntegrationJob job,
            String instanceId, RunTracker.RunHandle handle)
        {
            try
            {
                var report = await runner.RunAsync(job, CancellationToken.None, handle).ConfigureAwait(false);
                tracker.Finish(instanceId, report);
            }
            catch (Exception failure)
            {
                if (!handle.Started.IsCompleted)
                {
                    throw;
                }

                tracker.Abort(instanceId, failure.Message);
            }
        }

        /// <summary>A job the runtime refused: it never ran, and the caller has something to fix.</summary>
        private static IResult Reject(JobRejectedException failure)
        {
            var status = failure.Kind == JobErrorKinds.Conflict
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status400BadRequest;
            return Problem(status, failure.Message, failure.Kind);
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
