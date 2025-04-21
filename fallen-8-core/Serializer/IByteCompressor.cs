// MIT License
//
// IByteCompressor.cs
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

namespace NoSQL.GraphDB.Core.Serializer
{
    /// <summary>
    /// Interface to implement on a compressor class which can be used to compress/decompress the resulting byte array of the Fast serializer.
    /// </summary>
    public interface IByteCompressor
    {
        /// <summary>
        /// Compresses the specified serialized data.
        /// </summary>
        /// <param name="serializedData">The serialized data.</param>
        /// <returns>The  passed in serialized data in compressed form</returns>
        byte[] Compress(byte[] serializedData);

        /// <summary>
        /// Decompresses the specified compressed data.
        /// </summary>
        /// <param name="compressedData">The compressed data.</param>
        /// <returns>The  passed in de-serialized data in compressed form</returns>
        byte[] Decompress(byte[] compressedData);
    }
}
