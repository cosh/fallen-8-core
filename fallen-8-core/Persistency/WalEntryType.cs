// MIT License
//
// WalEntryType.cs
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

namespace NoSQL.GraphDB.Core.Persistency
{
    /// <summary>
    ///   The kind of a single write-ahead-log entry. The value is the first byte of every entry's
    ///   payload; the remainder (if any) is the transaction's serialized definition. Values are
    ///   fixed and MUST NOT be renumbered - they are the on-disk encoding of a logged operation.
    ///
    ///   Entries fall into two groups:
    ///   <list type="bullet">
    ///     <item>Transaction entries (<see cref="CreateVertex" />..<see cref="RemoveGraphElements" />)
    ///       carry a serialized definition and are replayed by re-executing the equivalent
    ///       transaction against the loaded snapshot.</item>
    ///     <item>Lifecycle markers (<see cref="Trim" />, <see cref="TabulaRasa" />) carry no
    ///       payload and are replayed by re-running the corresponding id-space operation, so the
    ///       exact id reassignment / reset is reproduced in commit order.</item>
    ///   </list>
    /// </summary>
    internal enum WalEntryType : byte
    {
        CreateVertex = 1,
        CreateVertices = 2,
        CreateEdge = 3,
        CreateEdges = 4,
        AddProperty = 5,
        AddProperties = 6,
        RemoveProperty = 7,
        RemoveGraphElement = 8,
        RemoveGraphElements = 9,

        /// <summary>An explicit or automatic compaction (id reassignment). No payload.</summary>
        Trim = 10,

        /// <summary>A full in-memory reset (clears the graph, resets the id space). No payload.</summary>
        TabulaRasa = 11,

        /// <summary>
        ///   A subgraph created from a persistable recipe. Payload: the serialized
        ///   <see cref="SubGraph.SubGraphRecipe"/> (JSON) + the source subgraph name (empty for a
        ///   root subgraph). Replayed by recompiling the recipe and re-executing the equivalent
        ///   create against the replayed graph, in commit order. Delegate-only subgraphs (no recipe)
        ///   are not logged, matching snapshot persistence.
        /// </summary>
        CreateSubGraph = 12,

        /// <summary>A subgraph removal. Payload: the subgraph name.</summary>
        RemoveSubGraph = 13,

        /// <summary>
        ///   A stored query registration (feature stored-query-library). Payload: the serialized
        ///   <see cref="StoredQueries.StoredQueryDefinition"/> (JSON). Unlike subgraphs there is no
        ///   unloggable case - a stored query IS its serializable source. Replayed by recompiling
        ///   the definition (via the registered <c>IStoredQueryCompiler</c>) and re-executing the
        ///   equivalent registration in commit order; a replay recompile failure keeps the entry as
        ///   Failed rather than dropping it (operator-registered state, never silently lost).
        /// </summary>
        RegisterStoredQuery = 14,

        /// <summary>A stored query removal (feature stored-query-library). Payload: the name.</summary>
        RemoveStoredQuery = 15,

        /// <summary>
        ///   A batch of named element-embedding writes (feature element-embeddings). Payload: the
        ///   serialized <see cref="Model.EmbeddingSetDefinition" /> list (element id, name, vector
        ///   or removal marker). Replayed by re-executing the equivalent
        ///   <see cref="Transaction.SetEmbeddingsTransaction" /> against the loaded snapshot.
        /// </summary>
        SetEmbeddings = 16
    }
}
