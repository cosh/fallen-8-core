// MIT License
//
// ChangeFeedRingBuffer.cs
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
    ///   The bounded catch-up buffer (feature change-feed): the last <c>capacity</c> events,
    ///   overwriting oldest. Sequence numbers are gap-free from 1, so slot placement is
    ///   <c>(seq - 1) % capacity</c> and the buffered window is always the contiguous range
    ///   <c>[OldestSeq, LastSeq]</c>. NOT thread-safe on its own: every access happens under the
    ///   dispatcher's gate (append on the dispatcher task, replay during subscribe) - never on
    ///   the writer thread.
    /// </summary>
    internal sealed class ChangeFeedRingBuffer
    {
        private readonly ChangeEvent[] _slots;

        /// <summary>The highest sequence number appended; 0 when empty.</summary>
        internal long LastSeq
        {
            get; private set;
        }

        /// <summary>The oldest sequence number still buffered; 1 when nothing was evicted yet.</summary>
        internal long OldestSeq
        {
            get
            {
                if (LastSeq == 0)
                {
                    return 1;
                }
                return Math.Max(1, LastSeq - _slots.Length + 1);
            }
        }

        internal ChangeFeedRingBuffer(int capacity)
        {
            _slots = new ChangeEvent[Math.Max(1, capacity)];
        }

        /// <summary>Appends an event (its <see cref="ChangeEvent.Seq"/> must be <c>LastSeq + 1</c>).</summary>
        internal void Append(ChangeEvent changeEvent)
        {
            _slots[(changeEvent.Seq - 1) % _slots.Length] = changeEvent;
            LastSeq = changeEvent.Seq;
        }

        /// <summary>
        ///   Whether a replay from (exclusive) <paramref name="sinceSeq"/> can be served gap-free:
        ///   every event with <c>seq &gt; sinceSeq</c> is still buffered and the seek is not past
        ///   the head.
        /// </summary>
        internal bool CanReplayFrom(long sinceSeq)
        {
            return sinceSeq >= OldestSeq - 1 && sinceSeq <= LastSeq;
        }

        /// <summary>Invokes <paramref name="deliver"/> for every buffered event with
        /// <c>seq &gt; sinceSeq</c>, in ascending order.</summary>
        internal void ReplayFrom(long sinceSeq, Action<ChangeEvent> deliver)
        {
            for (var seq = Math.Max(sinceSeq + 1, OldestSeq); seq <= LastSeq; seq++)
            {
                deliver(_slots[(seq - 1) % _slots.Length]);
            }
        }
    }
}
