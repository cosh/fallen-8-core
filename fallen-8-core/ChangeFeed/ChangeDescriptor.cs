// MIT License
//
// ChangeDescriptor.cs
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

namespace NoSQL.GraphDB.Core.ChangeFeed
{
    /// <summary>
    ///   The compact record of ONE committed transaction's changes, captured on the single writer
    ///   thread immediately after a successful <c>TryExecute</c> and BEFORE
    ///   <c>ReleaseAfterCompletion</c> drops the input payload (feature change-feed). It holds
    ///   only primitives - ids, labels, property keys, edge endpoints - plus one commit
    ///   timestamp: no property values and no model or definition references, so capturing never
    ///   re-introduces the retention the release exists to fix (M3). The dispatcher expands it
    ///   into per-element <see cref="ChangeEvent"/>s off the writer thread.
    /// </summary>
    public sealed class ChangeDescriptor
    {
        /// <summary>One captured change: the primitive fields of a future <see cref="ChangeEvent"/>.</summary>
        internal readonly struct Item
        {
            internal readonly ChangeEventKind Kind;
            internal readonly ChangeElementType Element;
            internal readonly Int32 Id;
            internal readonly String Label;
            internal readonly String Key;
            internal readonly Int32 SourceId;
            internal readonly Int32 TargetId;
            internal readonly String ResyncReason;

            internal Item(ChangeEventKind kind, ChangeElementType element, Int32 id, String label,
                String key, Int32 sourceId, Int32 targetId, String resyncReason)
            {
                Kind = kind;
                Element = element;
                Id = id;
                Label = label;
                Key = key;
                SourceId = sourceId;
                TargetId = targetId;
                ResyncReason = resyncReason;
            }
        }

        /// <summary>The commit timestamp (UTC), captured once on the writer.</summary>
        internal DateTime Ts
        {
            get;
        }

        /// <summary>The captured items, in mutation order within the transaction.</summary>
        internal List<Item> Items
        {
            get;
        }

        private ChangeDescriptor(DateTime ts, List<Item> items)
        {
            Ts = ts;
            Items = items;
        }

        /// <summary>
        ///   The writer-side collector a transaction's <c>DescribeChanges</c> fills. Building is
        ///   cheap: one list plus one struct append per element event.
        /// </summary>
        public sealed class Builder
        {
            private readonly List<Item> _items = new List<Item>();
            private readonly DateTime _ts = DateTime.UtcNow;

            public void VertexCreated(Int32 id, String label)
            {
                _items.Add(new Item(ChangeEventKind.VertexCreated, ChangeElementType.Vertex, id, label, null, -1, -1, null));
            }

            public void VertexRemoved(Int32 id, String label)
            {
                _items.Add(new Item(ChangeEventKind.VertexRemoved, ChangeElementType.Vertex, id, label, null, -1, -1, null));
            }

            public void EdgeCreated(Int32 id, String label, Int32 sourceId, Int32 targetId)
            {
                _items.Add(new Item(ChangeEventKind.EdgeCreated, ChangeElementType.Edge, id, label, null, sourceId, targetId, null));
            }

            public void EdgeRemoved(Int32 id, String label)
            {
                _items.Add(new Item(ChangeEventKind.EdgeRemoved, ChangeElementType.Edge, id, label, null, -1, -1, null));
            }

            public void PropertySet(ChangeElementType element, Int32 id, String label, String key)
            {
                _items.Add(new Item(ChangeEventKind.PropertySet, element, id, label, key, -1, -1, null));
            }

            public void PropertyRemoved(ChangeElementType element, Int32 id, String label, String key)
            {
                _items.Add(new Item(ChangeEventKind.PropertyRemoved, element, id, label, key, -1, -1, null));
            }

            /// <summary>Records a coarse resync (trim / tabulaRasa / load / delegateWrite).</summary>
            public void Resync(String reason)
            {
                _items.Add(new Item(ChangeEventKind.Resync, ChangeElementType.None, -1, null, null, -1, -1, reason));
            }

            /// <summary>The built descriptor, or null when the transaction described no changes.</summary>
            internal ChangeDescriptor BuildOrNull()
            {
                return _items.Count == 0 ? null : new ChangeDescriptor(_ts, _items);
            }
        }

        /// <summary>A single-item resync descriptor (dispatcher/engine internal signals).</summary>
        internal static ChangeDescriptor ForResync(String reason)
        {
            return new ChangeDescriptor(DateTime.UtcNow, new List<Item>
            {
                new Item(ChangeEventKind.Resync, ChangeElementType.None, -1, null, null, -1, -1, reason)
            });
        }
    }
}
