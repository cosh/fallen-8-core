// MIT License
//
// IFastSerializationTypeSurrogate.cs
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

#region Usings

using System;

#endregion

namespace NoSQL.GraphDB.Core.Serializer
{
    /// <summary>
    /// Interface to allow helper classes to be used to serialize objects
    /// that are not directly supported by SerializationWriter/SerializationReader
    /// </summary>
    public interface IFastSerializationTypeSurrogate
    {
        /// <summary>
        /// Allows a surrogate to be queried as to whether a particular type is supported
        /// </summary>
        /// <param name="type">The type being queried</param>
        /// <returns>true if the type is supported; otherwise false</returns>
        bool SupportsType(Type type);

        /// <summary>
        /// FastSerializes the object into the SerializationWriter.
        /// </summary>
        /// <param name="writer">The SerializationWriter into which the object is to be serialized.</param>
        /// <param name="value">The object to serialize.</param>
        void Serialize(SerializationWriter writer, object value);

        /// <summary>
        /// Deserializes an object of the supplied type from the SerializationReader.
        /// </summary>
        /// <param name="reader">The SerializationReader containing the serialized object.</param>
        /// <param name="type">The type of object required to be deserialized.</param>
        /// <returns></returns>
        object Deserialize(SerializationReader reader, Type type);
    }
}
