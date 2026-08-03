// MIT License
//
// VectorRankability.cs
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

namespace NoSQL.GraphDB.Core.Index.Vector
{
    /// <summary>
    ///   Why a vector can or cannot rank in a <see cref="VectorIndex"/> projection - the result of
    ///   <see cref="VectorIndex.Classify"/>. A vector ranks iff it matches the index dimension, is
    ///   all-finite, and (under Cosine only) has a non-zero norm. The failure members are ordered
    ///   by the priority the classification applies (dimension, then finiteness, then zero-norm),
    ///   so a caller reporting a specific reason reports the first failing condition.
    /// </summary>
    public enum VectorRankability
    {
        /// <summary>The vector can rank: correct dimension, all-finite, and non-zero-norm under Cosine.</summary>
        Ok,

        /// <summary>The vector's length does not equal the index dimension.</summary>
        WrongDimension,

        /// <summary>The vector contains a NaN or Infinity component.</summary>
        NonFinite,

        /// <summary>The vector has a zero norm and the index metric is Cosine (its score would be NaN).</summary>
        ZeroNormUnderCosine
    }
}
