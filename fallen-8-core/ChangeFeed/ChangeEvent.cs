// MIT License
//
// ChangeEvent.cs
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

namespace NoSQL.GraphDB.Core.ChangeFeed
{
    /// <summary>
    ///   One change-feed event: metadata about one committed mutation (feature change-feed).
    ///   Carries ids, labels and property KEYS only - never property values (payload size and
    ///   security posture; the consumer re-fetches the element when it needs the value).
    ///   Immutable after construction; the dispatcher assigns <see cref="Seq"/> exactly once.
    /// </summary>
    public sealed class ChangeEvent
    {
        /// <summary>
        ///   The monotonic sequence number, assigned by the dispatcher in commit order, gap-free
        ///   per process epoch. 0 until assigned.
        /// </summary>
        public long Seq
        {
            get; internal set;
        }

        /// <summary>The commit timestamp (UTC), captured once per transaction on the writer and
        /// shared by all of that transaction's events.</summary>
        public DateTime Ts
        {
            get;
        }

        /// <summary>The event kind.</summary>
        public ChangeEventKind Kind
        {
            get;
        }

        /// <summary>The element category (<see cref="ChangeElementType.None"/> for resync).</summary>
        public ChangeElementType Element
        {
            get;
        }

        /// <summary>The element id (element events only).</summary>
        public Int32 Id
        {
            get;
        }

        /// <summary>The element label; null when the element has none (or for resync).</summary>
        public String Label
        {
            get;
        }

        /// <summary>The property key (propertySet/propertyRemoved only).</summary>
        public String Key
        {
            get;
        }

        /// <summary>The source vertex id (edgeCreated only; -1 otherwise).</summary>
        public Int32 SourceId
        {
            get;
        }

        /// <summary>The target vertex id (edgeCreated only; -1 otherwise).</summary>
        public Int32 TargetId
        {
            get;
        }

        /// <summary>The resync reason (resync events only): trim, tabulaRasa, load,
        /// delegateWrite, overflow, or seekOutOfRange.</summary>
        public String ResyncReason
        {
            get;
        }

        internal ChangeEvent(DateTime ts, ChangeEventKind kind, ChangeElementType element, Int32 id,
            String label, String key, Int32 sourceId, Int32 targetId, String resyncReason)
        {
            Ts = ts;
            Kind = kind;
            Element = element;
            Id = id;
            Label = label;
            Key = key;
            SourceId = sourceId;
            TargetId = targetId;
            ResyncReason = resyncReason;
        }

        internal static ChangeEvent Resync(DateTime ts, String reason)
        {
            return new ChangeEvent(ts, ChangeEventKind.Resync, ChangeElementType.None, -1, null, null, -1, -1, reason);
        }
    }
}
