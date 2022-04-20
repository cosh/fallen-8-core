﻿// MIT License
//
// IWrite.cs
//
// Copyright (c) 2021 Henning Rauch
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
using System.Threading.Tasks;
using NoSQL.GraphDB.Core.Model;
using NoSQL.GraphDB.Core.Transaction;

namespace NoSQL.GraphDB.Core
{
    /// <summary>
    ///   Fallen8 write interface.
    /// </summary>
    public interface IWrite
    {
        #region update

        /// <summary>
        ///   Tries to add a property.
        /// </summary>
        /// <returns> <c>true</c> if the property was added; otherwise, <c>false</c> . </returns>
        /// <param name='graphElementId'> Graph element identifier. </param>
        /// <param name='propertyId'> Property identifier. </param>
        /// <param name='property'> The to be added property. </param>
        Boolean TryAddProperty(Int32 graphElementId, UInt16 propertyId, Object property);

        /// <summary>
        ///   Tries to remove a property.
        /// </summary>
        /// <returns> <c>true</c> if the property was removed; otherwise, <c>false</c> . </returns>
        /// <param name='graphElementId'> Graph element identifier. </param>
        /// <param name='propertyId'> Property identifier. </param>
        Boolean TryRemoveProperty(Int32 graphElementId, UInt16 propertyId);

        #endregion

        #region delete

        /// <summary>
        ///   Tries the remove graph element.
        /// </summary>
        /// <returns> <c>true</c> if the graph element was removed; otherwise, <c>false</c> . </returns>
        /// <param name='graphElementId'> Graph element identifier. </param>
        Boolean TryRemoveGraphElement(Int32 graphElementId);

        #endregion

        #region Tabula rasa

        /// <summary>
        ///   Put the database in its initial state.
        /// </summary>
        void TabulaRasa();

        #endregion

        #region Trim

        /// <summary>
        ///   Trims Fallen-8 to its minimum memory usage
        /// </summary>
        void Trim();

        #endregion

        #region Load

        /// <summary>
        /// Load a Fallen-8 from a specified path
        /// </summary>
        /// <param name="path">The path.</param>
        /// <param name="startServices">Start the services?</param>
        void Load(String path, Boolean startServices = false);

        #endregion

        #region Transaction

        /// <summary>
        /// Exqueues a new transaction
        /// </summary>
        /// <param name="tx">The transaction</param>
        /// <returns>The task of the transaction</returns>
        Task<TransactionInformation> EnqueueTransaction(ATransaction tx);

        /// <summary>
        /// Gets the state of a transaction
        /// </summary>
        /// <param name="txId">The transaction id</param>
        /// <returns>The task of the transaction</returns>
        TransactionState  GetTransactionState(String txId);

        #endregion
    }
}
