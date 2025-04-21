// MIT License
//
// IOwnedDataSerializable.cs
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
    /// Interface which allows a class to save/retrieve their internal data to/from an existing SerializationWriter/SerializationReader.
    /// </summary>
    public interface IOwnedDataSerializable
    {
        /// <summary>
        /// Lets the implementing class store internal data directly into a SerializationWriter.
        /// </summary>
        /// <param name="writer">The SerializationWriter to use</param>
        /// <param name="context">Optional context to use as a hint as to what to store (BitVector32 is useful)</param>
        void SerializeOwnedData(SerializationWriter writer, object context);

        /// <summary>
        /// Lets the implementing class retrieve internal data directly from a SerializationReader.
        /// </summary>
        /// <param name="reader">The SerializationReader to use</param>
        /// <param name="context">Optional context to use as a hint as to what to retrieve (BitVector32 is useful) </param>
        void DeserializeOwnedData(SerializationReader reader, object context);
    }
}
