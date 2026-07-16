// MIT License
//
// ChangeEventREST.cs
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
using System.Text.Json.Serialization;
using NoSQL.GraphDB.Core.ChangeFeed;

namespace NoSQL.GraphDB.App.Controllers.Model
{
    /// <summary>
    ///   One change-feed event as serialized onto the SSE stream (feature change-feed): metadata
    ///   about one committed mutation. Carries ids, labels and property KEYS only - never property
    ///   values. Fields are ABSENT (not null) when not applicable to the kind.
    /// </summary>
    public sealed class ChangeEventREST
    {
        /// <summary>Monotonic sequence number (commit order), gap-free per process epoch.</summary>
        /// <example>4712</example>
        [JsonPropertyName("seq")]
        public Int64 Seq
        {
            get; set;
        }

        /// <summary>The commit timestamp (UTC, ISO-8601), shared by a transaction's events.</summary>
        [JsonPropertyName("ts")]
        public DateTime Ts
        {
            get; set;
        }

        /// <summary>The event kind: vertexCreated, vertexRemoved, edgeCreated, edgeRemoved,
        /// propertySet, propertyRemoved, or resync.</summary>
        /// <example>propertySet</example>
        [JsonPropertyName("kind")]
        public String Kind
        {
            get; set;
        }

        /// <summary>"vertex" or "edge" (element events only).</summary>
        /// <example>vertex</example>
        [JsonPropertyName("element")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public String Element
        {
            get; set;
        }

        /// <summary>The element id (element events only).</summary>
        /// <example>42</example>
        [JsonPropertyName("id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Int32? Id
        {
            get; set;
        }

        /// <summary>The element label; absent when the element has none.</summary>
        /// <example>person</example>
        [JsonPropertyName("label")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public String Label
        {
            get; set;
        }

        /// <summary>The property key (propertySet/propertyRemoved only).</summary>
        /// <example>name</example>
        [JsonPropertyName("key")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public String Key
        {
            get; set;
        }

        /// <summary>The source vertex id (edgeCreated only).</summary>
        [JsonPropertyName("source")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Int32? Source
        {
            get; set;
        }

        /// <summary>The target vertex id (edgeCreated only).</summary>
        [JsonPropertyName("target")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Int32? Target
        {
            get; set;
        }

        /// <summary>The resync reason (resync only): trim, tabulaRasa, load, delegateWrite,
        /// overflow, or seekOutOfRange.</summary>
        /// <example>trim</example>
        [JsonPropertyName("reason")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public String Reason
        {
            get; set;
        }

        /// <summary>The wire name of an event kind (camelCase, part of the public contract).</summary>
        public static String KindName(ChangeEventKind kind)
        {
            switch (kind)
            {
                case ChangeEventKind.VertexCreated: return "vertexCreated";
                case ChangeEventKind.VertexRemoved: return "vertexRemoved";
                case ChangeEventKind.EdgeCreated: return "edgeCreated";
                case ChangeEventKind.EdgeRemoved: return "edgeRemoved";
                case ChangeEventKind.PropertySet: return "propertySet";
                case ChangeEventKind.PropertyRemoved: return "propertyRemoved";
                case ChangeEventKind.Resync: return "resync";
                default: return kind.ToString();
            }
        }

        internal static ChangeEventREST FromEvent(ChangeEvent changeEvent)
        {
            var isElementEvent = changeEvent.Element != ChangeElementType.None;
            return new ChangeEventREST
            {
                Seq = changeEvent.Seq,
                Ts = changeEvent.Ts,
                Kind = KindName(changeEvent.Kind),
                Element = isElementEvent
                    ? (changeEvent.Element == ChangeElementType.Vertex ? "vertex" : "edge")
                    : null,
                Id = isElementEvent ? changeEvent.Id : (Int32?)null,
                Label = changeEvent.Label,
                Key = changeEvent.Key,
                Source = changeEvent.Kind == ChangeEventKind.EdgeCreated ? changeEvent.SourceId : (Int32?)null,
                Target = changeEvent.Kind == ChangeEventKind.EdgeCreated ? changeEvent.TargetId : (Int32?)null,
                Reason = changeEvent.ResyncReason
            };
        }
    }
}
