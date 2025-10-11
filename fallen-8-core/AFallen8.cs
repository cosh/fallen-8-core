// MIT License
//
// AFallen8.cs
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
using System.Collections.Immutable;
using NoSQL.GraphDB.Core.Algorithms.Path;
using NoSQL.GraphDB.Core.Expression;
using NoSQL.GraphDB.Core.Index;
using NoSQL.GraphDB.Core.Index.Fulltext;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Service;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Core
{
    /// <summary>
    ///   Abstract base class for Fallen8 implementations.
    /// </summary>
    public abstract class AFallen8 : IFallen8
    {
        /// <summary>
        ///   Gets the unique identifier for this Fallen8 instance.
        /// </summary>
        public Guid Id
        {
            get;
        }

        /// <summary>
        ///   Gets the index factory.
        /// </summary>
        public abstract IndexFactory IndexFactory
        {
            get; internal set;
        }

        /// <summary>
        ///   Gets the service factory.
        /// </summary>
        public abstract ServiceFactory ServiceFactory
        {
            get; internal set;
        }

        /// <summary>
        ///   Gets the count of vertices.
        /// </summary>
        public abstract Int32 VertexCount
        {
            get; protected set;
        }

        /// <summary>
        ///   Gets the count of edges.
        /// </summary>
        public abstract Int32 EdgeCount
        {
            get; protected set;
        }

        /// <summary>
        ///   Initializes a new instance of the AFallen8 class.
        /// </summary>
        protected AFallen8()
        {
            Id = Guid.NewGuid();
        }

        #region IRead Members

        public abstract bool TryGetGraphElement(out AGraphElementModel result, int id);
        public abstract bool TryGetEdge(out EdgeModel result, int id);
        public abstract bool TryGetVertex(out VertexModel result, int id);
        public abstract ImmutableList<VertexModel> GetAllVertices(string interestingLabel = null);
        public abstract ImmutableList<EdgeModel> GetAllEdges(string interestingLabel = null);
        public abstract bool GraphScan(out List<AGraphElementModel> result, string propertyId, IComparable literal, BinaryOperator binOp = BinaryOperator.Equals, string interestingLabel = null);
        public abstract bool IndexScan(out ImmutableList<AGraphElementModel> result, string indexId, IComparable literal, BinaryOperator binOp = BinaryOperator.Equals);
        public abstract bool RangeIndexScan(out ImmutableList<AGraphElementModel> result, string indexId, IComparable leftLimit, IComparable rightLimit, bool includeLeft = true, bool includeRight = true);
        public abstract bool FulltextIndexScan(out FulltextSearchResult result, string indexId, string searchQuery);
        public abstract bool TryCalculateShortestPath(out List<Path> result, string plugin, ShortestPathDefinition definition);
        public abstract bool TryCalculateShortestPath<T>(out List<Path> result, ShortestPathDefinition definition) where T : IShortestPathAlgorithm;

        #endregion

        #region IWrite Members

        public abstract TransactionInformation EnqueueTransaction(ATransaction tx);
        public abstract TransactionState GetTransactionState(string txId);

        #endregion

        #region IDisposable Members

        public abstract void Dispose();

        #endregion
    }
}
