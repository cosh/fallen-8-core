// MIT License
//
// Fallen8ChangeFeedOptions.cs
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
using NoSQL.GraphDB.Core.ChangeFeed;

namespace NoSQL.GraphDB.App.Configuration
{
    /// <summary>
    ///   Change feed configuration for the hosted API, bound from the <c>Fallen8:ChangeFeed</c>
    ///   section (feature change-feed). Enabled by default in the hosted app (a read-only
    ///   surface with a small idle cost - what makes F8 Studio live out of the box); the ENGINE
    ///   default stays opt-in via <see cref="ChangeFeedOptions"/>.
    /// </summary>
    public sealed class Fallen8ChangeFeedOptions
    {
        /// <summary>The configuration section this binds from.</summary>
        public const String SectionName = "Fallen8:ChangeFeed";

        /// <summary>Whether the hosted engine carries a change feed. Default true; false restores
        /// today's behaviour exactly (the endpoint answers 503, the write path pays a null check).</summary>
        public Boolean Enabled { get; set; } = true;

        /// <summary>Ring-buffer capacity (events) for <c>?since=</c> catch-up. Default 8192.</summary>
        public Int32 BufferSize { get; set; } = 8192;

        /// <summary>Per-subscriber bounded queue capacity (events). Default 1024.</summary>
        public Int32 SubscriberQueueSize { get; set; } = 1024;

        /// <summary>Maximum concurrent SSE subscribers; beyond it the endpoint answers 503. Default 32.</summary>
        public Int32 MaxSubscribers { get; set; } = 32;

        /// <summary>SSE comment-heartbeat interval (seconds), bounding dead-connection detection and
        /// keeping proxies from idling the stream out. Default 15.</summary>
        public Int32 KeepAliveSeconds { get; set; } = 15;

        /// <summary>The engine-side options, or null when disabled.</summary>
        public ChangeFeedOptions ToEngineOptions()
        {
            if (!Enabled)
            {
                return null;
            }

            return new ChangeFeedOptions
            {
                BufferSize = BufferSize,
                SubscriberQueueSize = SubscriberQueueSize,
                MaxSubscribers = MaxSubscribers
            };
        }
    }
}
