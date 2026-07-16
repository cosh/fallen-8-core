// MIT License
//
// AnalyticsWriteBack.cs
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
using System.Collections.Generic;
using NoSQL.GraphDB.Core;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.App.Helper
{
    /// <summary>
    ///   The analytics property write-back executor (feature graph-analytics, spec §3.5): the
    ///   full per-vertex result's delivery vehicle, through the sanctioned plugin write path and
    ///   nothing else - chunked <see cref="DelegateTransaction"/> bodies calling
    ///   <c>IFallen8WriterContext.SetProperty</c> on the single writer thread.
    ///
    ///   <para>Each chunk (50 000 vertices) is atomic (the undo journal covers SetProperty); the
    ///   whole write-back is NOT atomic across chunks - a mid-way failure leaves earlier chunks
    ///   applied, remedied by re-running (idempotent overwrite). Chunking is the deliberate
    ///   trade against a single multi-second delegate body stalling every other write.
    ///   Durability is mode (a): snapshot-durable, not WAL-logged.</para>
    /// </summary>
    internal static class AnalyticsWriteBack
    {
        /// <summary>Vertices per DelegateTransaction chunk.</summary>
        internal const Int32 ChunkSize = 50_000;

        /// <summary>
        ///   Writes <paramref name="valueByVertexId"/> as the property <paramref name="propertyKey"/>
        ///   on each vertex, in ascending-id order, in atomic chunks. Returns false when a chunk
        ///   was rolled back - earlier chunks stay applied (documented non-atomicity).
        /// </summary>
        internal static Boolean TryExecute(out Int32 verticesWritten, out Int32 chunks,
            IFallen8 fallen8, IReadOnlyList<KeyValuePair<Int32, Object>> valueByVertexId,
            String propertyKey)
        {
            verticesWritten = 0;
            chunks = 0;

            for (var start = 0; start < valueByVertexId.Count; start += ChunkSize)
            {
                var chunkStart = start;
                var chunkEnd = Math.Min(start + ChunkSize, valueByVertexId.Count);

                var tx = new DelegateTransaction(context =>
                {
                    for (var i = chunkStart; i < chunkEnd; i++)
                    {
                        context.SetProperty(valueByVertexId[i].Key, propertyKey, valueByVertexId[i].Value);
                    }
                }, name: "analytics-writeback:" + propertyKey);

                var info = fallen8.EnqueueTransaction(tx);
                info.WaitUntilFinished();

                if (info.TransactionState != TransactionState.Finished)
                {
                    return false;
                }

                verticesWritten += chunkEnd - chunkStart;
                chunks++;
            }

            return true;
        }
    }
}
