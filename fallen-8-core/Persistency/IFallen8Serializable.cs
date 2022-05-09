﻿// MIT License
//
// IFallen8Serializable.cs
//
// Copyright (c) 2022 Henning Rauch
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

using NoSQL.GraphDB.Core.Serializer;

namespace NoSQL.GraphDB.Core.Persistency
{
    /// <summary>
    /// The interface for serializable things in Fallen-8
    /// </summary>
    public interface IFallen8Serializable
    {
        /// <summary>
        /// Save the plugin.
        /// </summary>
        /// <param name='writer'>
        /// Writer.
        /// </param>
        void Save(SerializationWriter writer);

        /// <summary>
        ///   Load the plugin.
        /// </summary>
        /// <param name="reader">The reader</param>
        /// <param name="fallen8">Fallen-8</param>
        void Load(SerializationReader reader, Fallen8 fallen8);
    }
}
