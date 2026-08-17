// MIT License
//
// McpBridgedEndpoints.cs
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

namespace NoSQL.GraphDB.Tests
{
    /// <summary>
    ///   The single source of truth for the REST endpoints the MCP bridge issues requests to
    ///   (bare routes; the <c>/ns/{ns}</c> twins are the same templates the bridge builds itself).
    ///   Shared by the contract test (every bridged route exists in the snapshot) and the
    ///   coverage/governance test (every REST route is bridged or deliberately deferred).
    /// </summary>
    internal static class McpBridgedEndpoints
    {
        public static readonly (String Method, String Path)[] All =
        {
            // Read tier.
            ("GET", "/status"),
            ("GET", "/statistics"),
            ("GET", "/ns"),
            ("GET", "/vertex/{vertexIdentifier}"),
            ("GET", "/edge/{edgeIdentifier}"),
            ("GET", "/graphelement/{graphElementIdentifier}"),
            ("POST", "/scan/index/all"),
            ("POST", "/scan/graph/property/{propertyId}"),
            ("POST", "/scan/graph/properties"),
            ("POST", "/scan/index/fulltext"),
            ("POST", "/scan/index/vector"),
            ("POST", "/embedding/search"),
            ("POST", "/path/{from}/to/{to}"),
            ("GET", "/analytics/algorithms"),
            ("POST", "/analytics/{algorithmName}"),
            // Plugin registry (feature plugin-registration). f8_plugins: list/get/invoke are Read;
            // delete is gated on the write capability; register_* on the code capability.
            ("GET", "/plugins"),
            ("GET", "/plugins/{name}"),
            ("POST", "/plugins/function/{name}/invoke"),
            ("DELETE", "/plugins/{name}"),
            ("POST", "/plugins/algorithm"),
            ("POST", "/plugins/function"),
            // Documents (features unstructured-ingestion, semantic-layer). f8_documents:
            // list/get/search/binding are Read; ingest_text/delete/bind are gated on the write
            // capability. The multipart file route is a recorded deferral (see
            // McpRestCoverageTest.Deferrals).
            ("GET", "/document"),
            ("GET", "/document/{documentId}"),
            ("POST", "/document/text"),
            ("POST", "/document/search"),
            ("DELETE", "/document/{documentId}"),
            ("GET", "/document/binding"),
            ("POST", "/document/binding/ensure"),
            ("GET", "/document/entities"),
            // Write tier.
            ("PUT", "/vertex"),
            ("PUT", "/edge"),
            ("PUT", "/vertices"),
            ("PUT", "/edges"),
            ("PUT", "/graphelement/{graphElementIdentifier}/{propertyIdString}"),
            ("DELETE", "/graphelement/{graphElementIdentifier}/{propertyIdString}"),
            ("DELETE", "/graphelement/{graphElementIdentifier}"),
            // The batch write path (feature platform-integrity-audit W2), bridged as OPS on the
            // existing f8_mutate (set_properties / remove_elements) rather than new tools - every
            // tool's schema is paid for in every agent's context on every call.
            ("PUT", "/graphelements/properties"),
            ("DELETE", "/graphelements"),
            ("PUT", "/graphelement/{graphElementIdentifier}/embedding/{embeddingName}"),
            ("PUT", "/subgraph"),
            ("PUT", "/ns/{name}"),
            ("PATCH", "/ns/{name}"),
            ("DELETE", "/ns/{name}"),
            // Admin tier. Activation is admin rather than write: it restores a checkpoint into the
            // running process (feature namespace-startup-load §4.8), which is durability work, not
            // part of the create/rename/drop lifecycle f8_namespace owns.
            ("POST", "/ns/{name}/activate"),
            ("PUT", "/save"),
            ("GET", "/savegames"),
            ("PUT", "/savegames/{id}/load"),
            ("HEAD", "/trim"),
            ("HEAD", "/tabularasa"),
        };
    }
}
