// MIT License
//
// ChangeEventKind.cs
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
    ///   The kind of a change-feed event (feature change-feed). Element kinds describe one
    ///   committed mutation of one element; <see cref="Resync"/> signals that continuity was lost
    ///   (or that an operation's effect cannot be expressed as element deltas) and the client must
    ///   re-fetch the state it cares about.
    /// </summary>
    [Flags]
    public enum ChangeEventKind
    {
        VertexCreated = 1,
        VertexRemoved = 2,
        EdgeCreated = 4,
        EdgeRemoved = 8,
        PropertySet = 16,
        PropertyRemoved = 32,

        /// <summary>
        ///   Continuity was lost or the operation is coarser than element deltas (trim,
        ///   tabula rasa, load, an opaque plugin write, buffer overflow, an out-of-range seek).
        ///   Bypasses every subscriber filter: a filter that could suppress it would silently
        ///   corrupt the client's view.
        /// </summary>
        Resync = 64
    }

    /// <summary>The element category an event describes.</summary>
    public enum ChangeElementType
    {
        /// <summary>No element (resync events).</summary>
        None,
        Vertex,
        Edge
    }
}
