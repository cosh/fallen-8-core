// MIT License
//
// McpRestCoverageTest.cs
//
// Copyright (c) 2026 Henning Rauch
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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   GOVERNANCE GATE — engine → REST → MCP (feature mcp-server §7). A capability that grows
    ///   in the engine and reaches the REST surface MUST also be surfaced to agents through an
    ///   MCP tool, or be a CONSCIOUS, reasoned deferral. This test makes that a failing build,
    ///   not a prose rule: every REST operation in the pinned OpenAPI snapshot must be either
    ///   bridged by an MCP tool (<see cref="McpBridgedEndpoints"/>) or matched by a deferral rule
    ///   below (each with a one-line reason). A NEW endpoint family that is neither trips this
    ///   test — forcing whoever added it to surface it in the MCP tool surface or record why not.
    ///   (The <c>/ns/{ns}</c> twins are excluded: the bridge constructs that prefix itself, so a
    ///   bridged bare route implies its twin.)
    /// </summary>
    [TestClass]
    public class McpRestCoverageTest
    {
        /// <summary>A deferral rule: a predicate over "<c>METHOD /path</c>" and the reason the
        /// matched endpoints are deliberately NOT surfaced to agents (yet).</summary>
        private sealed record Deferral(Func<String, Boolean> Matches, String Reason);

        private static readonly Deferral[] Deferrals =
        {
            new(op => op.Contains("/edges/") || op.EndsWith("/source") || op.EndsWith("/target"),
                "adjacency is surfaced by f8_get's `include` over the single element getter (no extra call)"),
            new(op => op.EndsWith("/count"),
                "element counts are surfaced by f8_overview"),
            new(op => op == "GET /graph",
                "a full-graph dump is token-heavy; agents use f8_search / f8_get"),
            new(op => op.Contains("/scan/index/range"),
                "range scans are deferred; index-equality, fulltext and vector modes cover the agent path"),
            new(op => op.Contains("/scan/index/spatial"),
                "spatial scans need a live reference geometry; not an agent-facing surface"),
            new(op => op.Contains("/partition/"),
                "analytics partition-member pagination is deferred; f8_analytics returns partition summaries"),
            new(op => op.StartsWith("GET /subgraph") || op.StartsWith("DELETE /subgraph") || op.EndsWith("/recalculate"),
                "subgraph read/recalculate/delete are deferred; define via f8_subgraph"),
            new(op => op.Contains("/index"),
                "index lifecycle (create/populate/drop) is operator/setup tooling; agents scan existing indices via f8_search"),
            new(op => op.Contains("/storedquery"),
                "stored-query registration/listing is code-gated setup; agents invoke by name via the storedQuery parameter"),
            new(op => op.Contains("/service") || op.Contains("/plugin"),
                "service/plugin administration is operator-only"),
            new(op => op.Contains("/bulk/"),
                "bulk import/export is stream-shaped operator-tier I/O (spec §7)"),
            new(op => op.Contains("/changefeed"),
                "the SSE change feed has no MCP-native primitive for a continuous ordered delta stream (spec §3.2)"),
            new(op => op.Contains("/delegates"),
                "dynamic-code validation is a code-gated editor/dev tool"),
            new(op => op.Contains("/benchmark") || op.Contains("/generate") || op.Contains("/unittest"),
                "development/benchmark/sample-generation tooling, not an agent surface"),
            new(op => op.Contains("/embedding/"),
                "element embedding writes (element/elements/text) and per-element embedding read/remove are deferred; " +
                "f8_mutate set_embedding writes, f8_search mode:semantic reads"),
            new(op => op == "GET /ns/{name}",
                "per-namespace detail is surfaced by f8_overview(namespace); the directory by f8_overview"),
            new(op => op.StartsWith("GET /savegames") || op == "DELETE /savegames/{id}",
                "save-game detail/deletion is operator housekeeping; f8_admin lists and restores by id"),
            new(op => op == "PUT /save/all" || op == "HEAD /tabularasa/all",
                "collection-wide save/reset variants are deferred; the single-namespace forms are bridged"),
            new(op => op == "PUT /load",
                "the file-path load is deferred; f8_admin load uses the id-based /savegames/{id}/load"),
        };

        [TestMethod]
        public void EveryRestOperation_IsBridgedOrConsciouslyDeferred()
        {
            var snapshot = Path.Combine(TestRepo.Root(), "features", "done", "web-ui", "openapi-v0.1.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(snapshot));

            var bridged = new HashSet<String>(
                McpBridgedEndpoints.All.Select(e => $"{e.Method} {e.Path}"), StringComparer.Ordinal);

            var uncovered = new List<String>();
            foreach (var path in doc.RootElement.GetProperty("paths").EnumerateObject())
            {
                // The /ns/{ns} twins are auto-generated from the bare routes the bridge already covers.
                if (path.Name.StartsWith("/ns/{ns}/", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var operation in path.Value.EnumerateObject())
                {
                    var method = operation.Name.ToUpperInvariant();
                    if (method is not ("GET" or "POST" or "PUT" or "DELETE" or "PATCH" or "HEAD"))
                    {
                        continue;
                    }

                    var op = $"{method} {path.Name}";
                    if (bridged.Contains(op) || Deferrals.Any(d => d.Matches(op)))
                    {
                        continue;
                    }
                    uncovered.Add(op);
                }
            }

            Assert.AreEqual(0, uncovered.Count,
                "These REST operations are neither bridged by an MCP tool nor consciously deferred. " +
                "Per the engine→REST→MCP rule (spec §7 / CLAUDE.md), surface each as an MCP tool " +
                "(add it to McpBridgedEndpoints + the tool surface) or record a deferral reason in " +
                "McpRestCoverageTest.Deferrals:\n" + String.Join("\n", uncovered.OrderBy(x => x)));
        }

        [TestMethod]
        public void EveryBridgedEndpoint_IsDistinct()
        {
            var all = McpBridgedEndpoints.All.Select(e => $"{e.Method} {e.Path}").ToList();
            Assert.AreEqual(all.Count, all.Distinct().Count(), "the bridged-endpoint list has no duplicates");
        }
    }
}
