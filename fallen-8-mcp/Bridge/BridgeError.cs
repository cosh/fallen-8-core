// MIT License
//
// BridgeError.cs
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

namespace NoSQL.GraphDB.Mcp.Bridge
{
    /// <summary>
    ///   A normalized downstream failure. The bridge maps Fallen-8's mixed error shapes
    ///   (problem+json, plain-string bodies, soft-not-found) into this single compact form
    ///   (spec §3.2); the tool layer renders it as an <c>isError</c> tool result. The Fallen-8
    ///   API key never appears here — the mapper only ever copies status/title/detail from the
    ///   response body, never request headers.
    /// </summary>
    public sealed class BridgeError : Exception
    {
        public BridgeError(Int32 status, String title, String detail, Boolean retryable = false)
            : base($"{status} {title}: {detail}")
        {
            Status = status;
            Title = title;
            Detail = detail;
            Retryable = retryable;
        }

        /// <summary>The downstream HTTP status (or a synthetic one for transport failures).</summary>
        public Int32 Status { get; }

        /// <summary>A short problem title (from problem+json, or a reason phrase).</summary>
        public String Title { get; }

        /// <summary>The human-readable detail (problem+json <c>detail</c>, or the string body).</summary>
        public String Detail { get; }

        /// <summary>True for a 429/503-style transient failure the agent may retry with backoff.</summary>
        public Boolean Retryable { get; }
    }
}
