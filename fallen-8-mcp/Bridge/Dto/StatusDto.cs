// MIT License
//
// StatusDto.cs
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

namespace NoSQL.GraphDB.Mcp.Bridge.Dto
{
    /// <summary>
    ///   The bridge's own minimal projection of the apiApp's <c>StatusREST</c> — the fields
    ///   f8_overview surfaces. It deliberately does NOT carry every StatusREST field, and it is
    ///   pinned against the OpenAPI snapshot by the contract test (success-shape only). Unknown
    ///   fields on the wire are ignored (forward-compatible).
    /// </summary>
    public sealed class StatusDto
    {
        public Int64 UsedMemory { get; set; }

        /// <summary>
        ///   The addressed namespace's residency: <c>ready</c>, or <c>notLoaded</c> when it is
        ///   cataloged but has no engine in the target process (feature namespace-startup-load).
        ///   /status is the only namespace-scoped route that answers in that state, and it then
        ///   reports no counts and no inventories at all.
        /// </summary>
        public String? NamespaceState { get; set; }

        /// <summary>Null for a <c>notLoaded</c> namespace - absent, deliberately never zero, so an
        /// agent cannot read "healthy and empty" off a namespace that holds data.</summary>
        public Int32? VertexCount { get; set; }

        /// <summary>Null for a <c>notLoaded</c> namespace (see <see cref="VertexCount"/>).</summary>
        public Int32? EdgeCount { get; set; }

        public List<IndexDto>? Indices { get; set; }

        public List<String>? AvailableIndexPlugins { get; set; }

        public List<String>? AvailablePathPlugins { get; set; }

        public List<String>? AvailableAnalyticsPlugins { get; set; }

        public Boolean ApiKeyRequired { get; set; }

        public Boolean Authenticated { get; set; }

        /// <summary>Embedding-provider state (null when the target wired no provider). Left as an
        /// opaque node the overview passes through; f8_overview reports whether it is present and
        /// enabled without re-modelling the whole provider stats shape.</summary>
        public EmbeddingStateDto? Embedding { get; set; }

        /// <summary>Chat-gateway state (feature instance-config; null when the target wired no
        /// provider). f8_overview reports chatEnabled from it, the agent-facing view of the
        /// otherwise-deferred POST /chat capability.</summary>
        public ChatStateDto? Chat { get; set; }
    }

    public sealed class IndexDto
    {
        public String? IndexId { get; set; }

        public String? PluginType { get; set; }

        public Int32? Keys { get; set; }

        public Int32? Values { get; set; }

        public String? EmbeddingName { get; set; }

        public String? Model { get; set; }
    }

    public sealed class EmbeddingStateDto
    {
        public Boolean Enabled { get; set; }

        public String? Model { get; set; }

        public Int32? Dimensions { get; set; }

        /// <summary>
        ///   The backend selector value the target is configured for (feature model-providers), so
        ///   an agent can tell where a prompt or an embed would go before sending one. Null when the
        ///   target did not report one, never a guessed default: an invented backend is a fact an
        ///   agent would act on.
        /// </summary>
        public String? Backend { get; set; }
    }

    public sealed class ChatStateDto
    {
        public Boolean Enabled { get; set; }

        public String? Model { get; set; }

        /// <summary>The chat backend selector value; same contract as
        /// <see cref="EmbeddingStateDto.Backend" />.</summary>
        public String? Backend { get; set; }
    }
}
