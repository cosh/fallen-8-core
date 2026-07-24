// MIT License
//
// McpContractTest.cs
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
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   Drift guard for the MCP REST bridge (feature mcp-server §3.2/§3.11). Every endpoint the
    ///   bridge builds a request for must exist in the pinned OpenAPI snapshot with the method the
    ///   bridge uses — so a REST-surface rename fails THIS suite, not production. Scoped to
    ///   path + HTTP method (and, where cheap, the presence of a 200/2xx success response); it
    ///   deliberately does NOT pin error-body shape, which the snapshot cannot encode faithfully
    ///   (it advertises ProblemDetails where the runtime returns plain strings — that is pinned by
    ///   the live round-trip error-mapping tests instead).
    /// </summary>
    [TestClass]
    public class McpContractTest
    {
        // Every (route template, method) the bridge issues. Bare routes; the /ns/{ns} twins are
        // the same templates under a prefix and are covered by the namespace round-trip tests.
        private static readonly (String Path, String Method)[] BridgedEndpoints =
        {
            ("/status", "get"),
            ("/ns", "get"),
            ("/statistics", "get"),
            ("/vertex/{vertexIdentifier}", "get"),
            ("/edge/{edgeIdentifier}", "get"),
            ("/graphelement/{graphElementIdentifier}", "get"),
            ("/scan/index/all", "post"),
            ("/scan/graph/property/{propertyId}", "post"),
            ("/scan/index/fulltext", "post"),
            ("/scan/index/vector", "post"),
            ("/embedding/search", "post"),
            ("/path/{from}/to/{to}", "post"),
            ("/analytics/algorithms", "get"),
            ("/analytics/{algorithmName}", "post"),
            // Write tier (Phase 2).
            ("/vertex", "put"),
            ("/edge", "put"),
            ("/graphelement/{graphElementIdentifier}/{propertyIdString}", "put"),
            ("/graphelement/{graphElementIdentifier}/{propertyIdString}", "delete"),
            ("/graphelement/{graphElementIdentifier}", "delete"),
            ("/graphelement/{graphElementIdentifier}/embedding/{embeddingName}", "put"),
            ("/subgraph", "put"),
            ("/ns/{name}", "put"),
            ("/ns/{name}", "patch"),
            ("/ns/{name}", "delete"),
            // Admin tier (Phase 2).
            ("/save", "put"),
            ("/savegames", "get"),
            ("/savegames/{id}/load", "put"),
            ("/trim", "head"),
            ("/tabularasa", "head"),
        };

        private static JsonElement Paths()
        {
            var snapshot = Path.Combine(TestRepo.Root(), "features", "done", "web-ui", "openapi-v0.1.json");
            Assert.IsTrue(File.Exists(snapshot), "the pinned OpenAPI snapshot exists at " + snapshot);
            var doc = JsonDocument.Parse(File.ReadAllText(snapshot));
            return doc.RootElement.GetProperty("paths").Clone();
        }

        [TestMethod]
        public void EveryBridgedEndpoint_ExistsInThePinnedSnapshotWithItsMethod()
        {
            var paths = Paths();
            var missing = new List<String>();

            foreach (var (path, method) in BridgedEndpoints)
            {
                if (!paths.TryGetProperty(path, out var operations) ||
                    !operations.TryGetProperty(method, out _))
                {
                    missing.Add($"{method.ToUpperInvariant()} {path}");
                }
            }

            Assert.AreEqual(0, missing.Count,
                "the MCP bridge targets REST endpoints absent from the pinned OpenAPI snapshot " +
                "(the REST surface drifted, or the bridge's route is wrong):\n" + String.Join("\n", missing));
        }
    }
}
