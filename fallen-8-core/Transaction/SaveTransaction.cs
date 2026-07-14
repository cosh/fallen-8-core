// MIT License
//
// SaveTransaction.cs
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

namespace NoSQL.GraphDB.Core.Transaction
{
    public class SaveTransaction : ATransaction
    {
        /// <summary>
        /// Number of partitions to split the graph elements into when persisting. A non-positive
        /// value (the default) lets the engine size the partition count by work and core count
        /// (<c>min(cores, ceil(count / target))</c>), so a large graph is not forced into a small
        /// fixed number of oversized bunches - which could push a single bunch past the 2 GB per-file
        /// limit (feature load-path-integrity L2). A positive value is honoured as an explicit cap.
        /// </summary>
        public Int32 SavePartitions
        {
            get;
            set;
        } = 0;

        /// <summary>
        /// The path where the data should be persisted
        /// </summary>
        public String Path
        {
            get;
            set;
        } = "Savegame.f8s";


        /// <summary>
        /// The path where the data should be persisted
        /// </summary>
        public String ActualPath
        {
            get;
            private set;
        }

        internal override void Rollback(Fallen8 f8)
        {
            //NOP
        }

        internal override Boolean TryExecute(Fallen8 f8)
        {
            ActualPath = f8.Save(Path, SavePartitions);

            return true;
        }

        public static UInt32 GetOptimalNumberOfPartitions()
        {
            return Convert.ToUInt32(Environment.ProcessorCount * 3 / 2);
        }

        internal override void Cleanup()
        {
            //NOOP
        }
    }
}
