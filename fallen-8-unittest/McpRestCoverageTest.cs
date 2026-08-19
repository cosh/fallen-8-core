// MIT License
//
// McpRestCoverageTest.cs
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
            // The batch element read (feature platform-integrity-audit W6) exists so a reconciling
            // client can diff HUNDREDS of elements before deciding what to write. That is the opposite
            // of an agent's read pattern, and shipping several hundred projected elements into a tool
            // result is exactly what the token-economy design forbids (mcp-server spec §3.5: byte
            // budgets, id-first results, pagination). f8_search plus f8_get's `fields` already answer
            // the few-element case within the budget. Bridging it as a mode on f8_get was considered and
            // rejected: f8_get takes a REQUIRED kind+id and has no mode enum, so a batch mode would
            // restructure a tool agents already depend on, to add a capability they should not use.
            new(op => op == "POST /graphelements/get",
                "bulk element reads are for reconciling clients, not agents; several hundred projected elements would blow the token budget f8_search/f8_get are shaped around"),
            new(op => op.Contains("/scan/index/range"),
                "range scans are deferred; index-equality, fulltext and vector modes cover the agent path"),
            new(op => op.Contains("/scan/index/spatial"),
                "spatial scans need a live reference geometry; not an agent-facing surface"),
            new(op => op.Contains("/partition/"),
                "analytics partition-member pagination is deferred; f8_analytics returns partition summaries"),
            new(op => op.StartsWith("GET /subgraph") || op.StartsWith("DELETE /subgraph") || op.EndsWith("/recalculate"),
                "subgraph read/recalculate/delete are deferred; define via f8_subgraph"),
            // " /index" (space-anchored) matches paths that START /index — NOT the bridged /scan/index/*.
            new(op => op.Contains(" /index"),
                "index lifecycle (create/populate/drop) is operator/setup tooling; agents scan existing indices via f8_search"),
            new(op => op.Contains("/storedquery"),
                "stored-query registration/listing is code-gated setup; agents invoke by name via the storedQuery parameter"),
            new(op => op.Contains("/service"),
                "service administration is operator-only"),
            // The plugin registry is bridged by f8_plugins (list/get/invoke/delete/register_*); only the
            // side-effect-free compile-check endpoints stay unbridged - they back the Studio authoring
            // editor, and agents register directly (feature plugin-registration).
            new(op => op.Contains("/plugins/") && op.EndsWith("/validate"),
                "the plugin compile-check endpoints back the Studio authoring editor; agents register directly"),
            new(op => op.Contains("/bulk/"),
                "bulk import/export is stream-shaped operator-tier I/O (spec §7)"),
            // f8_documents bridges the document surface; only the multipart FILE route stays
            // unbridged (feature unstructured-ingestion FR-10): base64 file payloads through LLM
            // tool calls are token-hostile - agents hold text (ingest_text), and binary uploads
            // are one curl away.
            new(op => op == "POST /document",
                "multipart file upload is token-hostile over MCP; agents ingest text via f8_documents ingest_text"),
            new(op => op.Contains("/changefeed"),
                "the SSE change feed has no MCP-native primitive for a continuous ordered delta stream (spec §3.2)"),
            new(op => op.Contains("/delegates"),
                "dynamic-code validation is a code-gated editor/dev tool"),
            new(op => op.Contains("/benchmark") || op.Contains("/generate") || op.Contains("/unittest"),
                "development/benchmark/sample-generation tooling, not an agent surface"),
            // Embedding endpoints EXCEPT the two bridged ones (semantic search + set_embedding).
            new(op => op.Contains("/embedding/")
                    && op != "POST /embedding/search"
                    && op != "PUT /graphelement/{graphElementIdentifier}/embedding/{embeddingName}",
                "element embedding writes (element/elements/text) and per-element embedding read/remove are deferred; " +
                "f8_mutate set_embedding writes, f8_search mode:semantic reads"),
            new(op => op == "GET /ns/{name}",
                "per-namespace detail is surfaced by f8_overview(namespace); the directory by f8_overview"),
            new(op => op == "GET /savegames/{id}" || op == "DELETE /savegames/{id}",
                "save-game detail/deletion is operator housekeeping; f8_admin lists and restores by id"),
            new(op => op == "PUT /save/all" || op == "HEAD /tabularasa/all",
                "collection-wide save/reset variants are deferred; the single-namespace forms are bridged"),
            new(op => op == "PUT /load",
                "the file-path load is deferred; f8_admin load uses the id-based /savegames/{id}/load"),
            new(op => op == "POST /chat",
                "the chat gateway is Studio's model path (browser -> instance -> Ollama); agents bring " +
                "their own model. Chat capability state is discoverable via f8_overview (chatEnabled)"),
            // GET /config is no longer deferred: feature writable-instance-config turned it into the whole
            // setting inventory (tier, source, effective value, and the reason a key is refused), and
            // f8_admin get_settings/set_settings bridge it. The deferral had to be DELETED rather than
            // narrowed, because this test asserts the bridged and deferred sets are disjoint.
            // All four /integrations routes are deferred rather than bridged (feature integrations
            // spec section 18). Three of them are DECLARATIONS rather than capabilities: the provider
            // catalog and the vocabulary describe what COULD be run, and snapshot validation is an
            // authoring aid - a provider is C# compiled into the fallen-8-integrations deployable, so
            // an agent cannot add one over the API at any tier. The fourth, running a job, has a real
            // agent case and one specific reason to withhold it: a run is a complete-snapshot write, so
            // a job submitted under an identity that is not EXACTLY the one that integration has always
            // used withdraws and deletes every element the real integration claimed, nothing can detect
            // it, and an agent composing a job is the caller most likely to invent a plausible-looking
            // identifier. Revisit when the runtime can tell a new identity from a mistyped one.
            // Contains, not StartsWith: the predicate matches "METHOD /path".
            new(op => op.Contains("/integrations"),
                "the integration runtime proxy is deferred: three routes are declarations, and a job run is a complete-snapshot write no unverifiable identity may trigger"),
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

        [TestMethod]
        public void NoBridgedEndpoint_MatchesADeferral()
        {
            // Bridged and deferred must be mutually exclusive, or a bridged route that is later
            // removed could be silently re-covered by a broad deferral rule — hiding the very
            // regression the tripwire exists to catch.
            var overlaps = McpBridgedEndpoints.All
                .Select(e => $"{e.Method} {e.Path}")
                .Where(op => Deferrals.Any(d => d.Matches(op)))
                .ToList();

            Assert.AreEqual(0, overlaps.Count,
                "a bridged endpoint is also matched by a deferral rule (narrow the rule so the sets stay disjoint):\n" +
                String.Join("\n", overlaps));
        }
    }
}
