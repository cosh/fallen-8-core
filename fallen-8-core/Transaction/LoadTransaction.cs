// MIT License
//
// LoadTransaction.cs
//
// Copyright (c) 2011-2026 Henning Rauch
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

using NoSQL.GraphDB.Core.Model;
using System;
using System.Diagnostics.CodeAnalysis;

namespace NoSQL.GraphDB.Core.Transaction
{
    /// <summary>
    ///   Loads a checkpoint. Annotated at the TYPE, which is the useful place for a trimming
    ///   consumer: the warning lands where the transaction is constructed rather than deep inside the
    ///   persistency layer, and it covers this transaction's body without marking the abstract
    ///   <see cref="ATransaction.TryExecute" /> - the write path itself stays trim-safe.
    /// </summary>
    [RequiresUnreferencedCode(RequiresReflectiveCheckpoint)]
    public class LoadTransaction : ATransaction
    {
        /// <summary>THE trim-requirement message shared by the two checkpoint transactions: a
        /// checkpoint stores property values plus index and service plugin NAMES, and load resolves
        /// all of them reflectively.</summary>
        internal const String RequiresReflectiveCheckpoint =
            "A checkpoint stores property values and index/service plugin names that are resolved reflectively on load, which a trimmer cannot preserve. The rest of a checkpoint round trip does work on a single-threaded host (its fan-out runs inline there), so this is a trimming requirement rather than a platform one.";

        public String Path
        {
            get;
            set;
        }

        public Boolean StartServices
        {
            get;
            set;
        } = false;

        internal override void Cleanup()
        {
            //NOP
        }

        internal override void Rollback(Fallen8 f8)
        {
            //TODO
        }

        internal override Boolean TryExecute(Fallen8 f8)
        {
            f8.Load_internal(Path, StartServices);

            return true;
        }

        internal override void DescribeChanges(Fallen8 f8, ChangeFeed.ChangeDescriptor.Builder builder)
        {
            // State was replaced wholesale; a subscriber re-fetches everything it cares about.
            builder.Resync(ChangeFeed.ChangeFeedDispatcher.ResyncReasonLoad);
        }
    }
}
